using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XASlave.Services;

/// <summary>
/// Static helpers for game addon interaction, target interaction, and UI callbacks.
/// Replaces SND-dependent commands like /interact with native Dalamud/FFXIVClientStructs calls.
///
/// Addon callbacks mirror SND's callbackXA("AddonName true 12") format but use
/// AtkUnitBase.FireCallback directly - no SND dependency.
///
/// Target interaction uses TargetSystem.InteractWithObject - equivalent to SND's /interact.
/// </summary>
public static class AddonHelper
{
    public const string TextErrorAddonName = "_TextError";
    public const string CannotSeeTargetText = "Cannot see target";
    public const string ContentsFinderConfirmAddonName = "ContentsFinderConfirm";

    private const int ContentsFinderConfirmCommenceCallback = 8;
    private const int ContentsFinderConfirmCommenceNodeListIndex = 7;

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
    //  Targeting / Pathing / Addon Visibility
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Targets the named object through the native game command path.
    /// </summary>
    public static void TargetByName(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return;

        if (TryTargetByName(targetName, out _))
            return;

        ChatHelper.SendMessage($"/target \"{targetName}\"");
    }

    /// <summary>
    /// Targets the closest targetable actor whose visible name contains the requested text.
    /// Mirrors SimpleTweaks' target-fix behavior without waiting for the game command to fail.
    /// </summary>
    public static unsafe bool TryTargetByName(string targetName, out string matchedName)
    {
        matchedName = string.Empty;
        var searchName = targetName.Trim();
        if (string.IsNullOrWhiteSpace(searchName))
            return false;

        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer == null)
                return false;

            IGameObject? closestMatch = null;
            var closestDistance = float.MaxValue;
            foreach (var actor in Plugin.ObjectTable)
            {
                var actorName = actor.Name.TextValue;
                if (string.IsNullOrWhiteSpace(actorName)
                    || !actorName.Contains(searchName, StringComparison.OrdinalIgnoreCase)
                    || !IsTargetable(actor))
                {
                    continue;
                }

                var distance = Vector3.Distance(localPlayer.Position, actor.Position);
                if (closestMatch != null && distance >= closestDistance)
                    continue;

                closestMatch = actor;
                closestDistance = distance;
            }

            if (closestMatch == null)
                return false;

            Plugin.TargetManager.Target = closestMatch;
            matchedName = closestMatch.Name.TextValue;
            Plugin.Log.Debug("[XASlave] AddonHelper.TryTargetByName selected {MatchedName} for {SearchName}.", matchedName, searchName);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] AddonHelper.TryTargetByName failed.");
            return false;
        }
    }

    /// <summary>
    /// Returns true when the current target name matches the requested value.
    /// </summary>
    public static bool CurrentTargetMatches(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return false;

        var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
        return target != null && target.Name.ToString().Equals(targetName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the current territory place-name shown by the game's zone lookup.
    /// Mirrors the debug GetZoneName path that resolves ClientState.TerritoryType via TerritoryType.PlaceName.
    /// </summary>
    public static string GetCurrentZoneName()
    {
        try
        {
            var zoneId = Plugin.ClientState.TerritoryType;
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            var row = sheet?.GetRowOrDefault(zoneId);
            return row?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Returns true when the provided zone name, or the current zone name when omitted,
    /// matches the visible Company Workshop territory naming used by GetZoneName.
    /// </summary>
    public static bool ZoneNameLooksLikeWorkshop(string? zoneName = null)
    {
        zoneName ??= GetCurrentZoneName();
        return !string.IsNullOrWhiteSpace(zoneName)
            && !zoneName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && zoneName.Contains("Company Workshop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the raw HousingManager workshop flag without the zone-name shortcut.
    /// </summary>
    public static unsafe bool IsInWorkshopByHousingManager()
    {
        try
        {
            var housingManager = HousingManager.Instance();
            return housingManager != null && housingManager->IsInWorkshop();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the local player is currently inside the FC workshop.
    /// Prefers the visible zone-name check because the workshop territories resolve as
    /// "Company Workshop - <district>" in TerritoryType.PlaceName.
    /// </summary>
    public static unsafe bool IsInWorkshop()
    {
        if (ZoneNameLooksLikeWorkshop())
            return true;

        return IsInWorkshopByHousingManager();
    }

    /// <summary>
    /// Starts a vnav path to the current target, stopping within the requested ring distance.
    /// </summary>
    public static bool TryPathToCurrentTarget(float stopDistance = 0.5f)
    {
        var plugin = Plugin.Instance;
        var local = Plugin.ObjectTable.LocalPlayer;
        var target = local?.TargetObject;
        if (plugin == null || local == null || target == null || !plugin.IpcClient.VnavIsReady())
            return false;

        return plugin.IpcClient.VnavPathfindAndMoveCloseTo(target.Position, false, stopDistance);
    }

    /// <summary>
    /// Returns true while any vnav pathing or movement operation is still active.
    /// </summary>
    public static bool IsVnavMovementActive()
    {
        var plugin = Plugin.Instance;
        return plugin != null
            && (plugin.IpcClient.VnavPathIsRunning()
                || plugin.IpcClient.VnavNavPathfindInProgress()
                || plugin.IpcClient.VnavSimpleMovePathfindInProgress());
    }

    /// <summary>
    /// Keeps the named target selected, paths into range if needed, and returns true once
    /// the player is inside the requested stop distance and vnav has stopped moving.
    /// </summary>
    public static bool IsCurrentTargetWithinStopDistanceAndStopped(string targetName, float stopDistance)
    {
        if (!CurrentTargetMatches(targetName))
        {
            TargetByName(targetName);
            return false;
        }

        var plugin = Plugin.Instance;
        var local = Plugin.ObjectTable.LocalPlayer;
        var target = local?.TargetObject;
        if (plugin == null || local == null || target == null)
            return false;

        var dx = target.Position.X - local.Position.X;
        var dy = target.Position.Y - local.Position.Y;
        var dz = target.Position.Z - local.Position.Z;
        var centerDistance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        var ringDistance = centerDistance - local.HitboxRadius - target.HitboxRadius;
        var movementActive = IsVnavMovementActive();

        if (ringDistance <= stopDistance)
        {
            if (movementActive)
            {
                plugin.IpcClient.VnavStop();
                return false;
            }

            return true;
        }

        if (!movementActive)
            TryPathToCurrentTarget(stopDistance);

        return false;
    }

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

    /// <summary>
    /// Sends the game's reset-camera input (END) to re-center the current view.
    /// </summary>
    public static void ResetCamera()
    {
        KeyInputHelper.PressKey(KeyInputHelper.VK_END);
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

    /// <summary>
    /// Returns true when the visible _TextError addon contains the requested text.
    /// </summary>
    public static bool TryGetTextErrorText(string text, out string matchedText, bool contains = true)
    {
        matchedText = GetFirstAddonText(TextErrorAddonName, text, contains);
        return !string.IsNullOrWhiteSpace(matchedText);
    }

    /// <summary>
    /// Returns true when the visible _TextError addon contains "Cannot see target".
    /// </summary>
    public static bool TryGetCannotSeeTargetTextError(out string matchedText)
    {
        return TryGetTextErrorText(CannotSeeTargetText, out matchedText, true);
    }

    /// <summary>
    /// Dismisses the visible _TextError addon when it is open.
    /// </summary>
    public static void DismissTextError()
    {
        CloseAddon(TextErrorAddonName);
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

    public static unsafe bool SelectAddonListText(string addonName, int callbackIndex, bool closeAfter = true)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.SelectAddonListText: '{addonName}' not visible or null.");
            return false;
        }

        if (callbackIndex < 0)
        {
            Plugin.Log.Warning($"[XASlave] AddonHelper.SelectAddonListText: callback index {callbackIndex} is invalid for '{addonName}'.");
            return false;
        }

        var ok = closeAfter
            ? FireCallbackAndClose(addonName, callbackIndex)
            : FireCallback(addonName, callbackIndex);
        if (ok)
        {
            Plugin.Log.Information(
                $"[XASlave] AddonHelper.SelectAddonListText: selected callback index {callbackIndex} in '{addonName}' (closeAfter={closeAfter}).");
        }

        return ok;
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
                atkValues[i] = default;
                atkValues[i].Type = AtkValueType.Int;
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
    /// In SND's callbackXA("AddonName true 12"), "true" is the updateState flag - it tells
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
            atkValues[0].Type = AtkValueType.Bool;
            atkValues[0].Int = 1; // true
            // Arg 1: Int value
            atkValues[1].Type = AtkValueType.Int;
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
                atkValues[i].Type = AtkValueType.Int;
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

    /// <summary>
    /// Commences the visible duty-ready prompt.
    /// Known-good callback shape from XA/Dhog notes: ContentsFinderConfirm true 8.
    /// </summary>
    public static bool ClickContentsFinderConfirmCommence()
    {
        if (!IsAddonReady(ContentsFinderConfirmAddonName))
            return false;

        if (FireCallbackAndClose(ContentsFinderConfirmAddonName, ContentsFinderConfirmCommenceCallback))
            return true;

        return ClickAddonButton(ContentsFinderConfirmAddonName, ContentsFinderConfirmCommenceNodeListIndex);
    }

    private static unsafe bool IsTargetable(IGameObject actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            return ((CSGameObject*)actor.Address)->GetIsTargetable();
        }
        catch
        {
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
