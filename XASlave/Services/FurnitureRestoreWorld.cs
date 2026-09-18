using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

internal sealed record FurniturePageSnapshot(FurnitureRestoreInventorySnapshot Inventory,
    FurnitureRestoreSource[] Occupied, int NominalSlots, int EmptySlots);

// Snapshot pointers never escape their immediate framework read. Session fields
// are identity tokens; every later read resolves and compares the live owner again.
internal sealed unsafe class FurnitureRestoreWorld
{
    private readonly FurnitureRestoreNativeBinding binding;
    private readonly FurnitureRestoreContext context;
    internal FurnitureRestoreWorld(FurnitureRestoreNativeBinding binding, FurnitureRestoreContext context)
    { this.binding = binding; this.context = context; }

    internal FurnitureRestoreSession Capture(long generation)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var host = context.RequireHost();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (!Plugin.PlayerState.IsLoaded || player == null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            throw new InvalidOperationException("A loaded character in the current housing edit session is required.");
        var character = (Character*)player.Address;
        if (character == null || character->ContentId == 0) throw new InvalidOperationException("The housing character identity is unavailable.");
        binding.Validate();
        var agents = AgentModule.Instance();
        if (agents == null) throw new InvalidOperationException("Housing agent module is unavailable.");
        var agent = agents->GetAgentByInternalId(AgentId.Housing);
        NearbyZoneNativeBinding.RequireReadable((nint)agent, 0xA700);
        var agentTable = FurnitureRestoreNativeBinding.ReadPointer((nint)agent);
        // Dalamud lifecycle installs a copied table; validate its registered original.
        var originalTable = Plugin.AgentLifecycle.GetOriginalVirtualTable(agentTable);
        if (originalTable != binding.AgentTable)
            throw new InvalidOperationException($"Furniture Housing agent identity mismatch: actual vtable=0x{agentTable:X}, original=0x{originalTable:X}, expected=0x{binding.AgentTable:X}, HousingGoods ID={host.Id}, epoch={host.Epoch}.");
        var childId = FurnitureRestoreNativeBinding.ReadUInt((nint)agent + 0x230);
        if (childId != host.Id)
            throw new InvalidOperationException($"Furniture Housing agent child mismatch: child ID={childId}, HousingGoods ID={host.Id}, main addon ID={agent->AddonId}, epoch={host.Epoch}. Close and reopen the housing furniture menu before retrying.");
        var listMode = FurnitureRestoreNativeBinding.ReadByte((nint)agent + 0xA6E0);
        if (listMode > 3) throw new InvalidOperationException("The housing list mode is unsupported.");
        var exterior = (listMode & 1) == 0;
        var manager = HousingManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(HousingManager));
        // Match the manager's actual outdoor-first dispatch, and additionally prove
        // that this is the currently loaded editing territory, never workshop fallback.
        var territory = exterior ? (nint)manager->OutdoorTerritory : (nint)manager->IndoorTerritory;
        if (territory == 0 || (nint)manager->CurrentTerritory != territory || (!exterior && manager->OutdoorTerritory != null))
            throw new InvalidOperationException("The furniture list and active estate territory disagree.");
        NearbyZoneNativeBinding.RequireReadable(territory, exterior ? sizeof(OutdoorTerritory) : sizeof(IndoorTerritory));
        var estate = exterior ? FurnitureRestoreNativeBinding.ReadByte(territory + 0x125D8) : FurnitureRestoreNativeBinding.ReadUlong(territory + 0x125D0);
        if (exterior ? estate >= 60 : estate is 0 or ulong.MaxValue)
            throw new InvalidOperationException("The housing editing estate is unavailable or unsupported.");
        var size = binding.ReadSize(exterior, estate, listMode >= 2);
        _ = FurnitureRestoreCapacity.Limit(exterior, size);
        context.RequireHost();
        return new(generation, host.Address, host.Id, host.Epoch, (nint)agent, (nint)manager, territory, exterior, estate, size, listMode,
            character->ContentId, Plugin.ClientState.TerritoryType);
    }

    internal void Require(FurnitureRestoreSession expected)
    {
        if (Capture(expected.Generation) != expected) throw new InvalidOperationException("Character, estate, native list or housing surface generation changed.");
    }

    internal FurniturePageSnapshot? Furniture(FurnitureRestoreSession session, bool stored)
    {
        Require(session);
        var pages = FurnitureRestoreCapacity.Pages(session.Exterior, stored, session.NativeSize);
        var snapshot = CopyPages(pages, session.Exterior ? 40 : 50);
        Require(session);
        return snapshot;
    }

    internal FurniturePageSnapshot? Bags(FurnitureRestoreSession session)
    {
        Require(session);
        var snapshot = CopyPages([0, 1, 2, 3], 35);
        Require(session);
        return snapshot;
    }

    private static FurniturePageSnapshot? CopyPages(uint[] pages, int expectedSize)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) throw new InvalidOperationException("Inventory manager is unavailable.");
        var occupied = new List<FurnitureRestoreSource>();
        var nominal = 0; var empty = 0; var incomplete = false;
        foreach (var page in pages)
        {
            var container = manager->GetInventoryContainer((InventoryType)page);
            if (container == null) throw new InvalidOperationException("Required furniture/inventory page is not allocated: " + page);
            NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
            if ((uint)container->Type != page || container->Size != expectedSize || container->Items == null)
                throw new InvalidOperationException("Required furniture/inventory page layout changed: " + page);
            nominal = checked(nominal + expectedSize);
            if (!container->IsLoaded) { incomplete = true; continue; }
            var items = container->Items;
            NearbyZoneNativeBinding.RequireReadable((nint)items, checked(expectedSize * sizeof(InventoryItem)));
            for (var index = 0; index < expectedSize; index++)
            {
                var item = items + index;
                RequireSlot(item, page, index);
                if (item->IsEmpty())
                {
                    if (item->GetItemId() != 0 || item->Quantity != 0) throw new InvalidOperationException("Empty inventory slot has conflicting item state.");
                    empty++; continue;
                }
                if (item->Quantity <= 0) throw new InvalidOperationException("Furniture/inventory quantity is invalid.");
                occupied.Add(new(page, checked((ushort)index), item->Quantity, InventoryShuttleItemState.Read(item)));
            }
            if (manager != InventoryManager.Instance() || container != manager->GetInventoryContainer((InventoryType)page)
                || !container->IsLoaded || container->Items != items || container->Size != expectedSize)
                throw new InvalidOperationException("Inventory allocation/loading changed while copying the furniture snapshot.");
        }
        if (incomplete) return null;
        return new(new(occupied), occupied.ToArray(), nominal, empty);
    }

    private static void RequireSlot(InventoryItem* item, uint page, int slot)
    {
        if (item == null || item->IsSymbolic || (uint)item->Container != page || item->Slot != slot
            || item->VirtualTable != InventoryItem.StaticVirtualTablePointer)
            throw new InvalidOperationException("Furniture/inventory slot identity or virtual layout changed.");
    }

    internal InventoryItem* ResolveImmediate(FurnitureRestoreSession session, FurnitureRestoreSource source)
    {
        Require(session);
        if (!FurnitureRestoreCapacity.Pages(session.Exterior, false, session.NativeSize)
            .Concat(FurnitureRestoreCapacity.Pages(session.Exterior, true, session.NativeSize)).Contains(source.Container))
            throw new InvalidOperationException("Appearance or unrelated inventory containers cannot be furniture sources.");
        var manager = InventoryManager.Instance();
        if (manager == null) throw new InvalidOperationException("Inventory manager is unavailable.");
        var container = manager->GetInventoryContainer((InventoryType)source.Container);
        NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
        var size = session.Exterior ? 40 : 50;
        if (!container->IsLoaded || (uint)container->Type != source.Container || container->Size != size || container->Items == null || source.Slot >= size)
            throw new InvalidOperationException("The furniture source is no longer fully available.");
        var item = container->Items + source.Slot;
        NearbyZoneNativeBinding.RequireReadable((nint)item, sizeof(InventoryItem));
        RequireSlot(item, source.Container, source.Slot);
        if (item->Quantity != source.Quantity || InventoryShuttleItemState.Read(item) != source.State)
            throw new InvalidOperationException("The furniture source identity/state changed before native admission.");
        return item;
    }

    internal int? NativeListCount(FurnitureRestoreSession session, FurniturePageSnapshot shown)
    {
        Require(session);
        var begin = FurnitureRestoreNativeBinding.ReadPointer(session.Agent + 0xA6E8);
        var end = FurnitureRestoreNativeBinding.ReadPointer(session.Agent + 0xA6F0);
        var capacity = FurnitureRestoreNativeBinding.ReadPointer(session.Agent + 0xA6F8);
        var bytes = checked((long)(end - begin));
        if (bytes < 0 || bytes % 0x88 != 0 || bytes > 600 * 0x88 || end > capacity || (begin == 0 && (end != 0 || capacity != 0)))
            throw new InvalidOperationException("HousingGoods native list allocation is invalid.");
        if (bytes != 0) NearbyZoneNativeBinding.RequireReadable(begin, checked((int)bytes));
        var count = checked((int)(bytes / 0x88));
        // The verified collector excludes only IsEmpty slots; extra visual rows
        // outside this vector (number-array index475) must not affect occupancy.
        if (count != shown.Occupied.Length) return null;
        Require(session); return count;
    }

    internal bool HasNativePending(FurnitureRestoreSession session)
    {
        Require(session);
        var first = session.Territory + (session.Exterior ? 0x12A88 : 0x12640);
        var second = session.Territory + (session.Exterior ? 0x12AB0 : 0x12668);
        var table = session.Exterior ? binding.OutdoorListenerTable : binding.IndoorListenerTable;
        foreach (var listener in new[] { first, second })
        {
            if (FurnitureRestoreNativeBinding.ReadPointer(listener) != table || FurnitureRestoreNativeBinding.ReadPointer(listener + 16) != session.Territory)
                throw new InvalidOperationException("Furniture warning listener ownership changed.");
            if (FurnitureRestoreNativeBinding.ReadPointer(listener + 24) != 0 || FurnitureRestoreNativeBinding.ReadUInt(listener + 32) != 0) return true;
        }
        // Indoor storage loading retains another item pointer outside the warning listeners.
        return !session.Exterior && FurnitureRestoreNativeBinding.ReadPointer(session.Territory + 0x128A8) != 0;
    }
}
