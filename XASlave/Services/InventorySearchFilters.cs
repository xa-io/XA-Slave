using System;

namespace XASlave.Services;

[Flags]
internal enum InventorySearchFilter
{
    None = 0,
    Tradable = 1,
    Untradable = 2,
    Bound = 4,
    Unique = 8,
}

internal readonly record struct InventorySearchItemTraits(
    bool IsUntradable,
    bool IsUnique,
    bool IsCollectable,
    ushort SpiritbondOrCollectability,
    uint GlamourId,
    bool HasCompanyCrest,
    bool IsRepairable,
    ushort Condition);

// Item restrictions only: location, receiver ownership/capacity and trade permissions
// are not item flags and are deliberately not inferred by an inventory search.
internal static class InventorySearchFilterPolicy
{
    private const InventorySearchFilter Known = InventorySearchFilter.Tradable | InventorySearchFilter.Untradable
        | InventorySearchFilter.Bound | InventorySearchFilter.Unique;

    internal static bool IsBound(InventorySearchItemTraits item)
        => !item.IsCollectable && item.SpiritbondOrCollectability > 0;

    internal static bool IsTradable(InventorySearchItemTraits item)
        => !item.IsUntradable && !item.IsCollectable && item.SpiritbondOrCollectability == 0
            && item.GlamourId == 0 && !item.HasCompanyCrest && (!item.IsRepairable || item.Condition >= 30_000);

    internal static InventorySearchFilter Toggle(InventorySearchFilter current, InventorySearchFilter toggled)
    {
        current &= Known;
        if (toggled is not (InventorySearchFilter.Tradable or InventorySearchFilter.Untradable
            or InventorySearchFilter.Bound or InventorySearchFilter.Unique)) return current;
        if ((current & toggled) != 0) return current & ~toggled;
        if (toggled == InventorySearchFilter.Tradable) current &= ~InventorySearchFilter.Untradable;
        if (toggled == InventorySearchFilter.Untradable) current &= ~InventorySearchFilter.Tradable;
        return current | toggled;
    }

    internal static bool Matches(string? name, uint itemId, string? query,
        InventorySearchFilter filters, InventorySearchItemTraits item)
    {
        if (itemId == 0 || (filters & ~Known) != 0 || !InventorySearchPolicy.Matches(name, itemId, query)) return false;
        var tradable = IsTradable(item);
        return ((filters & InventorySearchFilter.Tradable) == 0 || tradable)
            && ((filters & InventorySearchFilter.Untradable) == 0 || !tradable)
            && ((filters & InventorySearchFilter.Bound) == 0 || IsBound(item))
            && ((filters & InventorySearchFilter.Unique) == 0 || item.IsUnique);
    }
}
