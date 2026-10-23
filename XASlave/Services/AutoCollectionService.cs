using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Services.Tasks;

namespace XASlave.Services;

/// <summary>
/// Automates opening game windows and navigating UI to collect data that
/// requires manual interaction (saddlebag, FC members, FC housing info).
/// Uses a step-based state machine running on IFramework.Update.
///
/// After collection completes, calls onFinished which should trigger
/// an IPC save to XA Database.
/// </summary>
public sealed class AutoCollectionService : IDisposable
{
    private readonly Plugin plugin;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private readonly List<TaskStep> steps = new();
    private int stepIndex = -1;
    private DateTime stepStart;
    private bool stepActionDone;
    private bool running;
    private Action? onSave;
    private Action<bool>? onCompletion;
    private readonly List<string> logMessages = new();
    private const int MaxLogMessages = 8000;

    public bool IsRunning => running;
    public string StatusText { get; private set; } = string.Empty;
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public IReadOnlyList<string> LogMessages => logMessages;

    public AutoCollectionService(Plugin plugin, ICondition condition, IFramework framework, IObjectTable objectTable, IPluginLog log)
    {
        this.plugin = plugin;
        this.condition = condition;
        this.framework = framework;
        this.objectTable = objectTable;
        this.log = log;
    }

    public unsafe bool IsInFreeCompany()
    {
        try
        {
            var proxy = InfoProxyFreeCompany.Instance();
            return proxy != null && proxy->Id != 0;
        }
        catch { return false; }
    }

    public bool IsOnHomeWorld()
    {
        try
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null) return true;
            return localPlayer.CurrentWorld.RowId == localPlayer.HomeWorld.RowId;
        }
        catch { return true; }
    }

    public bool IsNormalCondition()
    {
        return CharacterSafetyHelper.IsNormalCondition(condition);
    }

    public void AddLog(string message)
    {
        if (logMessages.Count >= MaxLogMessages)
            logMessages.RemoveAt(0);
        logMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    public void ClearLog()
    {
        logMessages.Clear();
    }

    public void StartCollection(bool doArmouryChest, bool doSaddlebag, bool doJournal, bool doPersonalPlotInfo, bool doFc, Action? onSave = null, Action<bool>? onCompletion = null)
    {
        if (running) return;

        this.onSave = onSave;
        this.onCompletion = onCompletion;
        steps.Clear();
        logMessages.Clear();
        AddLog("XA Database collection started.");

        if (doArmouryChest)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S53649CACF2611F56"),
                OnEnter = () => ChatHelper.SendMessage("/armourychest"),
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SDAC2DC1DADA20909"), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // -- Saddlebag collection --
        if (doSaddlebag)
        {
            steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SA2B7258D99B30DD3"), OnEnter = () => OpenAgentWindow(AgentId.InventoryBuddy, "InventoryBuddy"), IsComplete = () => IsAddonReady("InventoryBuddy"), TimeoutSec = 3f });
            steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S9A0143089ADB4DE4"), IsComplete = () => !IsAddonReady("InventoryBuddy") || DelayComplete(1.0f), TimeoutSec = 2f });
            steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S8A2B93B57C5613AE"), OnEnter = () => CloseAddon("InventoryBuddy"), IsComplete = () => !IsAddonReady("InventoryBuddy"), TimeoutSec = 3f });
            steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S9C0C4DF158C10E1B"), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        if (doJournal)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SF7B51D61398CF038"),
                OnEnter = () =>
                {
                    AddLog("Opening Journal...");
                    ChatHelper.SendMessage("/journal");
                },
                IsComplete = () => IsAddonReady("Journal") || DelayComplete(2.0f),
                TimeoutSec = 3f,
            });
            steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S85732DC4FA199321"), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SE8299921A6AE18A8"),
                ShouldSkip = () => onSave == null || !IsAddonReady("Journal"),
                OnEnter = () =>
                {
                    AddLog("Saving Journal data to XA Database...");
                    onSave?.Invoke();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S1972610E01FD0B9A"),
                ShouldSkip = () => onSave == null || !IsAddonReady("Journal"),
                IsComplete = () => DelayComplete(0.5f),
                TimeoutSec = 1f,
            });
        }

        if (doPersonalPlotInfo)
        {
            steps.AddRange(MonthlyReloggerTask.BuildCollectPersonalPlotInfoSteps(plugin,
                msg => log.Information($"[XASlave] AutoCollection: {msg}")));
        }

        // -- FC collection - requires homeworld first, then FC membership --
        if (doFc)
        {
            if (!IsOnHomeWorld())
            {
                AddLog("Not on home world, skipping FC collection.");
                log.Information("[XASlave] AutoCollection: not on home world, skipping FC steps.");
            }
            else if (!IsInFreeCompany())
            {
                AddLog("Character is not in a Free Company, skipping FC collection.");
                log.Information("[XASlave] AutoCollection: character is not in a Free Company, skipping FC steps.");
            }
            else
            {
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SB062952FD2A35F98"), OnEnter = () => OpenAgentWindow(AgentId.FreeCompany, "FreeCompany"), IsComplete = () => IsAddonReady("FreeCompany"), TimeoutSec = 5f });
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S320C84AE04E84E12"), IsComplete = () => DelayComplete(1.0f), TimeoutSec = 2f });

                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S39F62D53ACFC42ED"), OnEnter = () => { FireAddonCallback("FreeCompany", 1); ClickAddonNode("FreeCompany", 8); }, IsComplete = () => IsAddonReady("FreeCompanyMember") || DelayComplete(3.0f), TimeoutSec = 5f });
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S53027077A76AC9C6"), IsComplete = () => DelayComplete(1.5f), TimeoutSec = 2f });

                // IPC save after members tab - XA Database addon watcher will also trigger on close
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S8DC43600493B2598"), OnEnter = () => onSave?.Invoke(), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 2f });

                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SAB2254CE34BA1273"), OnEnter = () => { FireAddonCallback("FreeCompany", 3); ClickAddonNode("FreeCompany", 4); }, IsComplete = () => IsAddonReady("FreeCompanyStatus") || DelayComplete(3.0f), TimeoutSec = 5f });
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SC66AD98A27551C47"), IsComplete = () => DelayComplete(1.0f), TimeoutSec = 2f });

                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SD1BE8023417A5B04"), OnEnter = () => { if (IsAddonReady("FreeCompanyStatus")) ClickAddonNode("FreeCompanyStatus", 12); }, IsComplete = () => IsAddonReady("HousingSignBoard") || DelayComplete(3.0f), TimeoutSec = 5f });
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S7B2949CEFDFA6F0F"), IsComplete = () => DelayComplete(1.5f), TimeoutSec = 2f });

                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SE732D3BBB184FB6C"), OnEnter = () => { CloseAddon("HousingSignBoard"); CloseAddon("FreeCompanyStatus"); CloseAddon("FreeCompanyMember"); }, IsComplete = () => DelayComplete(0.5f), TimeoutSec = 2f });
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("SDA29C79AF68E9D0E"), OnEnter = () => CloseAddon("FreeCompany"), IsComplete = () => !IsAddonReady("FreeCompany"), TimeoutSec = 5f });
                steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S7B6B3BD41DA48C19"), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            }
        }

        // Final IPC save step
        steps.Add(new TaskStep { NameMessage = global::XASlave.Localization.UiMessage.Key("S8F768F66B7F3A1B2"), OnEnter = () => onSave?.Invoke(), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 2f });

        if (steps.Count == 0)
        {
            AddLog("No collection steps were enabled.");
            onCompletion?.Invoke(true);
            return;
        }

        stepIndex = 0;
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        running = true;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, steps[0].NameMessage);
        framework.Update += OnTick;
        AddLog($"Collection queued with {steps.Count} steps.");
        log.Information($"[XASlave] AutoCollection started with {steps.Count} steps.");
    }

    public void Cancel()
    {
        if (!running) return;
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SD353A99EB4556847"));
        var completion = onCompletion;
        onSave = null;
        onCompletion = null;
        AddLog("Collection cancelled.");
        log.Information("[XASlave] AutoCollection cancelled.");
        completion?.Invoke(false);
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, steps[stepIndex].NameMessage);
        }

        if (!running || stepIndex < 0 || stepIndex >= steps.Count)
        {
            Finish();
            return;
        }

        if (!IsNormalCondition())
        {
            AddLog("Character conditions are no longer normal. Cancelling collection.");
            log.Warning("[XASlave] AutoCollection: conditions no longer normal, cancelling.");
            Cancel();
            return;
        }

        var step = steps[stepIndex];
        var elapsed = (float)(DateTime.UtcNow - stepStart).TotalSeconds;

        if (!stepActionDone)
        {
            AddLog($"STEP START {step.Name}");
            if (step.OnEnter != null)
            {
                try { step.OnEnter(); }
                catch (Exception ex)
                {
                    AddLog($"Action error in '{step.Name}': {ex.Message}");
                    log.Error($"[XASlave] AutoCollection step '{step.Name}' action error: {ex.Message}");
                }
            }
            stepActionDone = true;
        }

        try
        {
            if (step.IsComplete())
            {
                AddLog($"STEP DONE {step.Name}");
                log.Information($"[XASlave] AutoCollection step '{step.Name}' completed.");
                AdvanceStep();
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog($"Check error in '{step.Name}': {ex.Message}");
            log.Error($"[XASlave] AutoCollection step '{step.Name}' check error: {ex.Message}");
        }

        if (elapsed > step.TimeoutSec)
        {
            if (step.MaxRetries > 0 && step.RetryCount < step.MaxRetries)
            {
                step.RetryCount++;
                stepActionDone = false;
                stepStart = DateTime.UtcNow;
                AddLog($"STEP RETRY {step.Name} ({step.RetryCount}/{step.MaxRetries})");
                log.Information($"[XASlave] AutoCollection step '{step.Name}' retrying ({step.RetryCount}/{step.MaxRetries}).");
                return;
            }

            AddLog($"STEP TIMEOUT {step.Name} after {elapsed:0.00}s (limit {step.TimeoutSec:0.##}s)");
            log.Warning($"[XASlave] AutoCollection step '{step.Name}' timed out after {step.TimeoutSec}s, skipping.");
            try { step.OnTimeout?.Invoke(); }
            catch (Exception ex) { log.Error($"[XASlave] AutoCollection step '{step.Name}' timeout handler error: {ex.Message}"); }
            AdvanceStep();
        }
    }

    private void AdvanceStep()
    {
        stepIndex++;
        if (stepIndex >= steps.Count) { Finish(); return; }
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, steps[stepIndex].NameMessage);
    }

    private void Finish()
    {
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S143B270A32602D41"));
        var completion = onCompletion;
        onSave = null;
        onCompletion = null;
        AddLog("Collection finished.");
        log.Information("[XASlave] AutoCollection finished.");
        completion?.Invoke(true);
    }

    private bool DelayComplete(float seconds)
    {
        return (float)(DateTime.UtcNow - stepStart).TotalSeconds >= seconds;
    }

    private unsafe void OpenAgentWindow(AgentId agentId, string addonName)
    {
        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
            if (agent == null) { log.Warning($"[XASlave] OpenAgentWindow: agent {agentId} not found."); return; }
            if (!agent->IsAgentActive())
            {
                agent->Show();
                log.Information($"[XASlave] OpenAgentWindow: opened {addonName} via agent {agentId}.");
            }
        }
        catch (Exception ex) { log.Error($"[XASlave] OpenAgentWindow error for {agentId}: {ex.Message}"); }
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
            catch (Exception ex) { log.Warning($"[XASlave] CloseAddon '{name}' error: {ex.Message}"); }
        }
    }

    private unsafe void ClickAddonNode(string addonName, int nodeListIndex)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible ||
            !NativeArrayAccess.TryGetNode(&addon->UldManager, nodeListIndex, out var node)) return;

        try
        {
            var evt = node->AtkEventManager.Event;
            if (evt != null)
            {
                addon->ReceiveEvent((AtkEventType)25, (int)evt->Param, evt);
                log.Information($"[XASlave] ClickAddonNode: clicked node {nodeListIndex} in '{addonName}' (param: {evt->Param}).");
            }
        }
        catch (Exception ex) { log.Error($"[XASlave] ClickAddonNode error: {ex.Message}"); }
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
                atkValues[i] = default;
                atkValues[i].Type = AtkValueType.Int;
                atkValues[i].Int = callbackValues[i];
            }
            addon->FireCallback((uint)callbackValues.Length, atkValues);
            log.Information($"[XASlave] FireAddonCallback: fired on '{addonName}' with values [{string.Join(", ", callbackValues)}].");
        }
        catch (Exception ex) { log.Error($"[XASlave] FireAddonCallback error: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (running)
        {
            running = false;
            framework.Update -= OnTick;
        }
    }
}
