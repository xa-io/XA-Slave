using System;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

/// <summary>
/// Static helpers for game addon interaction, target interaction, and UI callbacks.
/// Replaces SND-dependent commands like /interact with native Dalamud/FFXIVClientStructs calls.
///
/// Addon callbacks mirror SND's callbackXA("AddonName true 12") format but use
/// AtkUnitBase.FireCallback directly — no SND dependency.
///
/// Target interaction uses TargetSystem.InteractWithObject — equivalent to SND's /interact.
/// </summary>
public static class AddonHelper
{
    // ═══════════════════════════════════════════════════
    //  Target Interaction (replaces /interact from SND)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Interact with the current target.
    /// Equivalent to SND's /interact command.
    /// Uses TargetSystem.InteractWithObject from FFXIVClientStructs.
    /// </summary>
    public static unsafe bool InteractWithTarget()
    {
        try
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
            {
                Plugin.Log.Warning("[XASlave] AddonHelper.InteractWithTarget: TargetSystem is null.");
                return false;
            }

            var target = targetSystem->GetTargetObject();
            if (target == null)
            {
                Plugin.Log.Warning("[XASlave] AddonHelper.InteractWithTarget: No target selected.");
                return false;
            }

            targetSystem->InteractWithObject(target);
            Plugin.Log.Information("[XASlave] AddonHelper.InteractWithTarget: Interacted with target.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.InteractWithTarget failed: {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════
    //  Addon Visibility / Readiness
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Gets a pointer to the named addon, or null if not found.
    /// </summary>
    public static unsafe AtkUnitBase* GetAddon(string name)
    {
        try { return AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(name); }
        catch { return null; }
    }

    /// <summary>
    /// Checks if the named addon exists and is visible.
    /// Equivalent to SND's IsAddonVisible(name).
    /// </summary>
    public static unsafe bool IsAddonVisible(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible;
    }

    /// <summary>
    /// Checks if the named addon exists, is visible, and is ready for interaction.
    /// Equivalent to SND's IsAddonReady(name).
    /// </summary>
    public static unsafe bool IsAddonReady(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    /// <summary>
    /// Closes the named addon if it is visible.
    /// </summary>
    public static unsafe void CloseAddon(string name)
    {
        var addon = GetAddon(name);
        if (addon != null && addon->IsVisible)
        {
            try { addon->Close(true); }
            catch (Exception ex) { Plugin.Log.Warning($"[XASlave] AddonHelper.CloseAddon '{name}' error: {ex.Message}"); }
        }
    }

    // ═══════════════════════════════════════════════════
    //  Addon Callbacks (replaces SND's callbackXA)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Fires a callback on the named addon with integer-only arguments.
    /// Equivalent to SND's callbackXA("AddonName 1 2 3") for int-only callbacks.
    /// </summary>
    public static unsafe bool FireCallback(string addonName, params int[] values)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible) return false;

        try
        {
            AtkValue* atkValues = stackalloc AtkValue[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                atkValues[i].Type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)3; // Int
                atkValues[i].Int = values[i];
            }
            addon->FireCallback((uint)values.Length, atkValues);
            Plugin.Log.Information($"[XASlave] AddonHelper.FireCallback: fired on '{addonName}' with [{string.Join(", ", values)}]");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.FireCallback error on '{addonName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fires a callback on the named addon with a "true" first argument + int second argument.
    /// In SND's callbackXA("AddonName true 12"), "true" is the updateState flag — it tells
    /// SND to close/update the addon after firing. The actual callback values sent are just the
    /// remaining args. For addons that DO need a Bool+Int pair, use this method.
    /// For SelectYesno and similar, use FireCallbackAndClose instead.
    /// </summary>
    public static unsafe bool FireCallbackTrueInt(string addonName, int intArg)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible) return false;

        try
        {
            var atkValues = stackalloc AtkValue[2];
            // Zero-init both values
            atkValues[0] = default;
            atkValues[1] = default;
            // Arg 0: Bool "true"
            atkValues[0].Type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)2; // Bool
            atkValues[0].Int = 1; // true
            // Arg 1: Int value
            atkValues[1].Type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)3; // Int
            atkValues[1].Int = intArg;
            addon->FireCallback(2, atkValues);
            Plugin.Log.Information($"[XASlave] AddonHelper.FireCallbackTrueInt: fired on '{addonName}' with [Bool:true, Int:{intArg}]");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.FireCallbackTrueInt error on '{addonName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fires a callback with integer args and then closes the addon.
    /// This matches SND's callbackXA("AddonName true ...") where "true" means
    /// "update state / close after firing". The actual values sent to the addon
    /// are the int args only.
    /// </summary>
    public static unsafe bool FireCallbackAndClose(string addonName, params int[] values)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible) return false;

        try
        {
            AtkValue* atkValues = stackalloc AtkValue[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                atkValues[i] = default;
                atkValues[i].Type = (FFXIVClientStructs.FFXIV.Component.GUI.ValueType)3; // Int
                atkValues[i].Int = values[i];
            }
            addon->FireCallback((uint)values.Length, atkValues);
            addon->Close(true);
            Plugin.Log.Information($"[XASlave] AddonHelper.FireCallbackAndClose: fired on '{addonName}' with [{string.Join(", ", values)}] + Close");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.FireCallbackAndClose error on '{addonName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clicks a button node in the named addon by its NodeList index.
    /// Uses ReceiveEvent with AtkEventType.ButtonClick (25).
    /// AtkEventManager lives on AtkResNode (which component nodes inherit).
    /// Node indices confirmed via Dalamud /xldata Addon Inspector.
    /// </summary>
    public static unsafe bool ClickAddonButton(string addonName, int nodeListIndex)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonButton: '{addonName}' not visible or null.");
            return false;
        }
        if (nodeListIndex >= addon->UldManager.NodeListCount)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonButton: node {nodeListIndex} out of bounds in '{addonName}' (count: {addon->UldManager.NodeListCount}).");
            return false;
        }

        var node = addon->UldManager.NodeList[nodeListIndex];
        if (node == null)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonButton: node {nodeListIndex} in '{addonName}' is null.");
            return false;
        }

        try
        {
            var evt = node->AtkEventManager.Event;
            if (evt != null)
            {
                addon->ReceiveEvent((AtkEventType)25, (int)evt->Param, evt);
                Plugin.Log.Information($"[XASlave] AddonHelper.ClickAddonButton: clicked node {nodeListIndex} in '{addonName}' (nodeType: {(ushort)node->Type}, param: {evt->Param})");
                return true;
            }
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonButton: node {nodeListIndex} in '{addonName}' has no event (nodeType: {(ushort)node->Type}, NodeListCount: {addon->UldManager.NodeListCount}).");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.ClickAddonButton error on '{addonName}' node {nodeListIndex}: {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════
    //  Complex UI Sequences
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Fires SelectYesno callback to click Yes (index 0) or No (index 1).
    /// Equivalent to SND's callbackXA("SelectYesno true 0") for Yes.
    /// In SND, "true" means close/update addon after callback; the actual
    /// value sent to SelectYesno is just Int:0 (Yes) or Int:1 (No).
    /// </summary>
    public static bool ClickYesNo(bool clickYes)
    {
        return FireCallbackAndClose("SelectYesno", clickYes ? 0 : 1);
    }
}
