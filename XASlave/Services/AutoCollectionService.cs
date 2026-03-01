using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private readonly List<CollectionStep> steps = new();
    private int stepIndex = -1;
    private DateTime stepStart;
    private bool stepActionDone;
    private bool running;
    private Action? onFinished;

    public bool IsRunning => running;
    public string StatusText { get; private set; } = string.Empty;

    private class CollectionStep
    {
        public string Name { get; init; } = string.Empty;
        public Action? OnEnter { get; init; }
        public Func<bool> IsComplete { get; init; } = () => true;
        public float TimeoutSec { get; init; } = 5f;
    }

    public AutoCollectionService(ICondition condition, IFramework framework, IObjectTable objectTable, IPluginLog log)
    {
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
        return !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.BoundByDuty]
            && !condition[ConditionFlag.WatchingCutscene]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent]
            && !condition[ConditionFlag.Occupied]
            && !condition[ConditionFlag.Occupied30]
            && !condition[ConditionFlag.Occupied33]
            && !condition[ConditionFlag.Occupied38]
            && !condition[ConditionFlag.Occupied39]
            && !condition[ConditionFlag.OccupiedInEvent]
            && !condition[ConditionFlag.OccupiedInQuestEvent]
            && !condition[ConditionFlag.OccupiedSummoningBell]
            && !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51];
    }

    public void StartCollection(bool doSaddlebag, bool doFc, Action? onFinished = null)
    {
        if (running) return;

        this.onFinished = onFinished;
        steps.Clear();

        // ── Saddlebag collection ──
        if (doSaddlebag)
        {
            steps.Add(new CollectionStep { Name = "Open Saddlebag", OnEnter = () => OpenAgentWindow(AgentId.InventoryBuddy, "InventoryBuddy"), IsComplete = () => IsAddonReady("InventoryBuddy"), TimeoutSec = 3f });
            steps.Add(new CollectionStep { Name = "Read Saddlebag", IsComplete = () => !IsAddonReady("InventoryBuddy") || DelayComplete(1.0f), TimeoutSec = 2f });
            steps.Add(new CollectionStep { Name = "Close Saddlebag", OnEnter = () => CloseAddon("InventoryBuddy"), IsComplete = () => !IsAddonReady("InventoryBuddy"), TimeoutSec = 3f });
            steps.Add(new CollectionStep { Name = "Saddlebag Cooldown", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
        }

        // ── FC collection — requires homeworld first, then FC membership ──
        if (doFc)
        {
            if (!IsOnHomeWorld())
            {
                log.Information("[XASlave] AutoCollection: not on home world, skipping FC steps.");
            }
            else if (!IsInFreeCompany())
            {
                log.Information("[XASlave] AutoCollection: character is not in a Free Company, skipping FC steps.");
            }
            else
            {
                steps.Add(new CollectionStep { Name = "Open FC Window", OnEnter = () => OpenAgentWindow(AgentId.FreeCompany, "FreeCompany"), IsComplete = () => IsAddonReady("FreeCompany"), TimeoutSec = 5f });
                steps.Add(new CollectionStep { Name = "FC Load Delay", IsComplete = () => DelayComplete(1.0f), TimeoutSec = 2f });

                steps.Add(new CollectionStep { Name = "Click Members Tab", OnEnter = () => { FireAddonCallback("FreeCompany", 1); ClickAddonNode("FreeCompany", 8); }, IsComplete = () => IsAddonReady("FreeCompanyMember") || DelayComplete(3.0f), TimeoutSec = 5f });
                steps.Add(new CollectionStep { Name = "Members Load Delay", IsComplete = () => DelayComplete(1.5f), TimeoutSec = 2f });

                // IPC save after members tab — XA Database addon watcher will also trigger on close
                steps.Add(new CollectionStep { Name = "Save Members", OnEnter = () => onFinished?.Invoke(), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 2f });

                steps.Add(new CollectionStep { Name = "Click Info Tab", OnEnter = () => { FireAddonCallback("FreeCompany", 3); ClickAddonNode("FreeCompany", 4); }, IsComplete = () => IsAddonReady("FreeCompanyStatus") || DelayComplete(3.0f), TimeoutSec = 5f });
                steps.Add(new CollectionStep { Name = "Status Load Delay", IsComplete = () => DelayComplete(1.0f), TimeoutSec = 2f });

                steps.Add(new CollectionStep { Name = "Click Housing Search", OnEnter = () => { if (IsAddonReady("FreeCompanyStatus")) ClickAddonNode("FreeCompanyStatus", 12); }, IsComplete = () => IsAddonReady("HousingSignBoard") || DelayComplete(3.0f), TimeoutSec = 5f });
                steps.Add(new CollectionStep { Name = "Housing Load Delay", IsComplete = () => DelayComplete(1.5f), TimeoutSec = 2f });

                steps.Add(new CollectionStep { Name = "Close Sub-Addons", OnEnter = () => { CloseAddon("HousingSignBoard"); CloseAddon("FreeCompanyStatus"); CloseAddon("FreeCompanyMember"); }, IsComplete = () => DelayComplete(0.5f), TimeoutSec = 2f });
                steps.Add(new CollectionStep { Name = "Close FC Window", OnEnter = () => CloseAddon("FreeCompany"), IsComplete = () => !IsAddonReady("FreeCompany"), TimeoutSec = 5f });
                steps.Add(new CollectionStep { Name = "FC Cooldown", IsComplete = () => DelayComplete(0.5f), TimeoutSec = 1f });
            }
        }

        // Final IPC save step
        steps.Add(new CollectionStep { Name = "Final Save", OnEnter = () => onFinished?.Invoke(), IsComplete = () => DelayComplete(0.5f), TimeoutSec = 2f });

        if (steps.Count == 0)
        {
            onFinished?.Invoke();
            return;
        }

        stepIndex = 0;
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        running = true;
        StatusText = steps[0].Name;
        framework.Update += OnTick;
        log.Information($"[XASlave] AutoCollection started with {steps.Count} steps.");
    }

    public void Cancel()
    {
        if (!running) return;
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = "Cancelled";
        log.Information("[XASlave] AutoCollection cancelled.");
    }

    private void OnTick(IFramework fw)
    {
        if (!running || stepIndex < 0 || stepIndex >= steps.Count)
        {
            Finish();
            return;
        }

        if (!IsNormalCondition())
        {
            log.Warning("[XASlave] AutoCollection: conditions no longer normal, cancelling.");
            Cancel();
            return;
        }

        var step = steps[stepIndex];
        var elapsed = (float)(DateTime.UtcNow - stepStart).TotalSeconds;

        if (!stepActionDone)
        {
            if (step.OnEnter != null)
            {
                try { step.OnEnter(); }
                catch (Exception ex) { log.Error($"[XASlave] AutoCollection step '{step.Name}' action error: {ex.Message}"); }
            }
            stepActionDone = true;
        }

        try
        {
            if (step.IsComplete())
            {
                log.Information($"[XASlave] AutoCollection step '{step.Name}' completed.");
                AdvanceStep();
                return;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[XASlave] AutoCollection step '{step.Name}' check error: {ex.Message}");
        }

        if (elapsed > step.TimeoutSec)
        {
            log.Warning($"[XASlave] AutoCollection step '{step.Name}' timed out after {step.TimeoutSec}s, skipping.");
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
        log.Information("[XASlave] AutoCollection finished.");
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
        if (addon == null || !addon->IsVisible || nodeListIndex >= addon->UldManager.NodeListCount) return;

        var node = addon->UldManager.NodeList[nodeListIndex];
        if (node == null) return;

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
                atkValues[i].Type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)3;
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
