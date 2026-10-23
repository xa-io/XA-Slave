using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Data;

namespace XASlave.Services.Tasks;

/// <summary>
/// Monthly Relogger - rotates through a list of characters, logging into each
/// and executing a configurable sequence of actions per character.
///
/// Uses IPC: AutoRetainer (relog, multi-mode), Lifestream (home/fc), TextAdvance, XA Database
/// Uses commands: /ays relog, /at y, /nastatus off, /inventory, /armourychest, /saddlebag, /freecompanycmd
/// </summary>
public sealed class MonthlyReloggerTask
{
    private readonly Plugin plugin;
    private InventoryOperationContext? activeInventoryOperation;

    // -- Configurable actions per character --
    public bool DoEnableTextAdvance { get; set; } = true;
    public bool DoRemoveSprout { get; set; } = true;
    public bool DoOpenInventory { get; set; } = true;
    public bool DoOpenArmouryChest { get; set; } = true;
    public bool DoOpenSaddlebags { get; set; } = true;
    public bool DoOpenJournal { get; set; } = true;
    public bool DoDiscardItems { get; set; } = false;
    public bool DoExpertDelivery { get; set; } = false;
    public bool DoReturnToHome { get; set; } = true;
    public bool DoCollectPersonalPlotInfo { get; set; } = true;
    public bool DoReturnToFc { get; set; } = true;
    public bool DoParseForXaDatabase { get; set; } = true;
    public bool DoLogoutOnComplete { get; set; } = false;
    public bool DoKillGameOnComplete { get; set; } = false;
    public bool DoEnableArMultiOnComplete { get; set; } = false;

    public MonthlyReloggerTask(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private bool IsVerboseTaskLoggingEnabled()
    {
        return plugin.Configuration.VerboseTaskLogging;
    }

    private static bool IsVerboseTaskLoggingEnabledStatic()
    {
        return Plugin.Instance?.Configuration.VerboseTaskLogging ?? false;
    }

    /// <summary>
    /// Builds the complete step list for the relogger task.
    /// 0. Pre-flight: detect main menu / char select / movie / logged-in state
    /// 1. Disable AR Multi Mode
    /// 2. For each character: relog → wait → per-char actions
    /// 3. Re-enable AR Multi Mode
    /// </summary>
    public List<TaskStep> BuildSteps(List<string> characters, TaskRunner runner, Action<string>? onCharacterCompleted = null)
    {
        var steps = new List<TaskStep>();

        // -- PRIORITY #1: Disable AR Multi Mode FIRST --
        // Must happen before pre-flight to prevent AR from relogging during checks
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S22C41FFCBA42741B"),
            OnEnter = () =>
            {
                runner.AddLog("Disabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SC4B6369ECA28F664"), 1.0f));

        // ═══════════════════════════════════════════════════════
        //  PRE-FLIGHT SEQUENCE
        //  Detects current game state and navigates to a safe
        //  starting point before processing any characters.
        // ═══════════════════════════════════════════════════════
        steps.AddRange(BuildPreFlightSteps(characters, runner));

        // -- Steps for each character --
        for (int i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;

            // Label update
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S5FB6F2415218C668", charIndex, charTotal, charName),
                OnEnter = () =>
                {
                    runner.RecordItemStart(charName); // begin active-processing timer for this character
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"-- Processing {charName} ({charIndex}/{charTotal}) --");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog if needed (with retry logic)
            var relogState = new RelogState();
            steps.AddRange(BuildRelogSteps(charName, runner, relogState));

            // Failsafe: if the relog wait timed out (deleted/unavailable character),
            // re-run the pre-flight checklist to recover a safe state before moving on.
            steps.AddRange(BuildRelogTimeoutRecoverySteps(charName, runner, relogState));

            steps.AddRange(BuildDutyGuardSteps(charName, runner, relogState));
            steps.AddRange(BuildHomeworldCheckSteps(charName, runner, relogState));

            // Per-character actions - built inline so ordering is correct
            // Each action step is gated by relog success
            var perCharActions = BuildPerCharacterActions(charName, charIndex, charTotal, runner, relogState);
            foreach (var action in perCharActions)
            {
                var origEnter = action.OnEnter;
                var origComplete = action.IsComplete;
                var origSkip = action.ShouldSkip;
                var origOnTimeout = action.OnTimeout;
                var origMaxRetries = action.MaxRetries;
                steps.Add(new TaskStep
                {
                    NameMessage = action.NameMessage,
                    ShouldSkip = () => relogState.ShouldHalt || (origSkip?.Invoke() ?? false),
                    OnEnter = () =>
                    {
                        if (relogState.ShouldHalt) return;
                        origEnter?.Invoke();
                    },
                    IsComplete = () => relogState.ShouldHalt || origComplete(),
                    TimeoutSec = action.TimeoutSec,
                    MaxRetries = origMaxRetries,
                    OnTimeout = () =>
                    {
                        if (relogState.ShouldHalt) return;
                        origOnTimeout?.Invoke();
                    },
                });
            }

            // Mark character complete + record last-logged-in
            var capturedCharName = charName;
            var capturedCharIndex = charIndex;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB5063DC812CCDD2B", capturedCharName),
                OnEnter = () =>
                {
                    runner.RecordItemEnd(capturedCharName); // stop the active-processing timer (any outcome)
                    runner.CompletedItems = capturedCharIndex;
                    if (relogState.Failed)
                    {
                        runner.AddLog($"Skipped {capturedCharName} ({capturedCharIndex}/{charTotal}) - relog failed");
                        return;
                    }

                    if (relogState.Incomplete)
                    {
                        // Logged in but the process could not finish - record the login (the relog
                        // goal was met) but leave the character selected so it can be re-run.
                        runner.AddLog($"Incomplete {capturedCharName} ({capturedCharIndex}/{charTotal}) - logged in but the process did not finish");
                        RecordReloggerLastLoggedIn(capturedCharName);
                        return;
                    }

                    runner.AddLog($"Finished {capturedCharName} ({capturedCharIndex}/{charTotal})");

                    // Record last-logged-in timestamp in persistent ReloggerCharacterInfo
                    RecordReloggerLastLoggedIn(capturedCharName);

                    // Deselect completed character from relogger checkbox list
                    try
                    {
                        onCharacterCompleted?.Invoke(capturedCharName);
                        runner.AddLog($"Unchecked {capturedCharName} from relogger list");
                    }
                    catch { /* non-critical */ }
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        // -- Summary: Report any failed characters --
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SD6BD177EDBD6AEDC"),
            OnEnter = () =>
            {
                if (!ShouldKeepLogoutCancelSuppressed(DoLogoutOnComplete, DoKillGameOnComplete))
                    runner.SuppressLogoutCancel = false;

                if (runner.FailedCharacters.Count > 0)
                {
                    runner.AddLog($"══ SUMMARY: {runner.FailedCharacters.Count} character(s) FAILED to relog ══");
                    foreach (var failed in runner.FailedCharacters)
                        runner.AddLog($"  ✗ {failed}");
                }
                else
                {
                    runner.AddLog($"══ SUMMARY: All {characters.Count} character(s) processed successfully ══");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        AddSharedCompletionSteps(steps, runner, DoLogoutOnComplete, DoKillGameOnComplete, DoEnableArMultiOnComplete);

        // -- Optional: Re-enable AR Multi Mode after all characters processed --
        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SB4678F98BF3E7AA8"), 1.0f));

        return steps;
    }

    /// <summary>
    /// Builds relog steps for a character with retry logic.
    /// Uses /ays relog Name@World command via CommandManager.
    ///
    /// Flow:
    ///   1. Check if already logged in → skip relog if yes
    ///   2. Send /ays relog, wait via SafeWait (relog takes 1-2 minutes)
    ///   3. After SafeWait, verify GetCurrentCharacterNameWorld() == target
    ///   4. If wrong character, retry up to 3 total attempts
    ///   5. If all 3 fail, skip character and log failure to runner.FailedCharacters
    /// </summary>
    private List<TaskStep> BuildRelogSteps(string charName, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();

        for (var attempt = 1; attempt <= MaxRelogAttempts; attempt++)
        {
            var capturedAttempt = attempt;
            Func<bool> skipAttempt = () => relogState.Failed || (relogState.Confirmed && relogState.Attempt != capturedAttempt);

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDFA21DAC50BD5A52", capturedAttempt, charName),
                ShouldSkip = skipAttempt,
                OnEnter = () =>
                {
                    if (skipAttempt())
                        return;

                    relogState.Attempt = capturedAttempt;
                    relogState.SawTransition = false;

                    var current = GetCurrentCharacterNameWorld();
                    Plugin.Log.Information($"[XASlave] Relog check: current='{current}' target='{charName}' attempt={capturedAttempt}/{MaxRelogAttempts}");
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Already logged in as {charName}");
                        relogState.Confirmed = true;
                        return;
                    }

                    if (capturedAttempt == 1 && IsCharacterMissingFromAutoRetainer(charName))
                        runner.AddLog($"Note: {charName} is not in AutoRetainer's data (not found in AR) - /ays relog may fail to find it.");

                    runner.SuppressLogoutCancel = true;
                    var actionLabel = capturedAttempt == 1 ? "Relogging" : "Retrying relog";
                    runner.AddLog($"{actionLabel} to {charName} (attempt {capturedAttempt}/{MaxRelogAttempts})...");
                    ChatHelper.SendMessage($"/ays relog {charName}");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });

            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S235EA4C48A13CB35", charName, capturedAttempt, MaxRelogAttempts), 2.0f, skipAttempt));

            bool? lastRelogPlayerLoaded = null;
            bool? lastRelogBusy = null;
            bool? lastRelogSawTransition = null;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S1591BECE0E1EB42F", charName, capturedAttempt, MaxRelogAttempts),
                ShouldSkip = skipAttempt,
                OnEnter = () =>
                {
                    lastRelogPlayerLoaded = null;
                    lastRelogBusy = null;
                    lastRelogSawTransition = null;
                    if (IsVerboseTaskLoggingEnabled())
                        runner.AddLog($"Relog wait: monitoring login transition for {charName} (attempt {capturedAttempt}/{MaxRelogAttempts})...");
                },
                IsComplete = () =>
                {
                    if (relogState.Failed || (relogState.Confirmed && relogState.Attempt != capturedAttempt))
                        return true;
                    if (relogState.Confirmed)
                        return true;

                    try
                    {
                        var playerLoaded = Plugin.PlayerState.IsLoaded && Plugin.ObjectTable.LocalPlayer != null;
                        var lifestreamBusy = plugin.IpcClient.LifestreamIsBusy();

                        if (IsVerboseTaskLoggingEnabled() && (lastRelogPlayerLoaded != playerLoaded || lastRelogBusy != lifestreamBusy || lastRelogSawTransition != relogState.SawTransition))
                        {
                            runner.AddLog($"Relog wait status: playerLoaded={playerLoaded}, lifestreamBusy={lifestreamBusy}, sawTransition={relogState.SawTransition}");
                            lastRelogPlayerLoaded = playerLoaded;
                            lastRelogBusy = lifestreamBusy;
                            lastRelogSawTransition = relogState.SawTransition;
                        }

                        if (!playerLoaded || lifestreamBusy)
                        {
                            relogState.SawTransition = true;
                            if (IsVerboseTaskLoggingEnabled() && lastRelogSawTransition != relogState.SawTransition)
                            {
                                runner.AddLog($"Relog wait status: playerLoaded={playerLoaded}, lifestreamBusy={lifestreamBusy}, sawTransition={relogState.SawTransition}");
                                lastRelogSawTransition = relogState.SawTransition;
                            }
                            return false;
                        }

                        return relogState.SawTransition && !lifestreamBusy;
                    }
                    catch
                    {
                        return relogState.SawTransition;
                    }
                },
                TimeoutSec = RelogWaitTimeoutSec,
                OnTimeout = () =>
                {
                    if (relogState.Failed || relogState.Confirmed)
                        return;

                    // A full RelogWaitTimeoutSec window elapsed without the character loading.
                    // The target is almost certainly deleted/unavailable and AutoRetainer is stuck
                    // (typically parked on character select). Fail this character immediately rather
                    // than burning additional timeout windows, flag it for pre-flight recovery, and
                    // let the failsafe return the game to a safe state for the next character.
                    runner.AddLog($"Relog attempt {capturedAttempt}/{MaxRelogAttempts} for {charName} exceeded the {RelogWaitTimeoutSec:0}s login timeout - character may be deleted or unavailable.");
                    relogState.TimedOut = true;
                    RecordRelogFailure(runner, relogState, charName,
                        $"FAILED: {charName} - login timed out after {RelogWaitTimeoutSec:0}s (character may have been deleted).");
                },
            });

            AddLoggedCharacterSafeWait3Pass(
                steps,
                global::XASlave.Localization.UiMessage.Format("S5306268DF5518984", charName, capturedAttempt, MaxRelogAttempts),
                30f,
                runner,
                skipAttempt);

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDB620FF87BA96C68", charName, capturedAttempt, MaxRelogAttempts),
                ShouldSkip = skipAttempt,
                OnEnter = () =>
                {
                    if (relogState.Failed || relogState.Confirmed)
                        return;

                    var current = GetCurrentCharacterNameWorld();
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Relog confirmed: {charName} (attempt {capturedAttempt}/{MaxRelogAttempts})");
                        relogState.Confirmed = true;
                        return;
                    }

                    if (capturedAttempt >= MaxRelogAttempts)
                    {
                        RecordRelogFailure(runner, relogState, charName,
                            $"FAILED: {charName} - could not relog after {MaxRelogAttempts} attempts (current: '{current}')");
                    }
                    else
                    {
                        runner.AddLog($"Relog verification failed: expected '{charName}', got '{current}' (attempt {capturedAttempt}/{MaxRelogAttempts})");
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
        }

        return steps;
    }

    private const int MaxRelogAttempts = 3;

    /// <summary>
    /// Hard cap on how long a single /ays relog login attempt may run before the
    /// failsafe kicks in. If the character never loads within this window it is
    /// almost certainly deleted/unavailable (AutoRetainer gets stuck on character
    /// select), so we fail it, run a pre-flight recovery, and move on.
    /// </summary>
    private const float RelogWaitTimeoutSec = 300f;

    /// <summary>Tracks state across relog retry steps via closure.</summary>
    private class RelogState
    {
        public int Attempt;
        public bool Confirmed;
        public bool Failed;        // could not log in as the target character (RED)
        public bool TimedOut;      // login wait hit RelogWaitTimeoutSec -> needs pre-flight recovery
        public bool Incomplete;    // logged in OK but the per-character process could not finish (PURPLE)
        public bool SawTransition;

        /// <summary>True once this character has reached a terminal outcome and the
        /// remaining per-character steps should be skipped.</summary>
        public bool ShouldHalt => Failed || Incomplete;
    }

    private static void RecordRelogFailure(TaskRunner runner, RelogState relogState, string charName, string message)
    {
        runner.AddLog(message);
        if (!runner.FailedCharacters.Contains(charName))
            runner.RecordFailedCharacter(charName);
        relogState.Failed = true;
    }

    /// <summary>
    /// Marks a character that logged in but could not complete its per-character
    /// process. Rendered PURPLE in the processing list (distinct from RED failures
    /// that never logged in). The remaining steps for the character are skipped.
    /// </summary>
    private static void RecordRelogIncomplete(TaskRunner runner, RelogState relogState, string charName, string message)
    {
        runner.AddLog(message);
        if (!runner.IncompleteCharacters.Contains(charName))
            runner.RecordIncompleteCharacter(charName);
        relogState.Incomplete = true;
    }

    /// <summary>
    /// True if the last AutoRetainer import/refresh found no data for this character.
    /// Such characters cannot be relogged with /ays relog and are expected to fail.
    /// </summary>
    private bool IsCharacterMissingFromAutoRetainer(string charName)
    {
        try
        {
            return plugin.Configuration.ReloggerCharacterInfo.TryGetValue(charName, out var data)
                && data.FoundInAutoRetainer == false;
        }
        catch { return false; }
    }

    /// <summary>Records the last-logged-in timestamp for a character (best effort).</summary>
    private void RecordReloggerLastLoggedIn(string charName)
    {
        try
        {
            var cfg = plugin.Configuration;
            if (!cfg.ReloggerCharacterInfo.TryGetValue(charName, out var data))
                data = new ReloggerCharacterData();
            data.LastLoggedIn = DateTime.UtcNow;
            cfg.ReloggerCharacterInfo[charName] = data;
            cfg.Save();
        }
        catch { /* non-critical */ }
    }

    /// <summary>
    /// Failsafe recovery that runs ONLY when a relog wait timed out (relogState.TimedOut),
    /// i.e. the target character was deleted/unavailable and AutoRetainer is stuck (typically
    /// parked on character select). Re-runs the pre-flight checklist to return the game to a
    /// safe state (main menu) so the next character can be processed. All steps are gated by
    /// relogState.TimedOut and skip instantly when no timeout occurred.
    /// </summary>
    private List<TaskStep> BuildRelogTimeoutRecoverySteps(string charName, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();
        var recovery = new PreFlightState();
        Func<bool> skip = () => !relogState.TimedOut;

        // Re-run the pre-flight state detection (same checks as the initial pre-flight).
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SB1348588F426DBB7", charName),
            ShouldSkip = skip,
            OnEnter = () =>
            {
                if (skip()) return;

                runner.AddLog($"Login-timeout failsafe: re-running pre-flight to recover a safe state after {charName}...");
                recovery.OnMovie = false;
                recovery.OnCharaSelect = false;
                recovery.OnMainMenu = false;

                if (AddonHelper.IsAddonVisible("MovieStaffList"))
                {
                    runner.AddLog("Recovery: main menu movie playing - will press ESC.");
                    recovery.OnMovie = true;
                    return;
                }

                if (AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu"))
                {
                    runner.AddLog("Recovery: on character select - will exit to main menu.");
                    recovery.OnCharaSelect = true;
                    return;
                }

                if (AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu"))
                {
                    runner.AddLog("Recovery: already on main menu - ready to proceed.");
                    recovery.OnMainMenu = true;
                    return;
                }

                runner.AddLog("Recovery: not on a title screen - proceeding to the next character.");
            },
            IsComplete = () => true,
            TimeoutSec = 5f,
        });

        // Exit character select back to the main menu.
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S84A7E6ACB4C5C32F", charName),
            ShouldSkip = () => skip() || !recovery.OnCharaSelect,
            OnEnter = () =>
            {
                runner.AddLog("Recovery: clicking Exit to return to main menu...");
                AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
            },
            IsComplete = () => AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu"),
            TimeoutSec = 15f,
            OnTimeout = () => runner.AddLog("Recovery: timeout waiting for main menu after CharaSelect exit - proceeding anyway."),
        });

        // Skip the main menu movie if one started playing.
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SBB87811BAF4516AA", charName),
            ShouldSkip = () => skip() || !recovery.OnMovie,
            OnEnter = () =>
            {
                runner.AddLog("Recovery: pressing ESC to skip movie...");
                KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
            },
            IsComplete = () => AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu"),
            TimeoutSec = 10f,
            OnTimeout = () => runner.AddLog("Recovery: timeout waiting for title screen after ESC - proceeding anyway."),
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S3E4C3E759FD8CC8C", charName), 1.0f, skip));

        return steps;
    }

    private sealed class InventoryOperationContext
    {
        internal readonly AutoRetainerInventoryOperation Operation;
        internal readonly TaskRunner Runner;
        internal readonly RelogState Relog;
        internal readonly string Character;

        internal InventoryOperationContext(AutoRetainerInventoryOperation operation, TaskRunner runner, RelogState relog, string character)
        {
            Operation = operation;
            Runner = runner;
            Relog = relog;
            Character = character;
        }
    }

    /// <summary>Stops our observation only; AutoRetainer has no scoped cancellation endpoint.</summary>
    public void EndInventoryOperation()
    {
        var context = activeInventoryOperation;
        activeInventoryOperation = null;
        if (context == null) return;
        // Terminal callbacks can run during logout or disposal. Use only recorded state;
        // never dereference a provider, abort its global queue, or modify its configuration.
        RecordRelogIncomplete(context.Runner, context.Relog, context.Character,
            $"Monthly Relogger stopped before the inventory pass finished for {context.Character}; character remains selected.");
        if (context.Operation.MayStillBeRunning)
            context.Runner.AddLog("AutoRetainer/Lifestream may still be running; stop them there before another relogger run.");
    }

    private TaskStep BuildInventoryOperationStep(AutoRetainerInventoryOperation.Kind kind, string charName, TaskRunner runner, RelogState relogState)
    {
        AutoRetainerInventoryOperation? operation = null;
        var previousLogoutSuppression = false;
        var labelMessage = kind == AutoRetainerInventoryOperation.Kind.Discard
            ? global::XASlave.Localization.UiMessage.Key("S827FBC7D11E4A520")
            : global::XASlave.Localization.UiMessage.Key("S13887C7442815B2B");
        var label = labelMessage.Original;

        void Halt(string reason)
        {
            runner.AddLog($"{label}: {reason}");
            EndInventoryOperation();
            runner.RequestHalt($"{label} did not finish for {charName}.");
        }

        return new TaskStep
        {
            NameMessage = labelMessage,
            OnEnter = () =>
            {
                previousLogoutSuppression = runner.SuppressLogoutCancel;
                runner.SuppressLogoutCancel = false;
                operation = new AutoRetainerInventoryOperation(plugin.IpcClient, kind,
                    () => GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase));
                activeInventoryOperation = new InventoryOperationContext(operation, runner, relogState, charName);
                runner.AddLog($"Starting AutoRetainer {label} for {charName} using its configured item rules...");
                if (operation.Start() == AutoRetainerInventoryOperation.State.Failed)
                    Halt(operation.Status);
                else
                    runner.AddLog(operation.Status);
            },
            IsComplete = () =>
            {
                if (operation == null || activeInventoryOperation == null) return false;
                var result = operation.Poll();
                if (result == AutoRetainerInventoryOperation.State.Failed)
                {
                    Halt(operation.Status);
                    return false;
                }
                if (result != AutoRetainerInventoryOperation.State.Finished) return false;
                activeInventoryOperation = null;
                runner.SuppressLogoutCancel = previousLogoutSuppression;
                runner.AddLog($"{label}: {operation.Status}");
                return true;
            },
            TimeoutSec = kind == AutoRetainerInventoryOperation.Kind.Discard ? 305f : 905f,
            OnTimeout = () => Halt("Timed out waiting for provider completion."),
        };
    }

    /// <summary>
    /// Builds the per-character action steps.
    /// </summary>
    private List<TaskStep> BuildPerCharacterActions(string charName, int idx, int total, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();

        // EnableTextAdvanceXA() → /at y
        if (DoEnableTextAdvance)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SA99EFDCC05A20738"),
                OnEnter = () =>
                {
                    runner.AddLog("Enabling TextAdvance...");
                    ChatHelper.SendMessage("/at y");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S4F82758AF63CDF43"), 0.5f));
        }

        // RemoveSproutXA() → /nastatus off
        if (DoRemoveSprout)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S738F6706366C8B8B"),
                OnEnter = () =>
                {
                    runner.AddLog("Removing New Adventurer Status...");
                    ChatHelper.SendMessage("/nastatus off");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SFBFF6DA4476610CB"), 0.5f));
        }

        // OpenInventoryXA() → /inventory
        if (DoOpenInventory)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S166DBD737506990A"),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Inventory...");
                    ChatHelper.SendMessage("/inventory");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S6DBBFE874CDD2B41"), 0.5f));
        }

        // OpenArmouryChestXA() → /armourychest
        if (DoOpenArmouryChest)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S53649CACF2611F56"),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SEBED4948042CC39B"), 0.5f));
        }

        // OpenSaddlebagsXA() → /saddlebag
        // Not every character has saddlebags unlocked - send command and use short timeout
        if (DoOpenSaddlebags)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S04135E2D90F4F408"),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("InventoryBuddy"),
                TimeoutSec = 3f, // Short timeout - if saddlebag not unlocked, skip gracefully
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S2DFAECE897A22861"), 0.5f));
        }

        if (DoOpenJournal)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SF7B51D61398CF038"),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Journal...");
                    ChatHelper.SendMessage("/journal");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("Journal"),
                TimeoutSec = 3f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S85732DC4FA199321"), 0.5f));
            if (plugin.IpcClient.IsXaDatabaseAvailable())
            {
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Key("SE8299921A6AE18A8"),
                    ShouldSkip = () => !AddonHelper.IsAddonVisible("Journal"),
                    OnEnter = () =>
                    {
                        runner.AddLog("Saving Journal data to XA Database...");
                        if (plugin.SaveToXaDatabaseAndRecordSync())
                            runner.AddLog("Saved Journal data to XA Database.");
                        else
                            runner.AddLog("XA Database Journal save failed.");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S1972610E01FD0B9A"), 0.5f, () => !AddonHelper.IsAddonVisible("Journal")));
            }
        }

        if (DoDiscardItems)
            steps.Add(BuildInventoryOperationStep(AutoRetainerInventoryOperation.Kind.Discard, charName, runner, relogState));
        if (DoExpertDelivery)
            steps.Add(BuildInventoryOperationStep(AutoRetainerInventoryOperation.Kind.ExpertDelivery, charName, runner, relogState));

        // return_to_homeXA() → Lifestream: /li home
        // Short timeout (5s) for "wait busy" - if char has no house, Lifestream never becomes busy
        if (DoReturnToHome)
        {
            steps.AddRange(BuildLifestreamTeleportSteps(
                "Home", "home", runner, waitBusyTimeoutSec: 5f));
        }

        if (DoCollectPersonalPlotInfo)
        {
            steps.AddRange(BuildCollectPersonalPlotInfoSteps(runner));
        }

        // return_to_fcXA() → Lifestream: /li fc
        if (DoReturnToFc)
        {
            steps.AddRange(BuildLifestreamTeleportSteps(
                "FC", "fc", runner, waitBusyTimeoutSec: 8f));
        }

        // Parse for XA Database - full FC collection mirroring AutoCollectionService
        // Opens FC window → Members tab → Info tab → Housing search → close all → save
        if (DoParseForXaDatabase)
        {
            // Track whether to skip FC steps (not on homeworld or not in FC)
            var skipFc = false;

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SF66C0CB29C6C1952"),
                OnEnter = () =>
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local != null && local.CurrentWorld.RowId != local.HomeWorld.RowId)
                    {
                        runner.AddLog("Not on home world - skipping FC collection (FC data unavailable when visiting)");
                        skipFc = true;
                        return;
                    }
                    var fcTag = local?.CompanyTag.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(fcTag))
                    {
                        runner.AddLog("Not in a Free Company - skipping FC collection");
                        skipFc = true;
                        return;
                    }
                    skipFc = false;
                    runner.AddLog("Opening FC window for full data collection...");
                    ChatHelper.SendMessage("/freecompanycmd");
                },
                IsComplete = () => skipFc || AddonHelper.IsAddonVisible("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S5265D8FD4D903B59"), 1.0f));

            // Click Members tab (callback 1, node 8)
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S848666A9D221CF64"),
                OnEnter = () =>
                {
                    if (skipFc) return;
                    runner.AddLog("Clicking FC Members tab...");
                    AddonHelper.FireCallback("FreeCompany", 1);
                    AddonHelper.ClickAddonButton("FreeCompany", 8);
                },
                IsComplete = () => skipFc || AddonHelper.IsAddonVisible("FreeCompanyMember"),
                TimeoutSec = 5f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S7E32BB2B81398E3B"), 1.5f));

            // Save after members (XA Database addon watcher also triggers on close)
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S34084C03124F43D7"),
                OnEnter = () =>
                {
                    if (skipFc) return;
                    runner.AddLog("Saving FC member data...");
                    plugin.SaveToXaDatabaseAndRecordSync();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S92232E222F4C75A9"), 0.5f));

            // Click Info tab (callback 3, node 4) → FreeCompanyStatus
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S9600DF2B75A19613"),
                OnEnter = () =>
                {
                    if (skipFc) return;
                    runner.AddLog("Clicking FC Info tab...");
                    AddonHelper.FireCallback("FreeCompany", 3);
                    AddonHelper.ClickAddonButton("FreeCompany", 4);
                },
                IsComplete = () => skipFc || AddonHelper.IsAddonVisible("FreeCompanyStatus"),
                TimeoutSec = 5f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S65FF3ACEF20CA9A2"), 1.0f));

            // Click Housing search button (FreeCompanyStatus node 12) → HousingSignBoard
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SF8E78520366FB775"),
                OnEnter = () =>
                {
                    if (skipFc) return;
                    if (AddonHelper.IsAddonReady("FreeCompanyStatus"))
                    {
                        runner.AddLog("Clicking FC Housing search...");
                        AddonHelper.ClickAddonButton("FreeCompanyStatus", 12);
                    }
                },
                IsComplete = () => skipFc || AddonHelper.IsAddonVisible("HousingSignBoard"),
                TimeoutSec = 5f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SADC6533DAE63E8A1"), 1.5f));

            // Close sub-addons
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S92EA906BF0CDF1EC"),
                OnEnter = () =>
                {
                    if (skipFc) return;
                    runner.AddLog("Closing FC sub-windows...");
                    AddonHelper.CloseAddon("HousingSignBoard");
                    AddonHelper.CloseAddon("FreeCompanyStatus");
                    AddonHelper.CloseAddon("FreeCompanyMember");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S339878A675435940"), 0.5f));

            // Close FC window
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S80872F61AD680ACB"),
                OnEnter = () =>
                {
                    if (skipFc) return;
                    AddonHelper.CloseAddon("FreeCompany");
                },
                IsComplete = () => skipFc || !AddonHelper.IsAddonVisible("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S7D55C434DB6843F5"), 0.5f));

            // Final save to XA Database - all collected data (inventory, armoury, saddlebag, FC, housing)
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S7C7B1E0817035D6B"),
                OnEnter = () =>
                {
                    runner.AddLog("Saving to XA Database (all collected data)...");
                    plugin.SaveToXaDatabaseAndRecordSync();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S9658C562B6505E71"), 0.5f));
        }

        return steps;
    }

    private List<TaskStep> BuildCollectPersonalPlotInfoSteps(TaskRunner runner)
    {
        return BuildCollectPersonalPlotInfoSteps(plugin, runner.AddLog);
    }

    internal static List<TaskStep> BuildCollectPersonalPlotInfoSteps(Plugin plugin, Action<string> addLog)
    {
        var steps = new List<TaskStep>();
        steps.AddRange(BuildCollectHousingTypeSteps(plugin, "Apartment", "Apartment", () =>
            AddonHelper.AddonHasText("HousingSubmenu", "Apartment Options")
            || AddonHelper.AddonHasText("HousingSubmenu", "View Room Details"), addLog));
        steps.AddRange(BuildCollectHousingTypeSteps(plugin, "Private Estate", "Private Estate", () =>
            AddonHelper.AddonHasText("HousingSubmenu", "Private Estate"), addLog));
        steps.AddRange(BuildCollectHousingTypeSteps(plugin, "Shared Estate", "Shared Estate", () =>
            AddonHelper.AddonHasText("HousingSubmenu", "Shared Estate", true), addLog, selectionContains: true));
        return steps;
    }

    private static List<TaskStep> BuildCollectHousingTypeSteps(Plugin plugin, string label, string selectionText, Func<bool> isDirectSubmenuMatch, Action<string> addLog, bool selectionContains = false)
    {
        var steps = new List<TaskStep>();
        var housingUiVisible = false;
        var canCollect = false;
        var selectionClicked = false;

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S317321A9575862F2", label),
            OnEnter = () =>
            {
                housingUiVisible = false;
                canCollect = false;
                selectionClicked = false;
                addLog($"Plot Info: opening housing menu for {label}...");
                ChatHelper.SendMessage("/housing");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("SAD6637FBE082E7B4", label), 1.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S898FBA2C4D91A124", label),
            OnEnter = () =>
            {
                housingUiVisible = AddonHelper.IsAddonVisible("HousingMenu")
                    || AddonHelper.IsAddonVisible("HousingSubmenu")
                    || AddonHelper.IsAddonVisible("HousingSelectHouse");

                if (!housingUiVisible)
                {
                    addLog($"Plot Info: housing UI did not open for {label}.");
                    return;
                }

                if (AddonHelper.IsAddonVisible("HousingMenu"))
                {
                    var variant = AddonHelper.GetHousingMenuVariant();
                    if (!string.IsNullOrEmpty(variant))
                        addLog($"Plot Info: detected {variant}.");

                    if (!AddonHelper.SelectAddonListText("HousingMenu", "Estate Settings"))
                        addLog($"Plot Info: failed to click Estate Settings for {label}.");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S4C307C65598A892A", label), 1.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SECE487A4481E6547", label),
            OnEnter = () =>
            {
                canCollect = false;
                selectionClicked = false;

                if (!housingUiVisible && !AddonHelper.IsAddonVisible("HousingSelectHouse") && !AddonHelper.IsAddonVisible("HousingSubmenu"))
                    return;

                if (AddonHelper.IsAddonVisible("HousingSelectHouse"))
                {
                    if (AddonHelper.AddonHasText("HousingSelectHouse", selectionText, selectionContains))
                    {
                        selectionClicked = AddonHelper.SelectAddonListText("HousingSelectHouse", selectionText, selectionContains);
                        canCollect = selectionClicked;
                        if (selectionClicked)
                            addLog($"Plot Info: selected {label}.");
                        else
                            addLog($"Plot Info: failed to select {label}.");
                        return;
                    }

                    addLog($"Plot Info: {label} not available in HousingSelectHouse.");
                    return;
                }

                if (AddonHelper.IsAddonVisible("HousingSubmenu") && isDirectSubmenuMatch())
                {
                    canCollect = true;
                    addLog($"Plot Info: {label} available via direct HousingSubmenu.");
                    return;
                }

                addLog($"Plot Info: {label} not available for this character.");
            },
            IsComplete = () => !selectionClicked || AddonHelper.IsAddonVisible("HousingSubmenu") || !AddonHelper.IsAddonVisible("HousingSelectHouse"),
            TimeoutSec = 5f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S425CBCF383E81A39", label), 0.75f, () => !canCollect));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SF580EF4066175BAB", label),
            ShouldSkip = () => !canCollect,
            OnEnter = () =>
            {
                var clicked = AddonHelper.SelectFirstAddonListText(
                    "HousingSubmenu",
                    out _,
                    out _,
                    ("View Room Details", false),
                    ("View Estate Details", false),
                    ("Details", true));

                if (clicked)
                    addLog($"Plot Info: opening {label} details...");
                else
                    addLog($"Plot Info: failed to open {label} details.");
            },
            IsComplete = () => AddonHelper.IsAddonVisible("HousingSignBoard"),
            TimeoutSec = 5f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("SE72D9F4EAD6A5A33", label), 1.0f, () => !canCollect));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SAB553F7F96585DDF", label),
            ShouldSkip = () => !canCollect,
            OnEnter = () =>
            {
                var address = AddonHelper.GetAddonTextEntries("HousingSignBoard")
                    .FirstOrDefault(text => text.Contains("Ward", StringComparison.OrdinalIgnoreCase) && text.Contains(",", StringComparison.Ordinal));

                if (!string.IsNullOrEmpty(address))
                    addLog($"Plot Info: {label} address = {address}");
                else
                    addLog($"Plot Info: {label} address text not found.");

                if (plugin.SaveToXaDatabaseAndRecordSync())
                    addLog($"Plot Info: saved {label} data to XA Database.");
                else
                    addLog($"Plot Info: XA Database save failed while collecting {label}.");
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S3EC4F8A8AE0D473B", label),
            OnEnter = () =>
            {
                AddonHelper.CloseAddon("HousingSignBoard");
                AddonHelper.CloseAddon("HousingSubmenu");
                AddonHelper.CloseAddon("HousingSelectHouse");
                AddonHelper.CloseAddon("HousingMenu");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S4EDB428E20C0F3F7", label), 0.5f));

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Lifestream Teleport Steps
    //  Pattern: command → 1s → poll busy → poll not-busy → confirm 3x → safe wait
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Builds the full Lifestream teleport step sequence.
    /// Issue command, wait 1s, poll Lifestream busy, wait for
    /// completion, confirm 3 times not-busy, then CharacterSafeWait.
    ///
    /// waitBusyTimeoutSec controls how long we wait for Lifestream to START being busy.
    /// Short timeout (3-5s) means "skip if this char doesn't have a house".
    /// </summary>
    private List<TaskStep> BuildLifestreamTeleportSteps(
        string label, string command, TaskRunner runner, float waitBusyTimeoutSec = 5f)
    {
        var steps = new List<TaskStep>();

        // 1. Issue the command
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SD3EA11B7C41DD852", label),
            OnEnter = () =>
            {
                runner.AddLog($"Teleporting to {label} (/li {command})...");
                plugin.IpcClient.LifestreamExecuteCommand(command);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        // 2. Wait 1 second for Lifestream to register the command
        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S844C63D9A936CDFD", label), 1.0f));

        // 3. Wait for Lifestream to become busy (start the teleport)
        //    Short timeout - if it never becomes busy, the teleport destination doesn't exist
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SFDAECBF3FCB181C8", label),
            IsComplete = () =>
            {
                try { return plugin.IpcClient.LifestreamIsBusy(); }
                catch { return true; } // Lifestream not available → skip
            },
            TimeoutSec = waitBusyTimeoutSec,
        });

        // 4. Wait for Lifestream to finish (busy → not busy)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB85F840A267646C5", label),
                IsComplete = () =>
                {
                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 60f,
            });
        }

        // 5. Confirm Lifestream is not busy - 3 consecutive 1-second checks
        //    This prevents false positives from brief not-busy flickers
        {
            int confirmCount = 0;
            DateTime lastCheck = DateTime.MinValue;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SC117A4810C7BC8F5", label),
                OnEnter = () => { confirmCount = 0; lastCheck = DateTime.UtcNow; },
                IsComplete = () =>
                {
                    try
                    {
                        // Only check once per second
                        if ((DateTime.UtcNow - lastCheck).TotalSeconds < 1.0)
                            return false;

                        lastCheck = DateTime.UtcNow;

                        if (!plugin.IpcClient.LifestreamIsBusy())
                        {
                            confirmCount++;
                            if (confirmCount >= 3) return true;
                        }
                        else
                        {
                            confirmCount = 0; // Reset if it became busy again
                        }

                        return false;
                    }
                    catch { return true; }
                },
                TimeoutSec = 15f,
            });
        }

        // 6. CharacterSafeWait after teleport
        steps.Add(BuildCharacterSafeWait(global::XASlave.Localization.UiMessage.Format("S4533F4ADA9AEFC30", label), 15f));

        // 7. Final settle
        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S169669DB1966224F", label), 1.0f));

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Pre-Flight Sequence
    //  Detects game state (main menu, char select, movie,
    //  logged-in) and navigates to a safe starting point.
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Builds the pre-flight step sequence executed BEFORE any character processing.
    /// Handles:
    ///   1. _TitleLogo / _TitleMenu (main menu) - already safe, proceed
    ///   2. CharaSelect - click Exit to return to main menu
    ///   3. MovieStaffList (main menu movie) - press ESC, wait for title screen
    ///   4. Logged in - check for duty, SafeWait 3-pass, reorder character list
    /// </summary>
    private List<TaskStep> BuildPreFlightSteps(List<string> characters, TaskRunner runner)
    {
        var steps = new List<TaskStep>();
        var preFlightState = new PreFlightState();

        // Step 1: Detect current game state
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S96CA66E22BA27937"),
            OnEnter = () =>
            {
                runner.AddLog("Pre-flight: detecting game state...");

                // Check MovieStaffList FIRST - most specific, only visible during main menu movie
                // During movie: _ScreenText, MovieStaffList, CursorAddon (no _TitleLogo/_TitleMenu)
                if (AddonHelper.IsAddonVisible("MovieStaffList"))
                {
                    runner.AddLog("Pre-flight: main menu movie playing - will press ESC.");
                    preFlightState.OnMovie = true;
                    return;
                }

                // Character select screen
                if (AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu"))
                {
                    runner.AddLog("Pre-flight: on character select - will exit to main menu.");
                    preFlightState.OnCharaSelect = true;
                    return;
                }

                // Main menu (title screen)
                if (AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu"))
                {
                    runner.AddLog("Pre-flight: on main menu - ready to proceed.");
                    preFlightState.OnMainMenu = true;
                    return;
                }

                // Not on any title screen - check if logged in
                var current = GetCurrentCharacterNameWorld();
                if (!string.IsNullOrEmpty(current))
                {
                    runner.AddLog($"Pre-flight: logged in as {current}.");
                    preFlightState.LoggedInAs = current;
                    preFlightState.IsLoggedIn = true;
                }
                else
                {
                    runner.AddLog("Pre-flight: unknown state - will attempt to proceed.");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 5f,
        });

        // Step 2: Handle CharaSelect - exit to main menu
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SFAB2D899BC0E99F8"),
            OnEnter = () =>
            {
                if (!preFlightState.OnCharaSelect) return;
                runner.AddLog("Pre-flight: clicking Exit to return to main menu...");
                // _CharaSelectReturn NodeList[1] = "Exit to Main Menu" button
                AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
            },
            IsComplete = () =>
            {
                if (!preFlightState.OnCharaSelect) return true;
                // Wait until we're back on the title screen
                return AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu");
            },
            TimeoutSec = 15f,
            OnTimeout = () =>
            {
                if (preFlightState.OnCharaSelect)
                    runner.AddLog("Pre-flight: timeout waiting for main menu after CharaSelect exit - proceeding anyway.");
            },
        });

        // Step 3: Handle MovieStaffList - press ESC and wait for title screen
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S1DC007A68D4CACC4"),
            OnEnter = () =>
            {
                if (!preFlightState.OnMovie) return;
                runner.AddLog("Pre-flight: pressing ESC to skip movie...");
                KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
            },
            IsComplete = () =>
            {
                if (!preFlightState.OnMovie) return true;
                return AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu");
            },
            TimeoutSec = 10f,
            OnTimeout = () =>
            {
                if (preFlightState.OnMovie)
                    runner.AddLog("Pre-flight: timeout waiting for title screen after ESC - proceeding anyway.");
            },
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SAC301590CB101166"), 1.0f));

        // Step 4: If logged in, check for duty
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S20DF65F360FB6B80"),
            OnEnter = () =>
            {
                if (!preFlightState.IsLoggedIn) return;
                if (Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    runner.AddLog("Pre-flight: in a duty - will attempt to leave before proceeding.");
                    preFlightState.InDuty = true;
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        // Step 4a-4e: Leave duty if in one (step-based, same as per-char duty guard)
        foreach (var dutyStep in BuildDutyLeaveSequence("Pre-Flight", runner, preFlightState))
        {
            steps.Add(dutyStep);
        }

        AddLoggedCharacterSafeWait3Pass(steps, global::XASlave.Localization.UiMessage.Key("SB41E20AE630FF68E"), 30f, runner, () => !preFlightState.IsLoggedIn);

        // Step 6: Reorder character list - move currently-logged-in character to position 0
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S1F845D49BB1DF162"),
            OnEnter = () =>
            {
                if (!preFlightState.IsLoggedIn || string.IsNullOrEmpty(preFlightState.LoggedInAs)) return;

                var currentChar = preFlightState.LoggedInAs;
                var idx = characters.FindIndex(c => c.Equals(currentChar, StringComparison.OrdinalIgnoreCase));
                if (idx > 0)
                {
                    // Move current character to position 0
                    var ch = characters[idx];
                    characters.RemoveAt(idx);
                    characters.Insert(0, ch);
                    runner.AddLog($"Pre-flight: moved {currentChar} to first position (was #{idx + 1}).");
                }
                else if (idx == 0)
                {
                    runner.AddLog($"Pre-flight: {currentChar} already first in list.");
                }
                else
                {
                    runner.AddLog($"Pre-flight: {currentChar} not in selected list - no reordering needed.");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S732158A9E86F28C9"), 0.5f));

        return steps;
    }

    /// <summary>Tracks state across pre-flight steps via closure.</summary>
    private class PreFlightState
    {
        public bool OnMainMenu;
        public bool OnCharaSelect;
        public bool OnMovie;
        public bool IsLoggedIn;
        public string LoggedInAs = string.Empty;
        public bool InDuty;
        public bool LeaveClickDispatched;
        public bool LeaveFailed;
    }

    /// <summary>
    /// Builds a step-based duty leave sequence for pre-flight.
    /// Gated by preFlightState.InDuty - skips immediately if not in duty.
    /// </summary>
    private static List<TaskStep> BuildDutyLeaveSequence(string label, TaskRunner runner, PreFlightState state)
    {
        var steps = new List<TaskStep>();

        // Wait for combat to end (up to 30s)
        {
            DateTime? combatStart = null;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S67A730102734D9BB", label),
                OnEnter = () =>
                {
                    if (!state.InDuty) return;
                    combatStart = DateTime.UtcNow;
                    if (Plugin.Condition[ConditionFlag.InCombat])
                        runner.AddLog($"{label}: waiting for combat to end...");
                },
                IsComplete = () =>
                {
                    if (!state.InDuty) return true;
                    return !Plugin.Condition[ConditionFlag.InCombat];
                },
                TimeoutSec = 35f,
                OnTimeout = () =>
                {
                    if (state.InDuty && Plugin.Condition[ConditionFlag.InCombat])
                        runner.AddLog($"{label}: still in combat after 30s - will attempt to leave anyway.");
                },
            });
        }

        // Press U to open duty finder menu
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S9744A45D97860DEF", label),
            OnEnter = () =>
            {
                if (!state.InDuty) return;
                runner.AddLog($"{label}: pressing U to open duty menu...");
                KeyInputHelper.PressKey(0x55); // VK_U
            },
            IsComplete = () =>
            {
                if (!state.InDuty) return true;
                return AddonHelper.IsAddonVisible("ContentsFinderMenu");
            },
            TimeoutSec = 5f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("SD46393846FAB0658", label), 0.5f));

        // Click Leave button
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S0352EA7FD037DC1D", label),
            OnEnter = () =>
            {
                if (!state.InDuty) return;
                runner.AddLog($"{label}: clicking Leave button...");
                state.LeaveClickDispatched = AddonHelper.ClickAddonButtonResolved(
                    "ContentsFinderMenu",
                    "Leave",
                    expectedNodeId: null,
                    fallbackNodeListIndex: AddonNodes.ContentsFinderMenuLeave).Succeeded;
                if (!state.LeaveClickDispatched)
                    runner.AddLog($"{label}: Leave button click was not dispatched; the addon node may have changed.");
            },
            IsComplete = () => !state.InDuty
                               || (state.LeaveClickDispatched && AddonHelper.IsAddonVisible("SelectYesno")),
            TimeoutSec = 3f,
            OnTimeout = () => HaltPreFlightDutyLeave(
                label,
                runner,
                state,
                "the Leave duty action could not be dispatched; refusing to continue the relog sequence"),
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S50925147147A152E", label), 0.5f));

        // Click Yes on confirmation
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S01FBF9BE3E48B0A4", label),
            OnEnter = () =>
            {
                if (!state.InDuty) return;
                runner.AddLog($"{label}: confirming Yes to leave duty...");
                AddonHelper.ClickYesNo(true);
            },
            IsComplete = () =>
            {
                if (!state.InDuty) return true;
                return !Plugin.Condition[ConditionFlag.BoundByDuty];
            },
            TimeoutSec = 30f,
            OnTimeout = () =>
            {
                if (state.InDuty && Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    HaltPreFlightDutyLeave(
                        label,
                        runner,
                        state,
                        "the character is still bound by duty after the leave attempt; refusing to continue the relog sequence");
                }
            },
        });

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Homeworld Check (always-on)
    //  After relog + duty guard, verify the character is on
    //  their homeworld. If not, use Lifestream to return.
    //  This ensures housing/FC data is collected correctly.
    // ═══════════════════════════════════════════════════════

    private static void HaltPreFlightDutyLeave(string label, TaskRunner runner, PreFlightState state, string reason)
    {
        if (state.LeaveFailed)
            return;

        state.LeaveFailed = true;
        if (!string.IsNullOrWhiteSpace(state.LoggedInAs))
            runner.RecordIncompleteCharacter(state.LoggedInAs);

        runner.RequestHalt($"{label}: {reason}.");
    }

    /// <summary>
    /// Builds homeworld check steps. If the character is not on their homeworld,
    /// uses Lifestream to travel back. Always-on - cannot be disabled.
    /// Gated by relogState.ShouldHalt (skipped if the character failed to log in or was
    /// already marked incomplete).
    /// </summary>
    private List<TaskStep> BuildHomeworldCheckSteps(string charName, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();
        var needsReturn = false;
        var worldReturnDispatched = false;

        AddLoggedCharacterSafeWait3Pass(steps, global::XASlave.Localization.UiMessage.Format("S13E3F6F51A3AC32E", charName), 30f, runner, () => relogState.ShouldHalt);

        // Check if on homeworld
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S838A91DD0A92C138", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt) return;
                try
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null)
                    {
                        runner.AddLog($"Homeworld Check: LocalPlayer unavailable for {charName}.");
                        return;
                    }
                    var currentWorldId = local.CurrentWorld.RowId;
                    var homeWorldId = local.HomeWorld.RowId;
                    var currentName = local.CurrentWorld.Value.Name.ToString();
                    var homeName = local.HomeWorld.Value.Name.ToString();
                    if (currentWorldId != homeWorldId)
                    {
                        runner.AddLog($"Not on homeworld - currently on {currentName}, returning to {homeName}...");
                        needsReturn = true;
                    }
                    else
                    {
                        runner.AddLog($"Homeworld Check: already on homeworld {homeName}.");
                    }
                }
                catch { /* player not loaded yet, skip */ }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        // If not on homeworld, use Lifestream ChangeWorld IPC to return
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S74EDBB48FD6D6FAD", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt || !needsReturn) return;
                try
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return;
                    var homeName = local.HomeWorld.Value.Name.ToString();
                    runner.AddLog($"Returning to homeworld {homeName} via Lifestream...");
                    worldReturnDispatched = plugin.IpcClient.LifestreamChangeWorld(homeName);
                    if (!worldReturnDispatched)
                        runner.AddLog($"Homeworld Return: Lifestream declined the request for {charName}.");
                }
                catch (Exception ex)
                {
                    runner.AddLog($"Homeworld return error: {ex.Message}");
                }
            },
            IsComplete = () => relogState.ShouldHalt || !needsReturn || worldReturnDispatched,
            TimeoutSec = 3f,
            OnTimeout = () => RecordRelogIncomplete(
                runner,
                relogState,
                charName,
                $"INCOMPLETE: {charName} - Lifestream did not accept the homeworld return request."),
        });

        // Wait for Lifestream to start (if returning) - 2s init delay then poll
        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S4D6D0D5ACA309575", charName), 2.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S29EDC5CE021CEED3", charName),
            IsComplete = () =>
            {
                if (relogState.ShouldHalt || !needsReturn) return true;
                try { return plugin.IpcClient.LifestreamIsBusy(); }
                catch { return true; }
            },
            TimeoutSec = 15f,
        });

        // Wait for Lifestream to finish
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S3C0F44A75B48E99C", charName),
            IsComplete = () =>
            {
                if (relogState.ShouldHalt || !needsReturn) return true;
                try { return !plugin.IpcClient.LifestreamIsBusy(); }
                catch { return true; }
            },
            TimeoutSec = 120f,
        });

        // Confirm not busy 3 consecutive times
        {
            int confirmCount = 0;
            DateTime lastCheck = DateTime.MinValue;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S9878D002D1F16790", charName),
                OnEnter = () => { confirmCount = 0; lastCheck = DateTime.UtcNow; },
                IsComplete = () =>
                {
                    if (relogState.ShouldHalt || !needsReturn) return true;
                    try
                    {
                        if ((DateTime.UtcNow - lastCheck).TotalSeconds < 1.0) return false;
                        lastCheck = DateTime.UtcNow;
                        if (!plugin.IpcClient.LifestreamIsBusy())
                        {
                            confirmCount++;
                            if (confirmCount >= 3) return true;
                        }
                        else confirmCount = 0;
                        return false;
                    }
                    catch { return true; }
                },
                TimeoutSec = 15f,
            });
        }

        AddLoggedCharacterSafeWait3Pass(steps, global::XASlave.Localization.UiMessage.Format("SB47137A92473035E", charName), 30f, runner, () => relogState.ShouldHalt || !needsReturn);

        // Verify we're on homeworld now
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S5E0ED19746F32830", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt || !needsReturn) return;
                try
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return;
                    var currentWorldId = local.CurrentWorld.RowId;
                    var homeWorldId = local.HomeWorld.RowId;
                    if (currentWorldId == homeWorldId)
                        runner.AddLog($"Returned to homeworld successfully.");
                    else
                        runner.AddLog($"WARNING: Still not on homeworld after return attempt.");
                }
                catch { }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Per-Character Duty Guard (step-based)
    //  Replaces the old blocking .Wait() approach that
    //  caused framework thread deadlocks.
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Builds step-based duty guard steps for a specific character.
    /// All steps gated by relogState - skipped if relog failed.
    /// Uses the same leave sequence as pre-flight but gated by per-char state.
    /// </summary>
    private static List<TaskStep> BuildDutyGuardSteps(string charName, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();
        var dutyDetected = false;
        var leaveClickDispatched = false;

        AddLoggedCharacterSafeWait3Pass(steps, global::XASlave.Localization.UiMessage.Format("SAD41C255BDB39486", charName), 30f, runner, () => relogState.ShouldHalt);

        // Check if in duty
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S0BE84DF1AC4F6624", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt) return;
                dutyDetected = Plugin.Condition[ConditionFlag.BoundByDuty];
                if (dutyDetected)
                    runner.AddLog($"WARNING: {charName} is in a duty - attempting to leave...");
                else
                    runner.AddLog($"Duty Guard: {charName} is not in a duty.");
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        AddLoggedCharacterSafeWait3Pass(steps, global::XASlave.Localization.UiMessage.Format("S6038E676B92574BE", charName), 30f, runner, () => relogState.ShouldHalt || !dutyDetected);

        // Wait for combat to end
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S99BB759E656AA9D7", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt || !dutyDetected) return;
                if (Plugin.Condition[ConditionFlag.InCombat])
                    runner.AddLog($"Duty Guard: {charName} is in combat, waiting to leave duty...");
                else
                    runner.AddLog($"Duty Guard: {charName} is not in combat.");
            },
            IsComplete = () => relogState.ShouldHalt || !dutyDetected || !Plugin.Condition[ConditionFlag.InCombat],
            TimeoutSec = 35f,
        });

        // Press U to open duty finder menu
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SAD6115DDDFB33D02", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt || !dutyDetected) return;
                runner.AddLog($"Duty Guard: pressing U to open duty menu...");
                KeyInputHelper.PressKey(0x55);
            },
            IsComplete = () => relogState.ShouldHalt || !dutyDetected || AddonHelper.IsAddonVisible("ContentsFinderMenu"),
            TimeoutSec = 10f,
            MaxRetries = 5,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("SF97DDAE926F5A775", charName), 1.0f));

        // Click Leave
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S6CD08D13D601F9A2", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt || !dutyDetected) return;
                runner.AddLog($"Duty Guard: clicking Leave button...");
                leaveClickDispatched = AddonHelper.ClickAddonButtonResolved(
                    "ContentsFinderMenu",
                    "Leave",
                    expectedNodeId: null,
                    fallbackNodeListIndex: AddonNodes.ContentsFinderMenuLeave).Succeeded;
                if (!leaveClickDispatched)
                    runner.AddLog("Duty Guard: Leave button click was not dispatched; the addon node may have changed.");
            },
            IsComplete = () => relogState.ShouldHalt
                               || !dutyDetected
                               || (leaveClickDispatched && AddonHelper.IsAddonVisible("SelectYesno")),
            TimeoutSec = 3f,
            OnTimeout = () => RecordRelogIncomplete(
                runner,
                relogState,
                charName,
                $"INCOMPLETE: {charName} - the Leave duty action could not be dispatched."),
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("SE4944A845C53D464", charName), 0.5f));

        // Click Yes
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SC422480545A52666", charName),
            OnEnter = () =>
            {
                if (relogState.ShouldHalt || !dutyDetected) return;
                runner.AddLog($"Duty Guard: confirming Yes to leave...");
                AddonHelper.ClickYesNo(true);
            },
            IsComplete = () => relogState.ShouldHalt || !dutyDetected || !Plugin.Condition[ConditionFlag.BoundByDuty],
            TimeoutSec = 45f,
            OnTimeout = () =>
            {
                if (!relogState.ShouldHalt && dutyDetected && Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    // Logged in OK but cannot leave the duty - mark PURPLE (incomplete) and halt
                    // the rest of this character's steps so the run moves on to the next character.
                    RecordRelogIncomplete(runner, relogState, charName, $"INCOMPLETE: {charName} - logged in but unable to leave duty, skipping remaining steps.");
                }
            },
        });

        AddLoggedCharacterSafeWait3Pass(steps, global::XASlave.Localization.UiMessage.Format("SD380BDE945E2BC4D", charName), 30f, runner, () => relogState.ShouldHalt || !dutyDetected);

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Helper step builders
    // ═══════════════════════════════════════════════════════

    private static void AddLoggedCharacterSafeWait3Pass(List<TaskStep> steps, global::XASlave.Localization.UiMessage labelMessage, float timeoutSec, TaskRunner runner, Func<bool>? skip = null)
    {
        var label = labelMessage.Original;
        skip ??= () => false;
        var verboseLogging = IsVerboseTaskLoggingEnabledStatic();

        for (var pass = 1; pass <= 3; pass++)
        {
            var passLabelMessage = global::XASlave.Localization.UiMessage.Format("S875056E7E8F940D0", labelMessage, pass);
            var passLabel = passLabelMessage.Original;
            string? lastMessage = null;

            steps.Add(new TaskStep
            {
                NameMessage = passLabelMessage,
                ShouldSkip = skip,
                OnEnter = () =>
                {
                    lastMessage = null;
                    if (!skip() && verboseLogging)
                        runner.AddLog($"{passLabel}: waiting for CharacterSafeWait readiness...");
                },
                IsComplete = () =>
                {
                    if (skip())
                        return true;

                    var ready = CharacterSafetyHelper.IsCharacterSafeWaitReady();
                    var message = ready
                        ? $"{passLabel}: ready ({GetCharacterSafeWaitStatusText()})"
                        : $"{passLabel}: waiting ({GetCharacterSafeWaitStatusText()})";

                    if (verboseLogging && !string.Equals(lastMessage, message, StringComparison.Ordinal))
                    {
                        runner.AddLog(message);
                        lastMessage = message;
                    }

                    return ready;
                },
                TimeoutSec = timeoutSec,
            });

            if (pass < 3)
                steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Format("S7C3F1B2AFBC8134E", labelMessage), 1.0f, skip));
        }
    }

    private static string GetCharacterSafeWaitStatusText()
    {
        try
        {
            var playerLoaded = Plugin.PlayerState.IsLoaded;
            var localAvailable = Plugin.ObjectTable.LocalPlayer != null;
            var namePlateReady = CharacterSafetyHelper.IsNamePlateReady();
            var normalCondition = CharacterSafetyHelper.IsNormalCondition(Plugin.Condition);
            var blockers = GetActiveConditionBlockers();
            var blockerText = blockers.Count > 0 ? string.Join(", ", blockers) : "none";
            return $"loaded={playerLoaded}, localPlayer={localAvailable}, namePlate={namePlateReady}, normal={normalCondition}, blockers={blockerText}";
        }
        catch (Exception ex)
        {
            return $"state unavailable ({ex.Message})";
        }
    }

    private static List<string> GetActiveConditionBlockers()
    {
        var blockers = new List<string>();

        try
        {
            var condition = Plugin.Condition;
            if (condition[ConditionFlag.InCombat]) blockers.Add(nameof(ConditionFlag.InCombat));
            if (condition[ConditionFlag.Casting]) blockers.Add(nameof(ConditionFlag.Casting));
            if (condition[ConditionFlag.BoundByDuty]) blockers.Add(nameof(ConditionFlag.BoundByDuty));
            if (condition[ConditionFlag.WatchingCutscene]) blockers.Add(nameof(ConditionFlag.WatchingCutscene));
            if (condition[ConditionFlag.OccupiedInCutSceneEvent]) blockers.Add(nameof(ConditionFlag.OccupiedInCutSceneEvent));
            if (condition[ConditionFlag.Occupied]) blockers.Add(nameof(ConditionFlag.Occupied));
            if (condition[ConditionFlag.Occupied30]) blockers.Add(nameof(ConditionFlag.Occupied30));
            if (condition[ConditionFlag.Occupied33]) blockers.Add(nameof(ConditionFlag.Occupied33));
            if (condition[ConditionFlag.Occupied38]) blockers.Add(nameof(ConditionFlag.Occupied38));
            if (condition[ConditionFlag.Occupied39]) blockers.Add(nameof(ConditionFlag.Occupied39));
            if (condition[ConditionFlag.OccupiedInEvent]) blockers.Add(nameof(ConditionFlag.OccupiedInEvent));
            if (condition[ConditionFlag.OccupiedInQuestEvent]) blockers.Add(nameof(ConditionFlag.OccupiedInQuestEvent));
            if (condition[ConditionFlag.OccupiedSummoningBell]) blockers.Add(nameof(ConditionFlag.OccupiedSummoningBell));
            if (condition[ConditionFlag.BetweenAreas]) blockers.Add(nameof(ConditionFlag.BetweenAreas));
            if (condition[ConditionFlag.BetweenAreas51]) blockers.Add(nameof(ConditionFlag.BetweenAreas51));
        }
        catch
        {
        }

        return blockers;
    }

    /// <summary>Creates a simple delay step.</summary>
    public static TaskStep MakeDelay(string name, float seconds, Func<bool>? shouldSkip = null)
        => MakeDelay(global::XASlave.Localization.UiMessage.Raw(name), seconds, shouldSkip);

    internal static TaskStep MakeDelay(global::XASlave.Localization.UiMessage name, float seconds, Func<bool>? shouldSkip = null)
    {
        DateTime? start = null;
        return new TaskStep
        {
            NameMessage = name,
            ShouldSkip = shouldSkip,
            OnEnter = () => start = DateTime.UtcNow,
            IsComplete = () => start.HasValue && (DateTime.UtcNow - start.Value).TotalSeconds >= seconds,
            TimeoutSec = seconds + 2f,
        };
    }

    /// <summary>
    /// CharacterSafeWait equivalent - waits for NamePlate addon + player available + not zoning or casting.
    /// </summary>
    public static TaskStep BuildCharacterSafeWait(string name, float timeoutSec)
        => BuildCharacterSafeWait(global::XASlave.Localization.UiMessage.Raw(name), timeoutSec);

    internal static TaskStep BuildCharacterSafeWait(global::XASlave.Localization.UiMessage name, float timeoutSec)
    {
        return new TaskStep
        {
            NameMessage = name,
            IsComplete = () => CharacterSafetyHelper.IsCharacterSafeWaitReady(),
            TimeoutSec = timeoutSec,
        };
    }

    /// <summary>
    /// CharacterSafeWait 3-Pass - confirms the player is truly safe by requiring
    /// 3 consecutive successful checks with 1-second intervals between each.
    /// This prevents false positives during zone transitions or animation states.
    ///
    /// Hard rule: Any time the plugin needs to confirm the player is safe before
    /// performing actions, use this 3-pass variant instead of the single-check version.
    ///
    ///   "Confirm not-busy 3 consecutive times with 1s intervals"
    /// </summary>
    public static List<TaskStep> BuildCharacterSafeWait3Pass(string label, float perPassTimeoutSec = 15f)
        => BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Raw(label), perPassTimeoutSec);

    internal static List<TaskStep> BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage label, float perPassTimeoutSec = 15f)
    {
        return new List<TaskStep>
        {
            BuildCharacterSafeWait(global::XASlave.Localization.UiMessage.Format("SB3F40EA2A257FFEC", label), perPassTimeoutSec),
            MakeDelay(global::XASlave.Localization.UiMessage.Format("S7C3F1B2AFBC8134E", label), 1.0f),
            BuildCharacterSafeWait(global::XASlave.Localization.UiMessage.Format("S66C5C9BBEF71F6AD", label), perPassTimeoutSec),
            MakeDelay(global::XASlave.Localization.UiMessage.Format("S7C3F1B2AFBC8134E", label), 1.0f),
            BuildCharacterSafeWait(global::XASlave.Localization.UiMessage.Format("SC62613467DDC5727", label), perPassTimeoutSec),
        };
    }

    public List<TaskStep> BuildPreFlightOnlySteps(List<string> characters, TaskRunner runner,
        Func<bool>? disableArMultiAndVerify = null)
    {
        var steps = new List<TaskStep>();
        if (disableArMultiAndVerify != null)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SBE2A61D79FD0A373"),
                OnEnter = () =>
                {
                    runner.AddLog("Pre-flight: disabling and verifying AutoRetainer Multi before game-state detection...");
                    if (!disableArMultiAndVerify())
                        runner.Cancel();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
        }
        steps.AddRange(BuildPreFlightSteps(characters, runner));
        return steps;
    }

    public static void AddLogoutOnCompleteSteps(List<TaskStep> steps, TaskRunner runner)
    {
        var logoutConfirmStart = DateTime.MinValue;
        var logoutConfirmed = false;

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SD0527E4B3D658351"),
            OnEnter = () =>
            {
                runner.AddLog("Sending /logout...");
                ChatHelper.SendMessage("/logout");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("SD17EDD86126E945D"), 1.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S48B20A0A432071AD"),
            OnEnter = () =>
            {
                logoutConfirmStart = DateTime.UtcNow;
                logoutConfirmed = false;
                runner.AddLog("Waiting for logout confirmation dialog...");
            },
            IsComplete = () =>
            {
                if (logoutConfirmed)
                    return true;

                if (AddonHelper.IsAddonReady("SelectYesno"))
                {
                    runner.AddLog("Confirming logout via SelectYesno...");
                    AddonHelper.ClickYesNo(true);
                    logoutConfirmed = true;
                    return true;
                }

                return (DateTime.UtcNow - logoutConfirmStart).TotalSeconds >= 5;
            },
            TimeoutSec = 6f,
        });
    }

    public static void AddKillGameOnCompleteSteps(List<TaskStep> steps, TaskRunner runner)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S5D2B1F638AB308C4"),
            OnEnter = () =>
            {
                runner.AddLog("Triggering XA kill-game flow...");
                if (Plugin.Instance!.InstantLogout.RequestKillGame(force: true))
                    runner.AddLog("XA kill-game flow requested.");
                else
                    runner.AddLog("XA kill-game flow could not start.");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S69FC887A64107DA0"), 1.0f));
    }

    public static void AddSharedCompletionSteps(List<TaskStep> steps, TaskRunner runner, bool logoutOnComplete, bool killGameOnComplete, bool enableArMultiOnComplete)
    {
        if (logoutOnComplete)
            AddLogoutOnCompleteSteps(steps, runner);
        else if (killGameOnComplete)
            AddKillGameOnCompleteSteps(steps, runner);

        if (!enableArMultiOnComplete)
            return;

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SC1B0164CD7BEF29C"),
            OnEnter = () =>
            {
                runner.AddLog("Re-enabling AutoRetainer Multi Mode...");
                Plugin.Instance!.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(MakeDelay(global::XASlave.Localization.UiMessage.Key("S064DC935888F00D5"), 1.0f));
    }

    public static bool ShouldKeepLogoutCancelSuppressed(bool logoutOnComplete, bool killGameOnComplete)
    {
        return logoutOnComplete || killGameOnComplete;
    }

    /// <summary>
    /// Mount up step - equivalent to MountUpXA() → /gaction "Mount Roulette".
    /// Usable as a template for future tasks that involve movement.
    /// </summary>
    public static TaskStep BuildMountUp(string name = "Mount Up")
        => BuildMountUp(global::XASlave.Localization.UiMessage.Raw(name));

    internal static TaskStep BuildMountUp(global::XASlave.Localization.UiMessage name)
    {
        return new TaskStep
        {
            NameMessage = name,
            OnEnter = () => ChatHelper.SendMessage("/gaction \"Mount Roulette\""),
            IsComplete = () => true,
            TimeoutSec = 3f,
        };
    }

    // ═══════════════════════════════════════════════════════
    //  Game state checks - reusable across tasks
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Gets current character name in "FirstName LastName@World" format.
    /// Equivalent to GetCharacterName(true) from dfunc/xafunc.
    /// </summary>
    public static string GetCurrentCharacterNameWorld()
    {
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null) return string.Empty;
            var name = local.Name.ToString();
            var worldId = local.HomeWorld.RowId;
            var worldInfo = WorldData.GetById(worldId);
            var worldName = worldInfo?.Name ?? "Unknown";
            return $"{name}@{worldName}";
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Returns a copy of <paramref name="step"/> whose ShouldSkip also returns true when
    /// <paramref name="extraSkip"/> does. Lets a caller gate a run of already-built steps (for
    /// example a per-character downstream sequence) behind a runtime condition such as "relog
    /// failed" without threading the condition into every individual step builder.
    /// </summary>
    public static TaskStep WithSkip(TaskStep step, Func<bool> extraSkip)
    {
        var inner = step.ShouldSkip;
        return new TaskStep
        {
            NameMessage = step.NameMessage,
            OnEnter = step.OnEnter,
            IsComplete = step.IsComplete,
            ShouldSkip = inner == null ? extraSkip : () => extraSkip() || inner(),
            TimeoutSec = step.TimeoutSec,
            MaxRetries = step.MaxRetries,
            OnTimeout = step.OnTimeout,
        };
    }

    /// <summary>
    /// Checks if NamePlate addon is ready and visible.
    /// Part of CharacterSafeWaitXA() checks.
    /// </summary>
    public static bool IsNamePlateReady()
    {
        return CharacterSafetyHelper.IsNamePlateReady();
    }

    /// <summary>
    /// Checks if the player is available (loaded and not in a blocking state).
    /// Part of CharacterSafeWaitXA() checks.
    /// </summary>
    public static bool IsPlayerAvailable()
    {
        return CharacterSafetyHelper.IsPlayerAvailable();
    }
}
