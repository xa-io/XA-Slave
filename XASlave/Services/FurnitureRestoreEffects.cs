using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using SheetItem = Lumina.Excel.Sheets.Item;

namespace XASlave.Services;

internal readonly record struct FurnitureEffectRow(uint Item, byte Usage, uint PlacedWarning, uint StoredWarning, bool Destroy);

internal sealed class FurnitureRestoreEffects
{
    private readonly IDataManager data;
    private readonly Dictionary<(bool Exterior, uint Item), FurnitureEffectRow> rows = new();

    internal FurnitureRestoreEffects(IDataManager data)
    {
        this.data = data;
        foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>())
            Add(false, new(row.Item.RowId, row.UsageType, row.Unplacement.RowId, row.UnplacementStorage.RowId, row.DestroyOnRemoval));
        foreach (var row in data.GetExcelSheet<HousingYardObject>())
            Add(true, new(row.Item.RowId, row.UsageType, row.Unplacement.RowId, row.UnplacementStorage.RowId, row.DestroyOnRemoval));
    }

    private void Add(bool exterior, FurnitureEffectRow row)
    {
        if (row.Item == 0) return;
        var key = (exterior, row.Item);
        if (rows.TryGetValue(key, out var previous) && previous != row)
            throw new InvalidOperationException("Furniture effect mapping is ambiguous for item " + row.Item);
        rows[key] = row;
    }

    internal FurnitureRestorePlanItem Plan(FurnitureRestoreMode mode, bool exterior, FurnitureRestoreSource source)
    {
        FurnitureRestoreCapacity.ValidateMode(mode);
        if (!rows.TryGetValue((exterior, source.State.BaseId), out var row))
            return new(source, FurnitureRestoreEffect.Unknown, null, 0, false);
        // The manager's storage path does not select Unplacement/UnplacementStorage.
        // Its own item restrictions remain authoritative and may visibly reject it.
        if (mode == FurnitureRestoreMode.PlacedToStoreroom)
            return new(source, FurnitureRestoreEffect.Ordinary, source.State, source.Quantity, false);
        var warning = mode == FurnitureRestoreMode.StoredToInventory ? row.StoredWarning : row.PlacedWarning;
        return Map(source, exterior, row.Usage, warning, row.Destroy);
    }

    internal static FurnitureRestorePlanItem Map(FurnitureRestoreSource source, bool exterior, byte usage, uint warning, bool destroy)
    {
        if (warning == 0 && !destroy) return new(source, FurnitureRestoreEffect.Ordinary, source.State, source.Quantity, false);
        if (destroy && (warning == 1 || (!exterior && usage == 7 && warning is 4 or 11)))
            return new(source, FurnitureRestoreEffect.Destructive, null, 0, true);
        if (!destroy && ((!exterior && usage == 3 && warning is 2 or 9 or 16) || (exterior && usage == 6 && warning is 3 or 10)))
            return new(source, FurnitureRestoreEffect.Permit, source.State, source.Quantity, true);
        if (!destroy && !exterior && warning is 5 or 6 or 7 or 8 or 12 or 13 or 14 or 15 or 17 or 18)
            return new(source, FurnitureRestoreEffect.Destructive, source.State, source.Quantity, true);
        return new(source, FurnitureRestoreEffect.Unknown, null, 0, false);
    }

    internal bool BagsFit(FurniturePageSnapshot bags, FurnitureRestorePlanItem item)
    {
        if (item.ReturnedQuantity == 0) return item.NeedsNativeDecision;
        if (item.ReturnedState is not { } returned || !data.GetExcelSheet<SheetItem>().TryGetRow(returned.BaseId, out var row))
            throw new InvalidOperationException("The expected furniture inventory result is not mapped.");
        return Fits(bags, returned, item.ReturnedQuantity, checked((int)row.StackSize), row.IsUnique);
    }

    internal static bool Fits(FurniturePageSnapshot bags, InventoryShuttleItemState returned, int quantity, int stackSize, bool unique)
    {
        if (bags.NominalSlots != 140 || bags.EmptySlots < 0 || bags.EmptySlots > 140 || bags.Occupied.Length + bags.EmptySlots != 140
            || quantity <= 0 || stackSize <= 0) throw new InvalidOperationException("Complete four-bag capacity is unavailable.");
        if (unique && (quantity > 1 || bags.Occupied.Any(item => item.State.BaseId == returned.BaseId))) return false;
        var perSlot = returned.Flags.HasFlag(InventoryItem.ItemFlags.Collectable) ? 1 : stackSize;
        long available = (long)bags.EmptySlots * perSlot;
        if (perSlot > 1)
            foreach (var item in bags.Occupied)
                if (item.State == returned)
                {
                    if (item.Quantity <= 0 || item.Quantity > perSlot) throw new InvalidOperationException("A matching inventory stack has an invalid quantity.");
                    available += perSlot - item.Quantity;
                }
        return available >= quantity;
    }
}
