using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Services.Tasks;
using PostProcessStep = XASlave.Services.TaskStep;

namespace XASlave.Services;

/// <summary>
/// Handles both pre-processing and post-processing for AutoRetainer multi-mode.
///
/// PRE-PROCESSING (before AR processes retainers):
///   Uses the Suppressed pattern - on character login, suppress AR, run collection
///   steps (inventory, saddlebag, FC window, XA Database save), then un-suppress.
///   AR waits while suppressed and starts retainer processing after un-suppress.
///
/// POST-PROCESSING (after AR finishes retainers, before relog):
///   Two-phase subscription pattern (AR clears the postprocess list between characters):
///   1. User enables -> subscribe to OnCharacterAdditionalTask + OnCharacterReadyForPostprocess
///   2. AR fires OnCharacterAdditionalTask - we call RequestCharacterPostprocess("XASlave")
///   3. AR fires OnCharacterReadyForPostprocess("XASlave") - we run collection steps
///   4. When done -> calls FinishCharacterPostprocessRequest so AR can continue
///
/// Uses the same step-based state machine pattern as AutoCollectionService.
/// </summary>
public sealed class ArPostProcessService : IDisposable
{
    private const string PluginName = "XASlave";
    private static readonly string[] ShipBailoutWatchedAddons = { "AirShipExplorationResult", "SelectString" };

    private readonly Plugin plugin;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly IDtrBar dtrBar;
    private IDtrBarEntry? dtrEntry;

    // Step machine state (shared between pre and post processing)
    private readonly List<PostProcessStep> steps = new();
    private int stepIndex = -1;
    private DateTime stepStart;
    private bool stepActionDone;
    private bool running;
    private bool registered;
    private bool isPreProcessing; // true = pre-processing mode, false = post-processing mode
    private bool isShipExplorationBailout;

    // Pre-processing: scheduled on login, runs after delay
    private DateTime? preProcessScheduledAt;
    private float preProcessScheduledDelaySeconds;
    private bool preProcessSkipPending;
    private string preProcessSkipMessage = string.Empty;
    private bool preProcessFrameworkHooked;
    private bool preProcessLoginSubscribed;
    private bool postProcessIpcSubscribed;
    private bool postProcessArSuppressedByTask;
    private bool startupRecoveryFrameworkHooked;
    private bool startupRecoveryCompleted;
    private bool startupRecoveryResetPending;
    private DateTime startupRecoveryNextCheckAt = DateTime.MinValue;
    private bool shipBailoutFrameworkHooked;
    private readonly Dictionary<string, DateTime> shipBailoutAddonVisibleSince = new(StringComparer.Ordinal);
    private bool shipBailoutShouldUnsuppressAtEnd;
    private bool shipBailoutShouldResumeMultiModeAtEnd;

    public bool IsRunning => running;
    public bool IsRegistered => registered;
    public string StatusText { get; private set; } = string.Empty;

    // Log messages for UI display
    private readonly List<string> logMessages = new();
    public IReadOnlyList<string> LogMessages => logMessages;
    private const int MaxLogMessages = 100;
    public int CharactersProcessed { get; private set; }
    public int CharactersPreProcessed { get; private set; }

    public ArPostProcessService(Plugin plugin, IClientState clientState, ICondition condition,
        IFramework framework, IObjectTable objectTable, IPluginLog log, IDtrBar dtrBar)
    {
        this.plugin = plugin;
        this.clientState = clientState;
        this.condition = condition;
        this.framework = framework;
        this.objectTable = objectTable;
        this.log = log;
        this.dtrBar = dtrBar;

        RefreshStartupRecoveryCheck();

        // If either pre or post processing is enabled, register on construction
        if (plugin.Configuration.ArPostProcessEnabled || plugin.Configuration.ArPreProcessEnabled || plugin.Configuration.ArShipExplorationBailoutEnabled)
            Register();
    }

    /// <summary>Subscribe to AR events for pre/post processing.
    /// Post: OnCharacterAdditionalTask + OnCharacterReadyForPostprocess (two-phase AR hook).
    /// Pre: ClientState.Login ->’ suppress AR -> run steps -> un-suppress.</summary>
    public void Register()
    {
        var needsRegistration = plugin.Configuration.ArPreProcessEnabled || plugin.Configuration.ArPostProcessEnabled || plugin.Configuration.ArShipExplorationBailoutEnabled;
        if (!needsRegistration)
        {
            Unregister();
            return;
        }

        var wasRegistered = registered;
        UpdateSubscriptions();
        registered = preProcessLoginSubscribed || postProcessIpcSubscribed || shipBailoutFrameworkHooked;

        if (!wasRegistered && registered)
        {
            var modes = new List<string>();
            if (plugin.Configuration.ArPreProcessEnabled) modes.Add("Pre");
            if (plugin.Configuration.ArPostProcessEnabled) modes.Add("Post");
            if (plugin.Configuration.ArShipExplorationBailoutEnabled) modes.Add("Ship Bailout");
            var modeStr = modes.Count > 0 ? string.Join("+", modes) : "None active";
            AddLog($"[AR Processing] Subscribed to events ({modeStr}).");
            LogInfo($"[XASlave] ArProcessing: Registered - {modeStr}.");
        }
    }

    /// <summary>Unsubscribe from all AR events.</summary>
    public void Unregister()
    {
        if (!registered && !preProcessLoginSubscribed && !postProcessIpcSubscribed && !shipBailoutFrameworkHooked) return;

        if (postProcessIpcSubscribed)
        {
            plugin.IpcClient.AutoRetainerUnsubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
            plugin.IpcClient.AutoRetainerUnsubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);
            postProcessIpcSubscribed = false;
        }

        if (preProcessLoginSubscribed)
        {
            clientState.Login -= OnLogin;
            preProcessLoginSubscribed = false;
        }

        if (shipBailoutFrameworkHooked)
        {
            framework.Update -= OnShipExplorationBailoutCheck;
            shipBailoutFrameworkHooked = false;
        }

        shipBailoutAddonVisibleSince.Clear();

        CancelPendingPreProcess();

        registered = false;
        AddLog("[AR Processing] Unsubscribed from all events.");
        LogInfo("[XASlave] ArProcessing: Unsubscribed from all events.");

        // If currently running, clean up appropriately
        if (running)
        {
            var wasPreProcess = isPreProcessing;
            var wasShipBailout = isShipExplorationBailout;
            Cancel();
            if (wasShipBailout)
            {
                if (shipBailoutShouldUnsuppressAtEnd)
                    plugin.IpcClient.AutoRetainerSetSuppressed(false);
            }
            else if (wasPreProcess)
                plugin.IpcClient.AutoRetainerSetSuppressed(false);
            else
            {
                ReleasePostProcessArSuppression();
                plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
            }
        }
    }

    private void UpdateSubscriptions()
    {
        if (plugin.Configuration.ArPostProcessEnabled)
        {
            if (!postProcessIpcSubscribed)
            {
                plugin.IpcClient.AutoRetainerSubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
                plugin.IpcClient.AutoRetainerSubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);
                postProcessIpcSubscribed = true;
            }
        }
        else if (postProcessIpcSubscribed)
        {
            plugin.IpcClient.AutoRetainerUnsubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
            plugin.IpcClient.AutoRetainerUnsubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);
            postProcessIpcSubscribed = false;

            if (running && !isPreProcessing)
            {
                Cancel();
                plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
            }
        }

        if (plugin.Configuration.ArPreProcessEnabled)
        {
            if (!preProcessLoginSubscribed)
            {
                clientState.Login += OnLogin;
                preProcessLoginSubscribed = true;
            }
        }
        else if (preProcessLoginSubscribed)
        {
            clientState.Login -= OnLogin;
            preProcessLoginSubscribed = false;
            CancelPendingPreProcess();

            if (running && isPreProcessing)
            {
                Cancel();
                plugin.IpcClient.AutoRetainerSetSuppressed(false);
            }
        }

        if (plugin.Configuration.ArShipExplorationBailoutEnabled)
        {
            if (!shipBailoutFrameworkHooked)
            {
                framework.Update += OnShipExplorationBailoutCheck;
                shipBailoutFrameworkHooked = true;
            }
        }
        else if (shipBailoutFrameworkHooked)
        {
            framework.Update -= OnShipExplorationBailoutCheck;
            shipBailoutFrameworkHooked = false;
            shipBailoutAddonVisibleSince.Clear();

            if (running && isShipExplorationBailout)
            {
                Cancel();
                if (shipBailoutShouldUnsuppressAtEnd)
                    plugin.IpcClient.AutoRetainerSetSuppressed(false);
                ResetShipExplorationBailoutState();
            }
        }
    }

    private void CancelPendingPreProcess()
    {
        preProcessScheduledAt = null;
        preProcessScheduledDelaySeconds = 0f;
        preProcessSkipPending = false;
        preProcessSkipMessage = string.Empty;
        if (preProcessFrameworkHooked)
        {
            framework.Update -= OnPreProcessCheck;
            preProcessFrameworkHooked = false;
        }
    }

    public void RefreshStartupRecoveryCheck()
    {
        if (startupRecoveryCompleted)
            return;

        if (plugin.Configuration.ArStartupRecoveryEnabled)
            ArmStartupRecoveryCheck();
        else
            DisarmStartupRecoveryCheck();
    }

    private void ArmStartupRecoveryCheck()
    {
        if (startupRecoveryCompleted || startupRecoveryFrameworkHooked || !plugin.Configuration.ArStartupRecoveryEnabled)
            return;

        startupRecoveryNextCheckAt = DateTime.MinValue;
        framework.Update += OnStartupRecoveryCheck;
        startupRecoveryFrameworkHooked = true;
    }

    private void DisarmStartupRecoveryCheck()
    {
        if (startupRecoveryFrameworkHooked)
        {
            framework.Update -= OnStartupRecoveryCheck;
            startupRecoveryFrameworkHooked = false;
        }

        startupRecoveryResetPending = false;
        startupRecoveryNextCheckAt = DateTime.MinValue;
    }

    private void CompleteStartupRecoveryCheck()
    {
        startupRecoveryCompleted = true;
        DisarmStartupRecoveryCheck();
    }

    private void OnStartupRecoveryCheck(IFramework fw)
    {
        if (!plugin.Configuration.ArStartupRecoveryEnabled)
        {
            DisarmStartupRecoveryCheck();
            return;
        }

        if (running || preProcessScheduledAt.HasValue)
            return;

        var now = DateTime.UtcNow;
        if (now < startupRecoveryNextCheckAt)
            return;

        startupRecoveryNextCheckAt = now.AddSeconds(1);

        if (startupRecoveryResetPending)
        {
            if (!Plugin.PlayerState.IsLoaded)
                return;

            AddLog("[AR Startup Recovery] Sending /ays reset now that the player has loaded.");
            LogWarning("[XASlave] ArStartupRecovery: Player loaded after release; sending /ays reset.");
            ChatHelper.SendMessage("/ays reset");
            CompleteStartupRecoveryCheck();
            return;
        }

        if (!plugin.IpcClient.IsAutoRetainerAvailable())
            return;

        if (!plugin.IpcClient.AutoRetainerGetSuppressed())
        {
            CompleteStartupRecoveryCheck();
            return;
        }

        AddLog("[AR Startup Recovery] AutoRetainer reported suppressed on plugin load. Releasing suppression and resetting task state.");
        LogWarning("[XASlave] ArStartupRecovery: AutoRetainer was suppressed on plugin load; performing one-time recovery.");

        if (plugin.IpcClient.AutoRetainerSetSuppressed(false))
            AddLog("[AR Startup Recovery] Released AutoRetainer suppression left behind by a reload or update.");
        else
            AddLog("[AR Startup Recovery] Failed to release AutoRetainer suppression through IPC; continuing with /ays reset.");

        if (Plugin.PlayerState.IsLoaded)
        {
            AddLog("[AR Startup Recovery] Sending /ays reset to clear AutoRetainer task state.");
            ChatHelper.SendMessage("/ays reset");
            CompleteStartupRecoveryCheck();
            return;
        }

        AddLog("[AR Startup Recovery] Waiting for player load before sending /ays reset.");
        startupRecoveryResetPending = true;
    }

    /// <summary>Called by AR per-character BEFORE checking the postprocess list.
    /// This is the signal for plugins to call RequestCharacterPostProcess to get into the list.</summary>
    private void OnCharacterAdditionalTask()
    {
        if (!plugin.Configuration.ArPostProcessEnabled) return;

        if (!plugin.IsCurrentCharacterSyncDue(plugin.Configuration.ArPrePostCheckEveryHours))
        {
            AddLog($"[AR Post-Process] Skipped - last XA sync is still within the {DescribeCadence(plugin.Configuration.ArPrePostCheckEveryHours)} window.");
            LogInfo("[XASlave] ArPostProcess: Skipping registration because sync cadence is not due.");
            return;
        }

        LogInfo("[XASlave] ArPostProcess: AR fired OnCharacterAdditionalTask - registering for this character.");
        AddLog("[AR Post-Process] AR signaled - registering for this character's post-processing.");

        var success = plugin.IpcClient.AutoRetainerRequestCharacterPostProcess(PluginName);
        if (success)
        {
            LogInfo("[XASlave] ArPostProcess: Successfully registered XASlave for this character.");
        }
        else
        {
            log.Error("[XASlave] ArPostProcess: Failed to register for this character's post-processing.");
            AddLog("[AR Post-Process] Failed to register for this character (AR may have rejected).");
        }
    }

    private void OnShipExplorationBailoutCheck(IFramework fw)
    {
        if (!plugin.Configuration.ArShipExplorationBailoutEnabled)
        {
            shipBailoutAddonVisibleSince.Clear();
            return;
        }

        if (running || preProcessScheduledAt.HasValue || !Plugin.PlayerState.IsLoaded)
        {
            shipBailoutAddonVisibleSince.Clear();
            return;
        }

        var autoRetainerMultiModeEnabled = plugin.IpcClient.AutoRetainerGetMultiModeEnabled();
        if (!autoRetainerMultiModeEnabled)
        {
            shipBailoutAddonVisibleSince.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        string? triggeredAddon = null;
        foreach (var addonName in ShipBailoutWatchedAddons)
        {
            if (!AddonHelper.IsAddonVisible(addonName))
            {
                shipBailoutAddonVisibleSince.Remove(addonName);
                continue;
            }

            if (!shipBailoutAddonVisibleSince.TryGetValue(addonName, out var visibleSince))
            {
                shipBailoutAddonVisibleSince[addonName] = now;
                continue;
            }

            if ((now - visibleSince).TotalSeconds >= plugin.Configuration.ArShipExplorationBailoutSeconds)
            {
                triggeredAddon = addonName;
                break;
            }
        }

        if (triggeredAddon == null)
            return;

        shipBailoutAddonVisibleSince.Clear();
        shipBailoutShouldResumeMultiModeAtEnd = plugin.IpcClient.AutoRetainerGetMultiModeEnabled();
        shipBailoutShouldUnsuppressAtEnd = shipBailoutShouldResumeMultiModeAtEnd;
        isPreProcessing = false;
        isShipExplorationBailout = true;
        AddLog($"[Ship Bailout] {triggeredAddon} stayed open for {plugin.Configuration.ArShipExplorationBailoutSeconds}s while AutoRetainer multi-mode was enabled. Starting bailout.");
        LogWarning($"[XASlave] ArShipBailout: Triggered bailout because {triggeredAddon} remained open.");
        BuildShipExplorationBailoutSteps();
        StartStepMachine($"ship exploration bailout ({triggeredAddon})");
    }

    // ------------------------------------------------------------------
    //  Pre-Processing - Login handler + Suppressed pattern
    // ------------------------------------------------------------------

    /// <summary>Called when any character logs in. Schedules pre-processing if enabled and AR multi-mode is active.</summary>
    private void OnLogin()
    {
        if (!plugin.Configuration.ArPreProcessEnabled) return;
        if (running) return; // Already running something

        // Only run pre-processing if AR multi-mode is enabled
        if (!plugin.IpcClient.AutoRetainerGetMultiModeEnabled())
        {
            LogInfo("[XASlave] ArPreProcess: Login detected but AR multi-mode not enabled - skipping.");
            return;
        }

        // Suppress AR immediately so it doesn't start retainer processing
        plugin.IpcClient.AutoRetainerSetSuppressed(true);
        AddLog("[AR Pre-Process] Login detected - AR suppressed, waiting for player to load...");
        LogInfo("[XASlave] ArPreProcess: Login detected, AR suppressed. Scheduling pre-processing.");

        // Schedule pre-processing with a delay (player needs time to fully load)
        preProcessScheduledAt = DateTime.UtcNow;
        preProcessSkipPending = !plugin.IsCurrentCharacterSyncDue(plugin.Configuration.ArPrePostCheckEveryHours);
        preProcessScheduledDelaySeconds = preProcessSkipPending ? 1f : plugin.Configuration.ArPreProcessLoginDelay;
        preProcessSkipMessage = preProcessSkipPending
            ? $"[AR Pre-Process] Skipped - last XA sync is still within the {DescribeCadence(plugin.Configuration.ArPrePostCheckEveryHours)} window. Releasing AR."
            : string.Empty;
        if (!preProcessFrameworkHooked)
        {
            framework.Update += OnPreProcessCheck;
            preProcessFrameworkHooked = true;
        }
    }

    /// <summary>Framework.Update check for scheduled pre-processing. Waits for player to be loaded + delay.</summary>
    private void OnPreProcessCheck(IFramework fw)
    {
        if (!preProcessScheduledAt.HasValue)
        {
            framework.Update -= OnPreProcessCheck;
            preProcessFrameworkHooked = false;
            return;
        }

        // Wait for player to be fully loaded
        if (!Plugin.PlayerState.IsLoaded) return;

        // Wait for configured delay
        var elapsed = (float)(DateTime.UtcNow - preProcessScheduledAt.Value).TotalSeconds;
        if (elapsed < preProcessScheduledDelaySeconds) return;

        // Ready to start pre-processing
        preProcessScheduledAt = null;
        framework.Update -= OnPreProcessCheck;
        preProcessFrameworkHooked = false;

        if (preProcessSkipPending)
        {
            var skipMessage = preProcessSkipMessage;
            preProcessScheduledDelaySeconds = 0f;
            preProcessSkipPending = false;
            preProcessSkipMessage = string.Empty;
            AddLog(skipMessage);
            LogInfo("[XASlave] ArPreProcess: Cadence gate skipped pre-processing and released AR.");
            plugin.IpcClient.AutoRetainerSetSuppressed(false);
            return;
        }

        preProcessScheduledDelaySeconds = 0f;
        preProcessSkipMessage = string.Empty;

        var charName = "Unknown";
        try
        {
            var lp = objectTable.LocalPlayer;
            if (lp != null) charName = lp.Name.ToString();
        }
        catch { /* ignore */ }

        AddLog($"[AR Pre-Process] Starting pre-processing for {charName}...");
        LogInfo($"[XASlave] ArPreProcess: Starting pre-processing for {charName}.");

        isPreProcessing = true;
        BuildPreProcessSteps();

        if (steps.Count == 0)
        {
            AddLog("[AR Pre-Process] No steps configured - un-suppressing AR.");
            LogInfo("[XASlave] ArPreProcess: No steps configured - un-suppressing AR.");
            plugin.IpcClient.AutoRetainerSetSuppressed(false);
            return;
        }

        StartStepMachine($"pre-processing for {charName}");
    }

    private void BuildPreProcessSteps()
    {
        steps.Clear();
        var config = plugin.Configuration;

        // Open Inventory
        if (config.ArPreProcessOpenInventory)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Pre: Open Inventory",
                OnEnter = () =>
                {
                    AddLog("Opening Inventory...");
                    ChatHelper.SendMessage("/inventory");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: Inventory Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // Open Armoury Chest
        if (config.ArPreProcessOpenArmouryChest)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Pre: Open Armoury Chest",
                OnEnter = () =>
                {
                    AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: Armoury Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // Open Saddlebags
        if (config.ArPreProcessOpenSaddlebags)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Pre: Open Saddlebags",
                OnEnter = () =>
                {
                    AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => IsAddonReady("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: Saddlebag Read Delay", IsComplete = () => DelayComplete(1.0f), TimeoutSec = 2f });
            steps.Add(new PostProcessStep
            {
                Name = "Pre: Close Saddlebags",
                OnEnter = () => CloseAddon("InventoryBuddy"),
                IsComplete = () => !IsAddonReady("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: Saddlebag Close Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        if (config.ArPreProcessOpenJournal)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Pre: Open Journal",
                OnEnter = () =>
                {
                    AddLog("Opening Journal...");
                    ChatHelper.SendMessage("/journal");
                },
                IsComplete = () => IsAddonReady("Journal") || DelayComplete(2.0f),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: Journal Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            if (config.ArPreProcessSaveToXaDatabase)
            {
                steps.Add(new PostProcessStep
                {
                    Name = "Pre: Save Journal to XA Database",
                    ShouldSkip = () => !IsAddonReady("Journal"),
                    OnEnter = () =>
                    {
                        AddLog("Saving Journal data to XA Database...");
                        if (plugin.SaveToXaDatabaseAndRecordSync())
                            AddLog("Saved Journal data to XA Database.");
                        else
                            AddLog("XA Database Journal save failed (plugin may not be loaded).");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(new PostProcessStep { Name = "Pre: Journal Save Delay", ShouldSkip = () => !IsAddonReady("Journal"), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            }
        }

        if (config.ArPreProcessCollectPersonalPlotInfo)
        {
            steps.AddRange(MonthlyReloggerTask.BuildCollectPersonalPlotInfoSteps(plugin, AddLog));
        }

        // FC Window - full processing
        if (config.ArPreProcessFcWindow)
        {
            var skipFc = false;

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Check Eligibility",
                OnEnter = () =>
                {
                    if (!IsOnHomeWorld())
                    {
                        AddLog("Not on home world - skipping FC collection.");
                        skipFc = true;
                    }
                    else if (!IsInFreeCompany())
                    {
                        AddLog("Not in a Free Company - skipping FC collection.");
                        skipFc = true;
                    }
                    else
                    {
                        AddLog("On home world and in FC - collecting FC data...");
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Open Window",
                OnEnter = () => { if (!skipFc) OpenAgentWindow(AgentId.FreeCompany, "FreeCompany"); },
                IsComplete = () => skipFc || IsAddonReady("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: FC Load Delay", IsComplete = () => skipFc || DelayComplete(1.0f), TimeoutSec = 2f });

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Members Tab",
                OnEnter = () => { if (!skipFc) { FireAddonCallback("FreeCompany", 1); ClickAddonNode("FreeCompany", 8); } },
                IsComplete = () => skipFc || IsAddonReady("FreeCompanyMember") || DelayComplete(3.0f),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: FC Members Load", IsComplete = () => skipFc || DelayComplete(1.5f), TimeoutSec = 2f });

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Info Tab",
                OnEnter = () => { if (!skipFc) { FireAddonCallback("FreeCompany", 3); ClickAddonNode("FreeCompany", 4); } },
                IsComplete = () => skipFc || IsAddonReady("FreeCompanyStatus") || DelayComplete(3.0f),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: FC Status Load", IsComplete = () => skipFc || DelayComplete(1.0f), TimeoutSec = 2f });

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Housing Search",
                OnEnter = () => { if (!skipFc && IsAddonReady("FreeCompanyStatus")) ClickAddonNode("FreeCompanyStatus", 12); },
                IsComplete = () => skipFc || IsAddonReady("HousingSignBoard") || DelayComplete(3.0f),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: FC Housing Load", IsComplete = () => skipFc || DelayComplete(1.5f), TimeoutSec = 2f });

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Close Sub-Addons",
                OnEnter = () => { if (!skipFc) { CloseAddon("HousingSignBoard"); CloseAddon("FreeCompanyStatus"); CloseAddon("FreeCompanyMember"); } },
                IsComplete = () => skipFc || DelayComplete(0.5f),
                TimeoutSec = 2f,
            });

            steps.Add(new PostProcessStep
            {
                Name = "Pre: FC Close Window",
                OnEnter = () => { if (!skipFc) CloseAddon("FreeCompany"); },
                IsComplete = () => skipFc || !IsAddonReady("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: FC Cooldown", IsComplete = () => skipFc || DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // Save to XA Database
        if (config.ArPreProcessSaveToXaDatabase)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Pre: Save to XA Database",
                OnEnter = () =>
                {
                    AddLog("Saving to XA Database...");
                    if (plugin.SaveToXaDatabaseAndRecordSync())
                        AddLog("Saved to XA Database.");
                    else
                        AddLog("XA Database save failed (plugin may not be loaded).");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Pre: Save Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }
    }

    private void BuildShipExplorationBailoutSteps()
    {
        steps.Clear();

        steps.Add(new PostProcessStep
        {
            Name = "Ship Bailout: Suppress AutoRetainer",
            OnEnter = () =>
            {
                AddLog("[Ship Bailout] Suppressing AutoRetainer before ESC bailout...");
                plugin.IpcClient.AutoRetainerSetSuppressed(true);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        steps.Add(BuildEscapeBailoutStep("Ship Bailout: Close Menus With ESC", 30f));
        steps.Add(BuildCharacterSafeWaitStep("Ship Bailout: SafeWait After Menus", 20f));
        steps.Add(new PostProcessStep
        {
            Name = "Ship Bailout: Reset AutoRetainer",
            OnEnter = () =>
            {
                AddLog("[Ship Bailout] Sending /ays reset to clear AutoRetainer task state...");
                ChatHelper.SendMessage("/ays reset");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
    }

    private PostProcessStep BuildCharacterSafeWaitStep(string name, float timeoutSec)
    {
        return new PostProcessStep
        {
            Name = name,
            IsComplete = () => CharacterSafetyHelper.IsCharacterSafeWaitReady(),
            TimeoutSec = timeoutSec,
        };
    }

    private static bool AreAnyShipBailoutMenusVisible()
    {
        foreach (var addonName in ShipBailoutWatchedAddons)
        {
            if (AddonHelper.IsAddonVisible(addonName))
                return true;
        }

        return false;
    }

    private PostProcessStep BuildEscapeBailoutStep(string name, float timeoutSec)
    {
        DateTime lastEscapeAt = DateTime.MinValue;

        return new PostProcessStep
        {
            Name = name,
            OnEnter = () =>
            {
                if (AreAnyShipBailoutMenusVisible())
                    AddLog("[Ship Bailout] Sending ESC until no ship result or SelectString menus remain...");
            },
            IsComplete = () =>
            {
                if (!AreAnyShipBailoutMenusVisible())
                    return true;

                var now = DateTime.UtcNow;
                if (lastEscapeAt == DateTime.MinValue || (now - lastEscapeAt).TotalSeconds >= 0.5)
                {
                    lastEscapeAt = now;
                    AddLog("[Ship Bailout] Sending ESC bailout press...");
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                }

                return false;
            },
            TimeoutSec = timeoutSec,
        };
    }
    /// <summary>Called by AR when it's ready for XASlave to run post-processing steps.</summary>
    private void OnCharacterReadyForPostprocess(string pluginName)
    {
        if (pluginName != PluginName) return;
        if (!plugin.Configuration.ArPostProcessEnabled) return;

        var charName = "Unknown";
        try
        {
            var lp = objectTable.LocalPlayer;
            if (lp != null) charName = lp.Name.ToString();
        }
        catch { /* ignore */ }

        LogInfo($"[XASlave] ArPostProcess: AR signaled character ready - {charName}");
        AddLog($"[AR Post-Process] Character ready: {charName}");

        isPreProcessing = false;
        BuildPostProcessSteps();

        if (steps.Count == 0)
        {
            AddLog("[AR Post-Process] No steps configured - signaling AR to continue.");
            plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
            return;
        }

        AcquirePostProcessArSuppression();
        StartStepMachine($"post-processing for {charName}");
    }

    private void BuildPostProcessSteps()
    {
        steps.Clear();
        var config = plugin.Configuration;

        // Small initial delay to let AR finish closing retainer windows
        steps.Add(new PostProcessStep
        {
            Name = "AR Settle Delay",
            IsComplete = () => DelayComplete(1.5f),
            TimeoutSec = 3f,
        });

        // Open Inventory
        if (config.ArPostProcessOpenInventory)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Open Inventory",
                OnEnter = () =>
                {
                    AddLog("Opening Inventory...");
                    ChatHelper.SendMessage("/inventory");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new PostProcessStep { Name = "Inventory Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // Open Armoury Chest
        if (config.ArPostProcessOpenArmouryChest)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Open Armoury Chest",
                OnEnter = () =>
                {
                    AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new PostProcessStep { Name = "Armoury Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // Open Saddlebags
        if (config.ArPostProcessOpenSaddlebags)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Open Saddlebags",
                OnEnter = () =>
                {
                    AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => IsAddonReady("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Saddlebag Read Delay", IsComplete = () => DelayComplete(1.0f), TimeoutSec = 2f });
            steps.Add(new PostProcessStep
            {
                Name = "Close Saddlebags",
                OnEnter = () => CloseAddon("InventoryBuddy"),
                IsComplete = () => !IsAddonReady("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Saddlebag Close Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        if (config.ArPostProcessOpenJournal)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Open Journal",
                OnEnter = () =>
                {
                    AddLog("Opening Journal...");
                    ChatHelper.SendMessage("/journal");
                },
                IsComplete = () => IsAddonReady("Journal") || DelayComplete(2.0f),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Journal Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            if (config.ArPostProcessSaveToXaDatabase)
            {
                steps.Add(new PostProcessStep
                {
                    Name = "Save Journal to XA Database",
                    ShouldSkip = () => !IsAddonReady("Journal"),
                    OnEnter = () =>
                    {
                        AddLog("Saving Journal data to XA Database...");
                        if (plugin.SaveToXaDatabaseAndRecordSync())
                            AddLog("Saved Journal data to XA Database.");
                        else
                            AddLog("XA Database Journal save failed (plugin may not be loaded).");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(new PostProcessStep { Name = "Journal Save Delay", ShouldSkip = () => !IsAddonReady("Journal"), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            }
        }

        if (config.ArPostProcessCollectPersonalPlotInfo)
        {
            steps.AddRange(MonthlyReloggerTask.BuildCollectPersonalPlotInfoSteps(plugin, AddLog));
        }

        // FC Window - full processing (Members, Info, Housing)
        if (config.ArPostProcessFcWindow)
        {
            // Only run FC steps if on home world and in an FC
            var skipFc = false;

            steps.Add(new PostProcessStep
            {
                Name = "FC: Check Eligibility",
                OnEnter = () =>
                {
                    if (!IsOnHomeWorld())
                    {
                        AddLog("Not on home world - skipping FC collection.");
                        skipFc = true;
                    }
                    else if (!IsInFreeCompany())
                    {
                        AddLog("Not in a Free Company - skipping FC collection.");
                        skipFc = true;
                    }
                    else
                    {
                        AddLog("On home world and in FC - collecting FC data...");
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });

            // Open FC Window
            steps.Add(new PostProcessStep
            {
                Name = "FC: Open Window",
                OnEnter = () => { if (!skipFc) OpenAgentWindow(AgentId.FreeCompany, "FreeCompany"); },
                IsComplete = () => skipFc || IsAddonReady("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "FC: Load Delay", IsComplete = () => skipFc || DelayComplete(1.0f), TimeoutSec = 2f });

            // Click Members Tab
            steps.Add(new PostProcessStep
            {
                Name = "FC: Members Tab",
                OnEnter = () =>
                {
                    if (!skipFc)
                    {
                        FireAddonCallback("FreeCompany", 1);
                        ClickAddonNode("FreeCompany", 8);
                    }
                },
                IsComplete = () => skipFc || IsAddonReady("FreeCompanyMember") || DelayComplete(3.0f),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "FC: Members Load", IsComplete = () => skipFc || DelayComplete(1.5f), TimeoutSec = 2f });

            // Click Info Tab
            steps.Add(new PostProcessStep
            {
                Name = "FC: Info Tab",
                OnEnter = () =>
                {
                    if (!skipFc)
                    {
                        FireAddonCallback("FreeCompany", 3);
                        ClickAddonNode("FreeCompany", 4);
                    }
                },
                IsComplete = () => skipFc || IsAddonReady("FreeCompanyStatus") || DelayComplete(3.0f),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "FC: Status Load", IsComplete = () => skipFc || DelayComplete(1.0f), TimeoutSec = 2f });

            // Click Housing Search
            steps.Add(new PostProcessStep
            {
                Name = "FC: Housing Search",
                OnEnter = () =>
                {
                    if (!skipFc && IsAddonReady("FreeCompanyStatus"))
                        ClickAddonNode("FreeCompanyStatus", 12);
                },
                IsComplete = () => skipFc || IsAddonReady("HousingSignBoard") || DelayComplete(3.0f),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "FC: Housing Load", IsComplete = () => skipFc || DelayComplete(1.5f), TimeoutSec = 2f });

            // Close all FC sub-addons
            steps.Add(new PostProcessStep
            {
                Name = "FC: Close Sub-Addons",
                OnEnter = () =>
                {
                    if (!skipFc)
                    {
                        CloseAddon("HousingSignBoard");
                        CloseAddon("FreeCompanyStatus");
                        CloseAddon("FreeCompanyMember");
                    }
                },
                IsComplete = () => skipFc || DelayComplete(0.5f),
                TimeoutSec = 2f,
            });

            // Close FC Window
            steps.Add(new PostProcessStep
            {
                Name = "FC: Close Window",
                OnEnter = () => { if (!skipFc) CloseAddon("FreeCompany"); },
                IsComplete = () => skipFc || !IsAddonReady("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(new PostProcessStep { Name = "FC: Cooldown", IsComplete = () => skipFc || DelayComplete(0.5f), TimeoutSec = 1f });
        }

        if (config.ArPostProcessCheckFcChestForGil)
        {
            var skipFcChest = false;
            var fcChestClosedChecks = 0;
            var fcChestLastEscAt = DateTime.MinValue;
            var fcChestRecoveryPhase = 0;
            var fcChestRecoveryAttempts = 0;
            var fcChestRecoveryPhaseStartedAt = DateTime.MinValue;
            const int maxFcChestRecoveryAttempts = 2;
            const float fcChestRecoveryMoveSeconds = 0.5f;
            const float fcChestRecoveryResetDelaySeconds = 0.25f;
            const float fcChestRecoveryRetrySettleSeconds = 0.35f;

            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Check Eligibility",
                OnEnter = () =>
                {
                    var zoneName = AddonHelper.GetCurrentZoneName();
                    if (!plugin.IpcClient.IsXaDatabaseAvailable())
                    {
                        AddLog("XA Database not available - skipping FC chest gil capture.");
                        skipFcChest = true;
                    }
                    else if (!AddonHelper.IsInWorkshop())
                    {
                        AddLog($"Current zone '{zoneName}' does not match Company Workshop - skipping FC chest gil capture.");
                        skipFcChest = true;
                    }
                    else if (!plugin.IpcClient.VnavIsReady())
                    {
                        AddLog("vnav not available - skipping FC chest gil capture.");
                        skipFcChest = true;
                    }
                    else
                    {
                        AddLog($"Current zone '{zoneName}' matches Company Workshop - checking FC chest for gil...");
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });

            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Target",
                ShouldSkip = () => skipFcChest,
                OnEnter = () =>
                {
                    AddLog("Targeting Company Chest...");
                    AddonHelper.TargetByName("Company Chest");
                },
                IsComplete = () => AddonHelper.CurrentTargetMatches("Company Chest"),
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Target Delay",
                ShouldSkip = () => skipFcChest,
                IsComplete = () => DelayComplete(0.5f),
                TimeoutSec = 1f,
            });

            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Path Into Range",
                ShouldSkip = () => skipFcChest,
                OnEnter = () =>
                {
                    AddLog("Pathing to Company Chest (1.5y stop)...");
                    AddonHelper.TryPathToCurrentTarget(1.5f);
                },
                IsComplete = () => AddonHelper.IsCurrentTargetWithinStopDistanceAndStopped("Company Chest", 1.5f),
                TimeoutSec = 20f,
                OnTimeout = () =>
                {
                    AddLog("FC chest pathing timed out - skipping FC chest gil capture.");
                    skipFcChest = true;
                    plugin.IpcClient.VnavStop();
                },
            });

            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Interact",
                ShouldSkip = () => skipFcChest,
                OnEnter = () =>
                {
                    fcChestRecoveryPhase = 0;
                    fcChestRecoveryAttempts = 0;
                    fcChestRecoveryPhaseStartedAt = DateTime.MinValue;
                    AddLog("Interacting with Company Chest...");
                    AddonHelper.DismissTextError();
                    AddonHelper.InteractWithTarget();
                },
                IsComplete = () =>
                {
                    if (IsAddonReady("FreeCompanyChest"))
                        return true;

                    var now = DateTime.UtcNow;

                    if (fcChestRecoveryPhase == 1)
                    {
                        if ((now - fcChestRecoveryPhaseStartedAt).TotalSeconds >= fcChestRecoveryMoveSeconds)
                        {
                            plugin.IpcClient.VnavStop();
                            AddLog("Company Chest recovery: stopping brief re-path and resetting camera.");
                            AddonHelper.ResetCamera();
                            AddonHelper.DismissTextError();
                            fcChestRecoveryPhase = 2;
                            fcChestRecoveryPhaseStartedAt = now;
                        }

                        return false;
                    }

                    if (fcChestRecoveryPhase == 2)
                    {
                        if ((now - fcChestRecoveryPhaseStartedAt).TotalSeconds >= fcChestRecoveryResetDelaySeconds)
                        {
                            AddLog("Retrying Company Chest interaction after reset camera...");
                            AddonHelper.TargetByName("Company Chest");
                            AddonHelper.DismissTextError();
                            AddonHelper.InteractWithTarget();
                            fcChestRecoveryPhase = 3;
                            fcChestRecoveryPhaseStartedAt = now;
                        }

                        return false;
                    }

                    if (fcChestRecoveryPhase == 3)
                    {
                        if ((now - fcChestRecoveryPhaseStartedAt).TotalSeconds < fcChestRecoveryRetrySettleSeconds)
                            return false;

                        fcChestRecoveryPhase = 0;
                    }

                    if (AddonHelper.TryGetCannotSeeTargetTextError(out var matchedText))
                    {
                        if (fcChestRecoveryAttempts >= maxFcChestRecoveryAttempts)
                        {
                            AddLog($"Company Chest interaction still reports _TextError '{matchedText}' after {maxFcChestRecoveryAttempts} recovery attempts - skipping FC chest gil capture.");
                            AddonHelper.DismissTextError();
                            skipFcChest = true;
                            return true;
                        }

                        fcChestRecoveryAttempts++;
                        AddLog($"Company Chest interaction reported _TextError '{matchedText}' - re-pathing for 0.5s, stopping vnav, and resetting camera ({fcChestRecoveryAttempts}/{maxFcChestRecoveryAttempts}).");
                        AddonHelper.DismissTextError();
                        AddonHelper.TryPathToCurrentTarget(1.5f);
                        fcChestRecoveryPhase = 1;
                        fcChestRecoveryPhaseStartedAt = now;
                        return false;
                    }

                    return false;
                },
                TimeoutSec = 12f,
                OnTimeout = () =>
                {
                    AddLog("FreeCompanyChest did not open after Company Chest interaction/recovery - skipping FC chest gil capture.");
                    skipFcChest = true;
                },
            });
            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Load Delay",
                ShouldSkip = () => skipFcChest || !IsAddonReady("FreeCompanyChest"),
                IsComplete = () => DelayComplete(0.5f),
                TimeoutSec = 1f,
            });

            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Save to XA Database",
                ShouldSkip = () => skipFcChest || !IsAddonReady("FreeCompanyChest"),
                OnEnter = () =>
                {
                    AddLog("Saving FC chest gil to XA Database...");
                    if (plugin.SaveToXaDatabaseAndRecordSync())
                        AddLog("Saved FC chest gil to XA Database.");
                    else
                        AddLog("XA Database FC chest save failed (plugin may not be loaded).");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Save Delay",
                ShouldSkip = () => skipFcChest || !IsAddonReady("FreeCompanyChest"),
                IsComplete = () => DelayComplete(0.5f),
                TimeoutSec = 1f,
            });

            steps.Add(new PostProcessStep
            {
                Name = "FC Chest: Close Window",
                ShouldSkip = () => skipFcChest || !IsAddonReady("FreeCompanyChest"),
                OnEnter = () =>
                {
                    fcChestClosedChecks = 0;
                    fcChestLastEscAt = DateTime.UtcNow.AddSeconds(-1.0);
                    AddLog("Closing FC chest window...");
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                },
                IsComplete = () =>
                {
                    if (!IsAddonReady("FreeCompanyChest"))
                    {
                        fcChestClosedChecks++;
                        return fcChestClosedChecks >= 2;
                    }

                    if ((DateTime.UtcNow - fcChestLastEscAt).TotalSeconds < 1.0)
                        return false;

                    fcChestClosedChecks = 0;
                    fcChestLastEscAt = DateTime.UtcNow;
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                    return false;
                },
                TimeoutSec = 12f,
                OnTimeout = () => AddLog("FC chest window stayed open after ESC retries; continuing."),
            });
        }

        // Save to XA Database
        if (config.ArPostProcessSaveToXaDatabase)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Save to XA Database",
                OnEnter = () =>
                {
                    AddLog("Saving to XA Database...");
                    if (plugin.SaveToXaDatabaseAndRecordSync())
                        AddLog("Saved to XA Database.");
                    else
                        AddLog("XA Database save failed (plugin may not be loaded).");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new PostProcessStep { Name = "Save Delay", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }
    }

    // ----------------------
    //  Shared Step Machine
    // ----------------------

    /// <summary>Start executing the current steps list.</summary>
    private void StartStepMachine(string label)
    {
        if (steps.Count == 0) return;
        stepIndex = 0;
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        running = true;
        StatusText = steps[0].Name;
        framework.Update += OnTick;
        UpdateArDtr();
        if (isShipExplorationBailout)
            AddLog($"[Ship Bailout] Starting {steps.Count} steps: {label}");
        else
            AddLog($"[AR {(isPreProcessing ? "Pre" : "Post")}-Process] Starting {steps.Count} steps - {label}");
    }

    public void Cancel()
    {
        if (!running) return;
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = "Cancelled";
        ClearArDtr();
        if (isShipExplorationBailout)
        {
            AddLog("[Ship Bailout] Cancelled.");
            LogInfo("[XASlave] ArShipBailout: Cancelled.");
            if (shipBailoutShouldUnsuppressAtEnd)
                plugin.IpcClient.AutoRetainerSetSuppressed(false);
            if (shipBailoutShouldResumeMultiModeAtEnd)
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            ResetShipExplorationBailoutState();
        }
        else
        {
            var mode = isPreProcessing ? "Pre" : "Post";
            AddLog($"[AR {mode}-Process] Cancelled.");
            LogInfo($"[XASlave] Ar{mode}Process: Cancelled.");
            if (!isPreProcessing)
                ReleasePostProcessArSuppression();
        }
    }

    private void OnTick(IFramework fw)
    {
        if (!running || stepIndex < 0 || stepIndex >= steps.Count)
        {
            Finish();
            return;
        }

        while (running && stepIndex >= 0 && stepIndex < steps.Count)
        {
            var pendingStep = steps[stepIndex];
            if (pendingStep.ShouldSkip == null || !pendingStep.ShouldSkip())
                break;

            stepIndex++;
            if (stepIndex >= steps.Count)
            {
                Finish();
                return;
            }

            stepStart = DateTime.UtcNow;
            stepActionDone = false;
            StatusText = steps[stepIndex].Name;
        }

        if (!running || stepIndex < 0 || stepIndex >= steps.Count)
        {
            Finish();
            return;
        }

        var step = steps[stepIndex];
        var elapsed = (float)(DateTime.UtcNow - stepStart).TotalSeconds;

        if (!stepActionDone)
        {
            if (step.OnEnter != null)
            {
                try { step.OnEnter(); }
                catch (Exception ex) { log.Error($"[XASlave] ArProcess step '{step.Name}' action error: {ex.Message}"); }
            }
            stepActionDone = true;
        }

        try
        {
            if (step.IsComplete())
            {
                AdvanceStep();
                return;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[XASlave] ArProcess step '{step.Name}' check error: {ex.Message}");
        }

        if (elapsed > step.TimeoutSec)
        {
            if (step.MaxRetries > 0 && step.RetryCount < step.MaxRetries)
            {
                step.RetryCount++;
                stepActionDone = false;
                stepStart = DateTime.UtcNow;
                AddLog($"[AR {(isPreProcessing ? "Pre" : "Post")}-Process] Retrying '{step.Name}' ({step.RetryCount}/{step.MaxRetries})...");
                return;
            }

            LogWarning($"[XASlave] ArPostProcess step '{step.Name}' timed out after {step.TimeoutSec}s, skipping.");
            try { step.OnTimeout?.Invoke(); }
            catch (Exception ex) { log.Error($"[XASlave] ArProcess step '{step.Name}' timeout handler error: {ex.Message}"); }
            AdvanceStep();
        }
    }

    private void AdvanceStep()
    {
        stepIndex++;
        if (stepIndex >= steps.Count) { Finish(); return; }
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        StatusText = steps[stepIndex].Name;
    }

    private void Finish()
    {
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = "Complete";
        ClearArDtr();

        if (isShipExplorationBailout)
        {
            AddLog("[Ship Bailout] Bailout complete.");
            LogInfo("[XASlave] ArShipBailout: Finished bailout flow.");
            if (shipBailoutShouldUnsuppressAtEnd)
            {
                AddLog("[Ship Bailout] Releasing AutoRetainer suppression.");
                plugin.IpcClient.AutoRetainerSetSuppressed(false);
            }
            if (shipBailoutShouldResumeMultiModeAtEnd)
            {
                AddLog("[Ship Bailout] Re-enabling AutoRetainer Multi Mode after reset.");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            }
            ResetShipExplorationBailoutState();
        }
        else if (isPreProcessing)
        {
            CharactersPreProcessed++;
            AddLog($"[AR Pre-Process] Done - un-suppressing AR. (Total pre-processed: {CharactersPreProcessed})");
            LogInfo("[XASlave] ArPreProcess: Finished - un-suppressing AR.");

            // CRITICAL: Un-suppress AR so it can start retainer processing
            plugin.IpcClient.AutoRetainerSetSuppressed(false);
        }
        else
        {
            CharactersProcessed++;
            AddLog($"[AR Post-Process] Done - signaling AR to continue. (Total post-processed: {CharactersProcessed})");
            LogInfo("[XASlave] ArPostProcess: Finished - signaling AR to continue.");

            ReleasePostProcessArSuppression();

            // CRITICAL: Tell AR we're done so it can relog to the next character
            plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
        }
    }

    public bool LogEnabled
    {
        get => plugin.Configuration.ArProcessLogEnabled;
        set
        {
            if (plugin.Configuration.ArProcessLogEnabled == value) return;
            plugin.Configuration.ArProcessLogEnabled = value;
            plugin.Configuration.Save();
        }
    }

    private void LogInfo(string message)
    {
        if (LogEnabled)
            log.Information(message);
    }

    private void LogWarning(string message)
    {
        if (LogEnabled)
            log.Warning(message);
    }

    public void AddLog(string message)
    {
        if (!LogEnabled) return;
        var ts = DateTime.Now.ToString("HH:mm:ss");
        logMessages.Add($"[{ts}] {message}");
        while (logMessages.Count > MaxLogMessages)
            logMessages.RemoveAt(0);
        LogInfo($"[XASlave] ArPostProcess: {message}");
    }

    public void ClearLog() => logMessages.Clear();

    public string GetLogText() => string.Join("\n", logMessages);

    // ------------------------------------------------------------------
    //  DTR Bar - shows XA:Pre-AR or XA:Post-AR during processing
    // ------------------------------------------------------------------

    private void UpdateArDtr()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            dtrEntry.Text = isShipExplorationBailout ? "XA:AR-Bail" : isPreProcessing ? "XA:Pre-AR" : "XA:Post-AR";
            dtrEntry.Shown = true;
        }
        catch { /* DTR bar may not be available */ }
    }

    private void ClearArDtr()
    {
        try
        {
            if (dtrEntry != null)
            {
                // Restore to idle - TaskRunner owns the DTR entry with the same name
                dtrEntry.Text = "XA: Idle";
                dtrEntry.Shown = true;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    //  Helpers (mirrored from AutoCollectionService)
    // ------------------------------------------------------------------
    private bool DelayComplete(float seconds)
    {
        return (float)(DateTime.UtcNow - stepStart).TotalSeconds >= seconds;
    }

    private static string DescribeCadence(int everyHours)
    {
        return everyHours <= 0 ? "Always" : $"{everyHours}hr";
    }

    private void AcquirePostProcessArSuppression()
    {
        postProcessArSuppressedByTask = false;

        try
        {
            if (plugin.IpcClient.AutoRetainerGetSuppressed())
            {
                AddLog("[AR Post-Process] AutoRetainer was already suppressed before post-processing; leaving that state unchanged.");
                return;
            }
        }
        catch
        {
            // If the read fails, still try to acquire suppression explicitly.
        }

        if (plugin.IpcClient.AutoRetainerSetSuppressed(true))
        {
            postProcessArSuppressedByTask = true;
            AddLog("[AR Post-Process] Suppressing AutoRetainer while XA Slave post-processing checkpoints run.");
        }
        else
        {
            AddLog("[AR Post-Process] Failed to set AutoRetainer suppression; continuing under the AR post-process pause.");
        }
    }

    private void ReleasePostProcessArSuppression()
    {
        if (!postProcessArSuppressedByTask)
            return;

        AddLog("[AR Post-Process] Releasing AutoRetainer suppression.");
        plugin.IpcClient.AutoRetainerSetSuppressed(false);
        postProcessArSuppressedByTask = false;
    }

    private unsafe bool IsInFreeCompany()
    {
        try
        {
            var proxy = InfoProxyFreeCompany.Instance();
            return proxy != null && proxy->Id != 0;
        }
        catch { return false; }
    }

    private bool IsOnHomeWorld()
    {
        try
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null) return true;
            return localPlayer.CurrentWorld.RowId == localPlayer.HomeWorld.RowId;
        }
        catch { return true; }
    }

    private unsafe void OpenAgentWindow(AgentId agentId, string addonName)
    {
        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
            if (agent == null) return;
            if (!agent->IsAgentActive())
                agent->Show();
        }
        catch (Exception ex) { log.Error($"[XASlave] ArPostProcess OpenAgentWindow error for {agentId}: {ex.Message}"); }
    }

    private unsafe AtkUnitBase* GetAddon(string name)
    {
        try { return AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(name); }
        catch { return null; }
    }

    private unsafe bool IsAddonReady(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible;
    }

    private unsafe void CloseAddon(string name)
    {
        var addon = GetAddon(name);
        if (addon != null && addon->IsVisible)
        {
            try { addon->Close(true); }
            catch (Exception ex) { log.Warning($"[XASlave] ArPostProcess CloseAddon '{name}' error: {ex.Message}"); }
        }
    }

    private unsafe void ClickAddonNode(string addonName, int nodeListIndex)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible || nodeListIndex >= addon->UldManager.NodeListCount) return;

        var node = addon->UldManager.NodeList[nodeListIndex];
        if (node == null) return;

        try
        {
            var evt = node->AtkEventManager.Event;
            if (evt != null)
                addon->ReceiveEvent((AtkEventType)25, (int)evt->Param, evt);
        }
        catch (Exception ex) { log.Error($"[XASlave] ArPostProcess ClickAddonNode error: {ex.Message}"); }
    }

    private unsafe void FireAddonCallback(string addonName, params int[] callbackValues)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible) return;

        try
        {
            AtkValue* atkValues = stackalloc AtkValue[callbackValues.Length];
            for (int i = 0; i < callbackValues.Length; i++)
            {
                atkValues[i].Type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)3;
                atkValues[i].Int = callbackValues[i];
            }
            addon->FireCallback((uint)callbackValues.Length, atkValues);
        }
        catch (Exception ex) { log.Error($"[XASlave] ArPostProcess FireAddonCallback error: {ex.Message}"); }
    }

    private void ResetShipExplorationBailoutState()
    {
        isShipExplorationBailout = false;
        shipBailoutAddonVisibleSince.Clear();
        shipBailoutShouldUnsuppressAtEnd = false;
        shipBailoutShouldResumeMultiModeAtEnd = false;
    }

    public void Dispose()
    {
        DisarmStartupRecoveryCheck();

        if (registered)
        {
            // Unsubscribe from all events
            if (postProcessIpcSubscribed)
            {
                plugin.IpcClient.AutoRetainerUnsubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
                plugin.IpcClient.AutoRetainerUnsubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);
                postProcessIpcSubscribed = false;
            }
            if (preProcessLoginSubscribed)
            {
                clientState.Login -= OnLogin;
                preProcessLoginSubscribed = false;
            }

            if (shipBailoutFrameworkHooked)
            {
                framework.Update -= OnShipExplorationBailoutCheck;
                shipBailoutFrameworkHooked = false;
            }

            // Cancel pre-process schedule if pending
            CancelPendingPreProcess();
            shipBailoutAddonVisibleSince.Clear();

            // If running, clean up appropriately based on mode
            if (running)
            {
                running = false;
                framework.Update -= OnTick;
                if (isShipExplorationBailout)
                {
                    if (shipBailoutShouldUnsuppressAtEnd)
                        plugin.IpcClient.AutoRetainerSetSuppressed(false);
                    ResetShipExplorationBailoutState();
                }
                else if (isPreProcessing)
                    plugin.IpcClient.AutoRetainerSetSuppressed(false);
                else
                {
                    ReleasePostProcessArSuppression();
                    plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
                }
            }
        }
    }
}
