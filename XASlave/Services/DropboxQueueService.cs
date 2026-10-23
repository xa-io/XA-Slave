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

internal interface IDropboxQueueClient
{
    bool IsDropboxAvailable();
    bool DropboxTryGetItemQuantity(uint itemId, bool isHq, out int quantity);
    bool DropboxSetItemQuantity(uint itemId, bool isHq, int quantity);
    bool DropboxIsBusy();
    bool DropboxBeginTrading();
}

internal sealed class DropboxQueueClientAdapter(IpcClient client) : IDropboxQueueClient
{
    public bool IsDropboxAvailable() => client.IsDropboxAvailable();
    public bool DropboxTryGetItemQuantity(uint itemId, bool isHq, out int quantity)
        => client.DropboxTryGetItemQuantity(itemId, isHq, out quantity);
    public bool DropboxSetItemQuantity(uint itemId, bool isHq, int quantity)
        => client.DropboxSetItemQuantity(itemId, isHq, quantity);
    public bool DropboxIsBusy() => client.DropboxIsBusy();
    public bool DropboxBeginTrading() => client.DropboxBeginTrading();
}

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

    private const string UsageText = "Usage: /xa db <itemId:qty ...> (negative qty keeps that many) | inv | clear | begin | request <itemId:qty ...> | shards | crystals | clusters | shards+crystals | crystals+clusters | shards+crystals+clusters | subloot | subgil <gil-value> | subsupplies <days>";
    private static global::XASlave.Localization.UiMessage UsageMessage => global::XASlave.Localization.UiMessage.Key("S92A9EBB8CF764199");

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
    private readonly IDropboxQueueClient ipcClient;
    private readonly IPluginLog log;

    public DropboxQueueService(IDalamudPluginInterface pluginInterface, IpcClient ipcClient, IPluginLog log)
        : this(pluginInterface, new DropboxQueueClientAdapter(ipcClient), log)
    {
        ipcClient.PrepareDropboxQueueEntry = TryPrepareQueueEntry;
    }

    internal DropboxQueueService(
        IDalamudPluginInterface pluginInterface,
        IDropboxQueueClient ipcClient,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.ipcClient = ipcClient;
        this.log = log;
    }

    public bool TryExecute(string arguments, out string message) => TryExecuteLocalizedCore(out _, arguments, out message);
    internal bool TryExecuteMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryExecuteLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryExecuteLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        var trimmed = arguments.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, UsageMessage);
            return false;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subcommand = parts[0];
        var subcommandArgs = parts.Length > 1 ? parts[1] : string.Empty;

        switch (subcommand)
        {
            case "*s":
            case "shards":
                return TryBuildRequestLocalizedCore(out replyPresentation, BuildCrystalRequestArguments(2, 6), out message);
            case "*c":
            case "crystals":
                return TryBuildRequestLocalizedCore(out replyPresentation, BuildCrystalRequestArguments(8, 6), out message);
            case "*x":
            case "clusters":
                return TryBuildRequestLocalizedCore(out replyPresentation, BuildCrystalRequestArguments(14, 6), out message);
            case "*sc":
            case "shards+crystals":
                return TryBuildRequestLocalizedCore(out replyPresentation, BuildCrystalRequestArguments(2, 12), out message);
            case "*cx":
            case "crystals+clusters":
                return TryBuildRequestLocalizedCore(out replyPresentation, BuildCrystalRequestArguments(8, 12), out message);
            case "*scx":
            case "shards+crystals+clusters":
                return TryBuildRequestLocalizedCore(out replyPresentation, BuildCrystalRequestArguments(2, 18), out message);
            case "subloot":
                return TryAddToQueueLocalizedCore(out replyPresentation, SublootArguments, out message);
            case "subgil":
                return TryQueueSublootValueLocalizedCore(out replyPresentation, subcommandArgs, out message);
            case "subsupplies":
                return TryQueueSubmarineSuppliesLocalizedCore(out replyPresentation, subcommandArgs, out message);
            case "request":
                return TryBuildRequestLocalizedCore(out replyPresentation, subcommandArgs, out message);
            case "inv":
                return TryQueueMainInventoryLocalizedCore(out replyPresentation, out message);
            case "clear":
                return TryClearQueueLocalizedCore(out replyPresentation, out message);
            case "begin":
                return TryBeginTradingLocalizedCore(out replyPresentation, out message);
            default:
                return TryAddToQueueLocalizedCore(out replyPresentation, trimmed, out message);
        }
    }

    private bool TryQueueSubmarineSupplies(string arguments, out string message) => TryQueueSubmarineSuppliesLocalizedCore(out _, arguments, out message);
    private bool TryQueueSubmarineSuppliesMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryQueueSubmarineSuppliesLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryQueueSubmarineSuppliesLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        if (!SubmarineSupplyCalculator.TryCalculate(arguments, out var tanks, out var repairKits))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S4932D60DF29756DF"), global::XASlave.Localization.UiMessage.Key("SC8EA6EEAC88C8450")));
            return false;
        }

        var queueArguments = FormattableString.Invariant(
            $"{SubmarineSupplyCalculator.CeruleumTankItemId}:{tanks} {SubmarineSupplyCalculator.MagitekRepairKitItemId}:{repairKits}");
        var started = TryAddToQueue(queueArguments, out var queueMessage);
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S48A32FB23A245978", arguments.Trim(), tanks), global::XASlave.Localization.UiMessage.Format("S9447B3D83E7CA844", repairKits, queueMessage)));
        return started;
    }

    public bool TryClearQueue(out string message) => TryClearQueueLocalizedCore(out _, out message);
    internal bool TryClearQueueMessage(out global::XASlave.Localization.UiMessage message)
    {
        var success = TryClearQueueLocalizedCore(out var presentation, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryClearQueueLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, out string message)
    {
        replyPresentation = null;
        if (!ipcClient.IsDropboxAvailable())
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S9030550D2CA76977"));
            return false;
        }

        if (!TryGetItemQuantities(out var itemQuantities))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SBAA08583C908960B"));
            return false;
        }

        itemQuantities.Clear();
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SE31DCC3C6708CADE"));
        return true;
    }

    public bool TryQueueSublootValue(string arguments, out string message) => TryQueueSublootValueLocalizedCore(out _, arguments, out message);
    internal bool TryQueueSublootValueMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryQueueSublootValueLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryQueueSublootValueLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        if (!SublootValueSelector.TryParseTarget(arguments, out var targetValue))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S09B291780377D89C"));
            return false;
        }

        if (!ipcClient.IsDropboxAvailable())
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S9030550D2CA76977"));
            return false;
        }

        if (!TryClearQueueLocalizedCore(out replyPresentation, out message))
            return false;

        var itemCounts = GetItemCounts();
        var stock = SublootValues
            .OrderBy(entry => entry.Key)
            .Select(entry =>
            {
                itemCounts.TryGetValue(entry.Key, out var count);
                var quantity = (int)Math.Min(
                    int.MaxValue,
                    (long)count.NormalQualityQuantity + count.HighQualityQuantity);
                return new SublootStockItem(entry.Key, entry.Value, quantity);
            })
            .ToArray();

        SublootValueSelectionResult selection;
        try
        {
            selection = SublootValueSelector.Select(stock, targetValue);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to calculate a minimum-overflow subloot selection.");
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S9877978E6792E7C0", ex.Message));
            return false;
        }

        if (selection.SelectedItemCount == 0)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S3EA58D1B680CDCE4", targetValue));
            return false;
        }

        var queuedEntries = 0;
        var queuedTotal = 0;
        foreach (var selected in selection.Quantities.OrderBy(entry => entry.Key))
        {
            var count = itemCounts[selected.Key];
            var nqQuantity = Math.Min(selected.Value, count.NormalQualityQuantity);
            var hqQuantity = Math.Min(selected.Value - nqQuantity, count.HighQualityQuantity);

            if (nqQuantity > 0)
            {
                if (!TryEnqueueItemLocalizedCore(out replyPresentation, selected.Key, false, nqQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += nqQuantity;
            }

            if (hqQuantity > 0)
            {
                if (!TryEnqueueItemLocalizedCore(out replyPresentation, selected.Key, true, hqQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += hqQuantity;
            }
        }

        var targetResult = (selection.TargetReached ? (selection.Overflow == 0 ? global::XASlave.Localization.UiMessage.Key("S6B8C7CBC10E07C5D") : global::XASlave.Localization.UiMessage.Format("SCF3CA99703563AF5" , selection.Overflow)) : global::XASlave.Localization.UiMessage.Format("SF2B2DF50BA2CFB15" , selection.Shortfall));
        if (!DropboxQueuePolicy.ShouldStartTrading(queuedEntries))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S49DB190C0F99938A", targetValue, targetResult));
            return false;
        }

        var startResult = TryStartTrading(out var partnerName);
        var queuedText = global::XASlave.Localization.UiMessage.Format("SF25B2CEBA8A52CAD" , global::XASlave.Localization.UiMessage.Format("S710BABB2F3BA63E5" , queuedEntries, ((queuedEntries == 1 ? global::XASlave.Localization.UiMessage.Key("SA1FCE4363854FF88") : global::XASlave.Localization.UiMessage.Key("S388F91EA04D6EAEB")))), global::XASlave.Localization.UiMessage.Format("S3C1D3CC678D99BD1" , queuedTotal, selection.SelectedValue), global::XASlave.Localization.UiMessage.Format("S481287861C6221AE" , targetValue, targetResult));
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, startResult switch
        {
            TradeStartResult.Started => global::XASlave.Localization.UiMessage.Format("S719C5A4E17E44597" , queuedText, partnerName),
            TradeStartResult.AlreadyTrading => global::XASlave.Localization.UiMessage.Format("S2F8B1D374683D59D" , queuedText),
            TradeStartResult.NoPartner => global::XASlave.Localization.UiMessage.Format("S3662FA3ED42E845C" , queuedText),
            _ => global::XASlave.Localization.UiMessage.Format("SE98C28D5D41F8B46" , queuedText),
        });
        return startResult == TradeStartResult.Started;
    }

    private bool TryBuildRequest(string arguments, out string message) => TryBuildRequestLocalizedCore(out _, arguments, out message);
    private bool TryBuildRequestMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryBuildRequestLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryBuildRequestLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SD34B55A9D4BD26D2"));
            return false;
        }

        var parsed = ParseArgumentsMessage(arguments, out var parseMessage);
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, parseMessage);
        if (parsed == null)
            return false;

        if (parsed.Any(item => item.Needed < 0))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SE3C8792D740E7BBD"));
            return false;
        }

        unsafe
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S6F9411C459B1B8ED"));
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

            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, (needed.Count == 0 ? global::XASlave.Localization.UiMessage.Key("S24EA61BF8B970967") : global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S98BD98427A6AD014"), global::XASlave.Localization.UiMessage.Value(string.Join(" ", needed)))));
            return true;
        }
    }

    private bool TryAddToQueue(string arguments, out string message) => TryAddToQueueLocalizedCore(out _, arguments, out message);
    private bool TryAddToQueueMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryAddToQueueLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryAddToQueueLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        if (!ipcClient.IsDropboxAvailable())
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S9030550D2CA76977"));
            return false;
        }

        var parsed = ParseArgumentsMessage(arguments, out var parseMessage);
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, parseMessage);
        if (parsed == null)
            return false;

        // Clear once per valid command; individual items remain additive within this batch.
        if (!TryClearQueueLocalizedCore(out replyPresentation, out message))
            return false;

        var itemCounts = GetItemCounts();
        var queuedEntries = 0;
        var queuedTotal = 0;
        var queuedValue = 0L;
        var keepRequestCount = parsed.Count(item => item.Needed < 0);

        foreach (var item in parsed)
        {
            if (!itemCounts.TryGetValue(item.ItemId, out var count))
                continue;

            var quantityPlan = DropboxQueueQuantityPlanner.Create(
                count.NormalQualityQuantity,
                count.HighQualityQuantity,
                item.Needed);
            var nqQuantity = quantityPlan.NormalQualityQuantity;
            var hqQuantity = quantityPlan.HighQualityQuantity;

            if (nqQuantity > 0)
            {
                if (!TryEnqueueItemLocalizedCore(out replyPresentation, item.ItemId, false, nqQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += nqQuantity;
            }

            if (hqQuantity > 0)
            {
                if (!TryEnqueueItemLocalizedCore(out replyPresentation, item.ItemId, true, hqQuantity, out message))
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

        var keepSummary = (keepRequestCount > 0 ? global::XASlave.Localization.UiMessage.Format("S18DF6A28E5F4DA2D" , keepRequestCount, ((keepRequestCount == 1 ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Key("S043A718774C572BD")))) : global::XASlave.Localization.UiMessage.Raw(string.Empty));
        if (!DropboxQueuePolicy.ShouldStartTrading(queuedEntries))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, (keepRequestCount > 0 ? global::XASlave.Localization.UiMessage.Key("S195C88F704745CF6") : global::XASlave.Localization.UiMessage.Key("S2799EB3F4FCD9D6F")));
            return false;
        }

        var startResult = TryStartTrading(out var partnerName);
        var queuedText = global::XASlave.Localization.UiMessage.Format("S9CE4EAF0B3065E1D" , queuedEntries, ((queuedEntries == 1 ? global::XASlave.Localization.UiMessage.Key("SA1FCE4363854FF88") : global::XASlave.Localization.UiMessage.Key("S388F91EA04D6EAEB"))), queuedTotal, FormatQueuedValueMessage(queuedValue), keepSummary);
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, startResult switch
        {
            TradeStartResult.Started => global::XASlave.Localization.UiMessage.Format("S719C5A4E17E44597" , queuedText, partnerName),
            TradeStartResult.AlreadyTrading => global::XASlave.Localization.UiMessage.Format("S2F8B1D374683D59D" , queuedText),
            TradeStartResult.NoPartner => global::XASlave.Localization.UiMessage.Format("S3662FA3ED42E845C" , queuedText),
            _ => global::XASlave.Localization.UiMessage.Format("SE98C28D5D41F8B46" , queuedText),
        });
        return startResult == TradeStartResult.Started;
    }

    private bool TryQueueMainInventory(out string message) => TryQueueMainInventoryLocalizedCore(out _, out message);
    private bool TryQueueMainInventoryMessage(out global::XASlave.Localization.UiMessage message)
    {
        var success = TryQueueMainInventoryLocalizedCore(out var presentation, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryQueueMainInventoryLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, out string message)
    {
        replyPresentation = null;
        if (!ipcClient.IsDropboxAvailable())
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S9030550D2CA76977"));
            return false;
        }

        if (!TryClearQueueLocalizedCore(out replyPresentation, out message))
            return false;

        var itemCounts = GetMainInventoryItemCounts();
        if (itemCounts.Count == 0)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S568BFE5B1CC92DE3"));
            return false;
        }

        var queuedEntries = 0;
        var queuedTotal = 0;
        var queuedValue = 0L;

        foreach (var entry in itemCounts.OrderBy(entry => entry.Key))
        {
            if (entry.Value.NormalQualityQuantity > 0)
            {
                if (!TryEnqueueItemLocalizedCore(out replyPresentation, entry.Key, false, entry.Value.NormalQualityQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += entry.Value.NormalQualityQuantity;
            }

            if (entry.Value.HighQualityQuantity > 0)
            {
                if (!TryEnqueueItemLocalizedCore(out replyPresentation, entry.Key, true, entry.Value.HighQualityQuantity, out message))
                    return false;

                queuedEntries++;
                queuedTotal += entry.Value.HighQualityQuantity;
            }

            if (SublootValues.TryGetValue(entry.Key, out var unitValue))
                queuedValue += unitValue * (entry.Value.NormalQualityQuantity + entry.Value.HighQualityQuantity);
        }

        if (!DropboxQueuePolicy.ShouldStartTrading(queuedEntries))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S0D3F6C266FD0013D"));
            return false;
        }

        var startResult = TryStartTrading(out var partnerName);
        var queuedText = global::XASlave.Localization.UiMessage.Format("S3CEA16DC4C17BA70" , queuedEntries, ((queuedEntries == 1 ? global::XASlave.Localization.UiMessage.Key("SA1FCE4363854FF88") : global::XASlave.Localization.UiMessage.Key("S388F91EA04D6EAEB"))), queuedTotal, FormatQueuedValueMessage(queuedValue));
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, startResult switch
        {
            TradeStartResult.Started => global::XASlave.Localization.UiMessage.Format("S719C5A4E17E44597" , queuedText, partnerName),
            TradeStartResult.AlreadyTrading => global::XASlave.Localization.UiMessage.Format("S2F8B1D374683D59D" , queuedText),
            TradeStartResult.NoPartner => global::XASlave.Localization.UiMessage.Format("S3662FA3ED42E845C" , queuedText),
            _ => global::XASlave.Localization.UiMessage.Format("SE98C28D5D41F8B46" , queuedText),
        });
        return startResult == TradeStartResult.Started;
    }

    public bool TryEnqueueXagmanItem(uint itemId, bool isHq, int quantity, out string message) => TryEnqueueXagmanItemLocalizedCore(out _, itemId, isHq, quantity, out message);
    internal bool TryEnqueueXagmanItemMessage(uint itemId, bool isHq, int quantity, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryEnqueueXagmanItemLocalizedCore(out var presentation, itemId, isHq, quantity, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryEnqueueXagmanItemLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, uint itemId, bool isHq, int quantity, out string message)
    {
        replyPresentation = null;
        return TryEnqueueItemLocalizedCore(out replyPresentation, itemId, isHq, quantity, out message);
    }

    private bool TryEnqueueItem(uint itemId, bool isHq, int quantity, out string message) => TryEnqueueItemLocalizedCore(out _, itemId, isHq, quantity, out message);
    private bool TryEnqueueItemMessage(uint itemId, bool isHq, int quantity, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryEnqueueItemLocalizedCore(out var presentation, itemId, isHq, quantity, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryEnqueueItemLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, uint itemId, bool isHq, int quantity, out string message)
    {
        replyPresentation = null;
        if (quantity <= 0)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S3E92678DA402A0C1", itemId, quantity));
            return false;
        }

        if (!ipcClient.DropboxTryGetItemQuantity(itemId, isHq, out var existingQuantity)
            || !DropboxQueuePolicy.TryCalculateTargetQuantity(existingQuantity, quantity, out var targetQuantity))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S0C4703A02F17450F", itemId, ((isHq ? global::XASlave.Localization.UiMessage.Key("S91A133F748E97815") : global::XASlave.Localization.UiMessage.Value(string.Empty)))));
            return false;
        }

        if (!ipcClient.DropboxSetItemQuantity(itemId, isHq, targetQuantity))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S6743B90A54B57954", itemId, ((isHq ? global::XASlave.Localization.UiMessage.Key("S91A133F748E97815") : global::XASlave.Localization.UiMessage.Value(string.Empty)))));
            return false;
        }

        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Raw(string.Empty));
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
        if (!ipcClient.DropboxBeginTrading())
            return TradeStartResult.NotStarted;
        return ipcClient.DropboxIsBusy() ? TradeStartResult.Started : TradeStartResult.NotStarted;
    }

    public bool TryBeginTrading(out string message) => TryBeginTradingLocalizedCore(out _, out message);
    internal bool TryBeginTradingMessage(out global::XASlave.Localization.UiMessage message)
    {
        var success = TryBeginTradingLocalizedCore(out var presentation, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryBeginTradingLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, out string message)
    {
        replyPresentation = null;
        if (!ipcClient.IsDropboxAvailable())
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S9030550D2CA76977"));
            return false;
        }

        var result = TryStartTrading(out var partnerName);
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, result switch
        {
            TradeStartResult.Started => global::XASlave.Localization.UiMessage.Format("S6206C8E4C192E24B" , partnerName),
            TradeStartResult.AlreadyTrading => global::XASlave.Localization.UiMessage.Key("SC0C74990B6A8250A"),
            TradeStartResult.NoPartner => global::XASlave.Localization.UiMessage.Key("S26E8E88849DD6E1E"),
            _ => global::XASlave.Localization.UiMessage.Key("S6FD54E28D83CE407"),
        });
        return result == TradeStartResult.Started;
    }

    private static string FormatQueuedValue(long queuedValue) => FormatQueuedValueMessage(queuedValue).Original;

    private static global::XASlave.Localization.UiMessage FormatQueuedValueMessage(long queuedValue)
    {
        return queuedValue > 0 ? global::XASlave.Localization.UiMessage.Format("S883E6A44479F2D92" , queuedValue) : global::XASlave.Localization.UiMessage.Raw(string.Empty);
    }

    private static string BuildCrystalRequestArguments(int startItemId, int count)
    {
        return string.Join(" ", Enumerable.Range(startItemId, count).Select(itemId => $"{itemId}:9999"));
    }

    private static IReadOnlyList<NeededItem>? ParseArguments(string arguments, out string message)
    {
        var result = ParseArgumentsMessage(arguments, out var presentation);
        message = presentation.Original;
        return result;
    }

    private static IReadOnlyList<NeededItem>? ParseArgumentsMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            message = UsageMessage;
            return null;
        }

        var items = new List<NeededItem>(tokens.Length);
        var errors = new List<global::XASlave.Localization.UiMessage>();

        foreach (var token in tokens)
        {
            var parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                errors.Add(global::XASlave.Localization.UiMessage.Format("S7486B12FCB516CE2" , token));
                continue;
            }

            if (!uint.TryParse(parts[0], out var itemId))
            {
                errors.Add(global::XASlave.Localization.UiMessage.Format("S19E4E59E47E9B62D" , parts[0]));
                continue;
            }

            int quantity;
            if (parts[1] == "*")
            {
                quantity = int.MaxValue;
            }
            else if (!int.TryParse(parts[1], out quantity))
            {
                errors.Add(global::XASlave.Localization.UiMessage.Format("SAA3FD7A4DA4EBE0F" , parts[1]));
                continue;
            }

            items.Add(new NeededItem(itemId, quantity));
        }

        if (errors.Count == 1)
        {
            message = global::XASlave.Localization.UiMessage.Format("SE74525E1F729DE58" , errors[0]);
            return null;
        }

        if (errors.Count > 1)
        {
            message = global::XASlave.Localization.UiMessage.Format("S2E60DEDC2C7AD82B" , global::XASlave.Localization.UiMessage.Join(" | ", errors));
            return null;
        }

        message = global::XASlave.Localization.UiMessage.Raw(string.Empty);
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
            if (!NativeArrayAccess.TryGetInventoryContainer(inventoryManager, inventoryType, out var container))
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                if (!NativeArrayAccess.TryGetInventorySlot(container, slotIndex, out var slot)
                    || slot->ItemId == 0 || slot->SpiritbondOrCollectability > 0)
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
            if (!NativeArrayAccess.TryGetInventoryContainer(inventoryManager, inventoryType, out var container))
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                if (!NativeArrayAccess.TryGetInventorySlot(container, slotIndex, out var slot)
                    || slot->ItemId == 0 || slot->SpiritbondOrCollectability > 0)
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

    private bool TryPrepareQueueEntry(uint itemId, bool isHq)
    {
        if (!TryGetItemQuantities(out var itemQuantities))
            return false;

        try
        {
            // Dropbox 1.0.2.15 writes ItemQuantities[key].Value without inserting missing keys.
            // Its UI normally creates the zero-valued box; commands must do that after clearing.
            var types = itemQuantities.GetType().GetGenericArguments();
            if (types.Length != 2)
                throw new InvalidOperationException("Unexpected Dropbox item queue type.");

            var key = Activator.CreateInstance(types[0], new object[] { itemId, isHq })
                ?? throw new InvalidOperationException("Could not create a Dropbox item descriptor.");
            if (itemQuantities.Contains(key))
                return true;

            var value = Activator.CreateInstance(types[1], new object[] { 0 })
                ?? throw new InvalidOperationException("Could not create a Dropbox quantity entry.");
            itemQuantities.Add(key, value);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Could not initialize Dropbox queue entry for item {ItemId}, HQ={IsHq}.", itemId, isHq);
            return false;
        }
    }

    private bool TryGetItemQuantities(out IDictionary itemQuantities)
    {
        itemQuantities = null!;

        try
        {
            var pluginInstance = TryGetDropboxPluginInstance();
            if (pluginInstance == null)
            {
                log.Warning("[XASlave] Dropbox queue reflection failed: no loaded Dropbox plugin instance was available.");
                return false;
            }

            var queueUiType = pluginInstance.GetType().Assembly.GetType("Dropbox.ItemQueueUI");
            if (queueUiType == null)
            {
                log.Warning(
                    "[XASlave] Dropbox queue reflection failed: type 'Dropbox.ItemQueueUI' was not found in {Assembly}.",
                    pluginInstance.GetType().Assembly.FullName ?? "unknown assembly");
                return false;
            }

            var itemQuantitiesField = queueUiType.GetField("ItemQuantities", BindingFlags.Public | BindingFlags.Static);
            if (itemQuantitiesField == null)
            {
                log.Warning("[XASlave] Dropbox queue reflection failed: public static field 'Dropbox.ItemQueueUI.ItemQuantities' was not found.");
                return false;
            }

            var reflectedValue = itemQuantitiesField.GetValue(null);
            if (reflectedValue is not IDictionary quantities)
            {
                log.Warning(
                    "[XASlave] Dropbox queue reflection failed: 'Dropbox.ItemQueueUI.ItemQuantities' returned {ActualType}, expected IDictionary.",
                    reflectedValue?.GetType().FullName ?? "null");
                return false;
            }

            itemQuantities = quantities;
            return true;
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
