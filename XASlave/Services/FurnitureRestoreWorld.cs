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
    internal string SnapshotWaitReason { get; private set; } = string.Empty;
    private global::XASlave.Localization.UiMessage? snapshotWaitMessage;
    internal global::XASlave.Localization.UiMessage SnapshotWaitMessage => global::XASlave.Localization.UiMessage.Capture(snapshotWaitMessage, SnapshotWaitReason);
    internal FurnitureRestoreWorld(FurnitureRestoreNativeBinding binding, FurnitureRestoreContext context)
    { this.binding = binding; this.context = context; }

    internal FurnitureRestoreSession Capture(long generation)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var host = context.RequireHost();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (!Plugin.PlayerState.IsLoaded || player == null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC94AACE427E3D315"), originalMessageText => new InvalidOperationException(originalMessageText));
        var character = (Character*)player.Address;
        if (character == null || character->ContentId == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S80C5953950E371F6"), originalMessageText => new InvalidOperationException(originalMessageText));
        binding.Validate();
        var agents = AgentModule.Instance();
        if (agents == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC51986BA36143B0F"), originalMessageText => new InvalidOperationException(originalMessageText));
        var agent = agents->GetAgentByInternalId(AgentId.Housing);
        NearbyZoneNativeBinding.RequireReadable((nint)agent, 0xA700);
        var agentTable = FurnitureRestoreNativeBinding.ReadPointer((nint)agent);
        // Dalamud lifecycle installs a copied table; validate its registered original.
        var originalTable = Plugin.AgentLifecycle.GetOriginalVirtualTable(agentTable);
        if (originalTable != binding.AgentTable)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S99E986ECB90A5C86" , agentTable, originalTable, binding.AgentTable, host.Id, host.Epoch), originalMessageText => new InvalidOperationException(originalMessageText));
        var childId = FurnitureRestoreNativeBinding.ReadUInt((nint)agent + 0x230);
        if (childId != host.Id)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S6FA4EB491C2A343D" , childId, host.Id, agent->AddonId, host.Epoch), originalMessageText => new InvalidOperationException(originalMessageText));
        var listMode = FurnitureRestoreNativeBinding.ReadByte((nint)agent + 0xA6E0);
        if (listMode > 3) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SADB3CA8EC246EB1C"), originalMessageText => new InvalidOperationException(originalMessageText));
        var exterior = (listMode & 1) == 0;
        var manager = HousingManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(HousingManager));
        // Match the manager's actual outdoor-first dispatch, and additionally prove
        // that this is the currently loaded editing territory, never workshop fallback.
        var territory = exterior ? (nint)manager->OutdoorTerritory : (nint)manager->IndoorTerritory;
        if (territory == 0 || (nint)manager->CurrentTerritory != territory || (!exterior && manager->OutdoorTerritory != null))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S08DCC8B68B995EB4"), originalMessageText => new InvalidOperationException(originalMessageText));
        NearbyZoneNativeBinding.RequireReadable(territory, exterior ? sizeof(OutdoorTerritory) : sizeof(IndoorTerritory));
        var estate = exterior ? FurnitureRestoreNativeBinding.ReadByte(territory + 0x125D8) : FurnitureRestoreNativeBinding.ReadUlong(territory + 0x125D0);
        if (exterior ? estate >= 60 : estate is 0 or ulong.MaxValue)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SAC9F9D73EA6D8AC4"), originalMessageText => new InvalidOperationException(originalMessageText));
        var size = binding.ReadSize(exterior, estate, listMode >= 2);
        _ = FurnitureRestoreCapacity.Limit(exterior, size);
        context.RequireHost();
        return new(generation, host.Address, host.Id, host.Epoch, (nint)agent, (nint)manager, territory, exterior, estate, size, listMode,
            character->ContentId, Plugin.ClientState.TerritoryType);
    }

    internal void Require(FurnitureRestoreSession expected)
    {
        if (Capture(expected.Generation) != expected) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2EE95AA92BF94F4D"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal FurniturePageSnapshot? Furniture(FurnitureRestoreSession session, bool stored)
    {
        Require(session);
        var pages = FurnitureRestoreCapacity.Pages(session.Exterior, stored, session.NativeSize);
        var snapshot = CopyPages(pages, session.Exterior ? 40 : 50);
        if (snapshot != null && session.Exterior && !stored && session.NativeSize == 0)
        {
            // Small estates do not require this overflow page to load. If it is
            // loaded, reject unexpected contents even when the stored list is shown.
            var overflow = CopyPages([25200], 40);
            if (overflow != null && overflow.Occupied.Length != 0)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SBD4CB71D9845E9D5"), originalMessageText => new InvalidOperationException(originalMessageText));
            SnapshotWaitReason = global::XASlave.Localization.UiMessage.Store(ref snapshotWaitMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        }
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

    private FurniturePageSnapshot? CopyPages(uint[] pages, int expectedSize)
    {
        SnapshotWaitReason = global::XASlave.Localization.UiMessage.Store(ref snapshotWaitMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        var manager = InventoryManager.Instance();
        if (manager == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S029D72042BCE47A6"), originalMessageText => new InvalidOperationException(originalMessageText));
        var occupied = new List<FurnitureRestoreSource>();
        var nominal = 0; var empty = 0; var incomplete = false;
        foreach (var page in pages)
        {
            var container = manager->GetInventoryContainer((InventoryType)page);
            if (container == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S615D161000D9D2F3" , page), originalMessageText => new InvalidOperationException(originalMessageText));
            NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
            if ((uint)container->Type != page || container->Size != expectedSize || container->Items == null)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S31B271A2218B5AC0" , page), originalMessageText => new InvalidOperationException(originalMessageText));
            nominal = checked(nominal + expectedSize);
            if (!container->IsLoaded)
            {
                if (!incomplete) SnapshotWaitReason = global::XASlave.Localization.UiMessage.Store(ref snapshotWaitMessage, global::XASlave.Localization.UiMessage.Format("S5487816CC4056774" , page));
                incomplete = true; continue;
            }
            var items = container->Items;
            NearbyZoneNativeBinding.RequireReadable((nint)items, checked(expectedSize * sizeof(InventoryItem)));
            for (var index = 0; index < expectedSize; index++)
            {
                var item = items + index;
                RequireSlot(item, page, index);
                if (item->IsEmpty())
                {
                    if (item->GetItemId() != 0 || item->Quantity != 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE6E1776F07D4E479"), originalMessageText => new InvalidOperationException(originalMessageText));
                    empty++; continue;
                }
                if (item->Quantity <= 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SFECB57ABEB6177E6"), originalMessageText => new InvalidOperationException(originalMessageText));
                occupied.Add(new(page, checked((ushort)index), item->Quantity, InventoryShuttleItemState.Read(item)));
            }
            if (manager != InventoryManager.Instance() || container != manager->GetInventoryContainer((InventoryType)page)
                || !container->IsLoaded || container->Items != items || container->Size != expectedSize)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S09970C150CAAB7E5"), originalMessageText => new InvalidOperationException(originalMessageText));
        }
        if (incomplete) return null;
        return new(new(occupied), occupied.ToArray(), nominal, empty);
    }

    private static void RequireSlot(InventoryItem* item, uint page, int slot)
    {
        if (item == null || item->IsSymbolic || (uint)item->Container != page || item->Slot != slot
            || item->VirtualTable != InventoryItem.StaticVirtualTablePointer)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S7FEE987BBDE4C663"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal InventoryItem* ResolveImmediate(FurnitureRestoreSession session, FurnitureRestoreSource source)
    {
        Require(session);
        if (!FurnitureRestoreCapacity.Pages(session.Exterior, false, session.NativeSize)
            .Concat(FurnitureRestoreCapacity.Pages(session.Exterior, true, session.NativeSize)).Contains(source.Container))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S0CAD81D827E913A4"), originalMessageText => new InvalidOperationException(originalMessageText));
        var manager = InventoryManager.Instance();
        if (manager == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S029D72042BCE47A6"), originalMessageText => new InvalidOperationException(originalMessageText));
        var container = manager->GetInventoryContainer((InventoryType)source.Container);
        NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
        var size = session.Exterior ? 40 : 50;
        if (!container->IsLoaded || (uint)container->Type != source.Container || container->Size != size || container->Items == null || source.Slot >= size)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB3ACBE214B3E080A"), originalMessageText => new InvalidOperationException(originalMessageText));
        var item = container->Items + source.Slot;
        NearbyZoneNativeBinding.RequireReadable((nint)item, sizeof(InventoryItem));
        RequireSlot(item, source.Container, source.Slot);
        if (item->Quantity != source.Quantity || InventoryShuttleItemState.Read(item) != source.State)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2FE7E2657213775D"), originalMessageText => new InvalidOperationException(originalMessageText));
        return item;
    }

    internal int? NativeListCount(FurnitureRestoreSession session, FurniturePageSnapshot shown)
    {
        SnapshotWaitReason = global::XASlave.Localization.UiMessage.Store(ref snapshotWaitMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        Require(session);
        var begin = FurnitureRestoreNativeBinding.ReadPointer(session.Agent + 0xA6E8);
        var end = FurnitureRestoreNativeBinding.ReadPointer(session.Agent + 0xA6F0);
        var capacity = FurnitureRestoreNativeBinding.ReadPointer(session.Agent + 0xA6F8);
        var bytes = checked((long)(end - begin));
        if (bytes < 0 || bytes % 0x88 != 0 || bytes > 600 * 0x88 || end > capacity || (begin == 0 && (end != 0 || capacity != 0)))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S391D024ED50ABAE4"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (bytes != 0) NearbyZoneNativeBinding.RequireReadable(begin, checked((int)bytes));
        var count = checked((int)(bytes / 0x88));
        // The verified collector excludes only IsEmpty slots; extra visual rows
        // outside this vector (number-array index475) must not affect occupancy.
        if (count != shown.Occupied.Length)
        {
            SnapshotWaitReason = global::XASlave.Localization.UiMessage.Store(ref snapshotWaitMessage, global::XASlave.Localization.UiMessage.Format("SEB00E07699669B49" , count, shown.Occupied.Length));
            return null;
        }
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
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEBA2AA1F5C244798"), originalMessageText => new InvalidOperationException(originalMessageText));
            if (FurnitureRestoreNativeBinding.ReadPointer(listener + 24) != 0 || FurnitureRestoreNativeBinding.ReadUInt(listener + 32) != 0) return true;
        }
        // Indoor storage loading retains another item pointer outside the warning listeners.
        return !session.Exterior && FurnitureRestoreNativeBinding.ReadPointer(session.Territory + 0x128A8) != 0;
    }
}
