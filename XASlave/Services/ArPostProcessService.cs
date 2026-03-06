using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

/// <summary>
/// Handles both pre-processing and post-processing for AutoRetainer multi-mode.
///
/// PRE-PROCESSING (before AR processes retainers):
///   Uses the Suppressed pattern — on character login, suppress AR, run collection
///   steps (inventory, saddlebag, FC window, XA Database save), then un-suppress.
///   AR waits while suppressed and starts retainer processing after un-suppress.
///
/// POST-PROCESSING (after AR finishes retainers, before relog):
///   Two-phase subscription pattern (AR clears the postprocess list between characters):
///   1. User enables → subscribe to OnCharacterAdditionalTask + OnCharacterReadyForPostprocess
///   2. AR fires OnCharacterAdditionalTask — we call RequestCharacterPostprocess("XASlave")
///   3. AR fires OnCharacterReadyForPostprocess("XASlave") — we run collection steps
///   4. When done → calls FinishCharacterPostprocessRequest so AR can continue
///
/// Uses the same step-based state machine pattern as AutoCollectionService.
/// </summary>
public sealed class ArPostProcessService : IDisposable
{
    private const string PluginName = "XASlave";

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

    // Pre-processing: scheduled on login, runs after delay
    private DateTime? preProcessScheduledAt;
    private bool preProcessFrameworkHooked;

    public bool IsRunning => running;
    public bool IsRegistered => registered;
    public string StatusText { get; private set; } = string.Empty;

    // Log messages for UI display
    private readonly List<string> logMessages = new();
    public IReadOnlyList<string> LogMessages => logMessages;
    private const int MaxLogMessages = 100;
    public int CharactersProcessed { get; private set; }
    public int CharactersPreProcessed { get; private set; }

    private class PostProcessStep
    {
        public string Name { get; init; } = string.Empty;
        public Action? OnEnter { get; init; }
        public Func<bool> IsComplete { get; init; } = () => true;
        public float TimeoutSec { get; init; } = 5f;
    }

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

        // If either pre or post processing is enabled, register on construction
        if (plugin.Configuration.ArPostProcessEnabled || plugin.Configuration.ArPreProcessEnabled)
            Register();
    }

    /// <summary>Subscribe to AR events for pre/post processing.
    /// Post: OnCharacterAdditionalTask + OnCharacterReadyForPostprocess (two-phase AR hook).
    /// Pre: ClientState.Login → suppress AR → run steps → un-suppress.</summary>
    public void Register()
    {
        if (registered) return;

        // Post-processing: subscribe to AR IPC events
        plugin.IpcClient.AutoRetainerSubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
        plugin.IpcClient.AutoRetainerSubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);

        // Pre-processing: subscribe to Login event
        clientState.Login += OnLogin;

        registered = true;

        var modes = new List<string>();
        if (plugin.Configuration.ArPreProcessEnabled) modes.Add("Pre");
        if (plugin.Configuration.ArPostProcessEnabled) modes.Add("Post");
        var modeStr = modes.Count > 0 ? string.Join("+", modes) : "None active";
        AddLog($"[AR Processing] Subscribed to events ({modeStr}).");
        log.Information($"[XASlave] ArProcessing: Registered — {modeStr}.");
    }

    /// <summary>Unsubscribe from all AR events.</summary>
    public void Unregister()
    {
        if (!registered) return;

        // Unsubscribe from post-processing AR events
        plugin.IpcClient.AutoRetainerUnsubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
        plugin.IpcClient.AutoRetainerUnsubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);

        // Unsubscribe from Login
        clientState.Login -= OnLogin;

        // Cancel pre-process schedule if pending
        preProcessScheduledAt = null;
        if (preProcessFrameworkHooked)
        {
            framework.Update -= OnPreProcessCheck;
            preProcessFrameworkHooked = false;
        }

        registered = false;
        AddLog("[AR Processing] Unsubscribed from all events.");
        log.Information("[XASlave] ArProcessing: Unsubscribed from all events.");

        // If currently running, clean up appropriately
        if (running)
        {
            var wasPreProcess = isPreProcessing;
            Cancel();
            if (wasPreProcess)
                plugin.IpcClient.AutoRetainerSetSuppressed(false);
            else
                plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
        }
    }

    /// <summary>Called by AR per-character BEFORE checking the postprocess list.
    /// This is the signal for plugins to call RequestCharacterPostProcess to get into the list.</summary>
    private void OnCharacterAdditionalTask()
    {
        log.Information("[XASlave] ArPostProcess: AR fired OnCharacterAdditionalTask — registering for this character.");
        AddLog("[AR Post-Process] AR signaled — registering for this character's post-processing.");

        var success = plugin.IpcClient.AutoRetainerRequestCharacterPostProcess(PluginName);
        if (success)
        {
            log.Information("[XASlave] ArPostProcess: Successfully registered XASlave for this character.");
        }
        else
        {
            log.Error("[XASlave] ArPostProcess: Failed to register for this character's post-processing.");
            AddLog("[AR Post-Process] Failed to register for this character (AR may have rejected).");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Pre-Processing — Login handler + Suppressed pattern
    // ═══════════════════════════════════════════════════

    /// <summary>Called when any character logs in. Schedules pre-processing if enabled and AR multi-mode is active.</summary>
    private void OnLogin()
    {
        if (!plugin.Configuration.ArPreProcessEnabled) return;
        if (running) return; // Already running something

        // Only run pre-processing if AR multi-mode is enabled
        if (!plugin.IpcClient.AutoRetainerGetMultiModeEnabled())
        {
            log.Information("[XASlave] ArPreProcess: Login detected but AR multi-mode not enabled — skipping.");
            return;
        }

        // Suppress AR immediately so it doesn't start retainer processing
        plugin.IpcClient.AutoRetainerSetSuppressed(true);
        AddLog("[AR Pre-Process] Login detected — AR suppressed, waiting for player to load...");
        log.Information("[XASlave] ArPreProcess: Login detected, AR suppressed. Scheduling pre-processing.");

        // Schedule pre-processing with a delay (player needs time to fully load)
        preProcessScheduledAt = DateTime.UtcNow;
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
        if (elapsed < plugin.Configuration.ArPreProcessLoginDelay) return;

        // Ready to start pre-processing
        preProcessScheduledAt = null;
        framework.Update -= OnPreProcessCheck;
        preProcessFrameworkHooked = false;

        var charName = "Unknown";
        try
        {
            var lp = objectTable.LocalPlayer;
            if (lp != null) charName = lp.Name.ToString();
        }
        catch { /* ignore */ }

        AddLog($"[AR Pre-Process] Starting pre-processing for {charName}...");
        log.Information($"[XASlave] ArPreProcess: Starting pre-processing for {charName}.");

        isPreProcessing = true;
        BuildPreProcessSteps();
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

        // FC Window — full processing
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
                        AddLog("Not on home world — skipping FC collection.");
                        skipFc = true;
                    }
                    else if (!IsInFreeCompany())
                    {
                        AddLog("Not in a Free Company — skipping FC collection.");
                        skipFc = true;
                    }
                    else
                    {
                        AddLog("On home world and in FC — collecting FC data...");
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
                    if (plugin.IpcClient.Save())
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

    // ═══════════════════════════════════════════════════
    //  Post-Processing — AR IPC hooks
    // ═══════════════════════════════════════════════════

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

        log.Information($"[XASlave] ArPostProcess: AR signaled character ready — {charName}");
        AddLog($"[AR Post-Process] Character ready: {charName}");

        isPreProcessing = false;
        BuildPostProcessSteps();

        if (steps.Count == 0)
        {
            AddLog("[AR Post-Process] No steps configured — signaling AR to continue.");
            plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
            return;
        }

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

        // FC Window — full processing (Members, Info, Housing)
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
                        AddLog("Not on home world — skipping FC collection.");
                        skipFc = true;
                    }
                    else if (!IsInFreeCompany())
                    {
                        AddLog("Not in a Free Company — skipping FC collection.");
                        skipFc = true;
                    }
                    else
                    {
                        AddLog("On home world and in FC — collecting FC data...");
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

        // Save to XA Database
        if (config.ArPostProcessSaveToXaDatabase)
        {
            steps.Add(new PostProcessStep
            {
                Name = "Save to XA Database",
                OnEnter = () =>
                {
                    AddLog("Saving to XA Database...");
                    if (plugin.IpcClient.Save())
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

    // ═══════════════════════════════════════════════════
    //  Shared Step Machine
    // ═══════════════════════════════════════════════════

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
        AddLog($"[AR {(isPreProcessing ? "Pre" : "Post")}-Process] Starting {steps.Count} steps — {label}");
    }

    public void Cancel()
    {
        if (!running) return;
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = "Cancelled";
        ClearArDtr();
        var mode = isPreProcessing ? "Pre" : "Post";
        AddLog($"[AR {mode}-Process] Cancelled.");
        log.Information($"[XASlave] Ar{mode}Process: Cancelled.");
    }

    private void OnTick(IFramework fw)
    {
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
            log.Warning($"[XASlave] ArPostProcess step '{step.Name}' timed out after {step.TimeoutSec}s, skipping.");
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

        if (isPreProcessing)
        {
            CharactersPreProcessed++;
            AddLog($"[AR Pre-Process] Done — un-suppressing AR. (Total pre-processed: {CharactersPreProcessed})");
            log.Information("[XASlave] ArPreProcess: Finished — un-suppressing AR.");

            // CRITICAL: Un-suppress AR so it can start retainer processing
            plugin.IpcClient.AutoRetainerSetSuppressed(false);
        }
        else
        {
            CharactersProcessed++;
            AddLog($"[AR Post-Process] Done — signaling AR to continue. (Total post-processed: {CharactersProcessed})");
            log.Information("[XASlave] ArPostProcess: Finished — signaling AR to continue.");

            // CRITICAL: Tell AR we're done so it can relog to the next character
            plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
        }
    }

    public bool LogEnabled { get; set; } = true;

    public void AddLog(string message)
    {
        if (!LogEnabled) return;
        var ts = DateTime.Now.ToString("HH:mm:ss");
        logMessages.Add($"[{ts}] {message}");
        while (logMessages.Count > MaxLogMessages)
            logMessages.RemoveAt(0);
        log.Information($"[XASlave] ArPostProcess: {message}");
    }

    public void ClearLog() => logMessages.Clear();

    public string GetLogText() => string.Join("\n", logMessages);

    // ═══════════════════════════════════════════════════
    //  DTR Bar — shows XA:Pre-AR or XA:Post-AR during processing
    // ═══════════════════════════════════════════════════

    private void UpdateArDtr()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            dtrEntry.Text = isPreProcessing ? "XA:Pre-AR" : "XA:Post-AR";
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
                // Restore to idle — TaskRunner owns the DTR entry with the same name
                dtrEntry.Text = "XA: Idle";
                dtrEntry.Shown = true;
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  Helpers (mirrored from AutoCollectionService)
    // ═══════════════════════════════════════════════════

    private bool DelayComplete(float seconds)
    {
        return (float)(DateTime.UtcNow - stepStart).TotalSeconds >= seconds;
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

    public void Dispose()
    {
        if (registered)
        {
            // Unsubscribe from all events
            plugin.IpcClient.AutoRetainerUnsubscribeCharacterAdditionalTask(OnCharacterAdditionalTask);
            plugin.IpcClient.AutoRetainerUnsubscribeCharacterPostProcess(OnCharacterReadyForPostprocess);
            clientState.Login -= OnLogin;

            // Cancel pre-process schedule if pending
            if (preProcessFrameworkHooked)
            {
                framework.Update -= OnPreProcessCheck;
                preProcessFrameworkHooked = false;
            }

            // If running, clean up appropriately based on mode
            if (running)
            {
                running = false;
                framework.Update -= OnTick;
                if (isPreProcessing)
                    plugin.IpcClient.AutoRetainerSetSuppressed(false);
                else
                    plugin.IpcClient.AutoRetainerFinishCharacterPostProcess();
            }
        }
    }
}
