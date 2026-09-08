using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

/// <summary>Shared range checks for game-owned arrays whose reported sizes must be treated as untrusted.</summary>
internal static class NativeArrayBounds
{
    public static bool ContainsIndex(int count, int index)
        => count >= 0 && index >= 0 && index < count;

    public static int ClampElementCount(uint requestedCount, int arrayCount, int highestBaseIndex, int stride = 1)
    {
        if (requestedCount == 0 || arrayCount <= highestBaseIndex || highestBaseIndex < 0 || stride <= 0)
            return 0;

        var availableCount = ((arrayCount - 1 - highestBaseIndex) / stride) + 1;
        return (int)Math.Min(requestedCount, (uint)Math.Max(0, availableCount));
    }

    public static int ClampCount(int reportedCount, int capacity)
        => Math.Clamp(reportedCount, 0, Math.Max(0, capacity));
}

/// <summary>
/// Typed, fail-closed access to game-owned arrays. Raw pointer indexing belongs here so every
/// caller supplies the native count and negative or past-end indices are rejected consistently.
/// </summary>
internal static unsafe class NativeArrayAccess
{
    public static bool TryGetAtkValue(AtkUnitBase* addon, int index, out AtkValue* value)
    {
        Plugin.AssertGameThread();
        value = null;
        return addon != null &&
               TryGetAtkValue(addon->AtkValues, addon->AtkValuesCount, index, out value);
    }

    public static bool TryGetAtkValue(AtkValue* values, int count, int index, out AtkValue* value)
    {
        value = null;
        var safeCount = NativeArrayBounds.ClampCount(count, count);
        if (values == null || !NativeArrayBounds.ContainsIndex(safeCount, index))
            return false;

        value = &values[index];
        return true;
    }

    public static bool TryGetAtkUInt(AtkUnitBase* addon, int index, out uint value)
    {
        value = default;
        if (!TryGetAtkValue(addon, index, out var atkValue))
            return false;

        value = atkValue->UInt;
        return true;
    }

    public static bool TrySetAtkUInt(AtkUnitBase* addon, int index, uint value)
    {
        if (!TryGetAtkValue(addon, index, out var atkValue))
            return false;

        atkValue->SetUInt(value);
        return true;
    }

    public static bool TryGetNode(AtkUldManager* manager, int index, out AtkResNode* node)
    {
        Plugin.AssertGameThread();
        node = null;
        if (manager == null)
            return false;

        var safeCount = NativeArrayBounds.ClampCount(manager->NodeListCount, manager->NodeListSize);
        return TryGetNode(manager->NodeList, safeCount, index, out node);
    }

    public static bool TryGetNode(AtkResNode** nodes, int count, int index, out AtkResNode* node)
    {
        node = null;
        var safeCount = NativeArrayBounds.ClampCount(count, count);
        if (nodes == null || !NativeArrayBounds.ContainsIndex(safeCount, index))
            return false;

        node = nodes[index];
        return node != null;
    }

    public static bool TryGetInt(NumberArrayData* array, int index, out int value)
    {
        Plugin.AssertGameThread();
        value = default;
        return array != null && TryGetInt(array->IntArray, array->Size, index, out value);
    }

    public static bool TryGetInt(int* values, int count, int index, out int value)
    {
        value = default;
        var safeCount = NativeArrayBounds.ClampCount(count, count);
        if (values == null || !NativeArrayBounds.ContainsIndex(safeCount, index))
            return false;

        value = values[index];
        return true;
    }

    public static bool TryGetInventoryContainer(
        InventoryManager* manager,
        InventoryType type,
        out InventoryContainer* container)
    {
        Plugin.AssertGameThread();
        container = manager == null ? null : manager->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded || container->Size < 0)
        {
            container = null;
            return false;
        }

        if (container->Size > 0 && container->Items == null)
        {
            container = null;
            return false;
        }

        return true;
    }

    public static bool TryGetInventorySlot(
        InventoryManager* manager,
        InventoryType type,
        int index,
        out InventoryItem* item)
    {
        item = null;
        return TryGetInventoryContainer(manager, type, out var container) &&
               TryGetInventorySlot(container, index, out item);
    }

    public static bool TryGetInventorySlot(InventoryContainer* container, int index, out InventoryItem* item)
    {
        Plugin.AssertGameThread();
        item = null;
        return container != null &&
               TryGetInventorySlot(container->Items, container->Size, index, out item);
    }

    public static bool TryGetInventorySlot(InventoryItem* items, int count, int index, out InventoryItem* item)
    {
        item = null;
        var safeCount = NativeArrayBounds.ClampCount(count, count);
        if (items == null || !NativeArrayBounds.ContainsIndex(safeCount, index))
            return false;

        item = &items[index];
        return true;
    }

    public static bool TryGetBuyback(
        ShopEventHandler* handler,
        int index,
        out ShopEventHandler.BuybackItem item)
    {
        Plugin.AssertGameThread();
        item = default;
        if (handler == null)
            return false;

        var values = handler->Buyback;
        var safeCount = NativeArrayBounds.ClampCount(handler->BuybackCount, values.Length);
        if (!NativeArrayBounds.ContainsIndex(safeCount, index))
            return false;

        item = values[index];
        return true;
    }
}
