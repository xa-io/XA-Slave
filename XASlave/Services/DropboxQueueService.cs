using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.SubKinds;
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

    private const string UsageText = "Usage: /xa db <itemId:qty ...> | inv | clear | begin | request <itemId:qty ...> | shards | crystals | clusters | shards+crystals | crystals+clusters | shards+crystals+clusters | subloot";

    // /xa db subloot expands to the eight subaquatic loot items (22500-22507), each at 99999.
    private static readonly string SublootArguments =
        string.Join(" ", Enumerable.Range(22500, 8).Select(itemId => $"{itemId}:99999"));

    // Vendor sale value per subaquatic salvage item; queued totals report the summed gil value.
    private static readonly Dictionary<uint, long> SublootValues = new()
    {
        [22500] = 8_000,    // Salvaged Ring
        [22501] = 9_000,    // Salvaged Bracelet
        [22502] = 10_000,   // Salvaged Earring
        [22503] = 13_000,   // Salvaged Necklace
        [22504] = 27_000,   // Extravagant Salvaged Ring
        [22505] = 28_500,   // Extravagant Salvaged Bracelet
        [22506] = 30_000,   // Extravagant Salvaged Earring
        [22507] = 34_500,   // Extravagant Salvaged Necklace
    };

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
            case "subloot":
                return TryAddToQueue(SublootArguments, out message);
            case "request":
                return TryBuildRequest(subcommandArgs, out message);
            case "inv":
                return TryQueueMainInventory(out message);
            case "clear":
                return TryClearQueue(out message);
            case "begin":
                return TryBeginTrading(out message);
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
        var queuedValue = 0L;

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
            {
                itemCounts[item.ItemId] = new ItemCount(count.NormalQualityQuantity - nqQuantity, count.HighQualityQuantity - hqQuantity);
                if (SublootValues.TryGetValue(item.ItemId, out var unitValue))
                    queuedValue += unitValue * (nqQuantity + hqQuantity);
            }
        }

        var startResult = TryStartTrading(out _);
        log.Debug($"[XASlave] Dropbox trade start after queueing: {startResult}.");
        message = queuedEntries > 0
            ? $"Queued {queuedEntries} Dropbox entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedTotal} item(s){FormatQueuedValue(queuedValue)}."
            : "Queued no matching local items.";
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
        var queuedValue = 0L;

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

            if (SublootValues.TryGetValue(entry.Key, out var unitValue))
                queuedValue += unitValue * (entry.Value.NormalQualityQuantity + entry.Value.HighQualityQuantity);
        }

        var startResult = TryStartTrading(out _);
        log.Debug($"[XASlave] Dropbox trade start after queueing Inventory1-4: {startResult}.");
        message = $"Queued {queuedEntries} Dropbox entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedTotal} item(s) from Inventory1-4{FormatQueuedValue(queuedValue)}.";
        return true;
    }

    public bool TryEnqueueXagmanItem(uint itemId, bool isHq, int quantity, out string message)
    {
        return TryEnqueueItem(itemId, isHq, quantity, out message);
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

    private enum TradeStartResult
    {
        Started,
        AlreadyTrading,
        NoPartner,
        NotStarted,
    }

    // Dropbox's BeginTradingQueue IPC silently no-ops unless its task manager is idle AND the focus
    // target is a player, so promote the current target to focus target before invoking it and read
    // the busy flag back to learn whether trading really started.
    private TradeStartResult TryStartTrading(out string partnerName)
    {
        partnerName = string.Empty;

        if (ipcClient.DropboxIsBusy())
            return TradeStartResult.AlreadyTrading;

        if (Plugin.TargetManager.FocusTarget is not IPlayerCharacter
            && Plugin.TargetManager.Target is IPlayerCharacter currentTarget)
        {
            Plugin.TargetManager.FocusTarget = currentTarget;
        }

        if (Plugin.TargetManager.FocusTarget is not IPlayerCharacter focus)
            return TradeStartResult.NoPartner;

        partnerName = focus.Name.ToString();
        ipcClient.DropboxBeginTrading();
        return ipcClient.DropboxIsBusy() ? TradeStartResult.Started : TradeStartResult.NotStarted;
    }

    public bool TryBeginTrading(out string message)
    {
        if (!ipcClient.IsDropboxAvailable())
        {
            message = "Dropbox is not available.";
            return false;
        }

        var result = TryStartTrading(out var partnerName);
        message = result switch
        {
            TradeStartResult.Started => $"Started Dropbox trading with {partnerName}.",
            TradeStartResult.AlreadyTrading => "Dropbox is already trading.",
            TradeStartResult.NoPartner => "Target or focus target your trade partner first.",
            _ => "Dropbox did not start trading (is the item queue empty?).",
        };
        return result == TradeStartResult.Started || result == TradeStartResult.AlreadyTrading;
    }

    private static string FormatQueuedValue(long queuedValue)
    {
        return queuedValue > 0 ? $", valued at {queuedValue:N0} gil" : string.Empty;
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
