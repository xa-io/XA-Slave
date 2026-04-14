using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public sealed class DropboxQueueService
{
    private static readonly InventoryType[] MainInventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    private static readonly InventoryType[] DefaultInventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
        InventoryType.Crystals,
        InventoryType.Currency,
    };

    private const string UsageText = "Usage: /xa db <itemId:qty ...> | inv | clear | request <itemId:qty ...> | shards | crystals | clusters | shards+crystals | crystals+clusters | shards+crystals+clusters";

    private readonly record struct NeededItem(uint ItemId, int Needed)
    {
        public override string ToString() => $"{ItemId}:{Needed}";
    }

    private readonly record struct ItemCount(int NormalQualityQuantity, int HighQualityQuantity);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IpcClient ipcClient;
    private readonly IPluginLog log;

    public DropboxQueueService(IDalamudPluginInterface pluginInterface, IpcClient ipcClient, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.ipcClient = ipcClient;
        this.log = log;
    }

    public bool TryExecute(string arguments, out string message)
    {
        var trimmed = arguments.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            message = UsageText;
            return false;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subcommand = parts[0];
        var subcommandArgs = parts.Length > 1 ? parts[1] : string.Empty;

        switch (subcommand)
        {
            case "*s":
            case "shards":
                return TryBuildRequest(BuildCrystalRequestArguments(2, 6), out message);
            case "*c":
            case "crystals":
                return TryBuildRequest(BuildCrystalRequestArguments(8, 6), out message);
            case "*x":
            case "clusters":
                return TryBuildRequest(BuildCrystalRequestArguments(14, 6), out message);
            case "*sc":
            case "shards+crystals":
                return TryBuildRequest(BuildCrystalRequestArguments(2, 12), out message);
            case "*cx":
            case "crystals+clusters":
                return TryBuildRequest(BuildCrystalRequestArguments(8, 12), out message);
            case "*scx":
            case "shards+crystals+clusters":
                return TryBuildRequest(BuildCrystalRequestArguments(2, 18), out message);
            case "request":
                return TryBuildRequest(subcommandArgs, out message);
            case "inv":
                return TryQueueMainInventory(out message);
            case "clear":
                return TryClearQueue(out message);
            default:
                return TryAddToQueue(trimmed, out message);
        }
    }

    public bool TryClearQueue(out string message)
    {
        if (!ipcClient.IsDropboxAvailable())
        {
            message = "Dropbox is not available.";
            return false;
        }

        if (!TryGetItemQuantities(out var itemQuantities))
        {
            message = "Could not access Dropbox's item queue UI. Make sure Dropbox is loaded.";
            return false;
        }

        itemQuantities.Clear();
        message = "Cleared the Dropbox item queue.";
        return true;
    }

    private bool TryBuildRequest(string arguments, out string message)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            message = "Usage: /xa db request item1:qty1 item2:qty2 [...]";
            return false;
        }

        var parsed = ParseArguments(arguments, out message);
        if (parsed == null)
            return false;

        unsafe
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                message = "Inventory manager is not available.";
                return false;
            }

            var needed = parsed
                .Select(item =>
                {
                    var haveNq = DefaultInventoryTypes.Sum(container => inventoryManager->GetItemCountInContainer(item.ItemId, container, isHq: false));
                    var haveHq = DefaultInventoryTypes.Sum(container => inventoryManager->GetItemCountInContainer(item.ItemId, container, isHq: true));
                    var have = haveNq + haveHq;
                    if (item.ItemId == 1)
                        have = (int)Math.Min((ulong)int.MaxValue, inventoryManager->GetGil());

                    return item with { Needed = item.Needed - have };
                })
                .Where(item => item.Needed > 0)
                .ToList();

            message = needed.Count == 0
                ? "No items need to be filled."
                : "/xa db " + string.Join(" ", needed);
            return true;
        }
    }

    private bool TryAddToQueue(string arguments, out string message)
    {
        if (!ipcClient.IsDropboxAvailable())
        {
            message = "Dropbox is not available.";
            return false;
        }

        var parsed = ParseArguments(arguments, out message);
        if (parsed == null)
            return false;

        var itemCounts = GetItemCounts();
        var queuedEntries = 0;
        var queuedTotal = 0;

        foreach (var item in parsed)
        {
            if (!itemCounts.TryGetValue(item.ItemId, out var count))
                continue;

            var nqQuantity = Math.Min(item.Needed, count.NormalQualityQuantity);
            var hqQuantity = Math.Min(item.Needed - nqQuantity, count.HighQualityQuantity);

            if (nqQuantity > 0)
            {
                if (!TryEnqueueItem(item.ItemId, false, nqQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += nqQuantity;
            }

            if (hqQuantity > 0)
            {
                if (!TryEnqueueItem(item.ItemId, true, hqQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += hqQuantity;
            }

            if (nqQuantity > 0 || hqQuantity > 0)
                itemCounts[item.ItemId] = new ItemCount(count.NormalQualityQuantity - nqQuantity, count.HighQualityQuantity - hqQuantity);
        }

        if (!ipcClient.DropboxBeginTrading())
        {
            message = "Failed to start the Dropbox trading queue.";
            return false;
        }

        message = queuedEntries > 0
            ? $"Queued {queuedEntries} Dropbox entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedTotal} item(s) and started trading."
            : "Queued no matching local items, but still started Dropbox trading.";
        return true;
    }

    private bool TryQueueMainInventory(out string message)
    {
        if (!ipcClient.IsDropboxAvailable())
        {
            message = "Dropbox is not available.";
            return false;
        }

        var itemCounts = GetMainInventoryItemCounts();
        if (itemCounts.Count == 0)
        {
            message = "Found no eligible tradable items in Inventory1-4; Dropbox trading was not started.";
            return true;
        }

        var queuedEntries = 0;
        var queuedTotal = 0;

        foreach (var entry in itemCounts.OrderBy(entry => entry.Key))
        {
            if (entry.Value.NormalQualityQuantity > 0)
            {
                if (!TryEnqueueItem(entry.Key, false, entry.Value.NormalQualityQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += entry.Value.NormalQualityQuantity;
            }

            if (entry.Value.HighQualityQuantity > 0)
            {
                if (!TryEnqueueItem(entry.Key, true, entry.Value.HighQualityQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += entry.Value.HighQualityQuantity;
            }
        }

        if (!ipcClient.DropboxBeginTrading())
        {
            message = "Failed to start the Dropbox trading queue.";
            return false;
        }

        message = $"Queued {queuedEntries} Dropbox entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedTotal} item(s) from Inventory1-4 and started trading.";
        return true;
    }

    private bool TryEnqueueItem(uint itemId, bool isHq, int quantity, out string message)
    {
        if (!ipcClient.DropboxTryGetItemQuantity(itemId, isHq, out var existingQuantity))
        {
            message = $"Failed to read the current Dropbox quantity for item {itemId}{(isHq ? " HQ" : string.Empty)}.";
            return false;
        }

        var targetQuantity = existingQuantity >= int.MaxValue - quantity
            ? int.MaxValue
            : existingQuantity + quantity;

        if (!ipcClient.DropboxSetItemQuantity(itemId, isHq, targetQuantity))
        {
            message = $"Failed to queue item {itemId}{(isHq ? " HQ" : string.Empty)} in Dropbox.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static string BuildCrystalRequestArguments(int startItemId, int count)
    {
        return string.Join(" ", Enumerable.Range(startItemId, count).Select(itemId => $"{itemId}:9999"));
    }

    private static IReadOnlyList<NeededItem>? ParseArguments(string arguments, out string message)
    {
        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            message = UsageText;
            return null;
        }

        var items = new List<NeededItem>(tokens.Length);
        var errors = new List<string>();

        foreach (var token in tokens)
        {
            var parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                errors.Add($"Unable to parse `{token}`.");
                continue;
            }

            if (!uint.TryParse(parts[0], out var itemId))
            {
                errors.Add($"Unable to parse item id `{parts[0]}`.");
                continue;
            }

            int quantity;
            if (parts[1] == "*")
            {
                quantity = int.MaxValue;
            }
            else if (!int.TryParse(parts[1], out quantity))
            {
                errors.Add($"Unable to parse quantity `{parts[1]}`.");
                continue;
            }

            items.Add(new NeededItem(itemId, quantity));
        }

        if (errors.Count == 1)
        {
            message = $"db: {errors[0]}";
            return null;
        }

        if (errors.Count > 1)
        {
            message = $"db: Multiple errors occurred: {string.Join(" | ", errors)}";
            return null;
        }

        message = string.Empty;
        return items;
    }

    private unsafe Dictionary<uint, ItemCount> GetItemCounts()
    {
        var itemCounts = new Dictionary<uint, ItemCount>();
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return itemCounts;

        foreach (var inventoryType in DefaultInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || slot->SpiritbondOrCollectability > 0)
                    continue;

                itemCounts.TryGetValue(slot->ItemId, out var count);
                var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                count = isHq
                    ? count with { HighQualityQuantity = count.HighQualityQuantity + (int)slot->Quantity }
                    : count with { NormalQualityQuantity = count.NormalQualityQuantity + (int)slot->Quantity };
                itemCounts[slot->ItemId] = count;
            }
        }

        var gil = (int)Math.Min((ulong)int.MaxValue, inventoryManager->GetGil());
        if (gil > 0)
            itemCounts[1] = new ItemCount(gil, 0);

        return itemCounts;
    }

    private unsafe Dictionary<uint, ItemCount> GetMainInventoryItemCounts()
    {
        var itemCounts = new Dictionary<uint, ItemCount>();
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return itemCounts;

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var inventoryType in MainInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || slot->SpiritbondOrCollectability > 0)
                    continue;

                if (!itemSheet.TryGetRow(slot->ItemId, out var itemRow) || itemRow.IsUntradable)
                    continue;

                itemCounts.TryGetValue(slot->ItemId, out var count);
                var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                count = isHq
                    ? count with { HighQualityQuantity = count.HighQualityQuantity + (int)slot->Quantity }
                    : count with { NormalQualityQuantity = count.NormalQualityQuantity + (int)slot->Quantity };
                itemCounts[slot->ItemId] = count;
            }
        }

        return itemCounts;
    }

    private bool TryGetItemQuantities(out IDictionary itemQuantities)
    {
        itemQuantities = null!;

        try
        {
            var pluginInstance = TryGetDropboxPluginInstance();
            if (pluginInstance == null)
                return false;

            var queueUiType = pluginInstance.GetType().Assembly.GetType("Dropbox.ItemQueueUI");
            itemQuantities = queueUiType?
                .GetField("ItemQuantities", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null) as IDictionary ?? null!;
            return itemQuantities != null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to reflect Dropbox item queue storage.");
            return false;
        }
    }

    private object? TryGetDropboxPluginInstance()
    {
        var pluginInstance = TryGetPluginInstanceFromCollection(pluginInterface.InstalledPlugins);
        if (pluginInstance != null)
            return pluginInstance;

        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pluginManagerServiceType == null || pluginManagerType == null)
                return null;

            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);
            if (pluginManager == null)
                return null;

            var installedPlugins = pluginManager.GetType()
                .GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(pluginManager) as IEnumerable;
            return installedPlugins == null ? null : TryGetPluginInstanceFromCollection(installedPlugins);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Could not inspect Dalamud's internal plugin manager for Dropbox.");
            return null;
        }
    }

    private static object? TryGetPluginInstanceFromCollection(IEnumerable installedPlugins)
    {
        foreach (var pluginState in installedPlugins)
        {
            if (pluginState == null || !IsLoaded(pluginState) || !IsDropbox(pluginState))
                continue;

            var pluginType = pluginState.GetType().Name == "LocalDevPlugin"
                ? pluginState.GetType().BaseType
                : pluginState.GetType();
            if (pluginType == null)
                continue;

            var instanceField = pluginType.GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic);
            var instance = instanceField?.GetValue(pluginState);
            if (instance != null)
                return instance;
        }

        return null;
    }

    private static bool IsLoaded(object pluginState)
    {
        return pluginState.GetType().GetProperty("IsLoaded")?.GetValue(pluginState) as bool? ?? false;
    }

    private static bool IsDropbox(object pluginState)
    {
        return IsMatchingPluginName(pluginState.GetType().GetProperty("InternalName")?.GetValue(pluginState)?.ToString())
               || IsMatchingPluginName(pluginState.GetType().GetProperty("Name")?.GetValue(pluginState)?.ToString());
    }

    private static bool IsMatchingPluginName(string? value)
    {
        return string.Equals(value, "Dropbox", StringComparison.OrdinalIgnoreCase);
    }
}
