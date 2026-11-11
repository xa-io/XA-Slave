namespace XASlave.Services;

// Storage UI contracts: CriticalCommonLib 34d364ea938e585b4f7eeab5b36e4261fdd817d0,
// Services/Ui/AtkInventoryBuddy.cs, AtkInventoryBuddy2.cs and AtkFreeCompanyChest.cs.
internal static class InventorySearchStoragePolicy
{
    internal static bool IsSaddlebag(string? host) => host is "InventoryBuddy" or "InventoryBuddy2";

    internal static bool TryMapSaddlebagSlot(string? host, int tab, int visibleSlot,
        out uint inventoryBase, out int sorterIndex, out uint nodeId)
    {
        inventoryBase = nodeId = 0;
        sorterIndex = -1;
        if (!IsSaddlebag(host) || tab is < 0 or > 1 || visibleSlot is < 0 or >= 70) return false;
        inventoryBase = tab == 0 ? 4000u : 4100u;
        sorterIndex = visibleSlot;
        nodeId = (uint)(visibleSlot < 35 ? 10 + visibleSlot : 46 + visibleSlot - 35);
        return true;
    }

    internal static bool TryMapCompanyChestSlot(uint nonItemTab, uint page, bool blocked, int visibleSlot,
        out uint inventoryType, out int physicalSlot, out uint nodeId)
    {
        inventoryType = nodeId = 0;
        physicalSlot = -1;
        if (nonItemTab != 0 || blocked || page >= 5 || visibleSlot is < 0 or >= 50) return false;
        inventoryType = 20000u + page;
        physicalSlot = visibleSlot;
        nodeId = (uint)(23 + visibleSlot);
        return true;
    }
}
