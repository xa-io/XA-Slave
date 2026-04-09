using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
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

    public static unsafe List<string> GetAddonTextEntries(string addonName)
    {
        var results = new List<string>();
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
            return results;

        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null)
                CollectText(node, results, 0);
        }

        return results;
    }

    public static unsafe int GetAddonTextEntryCount(string addonName)
    {
        return GetAddonTextEntries(addonName).Count;
    }

    public static unsafe bool AddonHasText(string addonName, string text, bool contains = false)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
            return false;

        return FindMatchingTextNode(addon, text, contains) != null;
    }

    public static unsafe string GetFirstAddonText(string addonName, string text, bool contains = true)
    {
        var entries = GetAddonTextEntries(addonName);
        foreach (var entry in entries)
        {
            if (contains)
            {
                if (entry.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            else if (entry.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return string.Empty;
    }

    public static unsafe bool ClickAddonText(string addonName, string text, bool contains = false)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonText: '{addonName}' not visible or null.");
            return false;
        }

        var matchedNode = FindMatchingTextNode(addon, text, contains);
        if (matchedNode == null)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonText: text '{text}' not found in '{addonName}'.");
            return false;
        }

        var clickableNode = FindClickableNode(matchedNode);
        if (clickableNode == null)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonText: no clickable ancestor found for text '{text}' in '{addonName}'.");
            return false;
        }

        try
        {
            var evt = clickableNode->AtkEventManager.Event;
            if (evt == null)
            {
                Plugin.Log.Warning($"[XASlave] AddonHelper.ClickAddonText: clickable ancestor for '{text}' in '{addonName}' has no event.");
                return false;
            }

            addon->ReceiveEvent((AtkEventType)25, (int)evt->Param, evt);
            Plugin.Log.Information($"[XASlave] AddonHelper.ClickAddonText: clicked text '{text}' in '{addonName}' (contains={contains}, param: {evt->Param})");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.ClickAddonText error on '{addonName}' text '{text}': {ex.Message}");
            return false;
        }
    }

    public static unsafe int GetAddonListTextCallbackIndex(string addonName, string text, bool contains = false)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
            return -1;

        return TryResolveAddonListSelection(addon, text, contains, out var callbackIndex, out _, out _)
            ? callbackIndex
            : -1;
    }

    public static unsafe bool SelectAddonListText(string addonName, string text, bool contains = false)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.SelectAddonListText: '{addonName}' not visible or null.");
            return false;
        }

        if (!TryResolveAddonListSelection(addon, text, contains, out var callbackIndex, out var path, out var matchedText))
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.SelectAddonListText: text '{text}' not resolved in '{addonName}'.");
            return false;
        }

        var ok = FireCallbackAndClose(addonName, callbackIndex);
        if (ok)
            Plugin.Log.Information($"[XASlave] AddonHelper.SelectAddonListText: selected text '{matchedText}' in '{addonName}' via callback index {callbackIndex} (path: {path}, contains={contains})");
        return ok;
    }

    public static unsafe bool SelectFirstAddonListText(string addonName, out int callbackIndex, out string matchedText, params (string Text, bool Contains)[] candidates)
    {
        callbackIndex = -1;
        matchedText = string.Empty;

        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.SelectFirstAddonListText: '{addonName}' not visible or null.");
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (!TryResolveAddonListSelection(addon, candidate.Text, candidate.Contains, out callbackIndex, out var path, out matchedText))
                continue;

            var ok = FireCallbackAndClose(addonName, callbackIndex);
            if (ok)
                Plugin.Log.Information($"[XASlave] AddonHelper.SelectFirstAddonListText: selected text '{matchedText}' in '{addonName}' via callback index {callbackIndex} (path: {path}, candidate='{candidate.Text}', contains={candidate.Contains})");
            return ok;
        }

        Plugin.Log.Warning($"[XASlave] AddonHelper.SelectFirstAddonListText: none of the candidate texts [{FormatAddonTextCandidates(candidates)}] resolved in '{addonName}'.");
        return false;
    }

    private static string FormatAddonTextCandidates((string Text, bool Contains)[] candidates)
    {
        if (candidates == null || candidates.Length == 0)
            return string.Empty;

        var parts = new string[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            parts[i] = candidates[i].Contains ? $"{candidates[i].Text} (contains)" : candidates[i].Text;

        return string.Join(", ", parts);
    }

    public static string GetHousingMenuVariant()
    {
        var count = GetAddonTextEntryCount("HousingMenu");
        return count switch
        {
            2 => "HousingMenuWorkshop",
            3 => "HousingMenuLobby",
            8 => "HousingMenuApartment",
            9 => "HousingMenuHouse",
            10 => "HousingMenuMain",
            _ => count > 0 ? $"HousingMenu({count})" : string.Empty,
        };
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

    private static unsafe void CollectText(AtkResNode* node, List<string> results, int depth)
    {
        if (node == null || depth > 6)
            return;

        if (node->Type == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add(text.Trim());
        }
        else if (node->Type == NodeType.Counter)
        {
            var text = ((AtkCounterNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add(text.Trim());
        }

        if ((int)node->Type < 1000)
            return;

        var compNode = (AtkComponentNode*)node;
        if (compNode->Component == null)
            return;

        var childCount = compNode->Component->UldManager.NodeListCount;
        for (var i = 0; i < childCount; i++)
        {
            var child = compNode->Component->UldManager.NodeList[i];
            if (child != null)
                CollectText(child, results, depth + 1);
        }
    }

    private static unsafe void CollectTextEntries(AtkResNode* node, string path, List<(string Path, string Text)> results, int depth)
    {
        if (node == null || depth > 6)
            return;

        if (node->Type == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add((path, text.Trim()));
        }
        else if (node->Type == NodeType.Counter)
        {
            var text = ((AtkCounterNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add((path, text.Trim()));
        }

        if ((int)node->Type < 1000)
            return;

        var compNode = (AtkComponentNode*)node;
        if (compNode->Component == null)
            return;

        var childCount = compNode->Component->UldManager.NodeListCount;
        for (var i = 0; i < childCount; i++)
        {
            var child = compNode->Component->UldManager.NodeList[i];
            if (child != null)
                CollectTextEntries(child, $"{path}->[{i}]", results, depth + 1);
        }
    }

    private static unsafe bool TryResolveAddonListSelection(AtkUnitBase* addon, string text, bool contains, out int callbackIndex, out string path, out string matchedText)
    {
        callbackIndex = -1;
        path = string.Empty;
        matchedText = string.Empty;

        var entries = new List<(string Path, string Text)>();
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null)
                CollectTextEntries(node, $"[{i}]", entries, 0);
        }

        foreach (var entry in entries)
        {
            if (!TextMatches(entry.Text, text, contains))
                continue;

            if (!TryGetListItemCallbackIndex(entry.Path, out callbackIndex))
                continue;

            path = entry.Path;
            matchedText = entry.Text;
            return true;
        }

        return false;
    }

    private static bool TextMatches(string entryText, string text, bool contains)
    {
        if (string.IsNullOrWhiteSpace(entryText))
            return false;

        if (contains)
            return entryText.Contains(text, StringComparison.OrdinalIgnoreCase);

        return entryText.Equals(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetListItemCallbackIndex(string path, out int callbackIndex)
    {
        callbackIndex = -1;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split("->", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        if (!TryParsePathIndex(segments[1], out var visibleRowIndex) || visibleRowIndex <= 0)
            return false;

        callbackIndex = visibleRowIndex - 1;
        return true;
    }

    private static bool TryParsePathIndex(string segment, out int index)
    {
        index = -1;

        if (string.IsNullOrWhiteSpace(segment) || segment.Length < 3 || segment[0] != '[' || segment[segment.Length - 1] != ']')
            return false;

        return int.TryParse(segment.Substring(1, segment.Length - 2), out index);
    }

    private static unsafe AtkResNode* FindMatchingTextNode(AtkUnitBase* addon, string text, bool contains)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null)
                continue;

            var match = FindMatchingTextNode(node, text, contains, 0);
            if (match != null)
                return match;
        }

        return null;
    }

    private static unsafe AtkResNode* FindMatchingTextNode(AtkResNode* node, string text, bool contains, int depth)
    {
        if (node == null || depth > 6)
            return null;

        if (node->Type == NodeType.Text)
        {
            var nodeText = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(nodeText))
            {
                if (contains)
                {
                    if (nodeText.Contains(text, StringComparison.OrdinalIgnoreCase))
                        return node;
                }
                else if (nodeText.Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
        }
        else if (node->Type == NodeType.Counter)
        {
            var nodeText = ((AtkCounterNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(nodeText))
            {
                if (contains)
                {
                    if (nodeText.Contains(text, StringComparison.OrdinalIgnoreCase))
                        return node;
                }
                else if (nodeText.Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
        }

        if ((int)node->Type < 1000)
            return null;

        var compNode = (AtkComponentNode*)node;
        if (compNode->Component == null)
            return null;

        var childCount = compNode->Component->UldManager.NodeListCount;
        for (var i = 0; i < childCount; i++)
        {
            var child = compNode->Component->UldManager.NodeList[i];
            if (child == null)
                continue;

            var match = FindMatchingTextNode(child, text, contains, depth + 1);
            if (match != null)
                return match;
        }

        return null;
    }

    private static unsafe AtkResNode* FindClickableNode(AtkResNode* node)
    {
        var current = node;
        while (current != null)
        {
            if (current->AtkEventManager.Event != null)
                return current;

            current = current->ParentNode;
        }

        return null;
    }

    // ═══════════════════════════════════════════════════
    //  Complex UI Sequences
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Fires SelectYesno callback to click Yes (index 0) or No (index 1).
    /// When closeAfter is true, also closes the addon after firing to match the older XA helper behavior.
    /// Some flows such as Expert Delivery need the raw callback without a forced close.
    /// </summary>
    public static unsafe bool ClickYesNo(bool clickYes, bool closeAfter)
    {
        var addon = (AddonSelectYesno*)GetAddon("SelectYesno");
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return false;

        try
        {
            addon->AtkUnitBase.FireCallbackInt(clickYes ? 0 : 1);
            if (closeAfter)
                addon->AtkUnitBase.Close(true);

            Plugin.Log.Information($"[XASlave] AddonHelper.ClickYesNo: fired on 'SelectYesno' with [{(clickYes ? 0 : 1)}]{(closeAfter ? " + Close" : string.Empty)}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] AddonHelper.ClickYesNo error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fires SelectYesno callback to click Yes (index 0) or No (index 1) and closes the addon afterward.
    /// Kept for existing XA flows that still expect the old helper behavior.
    /// </summary>
    public static bool ClickYesNo(bool clickYes)
    {
        return ClickYesNo(clickYes, true);
    }
}
