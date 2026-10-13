using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

internal sealed record NotificationDebugSnapshot(string AddonName, nint Address, uint AddonId,
    bool Visible, bool Ready, int? FirstInteger, string Report)
{
    internal bool Available => Address != 0 && AddonId != 0 && Visible && Ready;
}

/// <summary>Manual notification diagnostics. Captures retain managed values, never live native pointers.</summary>
internal static unsafe class NotificationDebugProbe
{
    private const int MaxNodes = 1024;
    private const int MaxVisits = 128;
    private const int MaxTextBytes = 4096;

    private static bool IsNotification(string name) => name is "_Notification" or "_NotificationFcMake"
        or "_NotificationFcJoin" or "_NotificationFriend";

    internal static NotificationDebugSnapshot Capture(string name)
    {
        Plugin.AssertGameThread();
        var report = new StringBuilder();
        report.AppendLine($"[{DateTime.UtcNow:O}] Addon: {name}");
        nint address = 0;
        uint id = 0;
        var visible = false;
        var ready = false;
        int? firstInteger = null;
        try
        {
            if (!IsNotification(name) && name != "SelectYesno")
                report.AppendLine("Capture refused: addon is outside the notification test allowlist.");
            else
            {
                var addon = AddonHelper.GetAddon(name);
                if (addon == null) report.AppendLine("Addon is absent.");
                else
                {
                    address = (nint)addon;
                    id = addon->Id;
                    visible = addon->IsVisible;
                    ready = addon->IsReady;
                    report.AppendLine($"Address=0x{address:X}; Id={id}; Visible={visible}; Ready={ready}");
                    if (!ready) report.AppendLine("Native details skipped while addon is loading.");
                    else
                    {
                        AppendValues(addon, report, out firstInteger);
                        AppendNodeTwo(&addon->UldManager, report);
                        var visited = new HashSet<nint>();
                        var visits = 0;
                        AppendNodes(&addon->UldManager, name, 0, visited, ref visits, report);
                        if (visits >= MaxVisits) report.AppendLine("Node report truncated at 128 visits.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ready = false;
            report.AppendLine($"Capture incomplete: {ex.GetType().Name}: {ex.Message}");
        }
        return new(name, address, id, visible, ready, firstInteger, report.ToString());
    }

    internal static bool TryOpen(NotificationDebugSnapshot expected, int requestKind, bool updateState, out string detail)
    {
        Plugin.AssertGameThread();
        detail = "Callback refused: log in and capture an available notification first.";
        if (!Plugin.ClientState.IsLoggedIn || !expected.Available || !IsNotification(expected.AddonName)) return false;
        if (requestKind is < 0 or > 65535)
        {
            detail = "Callback refused: enter a request kind between 0 and 65535.";
            return false;
        }
        // Captured first Int values identify the request kind. The second UInt is diagnostic only.
        var fixedKind = expected.AddonName switch
        {
            "_NotificationFcMake" => 14,
            "_NotificationFcJoin" => 15,
            "_NotificationFriend" => 20,
            _ => -1,
        };
        if (fixedKind >= 0 && (requestKind != fixedKind || expected.FirstInteger != fixedKind))
        {
            detail = $"Callback refused: {expected.AddonName} requires request kind {fixedKind} and a matching captured Int value.";
            return false;
        }
        try
        {
            var current = Capture(expected.AddonName);
            if (!current.Available || current.Address != expected.Address || current.AddonId != expected.AddonId
                || current.FirstInteger != expected.FirstInteger)
            {
                detail = "Callback refused: the notification changed or became unavailable; capture it again.";
                return false;
            }
            var host = AddonHelper.GetAddon("_Notification");
            if (host == null || !host->IsVisible || !host->IsReady || host->Id == 0)
            {
                detail = "Callback refused: _Notification host is not visible and ready.";
                return false;
            }
            if (AddonHelper.IsAddonVisible("SelectYesno"))
            {
                detail = "Callback refused: a SelectYesno confirmation is already visible; handle it in the game first.";
                return false;
            }
            // Opening is not acceptance. Do not dereference any native object after dispatch.
            var dispatched = AddonHelper.FireCallback("_Notification", updateState, 0, requestKind);
            detail = $"_Notification updateState={updateState} args=[Int:0, Int:{requestKind}]: "
                + (dispatched ? "callback dispatched; inspect the game and capture the resulting prompt."
                    : "callback was not dispatched.");
            return dispatched;
        }
        catch (Exception ex)
        {
            detail = $"Callback could not be confirmed ({ex.GetType().Name}); inspect the game before another test.";
            return false;
        }
    }

    private static void AppendValues(AtkUnitBase* addon, StringBuilder report, out int? firstInteger)
    {
        firstInteger = null;
        var count = addon->AtkValuesCount;
        report.AppendLine($"AtkValues count={count}");
        if (count == 0) return;
        if (addon->AtkValues == null || count > 1024)
        {
            report.AppendLine("AtkValues skipped: missing pointer or count outside diagnostic bounds.");
            return;
        }
        if (addon->AtkValues[0].Type == AtkValueType.Int) firstInteger = addon->AtkValues[0].Int;
        for (var index = 0; index < Math.Min((int)count, 16); index++)
        {
            var value = addon->AtkValues + index;
            var text = value->Type switch
            {
                AtkValueType.Int => value->Int.ToString(CultureInfo.InvariantCulture),
                AtkValueType.UInt => value->UInt.ToString(CultureInfo.InvariantCulture),
                AtkValueType.Bool => value->Bool.ToString(),
                AtkValueType.Float => value->Float.ToString(CultureInfo.InvariantCulture),
                AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString => ReadCString((byte*)value->String),
                _ => "(not dereferenced)",
            };
            report.AppendLine($"  [{index}] {value->Type} (raw:{(uint)value->Type}) = {text}");
        }
        if (count > 16) report.AppendLine("AtkValues report truncated after 16 entries.");
    }

    private static bool ValidNodes(AtkUldManager* manager) => manager != null && manager->NodeList != null
        && manager->NodeListCount <= MaxNodes && manager->NodeListCount <= manager->NodeListSize;

    private static void AppendNodeTwo(AtkUldManager* manager, StringBuilder report)
    {
        if (!ValidNodes(manager)) { report.AppendLine("NodeId 2: node list unavailable or outside bounds."); return; }
        AtkResNode* found = null;
        for (var index = 0; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || node->NodeId != 2) continue;
            if (found != null) { report.AppendLine("NodeId 2: ambiguous; multiple entries have this ID."); return; }
            found = node;
        }
        if (found == null) { report.AppendLine("NodeId 2: absent."); return; }
        report.AppendLine($"NodeId 2: address=0x{(nint)found:X}; rawType={(ushort)found->Type}; visible={found->IsVisible()}");
        if ((int)found->Type < 1000) { report.AppendLine("NodeId 2 is not a component node."); return; }
        var component = ((AtkComponentNode*)found)->Component;
        if (component == null) { report.AppendLine("NodeId 2 component is null."); return; }
        var ownerMatches = component->OwnerNode == (AtkComponentNode*)found;
        report.AppendLine($"Component=0x{(nint)component:X}; OwnerNode=0x{(nint)component->OwnerNode:X}; ownerMatches={ownerMatches}");
        // Preserve the reader's short circuit: do not invoke native type lookup after owner mismatch.
        if (!ownerMatches) { report.AppendLine("Native component type: skipped because OwnerNode does not match."); return; }
        var type = component->GetComponentType();
        report.AppendLine($"Native component type={type} ({(int)type}); expected Window={type == ComponentType.Window}");
    }

    private static void AppendNodes(AtkUldManager* manager, string path, int depth, HashSet<nint> visited,
        ref int visits, StringBuilder report)
    {
        if (!ValidNodes(manager)) { report.AppendLine($"{path}: node list skipped; unavailable or outside bounds."); return; }
        for (var index = 0; index < manager->NodeListCount && visits < MaxVisits; index++)
        {
            var node = manager->NodeList[index];
            visits++;
            if (node == null) continue;
            if (!visited.Add((nint)node)) { report.AppendLine($"{path}[{index}]: repeated node or cycle skipped."); continue; }
            var nodePath = $"{path}/#{node->NodeId}";
            report.AppendLine($"{nodePath}: address=0x{(nint)node:X}; rawType={(ushort)node->Type}; visible={node->IsVisible()}; ancestors={AncestorVisibility(node)}");
            if (node->Type == NodeType.Text)
            {
                var value = &((AtkTextNode*)node)->NodeText;
                var valid = (byte*)value->StringPtr != null && value->BufUsed is >= 1 and <= MaxTextBytes + 1
                    && value->BufSize >= value->BufUsed;
                report.AppendLine($"  Text={(valid ? Decode(value->AsSpan()) : "(buffer missing or outside bounds)")}");
            }
            if ((int)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;
            if (component->OwnerNode != (AtkComponentNode*)node)
            {
                report.AppendLine($"  Child nodes skipped: OwnerNode=0x{(nint)component->OwnerNode:X} does not match.");
                continue;
            }
            if (depth >= 4) { report.AppendLine("  Child nodes truncated at depth 4."); continue; }
            AppendNodes(&component->UldManager, nodePath, depth + 1, visited, ref visits, report);
        }
    }

    private static string AncestorVisibility(AtkResNode* node)
    {
        var visited = new HashSet<nint>();
        for (var depth = 0; node != null && depth < 64; depth++, node = node->ParentNode)
        {
            if (!visited.Add((nint)node)) return "cycle";
            if (!node->IsVisible()) return "hidden";
        }
        return node == null ? "visible" : "depth limit";
    }

    private static string ReadCString(byte* pointer)
    {
        if (pointer == null) return "(null)";
        var length = 0;
        while (length < MaxTextBytes && pointer[length] != 0) length++;
        return length == MaxTextBytes ? "(no terminator within 4096 bytes)" : Decode(new ReadOnlySpan<byte>(pointer, length));
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => JsonSerializer.Serialize(SeString.Parse(bytes).TextValue);
}
