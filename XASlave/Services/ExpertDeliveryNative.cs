using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

internal static unsafe class ExpertDeliveryNative
{
    // GC list backing array contract. A legacy
    // hardcoded stride is not used: installed ClientStructs supplies the row type.
    // Every row must additionally match the agent, inventory and displayed prefix.
    private const int OrderedItemsPointerOffset = 0x288;

    internal static bool TryGetOrderedRows(AtkUnitBase* addon, int count, out GrandCompanyItem* rows)
    {
        rows = null;
        if (addon == null || count is <= 0 or > 1024 || sizeof(AddonGrandCompanySupplyList) < OrderedItemsPointerOffset + sizeof(nint)
            || !IsReadable((nint)addon + OrderedItemsPointerOffset, sizeof(nint))) return false;
        var address = Marshal.ReadIntPtr((nint)addon + OrderedItemsPointerOffset);
        if (!IsReadable(address, checked(count * sizeof(GrandCompanyItem)))) return false;
        rows = (GrandCompanyItem*)address;
        return true;
    }

    internal static bool Select(AtkUnitBase* addon, int index)
    {
        Plugin.AssertGameThread();
        if (addon == null || !addon->IsReady || !addon->IsVisible || index is < 0 or >= 1024) return false;
        AtkValue* values = stackalloc AtkValue[3];
        values[0] = default;
        values[0].Type = AtkValueType.Int;
        values[0].Int = 1;
        values[1] = default;
        values[1].Type = AtkValueType.Int;
        values[1].Int = index;
        values[2] = default; // Type 0, not an integer zero.
        addon->FireCallback(3, values, true);
        return true; // Dispatch only; the caller still requires observed progress.
    }

    internal static bool Deliver(AddonGrandCompanySupplyReward* addon)
    {
        Plugin.AssertGameThread();
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible) return false;
        var button = addon->DeliverButton;
        // Match the typed field to the independently named button before dispatch.
        if (button == null || button != addon->AtkUnitBase.GetComponentButtonById(38) || !button->IsEnabled) return false;
        var node = button->AtkComponentBase.OwnerNode;
        if (node == null) return false;
        var evt = node->AtkResNode.AtkEventManager.Event;
        for (var i = 0; evt != null && i < 64; i++, evt = evt->NextEvent)
        {
            if (!IsReadable((nint)evt, sizeof(AtkEvent))) return false;
            if (evt->State.EventType != AtkEventType.ButtonClick || (nint)evt->Listener != (nint)addon) continue;
            addon->AtkUnitBase.ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
            return true;
        }
        return false;
    }

    internal static bool IsReadable(nint address, int length)
    {
        if (address == 0 || length <= 0) return false;
        var start = (nuint)address;
        if (start > nuint.MaxValue - (nuint)length) return false;
        var end = start + (nuint)length;
        for (var cursor = start; cursor < end;)
        {
            if (VirtualQuery((nint)cursor, out var page, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0 || page.State != 0x1000
                || (page.Protect & 0x101) != 0 || (page.Protect & 0xEE) == 0) return false;
            var next = (nuint)page.BaseAddress + page.RegionSize;
            if (next <= cursor) return false;
            cursor = next;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect, Alignment;
        public nuint RegionSize;
        public uint State, Protect, Type, Padding;
    }
    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryInfo information, nuint size);
}
