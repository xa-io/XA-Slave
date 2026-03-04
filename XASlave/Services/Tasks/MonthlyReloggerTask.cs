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
/// Monthly Relogger — rotates through a list of characters, logging into each
/// and executing a configurable sequence of actions per character.
///
/// Converted from: 7.35 XA Monthly Relogger.lua
/// Uses IPC: AutoRetainer (relog, multi-mode), Lifestream (home/fc), TextAdvance, XA Database
/// Uses commands: /ays relog, /at y, /nastatus off, /inventory, /armourychest, /saddlebag, /freecompanycmd
///
/// Lifestream wait pattern (mirrors xafunc.lua):
///   1. Issue command → wait 1s
///   2. Poll Lifestream.IsBusy every ~1s until it becomes busy (short timeout for missing house)
///   3. Poll until Lifestream.IsBusy becomes false
///   4. Confirm not-busy 3 consecutive times with 1s intervals
///   5. CharacterSafeWait
/// </summary>
public sealed class MonthlyReloggerTask
{
    private readonly Plugin plugin;

    // ── Configurable actions per character ──
    public bool DoEnableTextAdvance { get; set; } = true;
    public bool DoRemoveSprout { get; set; } = true;
    public bool DoOpenInventory { get; set; } = true;
    public bool DoOpenArmouryChest { get; set; } = true;
    public bool DoOpenSaddlebags { get; set; } = true;
    public bool DoReturnToHome { get; set; } = true;
    public bool DoReturnToFc { get; set; } = true;
    public bool DoParseForXaDatabase { get; set; } = true;
    public bool DoEnableArMultiOnComplete { get; set; } = false;

    public MonthlyReloggerTask(Plugin plugin)
    {
        this.plugin = plugin;
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

        runner.TotalItems = characters.Count;
        runner.CompletedItems = 0;
        runner.SuppressLogoutCancel = true; // Relogger expects logouts during /ays relog

        // ── PRIORITY #1: Disable AR Multi Mode FIRST ──
        // Must happen before pre-flight to prevent AR from relogging during checks
        steps.Add(new TaskStep
        {
            Name = "Disable AR Multi Mode",
            OnEnter = () =>
            {
                runner.AddLog("Disabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay("AR Disable Cooldown", 1.0f));

        // ═══════════════════════════════════════════════════════
        //  PRE-FLIGHT SEQUENCE
        //  Detects current game state and navigates to a safe
        //  starting point before processing any characters.
        // ═══════════════════════════════════════════════════════
        steps.AddRange(BuildPreFlightSteps(characters, runner));

        // ── Steps for each character ──
        for (int i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;

            // Label update
            steps.Add(new TaskStep
            {
                Name = $"[{charIndex}/{charTotal}] {charName}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"── Processing {charName} ({charIndex}/{charTotal}) ──");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog if needed (with retry logic)
            var relogState = new RelogState();
            steps.AddRange(BuildRelogSteps(charName, runner, relogState));

            // CharacterSafeWait 3-Pass — gated by relog success
            // Each pass checks if relog failed and skips immediately if so
            foreach (var sw in BuildCharacterSafeWait3Pass($"Post-Relog SafeWait ({charName})", 30f))
            {
                var originalComplete = sw.IsComplete;
                var gatedStep = new TaskStep
                {
                    Name = sw.Name,
                    OnEnter = sw.OnEnter,
                    IsComplete = () => relogState.Failed || originalComplete(),
                    TimeoutSec = sw.TimeoutSec,
                };
                steps.Add(gatedStep);
            }

            // Duty Guard — step-based (no blocking .Wait())
            // Checks if character is in a duty after login.
            // If in duty, attempts to leave via step-based sequence.
            foreach (var dutyStep in BuildDutyGuardSteps(charName, runner, relogState))
            {
                steps.Add(dutyStep);
            }

            // Homeworld check — always-on, cannot be disabled
            // If character is not on their homeworld, use Lifestream to return.
            // This ensures housing/FC data collection is accurate (only available on homeworld).
            foreach (var hwStep in BuildHomeworldCheckSteps(charName, runner, relogState))
            {
                steps.Add(hwStep);
            }

            // Per-character actions — built inline so ordering is correct
            // Each action step is gated by relog success
            var perCharActions = BuildPerCharacterActions(charName, charIndex, charTotal, runner);
            foreach (var action in perCharActions)
            {
                var origEnter = action.OnEnter;
                var origComplete = action.IsComplete;
                steps.Add(new TaskStep
                {
                    Name = action.Name,
                    OnEnter = () =>
                    {
                        if (relogState.Failed) return;
                        origEnter?.Invoke();
                    },
                    IsComplete = () => relogState.Failed || origComplete(),
                    TimeoutSec = action.TimeoutSec,
                });
            }

            // Mark character complete + record last-logged-in
            var capturedCharName = charName;
            var capturedCharIndex = charIndex;
            steps.Add(new TaskStep
            {
                Name = $"Complete: {capturedCharName}",
                OnEnter = () =>
                {
                    runner.CompletedItems = capturedCharIndex;
                    if (relogState.Failed)
                    {
                        runner.AddLog($"Skipped {capturedCharName} ({capturedCharIndex}/{charTotal}) — relog failed");
                        return;
                    }

                    runner.AddLog($"Finished {capturedCharName} ({capturedCharIndex}/{charTotal})");

                    // Record last-logged-in timestamp in persistent ReloggerCharacterInfo
                    try
                    {
                        var cfg = plugin.Configuration;
                        if (!cfg.ReloggerCharacterInfo.TryGetValue(capturedCharName, out var data))
                            data = new ReloggerCharacterData();
                        data.LastLoggedIn = DateTime.UtcNow;
                        cfg.ReloggerCharacterInfo[capturedCharName] = data;
                        cfg.Save();
                    }
                    catch { /* non-critical */ }

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

        // ── Summary: Report any failed characters ──
        steps.Add(new TaskStep
        {
            Name = "Relogger Summary",
            OnEnter = () =>
            {
                runner.SuppressLogoutCancel = false; // Re-enable logout cancellation now that relogger is done

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

        // ── Optional: Re-enable AR Multi Mode after all characters processed ──
        if (DoEnableArMultiOnComplete)
        {
            steps.Add(new TaskStep
            {
                Name = "Enable AR Multi Mode",
                OnEnter = () =>
                {
                    runner.AddLog("Re-enabling AutoRetainer Multi Mode...");
                    plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
        }

        steps.Add(MakeDelay("Final Cooldown", 1.0f));

        return steps;
    }

    /// <summary>
    /// Builds relog steps for a character with retry logic.
    /// Equivalent to ARRelogXA(name) in xafunc.lua.
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

        // Step 1: Check if already logged in, or send relog command
        steps.Add(new TaskStep
        {
            Name = $"Relog: {charName} [attempt]",
            OnEnter = () =>
            {
                var current = GetCurrentCharacterNameWorld();
                Plugin.Log.Information($"[XASlave] Relog check: current='{current}' target='{charName}' match={current == charName}");
                if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                {
                    runner.AddLog($"Already logged in as {charName}");
                    relogState.Confirmed = true;
                    return;
                }

                relogState.Attempt++;
                if (relogState.Attempt > MaxRelogAttempts)
                {
                    runner.AddLog($"FAILED: {charName} — exhausted {MaxRelogAttempts} relog attempts");
                    runner.FailedCharacters.Add(charName);
                    relogState.Failed = true;
                    return;
                }

                runner.AddLog($"Relogging to {charName} (attempt {relogState.Attempt}/{MaxRelogAttempts})...");
                ChatHelper.SendMessage($"/ays relog {charName}");
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        // Step 2: Delay for AR to start the relog process (plain delay, no condition check)
        steps.Add(MakeDelay($"Relog Init: {charName}", 2.0f));

        // Step 3: Wait for character to be fully logged in (SafeWait)
        // Relog can take 1-2 minutes — use 120s timeout
        steps.Add(new TaskStep
        {
            Name = $"Wait Relog: {charName}",
            IsComplete = () =>
            {
                if (relogState.Confirmed || relogState.Failed) return true;
                try
                {
                    if (!Plugin.PlayerState.IsLoaded) return false;
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return false;
                    return IsNamePlateReady() && IsPlayerAvailable();
                }
                catch { return false; }
            },
            TimeoutSec = 120f,
        });

        // Step 4: Verify correct character logged in
        steps.Add(new TaskStep
        {
            Name = $"Verify Relog: {charName}",
            OnEnter = () =>
            {
                if (relogState.Confirmed || relogState.Failed) return;

                var current = GetCurrentCharacterNameWorld();
                if (current == charName)
                {
                    runner.AddLog($"Relog confirmed: {charName} (attempt {relogState.Attempt}/{MaxRelogAttempts})");
                    relogState.Confirmed = true;
                }
                else
                {
                    runner.AddLog($"Relog verification failed: expected '{charName}', got '{current}' (attempt {relogState.Attempt}/{MaxRelogAttempts})");

                    if (relogState.Attempt >= MaxRelogAttempts)
                    {
                        runner.AddLog($"FAILED: {charName} — exhausted {MaxRelogAttempts} relog attempts");
                        runner.FailedCharacters.Add(charName);
                        relogState.Failed = true;
                    }
                    else
                    {
                        // Retry: re-send relog command
                        relogState.Attempt++;
                        runner.AddLog($"Retrying relog to {charName} (attempt {relogState.Attempt}/{MaxRelogAttempts})...");
                        ChatHelper.SendMessage($"/ays relog {charName}");
                    }
                }
            },
            IsComplete = () => relogState.Confirmed || relogState.Failed,
            TimeoutSec = 3f,
        });

        // Step 5: If retry was triggered, wait again for relog (2nd attempt)
        steps.Add(new TaskStep
        {
            Name = $"Wait Retry 2: {charName}",
            IsComplete = () =>
            {
                if (relogState.Confirmed || relogState.Failed) return true;
                try
                {
                    if (!Plugin.PlayerState.IsLoaded) return false;
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return false;
                    return IsNamePlateReady() && IsPlayerAvailable();
                }
                catch { return false; }
            },
            TimeoutSec = 120f,
        });

        // Step 6: Verify again after 2nd attempt
        steps.Add(new TaskStep
        {
            Name = $"Verify Retry 2: {charName}",
            OnEnter = () =>
            {
                if (relogState.Confirmed || relogState.Failed) return;

                var current = GetCurrentCharacterNameWorld();
                if (current == charName)
                {
                    runner.AddLog($"Relog confirmed: {charName} (attempt {relogState.Attempt}/{MaxRelogAttempts})");
                    relogState.Confirmed = true;
                }
                else
                {
                    if (relogState.Attempt >= MaxRelogAttempts)
                    {
                        runner.AddLog($"FAILED: {charName} — exhausted {MaxRelogAttempts} relog attempts");
                        runner.FailedCharacters.Add(charName);
                        relogState.Failed = true;
                    }
                    else
                    {
                        relogState.Attempt++;
                        runner.AddLog($"Retrying relog to {charName} (attempt {relogState.Attempt}/{MaxRelogAttempts})...");
                        ChatHelper.SendMessage($"/ays relog {charName}");
                    }
                }
            },
            IsComplete = () => relogState.Confirmed || relogState.Failed,
            TimeoutSec = 3f,
        });

        // Step 7: If retry was triggered, wait again for relog (3rd attempt)
        steps.Add(new TaskStep
        {
            Name = $"Wait Retry 3: {charName}",
            IsComplete = () =>
            {
                if (relogState.Confirmed || relogState.Failed) return true;
                try
                {
                    if (!Plugin.PlayerState.IsLoaded) return false;
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return false;
                    return IsNamePlateReady() && IsPlayerAvailable();
                }
                catch { return false; }
            },
            TimeoutSec = 120f,
        });

        // Step 8: Final verification after 3rd attempt
        steps.Add(new TaskStep
        {
            Name = $"Verify Final: {charName}",
            OnEnter = () =>
            {
                if (relogState.Confirmed || relogState.Failed) return;

                var current = GetCurrentCharacterNameWorld();
                if (current == charName)
                {
                    runner.AddLog($"Relog confirmed: {charName} (attempt {relogState.Attempt}/{MaxRelogAttempts})");
                    relogState.Confirmed = true;
                }
                else
                {
                    runner.AddLog($"FAILED: {charName} — could not relog after {MaxRelogAttempts} attempts (current: '{current}')");
                    runner.FailedCharacters.Add(charName);
                    relogState.Failed = true;
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        return steps;
    }

    private const int MaxRelogAttempts = 3;

    /// <summary>Tracks state across relog retry steps via closure.</summary>
    private class RelogState
    {
        public int Attempt;
        public bool Confirmed;
        public bool Failed;
    }

    /// <summary>
    /// Builds the per-character action steps.
    /// Equivalent to the sequence in ProcessToonXA() from the Lua script.
    /// </summary>
    private List<TaskStep> BuildPerCharacterActions(string charName, int idx, int total, TaskRunner runner)
    {
        var steps = new List<TaskStep>();

        // EnableTextAdvanceXA() → /at y
        if (DoEnableTextAdvance)
        {
            steps.Add(new TaskStep
            {
                Name = "Enable TextAdvance",
                OnEnter = () =>
                {
                    runner.AddLog("Enabling TextAdvance...");
                    ChatHelper.SendMessage("/at y");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("TextAdvance Delay", 0.5f));
        }

        // RemoveSproutXA() → /nastatus off
        if (DoRemoveSprout)
        {
            steps.Add(new TaskStep
            {
                Name = "Remove Sprout",
                OnEnter = () =>
                {
                    runner.AddLog("Removing New Adventurer Status...");
                    ChatHelper.SendMessage("/nastatus off");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Sprout Delay", 0.5f));
        }

        // OpenInventoryXA() → /inventory
        if (DoOpenInventory)
        {
            steps.Add(new TaskStep
            {
                Name = "Open Inventory",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Inventory...");
                    ChatHelper.SendMessage("/inventory");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Inventory Delay", 0.5f));
        }

        // OpenArmouryChestXA() → /armourychest
        if (DoOpenArmouryChest)
        {
            steps.Add(new TaskStep
            {
                Name = "Open Armoury Chest",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Armoury Delay", 0.5f));
        }

        // OpenSaddlebagsXA() → /saddlebag
        // Not every character has saddlebags unlocked — send command and use short timeout
        if (DoOpenSaddlebags)
        {
            steps.Add(new TaskStep
            {
                Name = "Open Saddlebags",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("InventoryBuddy"),
                TimeoutSec = 3f, // Short timeout — if saddlebag not unlocked, skip gracefully
            });
            steps.Add(MakeDelay("Saddlebag Delay", 0.5f));
        }

        // return_to_homeXA() → Lifestream: /li home
        // Short timeout (5s) for "wait busy" — if char has no house, Lifestream never becomes busy
        if (DoReturnToHome)
        {
            steps.AddRange(BuildLifestreamTeleportSteps(
                "Home", "home", runner, waitBusyTimeoutSec: 5f));
        }

        // return_to_fcXA() → Lifestream: /li fc
        if (DoReturnToFc)
        {
            steps.AddRange(BuildLifestreamTeleportSteps(
                "FC", "fc", runner, waitBusyTimeoutSec: 8f));
        }

        // Parse for XA Database — full FC collection mirroring AutoCollectionService
        // Opens FC window → Members tab → Info tab → Housing search → close all → save
        if (DoParseForXaDatabase)
        {
            // Track whether to skip FC steps (not on homeworld or not in FC)
            var skipFc = false;

            steps.Add(new TaskStep
            {
                Name = "Parse XA: FC Check",
                OnEnter = () =>
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local != null && local.CurrentWorld.RowId != local.HomeWorld.RowId)
                    {
                        runner.AddLog("Not on home world — skipping FC collection (FC data unavailable when visiting)");
                        skipFc = true;
                        return;
                    }
                    var fcTag = local?.CompanyTag.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(fcTag))
                    {
                        runner.AddLog("Not in a Free Company — skipping FC collection");
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
            steps.Add(MakeDelay("Parse XA: FC Load", 1.0f));

            // Click Members tab (callback 1, node 8)
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Members Tab",
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
            steps.Add(MakeDelay("Parse XA: Members Load", 1.5f));

            // Save after members (XA Database addon watcher also triggers on close)
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Save Members",
                OnEnter = () =>
                {
                    if (skipFc) return;
                    runner.AddLog("Saving FC member data...");
                    plugin.IpcClient.Save();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MakeDelay("Parse XA: Members Save Delay", 0.5f));

            // Click Info tab (callback 3, node 4) → FreeCompanyStatus
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Info Tab",
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
            steps.Add(MakeDelay("Parse XA: Status Load", 1.0f));

            // Click Housing search button (FreeCompanyStatus node 12) → HousingSignBoard
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Housing Search",
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
            steps.Add(MakeDelay("Parse XA: Housing Load", 1.5f));

            // Close sub-addons
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Close FC Sub-Addons",
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
            steps.Add(MakeDelay("Parse XA: Close Sub Delay", 0.5f));

            // Close FC window
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Close FC Window",
                OnEnter = () =>
                {
                    if (skipFc) return;
                    AddonHelper.CloseAddon("FreeCompany");
                },
                IsComplete = () => skipFc || !AddonHelper.IsAddonVisible("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(MakeDelay("Parse XA: FC Close Delay", 0.5f));

            // Final save to XA Database — all collected data (inventory, armoury, saddlebag, FC, housing)
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Save to Database",
                OnEnter = () =>
                {
                    runner.AddLog("Saving to XA Database (all collected data)...");
                    plugin.IpcClient.Save();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MakeDelay("Parse XA: Save Delay", 0.5f));
        }

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Lifestream Teleport Steps
    //  Pattern: command → 1s → poll busy → poll not-busy → confirm 3x → safe wait
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Builds the full Lifestream teleport step sequence.
    /// Mirrors the xafunc.lua pattern: issue command, wait 1s, poll Lifestream busy,
    /// wait for completion, confirm 3 times not-busy, then CharacterSafeWait.
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
            Name = $"Teleport {label}: Command",
            OnEnter = () =>
            {
                runner.AddLog($"Teleporting to {label} (/li {command})...");
                plugin.IpcClient.LifestreamExecuteCommand(command);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        // 2. Wait 1 second for Lifestream to register the command
        steps.Add(MakeDelay($"Teleport {label}: Init Wait", 1.0f));

        // 3. Wait for Lifestream to become busy (start the teleport)
        //    Short timeout — if it never becomes busy, the teleport destination doesn't exist
        steps.Add(new TaskStep
        {
            Name = $"Teleport {label}: Wait Start",
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
                Name = $"Teleport {label}: Wait Complete",
                IsComplete = () =>
                {
                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 60f,
            });
        }

        // 5. Confirm Lifestream is not busy — 3 consecutive 1-second checks
        //    This prevents false positives from brief not-busy flickers
        {
            int confirmCount = 0;
            DateTime lastCheck = DateTime.MinValue;
            steps.Add(new TaskStep
            {
                Name = $"Teleport {label}: Confirm (3x)",
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
        steps.Add(BuildCharacterSafeWait($"Teleport {label}: SafeWait", 15f));

        // 7. Final settle
        steps.Add(MakeDelay($"Teleport {label}: Settle", 1.0f));

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
    ///   1. _TitleLogo / _TitleMenu (main menu) — already safe, proceed
    ///   2. CharaSelect — click Exit to return to main menu
    ///   3. MovieStaffList (main menu movie) — press ESC, wait for title screen
    ///   4. Logged in — check for duty, SafeWait 3-pass, reorder character list
    /// </summary>
    private List<TaskStep> BuildPreFlightSteps(List<string> characters, TaskRunner runner)
    {
        var steps = new List<TaskStep>();
        var preFlightState = new PreFlightState();

        // Step 1: Detect current game state
        steps.Add(new TaskStep
        {
            Name = "Pre-Flight: Detect State",
            OnEnter = () =>
            {
                runner.AddLog("Pre-flight: detecting game state...");

                // Check MovieStaffList FIRST — most specific, only visible during main menu movie
                // During movie: _ScreenText, MovieStaffList, CursorAddon (no _TitleLogo/_TitleMenu)
                if (AddonHelper.IsAddonVisible("MovieStaffList"))
                {
                    runner.AddLog("Pre-flight: main menu movie playing — will press ESC.");
                    preFlightState.OnMovie = true;
                    return;
                }

                // Character select screen
                if (AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu"))
                {
                    runner.AddLog("Pre-flight: on character select — will exit to main menu.");
                    preFlightState.OnCharaSelect = true;
                    return;
                }

                // Main menu (title screen)
                if (AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu"))
                {
                    runner.AddLog("Pre-flight: on main menu — ready to proceed.");
                    preFlightState.OnMainMenu = true;
                    return;
                }

                // Not on any title screen — check if logged in
                var current = GetCurrentCharacterNameWorld();
                if (!string.IsNullOrEmpty(current))
                {
                    runner.AddLog($"Pre-flight: logged in as {current}.");
                    preFlightState.LoggedInAs = current;
                    preFlightState.IsLoggedIn = true;
                }
                else
                {
                    runner.AddLog("Pre-flight: unknown state — will attempt to proceed.");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 5f,
        });

        // Step 2: Handle CharaSelect — exit to main menu
        steps.Add(new TaskStep
        {
            Name = "Pre-Flight: Exit CharaSelect",
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
                    runner.AddLog("Pre-flight: timeout waiting for main menu after CharaSelect exit — proceeding anyway.");
            },
        });

        // Step 3: Handle MovieStaffList — press ESC and wait for title screen
        steps.Add(new TaskStep
        {
            Name = "Pre-Flight: ESC Movie",
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
                    runner.AddLog("Pre-flight: timeout waiting for title screen after ESC — proceeding anyway.");
            },
        });

        steps.Add(MakeDelay("Pre-Flight: Settle", 1.0f));

        // Step 4: If logged in, check for duty
        steps.Add(new TaskStep
        {
            Name = "Pre-Flight: Duty Check",
            OnEnter = () =>
            {
                if (!preFlightState.IsLoggedIn) return;
                if (Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    runner.AddLog("Pre-flight: in a duty — will attempt to leave before proceeding.");
                    preFlightState.InDuty = true;
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        // Step 4a-4e: Leave duty if in one (step-based, same as per-char duty guard)
        var dummyRelogState = new RelogState(); // not used for gating here
        foreach (var dutyStep in BuildDutyLeaveSequence("Pre-Flight", runner, preFlightState))
        {
            steps.Add(dutyStep);
        }

        // Step 5: CharacterSafeWait 3-Pass (only if logged in)
        foreach (var sw in BuildCharacterSafeWait3Pass("Pre-Flight SafeWait", 30f))
        {
            var originalComplete = sw.IsComplete;
            steps.Add(new TaskStep
            {
                Name = sw.Name,
                OnEnter = sw.OnEnter,
                IsComplete = () => !preFlightState.IsLoggedIn || originalComplete(),
                TimeoutSec = sw.TimeoutSec,
            });
        }

        // Step 6: Reorder character list — move currently-logged-in character to position 0
        steps.Add(new TaskStep
        {
            Name = "Pre-Flight: Reorder Characters",
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
                    runner.AddLog($"Pre-flight: {currentChar} not in selected list — no reordering needed.");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay("Pre-Flight: Complete", 0.5f));

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
    }

    /// <summary>
    /// Builds a step-based duty leave sequence for pre-flight.
    /// Gated by preFlightState.InDuty — skips immediately if not in duty.
    /// </summary>
    private static List<TaskStep> BuildDutyLeaveSequence(string label, TaskRunner runner, PreFlightState state)
    {
        var steps = new List<TaskStep>();

        // Wait for combat to end (up to 30s)
        {
            DateTime? combatStart = null;
            steps.Add(new TaskStep
            {
                Name = $"{label}: Wait Combat End",
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
                        runner.AddLog($"{label}: still in combat after 30s — will attempt to leave anyway.");
                },
            });
        }

        // Press U to open duty finder menu
        steps.Add(new TaskStep
        {
            Name = $"{label}: Open Duty Menu",
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

        steps.Add(MakeDelay($"{label}: Duty Menu Wait", 0.5f));

        // Click Leave button
        steps.Add(new TaskStep
        {
            Name = $"{label}: Click Leave",
            OnEnter = () =>
            {
                if (!state.InDuty) return;
                runner.AddLog($"{label}: clicking Leave button...");
                AddonHelper.ClickAddonButton("ContentsFinderMenu", 43);
            },
            IsComplete = () => !state.InDuty || true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay($"{label}: Leave Confirm Wait", 0.5f));

        // Click Yes on confirmation
        steps.Add(new TaskStep
        {
            Name = $"{label}: Confirm Leave",
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
                    runner.AddLog($"{label}: still in duty after leave attempt — proceeding anyway.");
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

    /// <summary>
    /// Builds homeworld check steps. If the character is not on their homeworld,
    /// uses Lifestream to travel back. Always-on — cannot be disabled.
    /// Gated by relogState.Failed.
    /// </summary>
    private List<TaskStep> BuildHomeworldCheckSteps(string charName, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();
        var needsReturn = false;

        // 3x SafeWait before homeworld check — ensure character is fully loaded
        foreach (var sw in BuildCharacterSafeWait3Pass($"Homeworld Pre-SafeWait ({charName})", 30f))
        {
            var origComplete = sw.IsComplete;
            steps.Add(new TaskStep
            {
                Name = sw.Name,
                OnEnter = sw.OnEnter,
                IsComplete = () => relogState.Failed || origComplete(),
                TimeoutSec = sw.TimeoutSec,
            });
        }

        // Check if on homeworld
        steps.Add(new TaskStep
        {
            Name = $"Homeworld Check: {charName}",
            OnEnter = () =>
            {
                if (relogState.Failed) return;
                try
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return;
                    var currentWorldId = local.CurrentWorld.RowId;
                    var homeWorldId = local.HomeWorld.RowId;
                    if (currentWorldId != homeWorldId)
                    {
                        var currentName = local.CurrentWorld.Value.Name.ToString();
                        var homeName = local.HomeWorld.Value.Name.ToString();
                        runner.AddLog($"Not on homeworld — currently on {currentName}, returning to {homeName}...");
                        needsReturn = true;
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
            Name = $"Homeworld Return: {charName}",
            OnEnter = () =>
            {
                if (relogState.Failed || !needsReturn) return;
                try
                {
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return;
                    var homeName = local.HomeWorld.Value.Name.ToString();
                    runner.AddLog($"Returning to homeworld {homeName} via Lifestream...");
                    plugin.IpcClient.LifestreamChangeWorld(homeName);
                }
                catch (Exception ex)
                {
                    runner.AddLog($"Homeworld return error: {ex.Message}");
                }
            },
            IsComplete = () => relogState.Failed || !needsReturn || true,
            TimeoutSec = 3f,
        });

        // Wait for Lifestream to start (if returning) — 2s init delay then poll
        steps.Add(MakeDelay($"Homeworld Return: Init ({charName})", 2.0f));

        steps.Add(new TaskStep
        {
            Name = $"Homeworld Return: Wait Start ({charName})",
            IsComplete = () =>
            {
                if (relogState.Failed || !needsReturn) return true;
                try { return plugin.IpcClient.LifestreamIsBusy(); }
                catch { return true; }
            },
            TimeoutSec = 15f,
        });

        // Wait for Lifestream to finish
        steps.Add(new TaskStep
        {
            Name = $"Homeworld Return: Wait Complete ({charName})",
            IsComplete = () =>
            {
                if (relogState.Failed || !needsReturn) return true;
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
                Name = $"Homeworld Return: Confirm ({charName})",
                OnEnter = () => { confirmCount = 0; lastCheck = DateTime.UtcNow; },
                IsComplete = () =>
                {
                    if (relogState.Failed || !needsReturn) return true;
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

        // SafeWait after world travel
        foreach (var sw in BuildCharacterSafeWait3Pass($"Homeworld Return: SafeWait ({charName})", 30f))
        {
            var origComplete = sw.IsComplete;
            steps.Add(new TaskStep
            {
                Name = sw.Name,
                OnEnter = sw.OnEnter,
                IsComplete = () => relogState.Failed || !needsReturn || origComplete(),
                TimeoutSec = sw.TimeoutSec,
            });
        }

        // Verify we're on homeworld now
        steps.Add(new TaskStep
        {
            Name = $"Homeworld Verify: {charName}",
            OnEnter = () =>
            {
                if (relogState.Failed || !needsReturn) return;
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
    /// All steps gated by relogState — skipped if relog failed.
    /// Uses the same leave sequence as pre-flight but gated by per-char state.
    /// </summary>
    private static List<TaskStep> BuildDutyGuardSteps(string charName, TaskRunner runner, RelogState relogState)
    {
        var steps = new List<TaskStep>();
        var dutyDetected = false;

        // SafeWait 3-pass BEFORE duty check — ensure character is fully loaded
        // Without this, duty guard fires too soon after relog (UI not ready)
        foreach (var sw in BuildCharacterSafeWait3Pass($"Duty Guard: Pre-SafeWait ({charName})", 30f))
        {
            var origComplete = sw.IsComplete;
            steps.Add(new TaskStep
            {
                Name = sw.Name,
                OnEnter = sw.OnEnter,
                IsComplete = () => relogState.Failed || origComplete(),
                TimeoutSec = sw.TimeoutSec,
            });
        }

        // Check if in duty
        steps.Add(new TaskStep
        {
            Name = $"Duty Check: {charName}",
            OnEnter = () =>
            {
                if (relogState.Failed) return;
                dutyDetected = Plugin.Condition[ConditionFlag.BoundByDuty];
                if (dutyDetected)
                    runner.AddLog($"WARNING: {charName} is in a duty — attempting to leave...");
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        // SafeWait 3-pass again if in duty — confirm character fully loaded in the duty instance
        foreach (var sw in BuildCharacterSafeWait3Pass($"Duty Guard: Duty SafeWait ({charName})", 30f))
        {
            var origComplete = sw.IsComplete;
            steps.Add(new TaskStep
            {
                Name = sw.Name,
                OnEnter = sw.OnEnter,
                IsComplete = () => relogState.Failed || !dutyDetected || origComplete(),
                TimeoutSec = sw.TimeoutSec,
            });
        }

        // Wait for combat to end
        steps.Add(new TaskStep
        {
            Name = $"Duty Guard: Wait Combat ({charName})",
            IsComplete = () => relogState.Failed || !dutyDetected || !Plugin.Condition[ConditionFlag.InCombat],
            TimeoutSec = 35f,
        });

        // Press U to open duty finder menu
        steps.Add(new TaskStep
        {
            Name = $"Duty Guard: Open Menu ({charName})",
            OnEnter = () =>
            {
                if (relogState.Failed || !dutyDetected) return;
                runner.AddLog($"Duty Guard: pressing U to open duty menu...");
                KeyInputHelper.PressKey(0x55);
            },
            IsComplete = () => relogState.Failed || !dutyDetected || AddonHelper.IsAddonVisible("ContentsFinderMenu"),
            TimeoutSec = 10f,
            MaxRetries = 5,
        });

        steps.Add(MakeDelay($"Duty Guard: Menu Wait ({charName})", 1.0f));

        // Click Leave
        steps.Add(new TaskStep
        {
            Name = $"Duty Guard: Click Leave ({charName})",
            OnEnter = () =>
            {
                if (relogState.Failed || !dutyDetected) return;
                runner.AddLog($"Duty Guard: clicking Leave button...");
                AddonHelper.ClickAddonButton("ContentsFinderMenu", 43);
            },
            IsComplete = () => relogState.Failed || !dutyDetected || true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay($"Duty Guard: Confirm Wait ({charName})", 0.5f));

        // Click Yes
        steps.Add(new TaskStep
        {
            Name = $"Duty Guard: Confirm Leave ({charName})",
            OnEnter = () =>
            {
                if (relogState.Failed || !dutyDetected) return;
                runner.AddLog($"Duty Guard: confirming Yes to leave...");
                AddonHelper.ClickYesNo(true);
            },
            IsComplete = () => relogState.Failed || !dutyDetected || !Plugin.Condition[ConditionFlag.BoundByDuty],
            TimeoutSec = 45f,
            OnTimeout = () =>
            {
                if (!relogState.Failed && dutyDetected && Plugin.Condition[ConditionFlag.BoundByDuty])
                {
                    runner.AddLog($"FAILED: {charName} — unable to leave duty, halting.");
                    runner.FailedCharacters.Add(charName);
                    relogState.Failed = true;
                }
            },
        });

        // SafeWait 3-pass AFTER leaving duty — ensure character is fully loaded in overworld
        foreach (var sw in BuildCharacterSafeWait3Pass($"Duty Guard: Post-SafeWait ({charName})", 30f))
        {
            var origComplete = sw.IsComplete;
            steps.Add(new TaskStep
            {
                Name = sw.Name,
                OnEnter = sw.OnEnter,
                IsComplete = () => relogState.Failed || !dutyDetected || origComplete(),
                TimeoutSec = sw.TimeoutSec,
            });
        }

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Helper step builders
    // ═══════════════════════════════════════════════════════

    /// <summary>Creates a simple delay step.</summary>
    public static TaskStep MakeDelay(string name, float seconds)
    {
        DateTime? start = null;
        return new TaskStep
        {
            Name = name,
            OnEnter = () => start = DateTime.UtcNow,
            IsComplete = () => start.HasValue && (DateTime.UtcNow - start.Value).TotalSeconds >= seconds,
            TimeoutSec = seconds + 2f,
        };
    }

    /// <summary>
    /// CharacterSafeWait equivalent — waits for NamePlate addon + player available + not zoning.
    /// Mirrors CharacterSafeWaitXA() from xafunc.lua.
    /// </summary>
    public static TaskStep BuildCharacterSafeWait(string name, float timeoutSec)
    {
        return new TaskStep
        {
            Name = name,
            IsComplete = () => IsNamePlateReady() && IsPlayerAvailable(),
            TimeoutSec = timeoutSec,
        };
    }

    /// <summary>
    /// CharacterSafeWait 3-Pass — confirms the player is truly safe by requiring
    /// 3 consecutive successful checks with 1-second intervals between each.
    /// This prevents false positives during zone transitions or animation states.
    ///
    /// Hard rule: Any time the plugin needs to confirm the player is safe before
    /// performing actions, use this 3-pass variant instead of the single-check version.
    ///
    /// Mirrors the xafunc.lua Lifestream wait pattern:
    ///   "Confirm not-busy 3 consecutive times with 1s intervals"
    /// </summary>
    public static List<TaskStep> BuildCharacterSafeWait3Pass(string label, float perPassTimeoutSec = 15f)
    {
        return new List<TaskStep>
        {
            BuildCharacterSafeWait($"{label} [pass 1/3]", perPassTimeoutSec),
            MakeDelay($"{label} [wait 1s]", 1.0f),
            BuildCharacterSafeWait($"{label} [pass 2/3]", perPassTimeoutSec),
            MakeDelay($"{label} [wait 1s]", 1.0f),
            BuildCharacterSafeWait($"{label} [pass 3/3]", perPassTimeoutSec),
        };
    }

    /// <summary>
    /// Mount up step — equivalent to MountUpXA() → /gaction "Mount Roulette".
    /// Usable as a template for future tasks that involve movement.
    /// </summary>
    public static TaskStep BuildMountUp(string name = "Mount Up")
    {
        return new TaskStep
        {
            Name = name,
            OnEnter = () => ChatHelper.SendMessage("/gaction \"Mount Roulette\""),
            IsComplete = () => true,
            TimeoutSec = 3f,
        };
    }

    // ═══════════════════════════════════════════════════════
    //  Game state checks — reusable across tasks
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
    /// Checks if NamePlate addon is ready and visible.
    /// Part of CharacterSafeWaitXA() checks.
    /// </summary>
    public static unsafe bool IsNamePlateReady()
    {
        try
        {
            var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("NamePlate");
            return addon != null && addon->IsVisible;
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks if the player is available (loaded and not in a blocking state).
    /// Part of CharacterSafeWaitXA() checks.
    /// </summary>
    public static bool IsPlayerAvailable()
    {
        try
        {
            if (!Plugin.PlayerState.IsLoaded) return false;
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null) return false;
            // Not zoning
            if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]) return false;
            // Not in cutscene (commands silently dropped during cutscenes)
            if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
            if (Plugin.Condition[ConditionFlag.WatchingCutscene]) return false;
            if (Plugin.Condition[ConditionFlag.WatchingCutscene78]) return false;
            // Not logging out
            if (Plugin.Condition[ConditionFlag.LoggingOut]) return false;
            // Not casting (mid-cast blocks interaction)
            if (Plugin.Condition[ConditionFlag.Casting]) return false;
            return true;
        }
        catch { return false; }
    }
}
