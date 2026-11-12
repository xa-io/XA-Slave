using System;
using System.Globalization;

namespace XASlave.Services;

internal readonly record struct InventorySearchHost(string Name, long Epoch, nint Address, ushort Id, nint Root);
internal readonly record struct InventorySearchSlot(InventorySearchHost Host, InventorySearchHost Grid, int Index, nint Node, uint NodeId);
internal readonly record struct InventorySearchItemIdentity(uint Container, int Slot, uint ItemId, int Quantity,
    ushort SpiritbondOrCollectability, ushort Condition, byte Flags, uint GlamourId);
internal readonly record struct InventorySearchMatch(InventorySearchSlot Slot, bool Dim, InventorySearchItemIdentity? Item = null);

internal static class InventorySearchOwnership
{
    internal static bool IsCurrent(InventorySearchHost expected, InventorySearchHost? current, long currentEpoch)
        => expected.Address != 0 && expected.Id != 0 && expected.Root != 0
            && currentEpoch == expected.Epoch && current.HasValue && current.Value == expected;
}

internal static class InventorySearchPolicy
{
    internal static bool Matches(string? name, uint itemId, string? query)
    {
        var needle = query?.Trim();
        return string.IsNullOrEmpty(needle)
            || (name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || itemId.ToString(CultureInfo.InvariantCulture).Contains(needle, StringComparison.Ordinal);
    }

    // The returned coordinates address the visible ordering, before the sorter maps to bags.
    internal static bool TryMapSlot(string? parentName, int tab, string? gridName, int visibleSlot,
        out int page, out int slot)
    {
        page = slot = -1;
        if (visibleSlot < 0 || visibleSlot >= 35)
            return false;

        int displayPage;
        switch (parentName)
        {
            case "Inventory" when tab >= 0 && tab < 4 && gridName == "InventoryGrid":
                page = tab;
                slot = visibleSlot;
                return true;
            case "InventoryLarge" when tab >= 0 && tab < 2:
                displayPage = gridName switch { "InventoryGrid0" => 0, "InventoryGrid1" => 1, _ => -1 };
                if (displayPage < 0) return false;
                page = tab * 2 + displayPage;
                slot = visibleSlot;
                return true;
            case "InventoryExpansion" when tab == 0:
                displayPage = gridName switch
                {
                    "InventoryGrid0E" => 0, "InventoryGrid1E" => 1,
                    "InventoryGrid2E" => 2, "InventoryGrid3E" => 3, _ => -1,
                };
                if (displayPage < 0) return false;
                page = displayPage;
                slot = visibleSlot;
                return true;
            case "InventoryRetainer" when tab >= 0 && tab < 5 && gridName == "RetainerGrid":
                displayPage = tab;
                break;
            case "InventoryRetainerLarge" when tab >= 0 && tab < 3:
                displayPage = gridName switch
                {
                    "RetainerGrid0" => 0, "RetainerGrid1" => 1, "RetainerGrid2" => 2,
                    "RetainerGrid3" => 3, "RetainerGrid4" => 4, _ => -1,
                };
                // Expanded retainers keep global grid names: tab 0 shows 0/1,
                // tab 1 shows 2/3, and tab 2 shows 4 beside the crystal panel.
                if (displayPage < 0 || displayPage / 2 != tab) return false;
                break;
            default:
                return false;
        }

        // Retainers display five pages of 35; their sorter uses seven pages of 25.
        var displayIndex = displayPage * 35 + visibleSlot;
        page = displayIndex / 25;
        slot = displayIndex % 25;
        return true;
    }

    internal static bool TryGetSorterIndex(int page, int slot, int itemsPerPage, int itemCount, out int index)
    {
        index = -1;
        var pages = itemsPerPage switch { 35 => 4, 25 => 7, _ => 0 };
        if (page < 0 || page >= pages || slot < 0 || slot >= itemsPerPage || itemCount < 0)
            return false;
        var candidate = page * itemsPerPage + slot;
        if (candidate >= itemCount)
            return false;
        index = candidate;
        return true;
    }
}

// The native owner must validate addon/slot/node identity before reading or writing the byte.
internal sealed class InventorySearchDimState
{
    private bool owned;
    private bool relinquished;
    private byte original;
    private byte lastWritten;

    internal bool TryApply(byte current, bool dim, out byte replacement, int opacityPercent = 50)
    {
        replacement = current;
        if (!dim)
            return TryRestore(current, out replacement);
        if (relinquished)
            return false;
        if (owned && current != lastWritten)
        {
            // An external writer has superseded this filter; do not fight it or restore over it.
            owned = false;
            relinquished = true;
            return false;
        }
        if (!owned)
            original = current;
        // Recompute from the captured original, including live option changes.
        // Keep ownership through 100% so later changes cannot compound the alpha.
        var desired = (byte)(original * Math.Clamp(opacityPercent, 0, 100) / 100);
        if (desired == current)
            return false;
        lastWritten = desired;
        owned = true;
        replacement = lastWritten;
        return true;
    }

    internal bool TryRestore(byte current, out byte restored)
    {
        restored = current;
        var canRestore = owned && current == lastWritten;
        if (canRestore)
            restored = original;
        owned = false;
        relinquished = false;
        return canRestore;
    }
}

// A live node can be reused for different inventory content without changing its
// address or node ID. Reacquire only after a verified item identity change; an
// alpha overwrite for unchanged content still belongs to the other writer.
internal sealed class InventorySearchSlotDimState
{
    private InventorySearchItemIdentity? identity;
    private InventorySearchDimState appearance = new();

    internal bool TryResetForItem(InventorySearchItemIdentity? item, byte current, out byte restored)
    {
        restored = current;
        if (identity == item) return false;
        var shouldRestore = appearance.TryRestore(current, out restored);
        identity = item;
        appearance = new InventorySearchDimState();
        return shouldRestore;
    }

    // The native owner must re-read alpha after applying TryResetForItem's
    // restoration. Unknown content is never a new basis for visual ownership.
    internal bool TryApply(byte current, bool dim, out byte replacement, int opacityPercent = 50)
        => appearance.TryApply(current, dim && identity.HasValue, out replacement, opacityPercent);

    internal bool TryRestore(byte current, out byte restored)
    {
        var shouldRestore = appearance.TryRestore(current, out restored);
        identity = null;
        appearance = new InventorySearchDimState();
        return shouldRestore;
    }
}
