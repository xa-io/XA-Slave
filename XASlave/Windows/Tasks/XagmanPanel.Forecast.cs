using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const double XagmanTradeCapacityRefreshSeconds = 5.0;
    private const double XagmanTradeCapacityInventoryRefreshSeconds = 30.0;
    private const double XagmanTradeCapacityQueryStepDelaySeconds = 0.1;
    private const double XagmanTradeCapacityStartupDelaySeconds = 2.0;
    private const double XagmanTradeCapacityInventoryMaxAgeDays = 45.0;
    private const double XagmanTradeCapacityPeerFreshSeconds = 15.0;
    private const double XagmanTradeCapacityForecastFreshSeconds = 30.0;
    private const int XagmanTradeCapacityMaxPublishedOwnerKeys = 512;
    private const int XagmanTradeCapacityMaxPublishedItemRows = 128;

    private static readonly InventoryType[] XagmanMainInventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    private static readonly InventoryType[] XagmanCrystalInventoryTypes =
    {
        InventoryType.Crystals,
    };

    private const uint XagmanFirstElementalCrystalItemId = 2;
    private const uint XagmanLastElementalCrystalItemId = 19;

    private DateTime xagmanTradeCapacityNextRefreshUtc = DateTime.MinValue;
    private DateTime xagmanTradeCapacityNextInventoryRefreshUtc = DateTime.MinValue;
    private readonly DateTime xagmanTradeCapacityStartupNotBeforeUtc =
        DateTime.UtcNow.AddSeconds(XagmanTradeCapacityStartupDelaySeconds);
    private bool xagmanTradeCapacityDirty = true;
    private int xagmanTradeCapacityQueryBudget;
    private bool xagmanTradeCapacityQueryDeferred;
    private XagmanTradeCapacityForecast? xagmanLocalTradeCapacityForecast;
    private XagmanTradeCapacityView? xagmanTradeCapacityView;
    private XagmanTradeCapacityView? xagmanPriorityTradeCapacityBaseline;
    private XagmanOwnerForecastView? xagmanOwnerForecastView;
    private readonly Dictionary<string, XagmanForecastSearchCacheEntry> xagmanTradeCapacitySearchCache = new(StringComparer.OrdinalIgnoreCase);
    private bool xagmanTradeCapacityDatabaseFallbackAttempted;
    private List<XagmanDbSearchMatch>? xagmanTradeCapacityDatabaseFallbackRows;

    private readonly record struct XagmanForecastItemKey(uint ItemId, bool IsHq);

    private readonly record struct XagmanForecastPolicyKey(
        uint ItemId,
        bool IsHq,
        XagmanItemApplicability Applicability);

    private readonly record struct XagmanForecastAggregateKey(string GroupKey, XagmanForecastItemKey ItemKey);

    private sealed class XagmanForecastItemDefinition
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public int StackSize { get; init; }

        public XagmanForecastItemKey Key => new(ItemId, IsHq);
    }

    private sealed class XagmanForecastSearchCacheEntry
    {
        public bool Succeeded { get; init; }
        public List<XagmanDbSearchMatch> Results { get; init; } = new();
    }

    private sealed class XagmanForecastInventorySnapshot
    {
        public bool IsKnown { get; set; }
        public int FreeSlots { get; set; }
        public long Gil { get; set; }
        public Dictionary<XagmanForecastItemKey, List<int>> Stacks { get; } = new();

        public void AddStack(XagmanForecastItemKey key, int quantity)
        {
            if (quantity <= 0)
                return;
            if (!Stacks.TryGetValue(key, out var quantities))
            {
                quantities = new List<int>();
                Stacks[key] = quantities;
            }
            quantities.Add(quantity);
        }

        public long GetQuantity(XagmanForecastItemKey key)
        {
            return Stacks.TryGetValue(key, out var quantities)
                ? quantities.Sum(quantity => (long)Math.Max(0, quantity))
                : 0L;
        }

        public long GetPartialStackHeadroom(XagmanForecastItemKey key, int stackSize)
        {
            if (stackSize <= 1)
                return 0L;
            if (IsXagmanElementalCrystalItem(key.ItemId))
                return Math.Max(0L, stackSize - GetQuantity(key));
            if (!Stacks.TryGetValue(key, out var quantities))
                return 0L;
            return quantities.Sum(quantity => (long)Math.Max(0, stackSize - Math.Max(0, quantity)));
        }
    }

    private sealed class XagmanTradeCapacityView
    {
        public DateTime GeneratedAtUtc { get; init; }
        public bool ServerMatching { get; init; }
        public int ConnectedOwnerPeerCount { get; init; }
        public int MissingForecastPeerCount { get; init; }
        public int StaleForecastPeerCount { get; init; }
        public int StalePresencePeerCount { get; init; }
        public int TruncatedForecastPeerCount { get; init; }
        public int SelectedOwnerCount { get; init; }
        public int KnownOwnerCount { get; init; }
        public int UnknownOwnerCount { get; init; }
        public int SelectedTonyCount { get; init; }
        public int OverallCollectionItemTypeCount { get; init; }
        public bool ShowCollectionFirstProjection { get; init; }
        public XagmanCollectionCapacityView? OverallCollectionCapacity { get; init; }
        public List<string> DuplicateOwnerKeys { get; init; } = new();
        public List<XagmanTradeCapacityViewGroup> Groups { get; init; } = new();
    }

    private sealed class XagmanOwnerForecastView
    {
        public DateTime GeneratedAtUtc { get; init; }
        public int SelectedOwnerCount { get; init; }
        public int KnownOwnerCount { get; init; }
        public int UnknownOwnerCount { get; init; }
        public int UnknownConditionalPolicyOwnerCount { get; init; }
        public int InvalidPlanningItemCount { get; init; }
        public int DuplicateItemKeyCount { get; init; }
        public int AllAvailableGiveItemCount { get; init; }
        public int AllAvailableTakeItemCount { get; init; }
        public bool IsTruncated { get; init; }
        public int KnownTakeBagReadyOwnerCount { get; init; }
        public int KnownTakeNeedingSlotsOwnerCount { get; init; }
        public long KnownTakeRequiredSlots { get; init; }
        public long KnownTakeFreeSlots { get; init; }
        public long KnownTakeShortSlots { get; init; }
        public long KnownTakeCrystalCapacityShortQuantity { get; init; }
        public bool HasFiniteTakeGil { get; init; }
        public List<XagmanOwnerGiveForecastViewItem> GiveItems { get; init; } = new();
        public List<XagmanOwnerBalanceForecastViewItem> BalanceItems { get; init; } = new();
        public List<XagmanOwnerTakeForecastViewItem> TakeItems { get; init; } = new();
    }

    private sealed class XagmanOwnerGiveForecastViewItem
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public string PolicyLabel { get; init; } = string.Empty;
        public int TargetQuantity { get; init; }
        public int SelectedOwnerCount { get; init; }
        public int KnownOwnerCount { get; init; }
        public int UnknownOwnerCount { get; init; }
        public long KnownToGiveQuantity { get; init; }
        public long ConfirmedShortageQuantity { get; init; }
        public long MaximumShortageQuantity { get; init; }
    }

    private sealed class XagmanOwnerBalanceForecastViewItem
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public string PolicyLabel { get; init; } = string.Empty;
        public int PerOwnerTargetQuantity { get; init; }
        public int SelectedOwnerCount { get; init; }
        public int KnownOwnerCount { get; init; }
        public int UnknownOwnerCount { get; init; }
        public int KnownOwnersShortCount { get; init; }
        public int MaximumOwnersShortCount { get; init; }
        public long TotalTargetQuantity { get; init; }
        public long KnownFilledQuantity { get; init; }
        public long ConfirmedNeededQuantity { get; init; }
        public long MaximumNeededQuantity { get; init; }
    }

    private sealed class XagmanOwnerTakeForecastViewItem
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public string PolicyLabel { get; init; } = string.Empty;
        public bool IsGil { get; init; }
        public int StackSize { get; init; }
        public int PerOwnerQuantity { get; init; }
        public int SelectedOwnerCount { get; init; }
        public int KnownOwnerCount { get; init; }
        public int UnknownOwnerCount { get; init; }
        public long KnownPartialFitQuantity { get; init; }
        public long KnownNewSlotsRequired { get; init; }
        public bool UsesCrystalPouch { get; init; }
        public long KnownCrystalCapacityShortQuantity { get; init; }
    }

    private sealed class XagmanTradeCapacityViewGroup
    {
        public string GroupKey { get; init; } = string.Empty;
        public int SelectedOwnerCount { get; set; }
        public int SelectedTonyCount { get; set; }
        public int KnownTonyCount { get; set; }
        public int UnknownTonyCount { get; set; }
        public int UnknownOwnerCount { get; set; }
        public long KnownFreeSlots { get; set; }
        public long RequiredCollectionSlots { get; set; }
        public int CollectionItemTypeCount { get; set; }
        public XagmanCollectionCapacityView? CollectionCapacity { get; set; }
        public List<XagmanRegionalCollectionCapacityView> FixedWorldRegionCapacities { get; init; } = new();
        public long SupplyShortageUnits { get; set; }
        public int SupplyShortageItemCount { get; set; }
        public long SupplyAfterCollectionShortageUnits { get; set; }
        public int SupplyAfterCollectionShortageItemCount { get; set; }
        public int AllAvailableRequestCount { get; set; }
        public List<XagmanTradeCapacityViewItem> Items { get; init; } = new();
        public List<XagmanTonySupplyAvailabilityViewItem> TonySupplyAvailability { get; init; } = new();
    }

    private sealed class XagmanTonySupplyAvailabilityViewItem
    {
        public string TonyCharacter { get; init; } = string.Empty;
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public long PooledNeededQuantity { get; init; }
        public int AllAvailableRequestCount { get; init; }
        public bool IsTonyInventoryKnown { get; init; }
        public long TonyAvailableQuantity { get; init; }
    }

    private sealed class XagmanTradeCapacityViewItem
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public int StackSize { get; init; }
        public long IncomingToTonyQuantity { get; init; }
        public long PartialStackHeadroom { get; init; }
        public long RequiredCollectionSlots { get; init; }
        public long NeededFromTonyQuantity { get; init; }
        public long TonyStockQuantity { get; init; }
        public long SupplyShortageQuantity { get; init; }
        public long ProjectedTonyStockAfterCollection { get; init; }
        public long ProjectedSupplyShortageAfterCollection { get; init; }
        public int AllAvailableRequestCount { get; init; }
        public int UnknownOwnerCount { get; init; }
    }

    private sealed class XagmanCollectionCapacityView
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public int StackSize { get; init; }
        public long KnownEmptyStackSlots { get; init; }
        public long EmptyStackCapacityQuantity { get; init; }
        public long PartialStackHeadroom { get; init; }
        public long TotalItemCapacityQuantity { get; init; }
        public long IncomingQuantity { get; init; }
        public long CollectableQuantity { get; init; }
        public long RemainingQuantity { get; init; }
        public long NewStackSlotsUsed { get; init; }
    }

    private sealed class XagmanRegionalCollectionCapacityView
    {
        public string RegionKey { get; init; } = string.Empty;
        public int SelectedTonyCount { get; init; }
        public int KnownTonyCount { get; init; }
        public int UnknownTonyCount { get; init; }
        public long KnownEmptyStackSlots { get; init; }
        public int StackSize { get; init; }
        public long EmptyStackCapacityQuantity { get; init; }
        public long PartialStackHeadroom { get; init; }
        public long TotalItemCapacityQuantity { get; init; }
    }

    private void InvalidateXagmanTradeCapacityForecast()
    {
        xagmanTradeCapacityDirty = true;
        xagmanTradeCapacityNextRefreshUtc = DateTime.MinValue;
    }

    private void FreezeXagmanPriorityTradeCapacityForecastBaseline()
    {
        InvalidateXagmanTradeCapacityForecast();
        UpdateXagmanTradeCapacityForecast();
        xagmanPriorityTradeCapacityBaseline = xagmanTradeCapacityView;
    }

    private void ClearXagmanPriorityTradeCapacityForecastBaseline()
    {
        xagmanPriorityTradeCapacityBaseline = null;
        InvalidateXagmanTradeCapacityForecast();
    }

    private void RefreshXagmanTradeCapacityInventorySnapshots()
    {
        ClearXagmanTradeCapacityInventorySearchCache();
        xagmanTradeCapacityNextInventoryRefreshUtc = DateTime.UtcNow.AddSeconds(XagmanTradeCapacityInventoryRefreshSeconds);
        InvalidateXagmanTradeCapacityForecast();
    }

    private void ClearXagmanTradeCapacityInventorySearchCache()
    {
        xagmanTradeCapacitySearchCache.Clear();
        xagmanTradeCapacityDatabaseFallbackAttempted = false;
        xagmanTradeCapacityDatabaseFallbackRows = null;
    }

    private XagmanTradeCapacityForecast? GetXagmanLocalTradeCapacityForecastForPresence()
    {
        if (plugin.Configuration.XagmanOutsideNetworkHelper || !plugin.XagmanPeers.IsConnected)
            return null;
        var role = xagmanRunning ? xagmanActiveRole : plugin.Configuration.XagmanRole;
        if (role != XagmanRole.FranchiseOwner)
            return null;

        // Completion, skip, and failure state can change immediately before a presence publish. Keep
        // the published owner set aligned with the still-pending run plan instead of waiting for the
        // normal forecast refresh interval.
        var expectedOwnerKeys = GetXagmanForecastOwnerKeys()
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var publishedOwnerKeys = xagmanLocalTradeCapacityForecast?.SelectedOwnerKeys ?? new List<string>();
        if (xagmanLocalTradeCapacityForecast == null
            || xagmanLocalTradeCapacityForecast.SelectedOwnerCount != expectedOwnerKeys.Count
            || !publishedOwnerKeys.SequenceEqual(
                expectedOwnerKeys.Take(XagmanTradeCapacityMaxPublishedOwnerKeys),
                StringComparer.OrdinalIgnoreCase))
        {
            InvalidateXagmanTradeCapacityForecast();
            UpdateXagmanTradeCapacityForecast();
        }

        return xagmanLocalTradeCapacityForecast;
    }

    private void UpdateXagmanTradeCapacityForecast()
    {
        var now = DateTime.UtcNow;
        CheckXagmanTonyAutoSelectionState(now);
        // Forecasts are advisory and may cold-JIT or query cached inventory data. Keep that work
        // out of the immediate plugin-load window, but never delay an operator-started Xagman run.
        if (!xagmanRunning && now < xagmanTradeCapacityStartupNotBeforeUtc)
            return;

        if (!xagmanTradeCapacityDirty && now < xagmanTradeCapacityNextRefreshUtc)
            return;

        xagmanTradeCapacityDirty = false;
        xagmanTradeCapacityNextRefreshUtc = now.AddSeconds(XagmanTradeCapacityRefreshSeconds);
        var cfg = plugin.Configuration;
        var role = xagmanRunning ? xagmanActiveRole : cfg.XagmanRole;
        if (role == XagmanRole.Tony
            && IsXagmanCollectionFirstCollectionPhase()
            && xagmanPriorityTradeCapacityBaseline != null)
        {
            xagmanTradeCapacityDirty = false;
            xagmanTradeCapacityNextRefreshUtc = now.AddSeconds(XagmanTradeCapacityRefreshSeconds);
            xagmanLocalTradeCapacityForecast = null;
            xagmanOwnerForecastView = null;
            xagmanTradeCapacityView = xagmanPriorityTradeCapacityBaseline;
            return;
        }
        if (role != XagmanRole.FranchiseOwner
            && (cfg.XagmanOutsideNetworkHelper || !plugin.XagmanPeers.IsConnected))
        {
            xagmanLocalTradeCapacityForecast = null;
            xagmanTradeCapacityView = null;
            xagmanOwnerForecastView = null;
            xagmanTradeCapacityQueryDeferred = false;
            ClearXagmanTradeCapacityInventorySearchCache();
            xagmanTradeCapacityNextInventoryRefreshUtc = DateTime.MinValue;
            return;
        }

        var continuingDeferredQuerySequence = xagmanTradeCapacityQueryDeferred;
        if (now >= xagmanTradeCapacityNextInventoryRefreshUtc && !continuingDeferredQuerySequence)
        {
            ClearXagmanTradeCapacityInventorySearchCache();
            xagmanTradeCapacityNextInventoryRefreshUtc = now.AddSeconds(XagmanTradeCapacityInventoryRefreshSeconds);
        }

        xagmanTradeCapacityQueryBudget = 1;
        xagmanTradeCapacityQueryDeferred = false;
        try
        {
            if (role == XagmanRole.FranchiseOwner)
            {
                xagmanLocalTradeCapacityForecast = BuildXagmanLocalTradeCapacityForecast(now, out var ownerForecastView);
                xagmanOwnerForecastView = ownerForecastView;
                xagmanTradeCapacityView = null;
            }
            else
            {
                xagmanLocalTradeCapacityForecast = null;
                xagmanOwnerForecastView = null;
                var ownerPeers = plugin.XagmanPeers.Peers
                    .Where(peer => peer.Role == XagmanRole.FranchiseOwner).ToList();
                xagmanTradeCapacityView = BuildXagmanTonyTradeCapacityView(now, ownerPeers);
                UpdateXagmanTonyAutoSelection(now, ownerPeers);
            }
            if (xagmanTradeCapacityQueryDeferred)
                xagmanTradeCapacityNextRefreshUtc = now.AddSeconds(XagmanTradeCapacityQueryStepDelaySeconds);
            else if (continuingDeferredQuerySequence)
                xagmanTradeCapacityNextInventoryRefreshUtc = now.AddSeconds(XagmanTradeCapacityInventoryRefreshSeconds);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Xagman] Trade-capacity forecast refresh failed");
            if (xagmanTonyAutoPending || xagmanTonyAutoTracking)
                StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("SFDAAB458DA9F7897"));
            xagmanLocalTradeCapacityForecast = null;
            xagmanTradeCapacityView = null;
            xagmanOwnerForecastView = null;
            xagmanTradeCapacityQueryDeferred = false;
            xagmanTradeCapacityNextInventoryRefreshUtc = DateTime.MinValue;
            xagmanTradeCapacityNextRefreshUtc = now.AddSeconds(15);
        }
    }

    private XagmanTradeCapacityForecast BuildXagmanLocalTradeCapacityForecast(
        DateTime now,
        out XagmanOwnerForecastView ownerForecastView)
    {
        var selectedOwners = GetXagmanForecastOwnerKeys()
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var configuredItems = plugin.Configuration.XagmanItems.ToList();
        var definitions = BuildXagmanForecastItemDefinitions(configuredItems);
        var definitionByKey = definitions.ToDictionary(definition => definition.Key);
        var conditionalKeys = configuredItems
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .Where(item => definitionByKey.ContainsKey(new XagmanForecastItemKey(item.ItemId, item.IsHq)))
            .GroupBy(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
            .Where(group => group.Any(item => item.Applicability != XagmanItemApplicability.All))
            .Select(group => group.Key)
            .ToHashSet();
        var snapshots = BuildXagmanForecastInventorySnapshots(selectedOwners, definitions);
        var rows = new Dictionary<XagmanForecastAggregateKey, XagmanTradeCapacityForecastItem>();
        var knownOwners = 0;

        foreach (var owner in selectedOwners)
        {
            snapshots.TryGetValue(owner, out var snapshot);
            var reconciledPartial = IsXagmanSupplyCycleActive() ? xagmanPartialOwners.GetValueOrDefault(owner) : null;
            var inventoryKnown = snapshot?.IsKnown == true || reconciledPartial != null;
            var effectiveItems = ResolveXagmanItemsForOwner(
                    configuredItems,
                    owner,
                    out var skippedUnknownConditionalGroup)
                .Where(item => definitionByKey.ContainsKey(new XagmanForecastItemKey(item.ItemId, item.IsHq)))
                .GroupBy(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
                .ToDictionary(group => group.Key, group => group.First());
            if (inventoryKnown && !skippedUnknownConditionalGroup)
                knownOwners++;
            var groupKey = NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(owner));
            var ownerKeys = effectiveItems.Keys.ToHashSet();
            if (skippedUnknownConditionalGroup)
                ownerKeys.UnionWith(conditionalKeys);

            foreach (var itemKey in ownerKeys)
            {
                var definition = definitionByKey[itemKey];
                var aggregateKey = new XagmanForecastAggregateKey(groupKey, itemKey);
                if (!rows.TryGetValue(aggregateKey, out var row))
                {
                    row = new XagmanTradeCapacityForecastItem
                    {
                        GroupKey = groupKey,
                        ItemId = definition.ItemId,
                        ItemName = definition.ItemName,
                        IsHq = definition.IsHq,
                        StackSize = definition.StackSize,
                    };
                    rows[aggregateKey] = row;
                }

                if (!effectiveItems.TryGetValue(itemKey, out var effectiveItem))
                {
                    row.UnknownOwnerCount++;
                    continue;
                }

                if (reconciledPartial != null)
                {
                    // The live visit's remaining ledger supersedes the pre-trade database
                    // snapshot, including finite Take/Give obligations and already supplied items.
                    row.KnownOwnerCount++;
                    foreach (var request in reconciledPartial.RequestedItems.Where(item =>
                        item.SelectorKind == XagmanItemSelectorKind.ExactItem
                        && item.ItemId == itemKey.ItemId && item.IsHq == itemKey.IsHq))
                    {
                        if (IsXagmanOpenEndedSupplyRequest(request)) row.AllAvailableRequestCount++;
                        else row.NeededFromTonyQuantity += Math.Max(0, request.Quantity);
                    }
                    foreach (var give in reconciledPartial.PendingGiveItems.Where(item =>
                        item.ItemId == itemKey.ItemId && item.IsHq == itemKey.IsHq))
                        row.IncomingToTonyQuantity += Math.Max(0, give.Quantity);
                    continue;
                }
                if (!inventoryKnown || snapshot == null)
                {
                    row.UnknownOwnerCount++;
                    // Take is a request from Tony, so its fixed/all-available demand depends on the
                    // selected owner, not on whether that owner's inventory snapshot was readable.
                    if (effectiveItem.Mode == XagmanItemMode.Take)
                    {
                        if (effectiveItem.Quantity <= 0)
                            row.AllAvailableRequestCount++;
                        else
                            row.NeededFromTonyQuantity += Math.Max(0, effectiveItem.Quantity);
                    }
                    continue;
                }

                row.KnownOwnerCount++;
                var currentQuantity = IsXagmanGilItem(definition.ItemId)
                    ? snapshot.Gil
                    : snapshot.GetQuantity(itemKey);
                var targetQuantity = Math.Max(0, effectiveItem.Quantity);
                switch (effectiveItem.Mode)
                {
                    case XagmanItemMode.Give:
                        row.IncomingToTonyQuantity += effectiveItem.Quantity <= 0
                            ? currentQuantity
                            : Math.Min(currentQuantity, targetQuantity);
                        break;
                    case XagmanItemMode.Take:
                        if (effectiveItem.Quantity <= 0)
                            row.AllAvailableRequestCount++;
                        else
                            row.NeededFromTonyQuantity += targetQuantity;
                        break;
                    case XagmanItemMode.Balance:
                        if (currentQuantity > targetQuantity)
                            row.IncomingToTonyQuantity += currentQuantity - targetQuantity;
                        else
                            row.NeededFromTonyQuantity += targetQuantity - currentQuantity;
                        break;
                    case XagmanItemMode.TopUp:
                        row.NeededFromTonyQuantity += Math.Max(0L, targetQuantity - currentQuantity);
                        break;
                }
            }
        }

        var orderedRows = rows.Values
            .OrderBy(row => GetXagmanForecastGroupSortIndex(row.GroupKey))
            .ThenBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ThenBy(row => row.IsHq)
            .ToList();
        var isTruncated = selectedOwners.Count > XagmanTradeCapacityMaxPublishedOwnerKeys
            || orderedRows.Count > XagmanTradeCapacityMaxPublishedItemRows;
        ownerForecastView = BuildXagmanOwnerForecastView(
            now,
            selectedOwners,
            definitions,
            snapshots,
            configuredItems,
            isTruncated);
        return new XagmanTradeCapacityForecast
        {
            GeneratedAtUtc = now,
            Revision = now.Ticks.ToString(CultureInfo.InvariantCulture),
            SelectedOwnerCount = selectedOwners.Count,
            KnownOwnerCount = knownOwners,
            UnknownOwnerCount = selectedOwners.Count - knownOwners,
            IsTruncated = isTruncated,
            // Green-value selectors and unsupported definitions are not represented by
            // these exact-item rows. Their absence cannot authorize Tony retirement.
            SchedulingPoliciesComplete = configuredItems
                .All(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                    && definitionByKey.ContainsKey(new XagmanForecastItemKey(item.ItemId, item.IsHq))),
            SelectedOwnerKeys = selectedOwners.Take(XagmanTradeCapacityMaxPublishedOwnerKeys).ToList(),
            Items = orderedRows.Take(XagmanTradeCapacityMaxPublishedItemRows).ToList(),
        };
    }

    private IReadOnlyList<string> GetXagmanForecastOwnerKeys()
    {
        if (xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && IsXagmanCollectionFirstCollectionPhase()
            && xagmanCollectionFirstOwnerFullPlan.Count > 0)
        {
            return xagmanCollectionFirstOwnerFullPlan.ToList();
        }
        if (xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && xagmanOwnerRunPlan.Count > 0)
        {
            // Earlier partial owners remain pending after the current pass advances beyond them.
            // They must stay in the forecast until their remaining requests are actually fulfilled.
            var failedOwnerKeys = plugin.TaskRunner.FailedCharacters
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return xagmanOwnerRunPlan
                .Concat(xagmanPartialOwners.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => !xagmanOwnerCompletedKeys.Contains(key))
                .Where(key => !xagmanSkippedCharacters.Contains(key))
                .Where(key => !failedOwnerKeys.Contains(key))
                .ToList();
        }

        return GetSelectedXagmanFranchiseCharacters();
    }

    private XagmanOwnerForecastView BuildXagmanOwnerForecastView(
        DateTime now,
        IReadOnlyCollection<string> selectedOwners,
        IReadOnlyCollection<XagmanForecastItemDefinition> definitions,
        IReadOnlyDictionary<string, XagmanForecastInventorySnapshot> snapshots,
        IReadOnlyCollection<XagmanItemEntry> configuredItems,
        bool isTruncated)
    {
        var definitionByKey = definitions.ToDictionary(definition => definition.Key);
        var planningItems = configuredItems
            .Where(item => (item.Mode == XagmanItemMode.Give && item.Quantity > 0)
                || (item.Mode == XagmanItemMode.Balance && item.Quantity > 0)
                || item.Mode == XagmanItemMode.Take)
            .ToList();
        var knownOwners = selectedOwners
            .Where(owner => snapshots.TryGetValue(owner, out var snapshot) && snapshot.IsKnown)
            .ToList();
        var knownOwnerCount = knownOwners.Count;
        var unknownOwnerCount = Math.Max(0, selectedOwners.Count - knownOwnerCount);
        var unknownConditionalPolicyOwnerCount = 0;
        var effectivePolicies = new List<(string Owner, XagmanItemEntry Item)>();
        foreach (var owner in selectedOwners)
        {
            var ownerItems = ResolveXagmanItemsForOwner(
                configuredItems,
                owner,
                out var skippedUnknownConditionalGroup);
            if (skippedUnknownConditionalGroup)
                unknownConditionalPolicyOwnerCount++;

            foreach (var item in ownerItems)
            {
                var itemKey = new XagmanForecastItemKey(item.ItemId, item.IsHq);
                if (definitionByKey.ContainsKey(itemKey))
                    effectivePolicies.Add((owner, item));
            }
        }

        var effectivePlanningPolicyGroups = effectivePolicies
            .Where(entry => (entry.Item.Mode == XagmanItemMode.Give && entry.Item.Quantity > 0)
                || (entry.Item.Mode == XagmanItemMode.Balance && entry.Item.Quantity > 0)
                || entry.Item.Mode == XagmanItemMode.Take)
            .GroupBy(entry => new XagmanForecastPolicyKey(
                entry.Item.ItemId,
                entry.Item.IsHq,
                entry.Item.Applicability))
            .Select(group => new
            {
                PolicyKey = group.Key,
                Policy = group.First().Item,
                Owners = group
                    .Select(entry => entry.Owner)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            .ToList();
        var giveRows = new List<XagmanOwnerGiveForecastViewItem>();
        var balanceRows = new List<XagmanOwnerBalanceForecastViewItem>();
        var takeRows = new List<XagmanOwnerTakeForecastViewItem>();

        foreach (var policyGroup in effectivePlanningPolicyGroups.Where(group =>
                     group.Policy.Mode == XagmanItemMode.Give && group.Policy.Quantity > 0))
        {
            var policy = policyGroup.Policy;
            var itemKey = new XagmanForecastItemKey(policy.ItemId, policy.IsHq);
            var definition = definitionByKey[itemKey];
            var policyKnownOwners = policyGroup.Owners
                .Where(owner => snapshots.TryGetValue(owner, out var snapshot) && snapshot.IsKnown)
                .ToList();
            var policyUnknownOwnerCount = Math.Max(0, policyGroup.Owners.Count - policyKnownOwners.Count);
            var target = (long)Math.Max(0, policy.Quantity);
            var knownToGive = policyKnownOwners.Sum(owner =>
            {
                var snapshot = snapshots[owner];
                var current = IsXagmanGilItem(definition.ItemId)
                    ? snapshot.Gil
                    : snapshot.GetQuantity(itemKey);
                return Math.Min(Math.Max(0L, current), target);
            });
            var knownShortage = Math.Max(0L, target - knownToGive);
            giveRows.Add(new XagmanOwnerGiveForecastViewItem
            {
                ItemId = definition.ItemId,
                ItemName = definition.ItemName,
                IsHq = definition.IsHq,
                PolicyLabel = GetXagmanItemPolicyLabel(policy),
                TargetQuantity = Math.Max(0, policy.Quantity),
                SelectedOwnerCount = policyGroup.Owners.Count,
                KnownOwnerCount = policyKnownOwners.Count,
                UnknownOwnerCount = policyUnknownOwnerCount,
                KnownToGiveQuantity = knownToGive,
                ConfirmedShortageQuantity = policyUnknownOwnerCount > 0 && knownShortage > 0 ? 0L : knownShortage,
                MaximumShortageQuantity = knownShortage,
            });
        }

        foreach (var policyGroup in effectivePlanningPolicyGroups.Where(group =>
                     group.Policy.Mode == XagmanItemMode.Balance && group.Policy.Quantity > 0))
        {
            var policy = policyGroup.Policy;
            var itemKey = new XagmanForecastItemKey(policy.ItemId, policy.IsHq);
            var definition = definitionByKey[itemKey];
            var policyKnownOwners = policyGroup.Owners
                .Where(owner => snapshots.TryGetValue(owner, out var snapshot) && snapshot.IsKnown)
                .ToList();
            var policyUnknownOwnerCount = Math.Max(0, policyGroup.Owners.Count - policyKnownOwners.Count);
            var target = (long)Math.Max(0, policy.Quantity);
            var knownFilled = 0L;
            var confirmedNeeded = 0L;
            var knownOwnersShort = 0;
            foreach (var owner in policyKnownOwners)
            {
                var snapshot = snapshots[owner];
                var current = IsXagmanGilItem(definition.ItemId)
                    ? snapshot.Gil
                    : snapshot.GetQuantity(itemKey);
                current = Math.Max(0L, current);
                knownFilled += Math.Min(current, target);
                var needed = Math.Max(0L, target - current);
                confirmedNeeded += needed;
                if (needed > 0)
                    knownOwnersShort++;
            }
            balanceRows.Add(new XagmanOwnerBalanceForecastViewItem
            {
                ItemId = definition.ItemId,
                ItemName = definition.ItemName,
                IsHq = definition.IsHq,
                PolicyLabel = GetXagmanItemPolicyLabel(policy),
                PerOwnerTargetQuantity = Math.Max(0, policy.Quantity),
                SelectedOwnerCount = policyGroup.Owners.Count,
                KnownOwnerCount = policyKnownOwners.Count,
                UnknownOwnerCount = policyUnknownOwnerCount,
                KnownOwnersShortCount = knownOwnersShort,
                MaximumOwnersShortCount = knownOwnersShort + policyUnknownOwnerCount,
                TotalTargetQuantity = target * Math.Max(0L, policyGroup.Owners.Count),
                KnownFilledQuantity = knownFilled,
                ConfirmedNeededQuantity = confirmedNeeded,
                MaximumNeededQuantity = confirmedNeeded + (target * Math.Max(0L, policyUnknownOwnerCount)),
            });
        }

        var requiredTakeSlotsByOwner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var policyGroup in effectivePlanningPolicyGroups.Where(group =>
                     group.Policy.Mode == XagmanItemMode.Take && group.Policy.Quantity > 0))
        {
            var policy = policyGroup.Policy;
            var itemKey = new XagmanForecastItemKey(policy.ItemId, policy.IsHq);
            var definition = definitionByKey[itemKey];
            var policyKnownOwners = policyGroup.Owners
                .Where(owner => snapshots.TryGetValue(owner, out var snapshot) && snapshot.IsKnown)
                .ToList();
            var policyUnknownOwnerCount = Math.Max(0, policyGroup.Owners.Count - policyKnownOwners.Count);
            var perOwnerQuantity = (long)Math.Max(0, policy.Quantity);
            var isGil = IsXagmanGilItem(definition.ItemId);
            var isElementalCrystal = IsXagmanElementalCrystalItem(definition.ItemId);
            var stackSize = isGil ? 0 : Math.Max(1, definition.StackSize);
            var knownPartialFit = 0L;
            var knownNewSlots = 0L;
            var knownCrystalCapacityShort = 0L;
            foreach (var owner in policyKnownOwners)
            {
                requiredTakeSlotsByOwner.TryAdd(owner, 0L);
                if (isGil)
                    continue;
                var snapshot = snapshots[owner];
                var partialFit = Math.Min(perOwnerQuantity, snapshot.GetPartialStackHeadroom(itemKey, stackSize));
                var remaining = Math.Max(0L, perOwnerQuantity - partialFit);
                var newSlots = isElementalCrystal || remaining <= 0
                    ? 0L
                    : (remaining + stackSize - 1L) / stackSize;
                knownPartialFit += partialFit;
                knownNewSlots += newSlots;
                if (isElementalCrystal)
                    knownCrystalCapacityShort += remaining;
                requiredTakeSlotsByOwner[owner] += newSlots;
            }
            takeRows.Add(new XagmanOwnerTakeForecastViewItem
            {
                ItemId = definition.ItemId,
                ItemName = definition.ItemName,
                IsHq = definition.IsHq,
                PolicyLabel = GetXagmanItemPolicyLabel(policy),
                IsGil = isGil,
                StackSize = stackSize,
                PerOwnerQuantity = Math.Max(0, policy.Quantity),
                SelectedOwnerCount = policyGroup.Owners.Count,
                KnownOwnerCount = policyKnownOwners.Count,
                UnknownOwnerCount = policyUnknownOwnerCount,
                KnownPartialFitQuantity = knownPartialFit,
                KnownNewSlotsRequired = knownNewSlots,
                UsesCrystalPouch = isElementalCrystal,
                KnownCrystalCapacityShortQuantity = knownCrystalCapacityShort,
            });
        }

        var knownTakeFreeSlots = requiredTakeSlotsByOwner.Keys.Sum(owner =>
            (long)Math.Max(0, snapshots[owner].FreeSlots));
        var knownTakeRequiredSlots = requiredTakeSlotsByOwner.Values.Sum();
        var knownTakeShortSlots = requiredTakeSlotsByOwner.Keys.Sum(owner =>
            Math.Max(0L, requiredTakeSlotsByOwner[owner] - Math.Max(0, snapshots[owner].FreeSlots)));
        var knownTakeBagReadyOwnerCount = requiredTakeSlotsByOwner.Keys.Count(owner =>
            requiredTakeSlotsByOwner[owner] <= Math.Max(0, snapshots[owner].FreeSlots));
        var invalidPlanningItemCount = planningItems.Count(item =>
            item.ItemId == 0
            || string.IsNullOrWhiteSpace(item.ItemName)
            || !definitionByKey.ContainsKey(new XagmanForecastItemKey(item.ItemId, item.IsHq)));
        var duplicateItemKeyCount = configuredItems
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .Where(item => definitionByKey.ContainsKey(new XagmanForecastItemKey(item.ItemId, item.IsHq)))
            .GroupBy(item => new XagmanForecastPolicyKey(
                item.ItemId,
                item.IsHq,
                item.Applicability))
            .Count(group => group.Count() > 1);
        var allAvailableGiveItemCount = effectivePolicies
            .Where(entry => entry.Item.Mode == XagmanItemMode.Give && entry.Item.Quantity <= 0)
            .Select(entry => new XagmanForecastPolicyKey(
                entry.Item.ItemId,
                entry.Item.IsHq,
                entry.Item.Applicability))
            .Distinct()
            .Count();
        var allAvailableTakeItemCount = effectivePolicies
            .Where(entry => entry.Item.Mode == XagmanItemMode.Take && entry.Item.Quantity <= 0)
            .Select(entry => new XagmanForecastPolicyKey(
                entry.Item.ItemId,
                entry.Item.IsHq,
                entry.Item.Applicability))
            .Distinct()
            .Count();

        return new XagmanOwnerForecastView
        {
            GeneratedAtUtc = now,
            SelectedOwnerCount = selectedOwners.Count,
            KnownOwnerCount = knownOwnerCount,
            UnknownOwnerCount = unknownOwnerCount,
            UnknownConditionalPolicyOwnerCount = unknownConditionalPolicyOwnerCount,
            InvalidPlanningItemCount = invalidPlanningItemCount,
            DuplicateItemKeyCount = duplicateItemKeyCount,
            AllAvailableGiveItemCount = allAvailableGiveItemCount,
            AllAvailableTakeItemCount = allAvailableTakeItemCount,
            IsTruncated = isTruncated,
            KnownTakeBagReadyOwnerCount = knownTakeBagReadyOwnerCount,
            KnownTakeNeedingSlotsOwnerCount = Math.Max(
                0,
                requiredTakeSlotsByOwner.Count - knownTakeBagReadyOwnerCount),
            KnownTakeRequiredSlots = knownTakeRequiredSlots,
            KnownTakeFreeSlots = knownTakeFreeSlots,
            KnownTakeShortSlots = knownTakeShortSlots,
            KnownTakeCrystalCapacityShortQuantity = takeRows.Sum(item =>
                Math.Max(0L, item.KnownCrystalCapacityShortQuantity)),
            HasFiniteTakeGil = takeRows.Any(item => item.IsGil),
            GiveItems = giveRows
                .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ThenBy(item => item.IsHq)
                .ThenBy(item => item.PolicyLabel, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            BalanceItems = balanceRows
                .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ThenBy(item => item.IsHq)
                .ThenBy(item => item.PolicyLabel, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            TakeItems = takeRows
                .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ThenBy(item => item.IsHq)
                .ThenBy(item => item.PolicyLabel, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private XagmanTradeCapacityView BuildXagmanTonyTradeCapacityView(
        DateTime now, IReadOnlyList<XagmanPeerPresence> allOwnerPeers)
    {
        var liveOwnerPeers = allOwnerPeers
            .Where(peer => IsXagmanTradeCapacityPeerFresh(peer, now))
            .ToList();
        var missingForecastPeerCount = liveOwnerPeers.Count(peer => peer.TradeCapacityForecast == null);
        var staleForecastPeerCount = liveOwnerPeers.Count(peer => peer.TradeCapacityForecast != null
            && !IsXagmanTradeCapacityForecastFresh(peer.TradeCapacityForecast, now));
        var forecasts = liveOwnerPeers
            .Select(peer => peer.TradeCapacityForecast)
            .Where(forecast => forecast != null && IsXagmanTradeCapacityForecastFresh(forecast, now))
            .Cast<XagmanTradeCapacityForecast>()
            .ToList();
        var serverMatching = xagmanRunning
            ? xagmanServerMatchingActive
            : plugin.Configuration.XagmanServerMatchingEnabled;
        var duplicateOwnerKeys = forecasts
            .SelectMany(forecast => forecast.SelectedOwnerKeys ?? new List<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedTonys = GetSelectedXagmanTonyCharacters()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.CharacterNameWorld))
            .GroupBy(entry => entry.CharacterNameWorld, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var aggregateRows = new Dictionary<XagmanForecastAggregateKey, XagmanTradeCapacityForecastItem>();

        foreach (var forecast in forecasts)
        {
            foreach (var source in forecast.Items ?? new List<XagmanTradeCapacityForecastItem>())
            {
                if (source.ItemId == 0)
                    continue;
                var groupKey = serverMatching ? NormalizeXagmanForecastRegion(source.GroupKey) : "All";
                var itemKey = new XagmanForecastItemKey(source.ItemId, source.IsHq);
                var aggregateKey = new XagmanForecastAggregateKey(groupKey, itemKey);
                if (!aggregateRows.TryGetValue(aggregateKey, out var row))
                {
                    row = new XagmanTradeCapacityForecastItem
                    {
                        GroupKey = groupKey,
                        ItemId = source.ItemId,
                        ItemName = source.ItemName,
                        IsHq = source.IsHq,
                        StackSize = GetXagmanForecastStackSize(source.ItemId, source.StackSize),
                    };
                    aggregateRows[aggregateKey] = row;
                }
                row.IncomingToTonyQuantity = SaturatingXagmanCapacityAdd(
                    row.IncomingToTonyQuantity,
                    source.IncomingToTonyQuantity);
                row.NeededFromTonyQuantity = SaturatingXagmanCapacityAdd(
                    row.NeededFromTonyQuantity,
                    source.NeededFromTonyQuantity);
                row.AllAvailableRequestCount = SaturatingXagmanCountAdd(
                    row.AllAvailableRequestCount,
                    source.AllAvailableRequestCount);
                row.KnownOwnerCount = SaturatingXagmanCountAdd(
                    row.KnownOwnerCount,
                    source.KnownOwnerCount);
                row.UnknownOwnerCount = SaturatingXagmanCountAdd(
                    row.UnknownOwnerCount,
                    source.UnknownOwnerCount);
            }
        }

        var definitions = aggregateRows.Values
            .GroupBy(row => new XagmanForecastItemKey(row.ItemId, row.IsHq))
            .Select(group =>
            {
                var row = group.First();
                return new XagmanForecastItemDefinition
                {
                    ItemId = row.ItemId,
                    ItemName = row.ItemName,
                    IsHq = row.IsHq,
                    StackSize = GetXagmanForecastStackSize(row.ItemId, row.StackSize),
                };
            })
            .ToList();
        var tonyKeys = selectedTonys.Select(entry => entry.CharacterNameWorld).ToList();
        var tonySnapshots = BuildXagmanForecastInventorySnapshots(tonyKeys, definitions);
        var groupOwnerCounts = forecasts
            .SelectMany(forecast => forecast.SelectedOwnerKeys ?? new List<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => serverMatching ? NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(key)) : "All", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var groupKeys = aggregateRows.Keys.Select(key => key.GroupKey)
            .Concat(groupOwnerCounts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetXagmanForecastGroupSortIndex)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = new List<XagmanTradeCapacityViewGroup>();

        foreach (var groupKey in groupKeys)
        {
            var groupTonyKeys = selectedTonys
                .Where(entry => !serverMatching
                    || NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(entry.CharacterNameWorld))
                        .Equals(groupKey, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.CharacterNameWorld)
                .ToList();
            if (serverMatching)
            {
                // Match the Tony run-plan ordering: stay inside this region, then follow the
                // configured sweep ordinal and use the character key as the stable tie-breaker.
                groupTonyKeys = groupTonyKeys
                    .OrderBy(key => WorldData.GetSweepOrdinalForWorld(GetWorldFromKey(key)))
                    .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            var knownTonySnapshots = groupTonyKeys
                .Where(key => tonySnapshots.TryGetValue(key, out var snapshot) && snapshot.IsKnown)
                .Select(key => tonySnapshots[key])
                .ToList();
            var sourceRows = aggregateRows
                .Where(pair => pair.Key.GroupKey.Equals(groupKey, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ItemId)
                .ThenBy(row => row.IsHq)
                .ToList();
            var group = new XagmanTradeCapacityViewGroup
            {
                GroupKey = groupKey,
                SelectedOwnerCount = groupOwnerCounts.TryGetValue(groupKey, out var ownerCount) ? ownerCount : 0,
                SelectedTonyCount = groupTonyKeys.Count,
                KnownTonyCount = knownTonySnapshots.Count,
                UnknownTonyCount = groupTonyKeys.Count - knownTonySnapshots.Count,
                KnownFreeSlots = knownTonySnapshots.Sum(snapshot => (long)Math.Max(0, snapshot.FreeSlots)),
                UnknownOwnerCount = sourceRows.Count == 0 ? 0 : sourceRows.Max(row => Math.Max(0, row.UnknownOwnerCount)),
            };

            foreach (var source in sourceRows)
            {
                var key = new XagmanForecastItemKey(source.ItemId, source.IsHq);
                var isGil = IsXagmanGilItem(source.ItemId);
                var isElementalCrystal = IsXagmanElementalCrystalItem(source.ItemId);
                var stackSize = isGil ? 0 : GetXagmanForecastStackSize(source.ItemId, source.StackSize);
                var partialHeadroom = isGil
                    ? 0L
                    : knownTonySnapshots.Sum(snapshot => snapshot.GetPartialStackHeadroom(key, stackSize));
                var incomingAfterPartials = Math.Max(0L, source.IncomingToTonyQuantity - partialHeadroom);
                var requiredSlots = isGil || isElementalCrystal || incomingAfterPartials <= 0
                    ? 0L
                    : ((incomingAfterPartials - 1L) / stackSize) + 1L;
                var tonyStock = isGil
                    ? knownTonySnapshots.Sum(snapshot => Math.Max(0L, snapshot.Gil - GetXagmanTonyGilMinimum()))
                    : knownTonySnapshots.Sum(snapshot => snapshot.GetQuantity(key));
                var supplyShortage = Math.Max(0L, source.NeededFromTonyQuantity - tonyStock);
                var projectedTonyStockAfterCollection = SaturatingXagmanCapacityAdd(
                    Math.Max(0L, tonyStock),
                    Math.Max(0L, source.IncomingToTonyQuantity));
                var projectedSupplyShortageAfterCollection = Math.Max(
                    0L,
                    source.NeededFromTonyQuantity - projectedTonyStockAfterCollection);
                var viewItem = new XagmanTradeCapacityViewItem
                {
                    ItemId = source.ItemId,
                    ItemName = source.ItemName,
                    IsHq = source.IsHq,
                    StackSize = stackSize,
                    IncomingToTonyQuantity = Math.Max(0L, source.IncomingToTonyQuantity),
                    PartialStackHeadroom = partialHeadroom,
                    RequiredCollectionSlots = requiredSlots,
                    NeededFromTonyQuantity = Math.Max(0L, source.NeededFromTonyQuantity),
                    TonyStockQuantity = tonyStock,
                    SupplyShortageQuantity = supplyShortage,
                    ProjectedTonyStockAfterCollection = projectedTonyStockAfterCollection,
                    ProjectedSupplyShortageAfterCollection = projectedSupplyShortageAfterCollection,
                    AllAvailableRequestCount = Math.Max(0, source.AllAvailableRequestCount),
                    UnknownOwnerCount = Math.Max(0, source.UnknownOwnerCount),
                };
                group.Items.Add(viewItem);
                group.RequiredCollectionSlots = SaturatingXagmanCapacityAdd(
                    group.RequiredCollectionSlots,
                    requiredSlots);
                group.SupplyShortageUnits = SaturatingXagmanCapacityAdd(
                    group.SupplyShortageUnits,
                    supplyShortage);
                if (supplyShortage > 0)
                    group.SupplyShortageItemCount++;
                group.SupplyAfterCollectionShortageUnits = SaturatingXagmanCapacityAdd(
                    group.SupplyAfterCollectionShortageUnits,
                    projectedSupplyShortageAfterCollection);
                if (projectedSupplyShortageAfterCollection > 0)
                    group.SupplyAfterCollectionShortageItemCount++;
                group.AllAvailableRequestCount = SaturatingXagmanCountAdd(
                    group.AllAvailableRequestCount,
                    viewItem.AllAvailableRequestCount);
            }
            var collectionItems = group.Items
                .Where(item => item.IncomingToTonyQuantity > 0 && item.StackSize > 0)
                .ToList();
            group.CollectionItemTypeCount = collectionItems.Count;
            if (collectionItems.Count == 1)
                group.CollectionCapacity = BuildXagmanCollectionCapacityView(
                    collectionItems[0],
                    group.KnownFreeSlots);
            if (!serverMatching && collectionItems.Count > 0)
            {
                group.FixedWorldRegionCapacities.AddRange(
                    BuildXagmanFixedWorldRegionCapacities(
                        groupTonyKeys,
                        tonySnapshots,
                        collectionItems.Count == 1 ? collectionItems[0] : null));
            }
            foreach (var tonyKey in groupTonyKeys)
            {
                var tonyKnown = tonySnapshots.TryGetValue(tonyKey, out var tonySnapshot)
                    && tonySnapshot.IsKnown;
                foreach (var source in sourceRows.Where(row =>
                             row.NeededFromTonyQuantity > 0 || row.AllAvailableRequestCount > 0))
                {
                    var itemKey = new XagmanForecastItemKey(source.ItemId, source.IsHq);
                    var available = !tonyKnown || tonySnapshot == null
                        ? 0L
                        : IsXagmanGilItem(source.ItemId)
                            ? Math.Max(0L, tonySnapshot.Gil - GetXagmanTonyGilMinimum())
                            : tonySnapshot.GetQuantity(itemKey);
                    group.TonySupplyAvailability.Add(new XagmanTonySupplyAvailabilityViewItem
                    {
                        TonyCharacter = tonyKey,
                        ItemId = source.ItemId,
                        ItemName = source.ItemName,
                        IsHq = source.IsHq,
                        PooledNeededQuantity = Math.Max(0L, source.NeededFromTonyQuantity),
                        AllAvailableRequestCount = Math.Max(0, source.AllAvailableRequestCount),
                        IsTonyInventoryKnown = tonyKnown,
                        TonyAvailableQuantity = Math.Max(0L, available),
                    });
                }
            }
            groups.Add(group);
        }

        var overallCollectionItemKeys = groups
            .SelectMany(group => group.Items)
            .Where(item => item.IncomingToTonyQuantity > 0 && item.StackSize > 0)
            .Select(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
            .Distinct()
            .ToList();
        var overallCollectionCapacity = serverMatching && overallCollectionItemKeys.Count == 1
            ? BuildXagmanOverallCollectionCapacityView(groups, overallCollectionItemKeys[0])
            : null;

        return new XagmanTradeCapacityView
        {
            GeneratedAtUtc = now,
            ServerMatching = serverMatching,
            ConnectedOwnerPeerCount = liveOwnerPeers.Count,
            MissingForecastPeerCount = missingForecastPeerCount,
            StaleForecastPeerCount = staleForecastPeerCount,
            StalePresencePeerCount = allOwnerPeers.Count - liveOwnerPeers.Count,
            TruncatedForecastPeerCount = forecasts.Count(forecast => forecast.IsTruncated),
            SelectedOwnerCount = forecasts.Sum(forecast => Math.Max(0, forecast.SelectedOwnerCount)),
            KnownOwnerCount = forecasts.Sum(forecast => Math.Max(0, forecast.KnownOwnerCount)),
            UnknownOwnerCount = forecasts.Sum(forecast => Math.Max(0, forecast.UnknownOwnerCount)),
            SelectedTonyCount = selectedTonys.Count,
            OverallCollectionItemTypeCount = overallCollectionItemKeys.Count,
            ShowCollectionFirstProjection = (IsXagmanCollectionFirstRunActive()
                    || (liveOwnerPeers.Count > 0
                        && liveOwnerPeers.All(peer =>
                            peer.CoordinationProtocolRevision == XagmanCollectionFirstCoordinationProtocolRevision
                            && peer.CollectionFirstRequested
                            && peer.HasConditionalItemPolicies)))
                && !IsXagmanCollectionFirstRestockPhase(),
            OverallCollectionCapacity = overallCollectionCapacity,
            DuplicateOwnerKeys = duplicateOwnerKeys,
            Groups = groups,
        };
    }

    private List<XagmanRegionalCollectionCapacityView> BuildXagmanFixedWorldRegionCapacities(
        IEnumerable<string> tonyKeys,
        IReadOnlyDictionary<string, XagmanForecastInventorySnapshot> tonySnapshots,
        XagmanTradeCapacityViewItem? collectionItem)
    {
        var itemKey = collectionItem == null
            ? default
            : new XagmanForecastItemKey(collectionItem.ItemId, collectionItem.IsHq);
        var stackSize = collectionItem == null ? 0 : Math.Max(1, collectionItem.StackSize);
        var isElementalCrystal = collectionItem != null
            && IsXagmanElementalCrystalItem(collectionItem.ItemId);
        var rows = new List<XagmanRegionalCollectionCapacityView>();

        foreach (var regionGroup in tonyKeys
                     .GroupBy(
                         key => NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(key)),
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => GetXagmanForecastGroupSortIndex(group.Key))
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var selectedKeys = regionGroup.ToList();
            var knownSnapshots = selectedKeys
                .Where(key => tonySnapshots.TryGetValue(key, out var snapshot) && snapshot.IsKnown)
                .Select(key => tonySnapshots[key])
                .ToList();
            var knownEmptyStackSlots = isElementalCrystal
                ? 0L
                : knownSnapshots.Aggregate(
                    0L,
                    (total, snapshot) => SaturatingXagmanCapacityAdd(
                        total,
                        Math.Max(0, snapshot.FreeSlots)));
            var emptyStackCapacity = stackSize > 0
                ? SaturatingXagmanCapacityMultiply(knownEmptyStackSlots, stackSize)
                : 0L;
            var partialStackHeadroom = stackSize > 0
                ? knownSnapshots.Aggregate(
                    0L,
                    (total, snapshot) => SaturatingXagmanCapacityAdd(
                        total,
                        snapshot.GetPartialStackHeadroom(itemKey, stackSize)))
                : 0L;

            rows.Add(new XagmanRegionalCollectionCapacityView
            {
                RegionKey = regionGroup.Key,
                SelectedTonyCount = selectedKeys.Count,
                KnownTonyCount = knownSnapshots.Count,
                UnknownTonyCount = selectedKeys.Count - knownSnapshots.Count,
                KnownEmptyStackSlots = knownEmptyStackSlots,
                StackSize = stackSize,
                EmptyStackCapacityQuantity = emptyStackCapacity,
                PartialStackHeadroom = partialStackHeadroom,
                TotalItemCapacityQuantity = SaturatingXagmanCapacityAdd(
                    emptyStackCapacity,
                    partialStackHeadroom),
            });
        }

        return rows;
    }

    private static XagmanCollectionCapacityView BuildXagmanCollectionCapacityView(
        XagmanTradeCapacityViewItem item,
        long knownFreeSlots)
    {
        var stackSize = Math.Max(1, item.StackSize);
        var emptyStackSlots = IsXagmanElementalCrystalItem(item.ItemId)
            ? 0L
            : Math.Max(0L, knownFreeSlots);
        var emptyStackCapacity = SaturatingXagmanCapacityMultiply(emptyStackSlots, stackSize);
        var partialStackHeadroom = Math.Max(0L, item.PartialStackHeadroom);
        var totalItemCapacity = SaturatingXagmanCapacityAdd(emptyStackCapacity, partialStackHeadroom);
        var incomingQuantity = Math.Max(0L, item.IncomingToTonyQuantity);
        var collectableQuantity = Math.Min(incomingQuantity, totalItemCapacity);
        var collectedIntoPartials = Math.Min(collectableQuantity, partialStackHeadroom);
        var collectedIntoEmptySlots = Math.Max(0L, collectableQuantity - collectedIntoPartials);
        var newStackSlotsUsed = collectedIntoEmptySlots <= 0
            ? 0L
            : ((collectedIntoEmptySlots - 1L) / stackSize) + 1L;

        return new XagmanCollectionCapacityView
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            IsHq = item.IsHq,
            StackSize = stackSize,
            KnownEmptyStackSlots = emptyStackSlots,
            EmptyStackCapacityQuantity = emptyStackCapacity,
            PartialStackHeadroom = partialStackHeadroom,
            TotalItemCapacityQuantity = totalItemCapacity,
            IncomingQuantity = incomingQuantity,
            CollectableQuantity = collectableQuantity,
            RemainingQuantity = Math.Max(0L, incomingQuantity - collectableQuantity),
            NewStackSlotsUsed = newStackSlotsUsed,
        };
    }

    private static XagmanCollectionCapacityView? BuildXagmanOverallCollectionCapacityView(
        IEnumerable<XagmanTradeCapacityViewGroup> groups,
        XagmanForecastItemKey itemKey)
    {
        var regionalCapacity = groups
            .Select(group => group.CollectionCapacity)
            .Where(capacity => capacity != null
                && capacity.ItemId == itemKey.ItemId
                && capacity.IsHq == itemKey.IsHq)
            .Cast<XagmanCollectionCapacityView>()
            .ToList();
        if (regionalCapacity.Count == 0)
            return null;

        var first = regionalCapacity[0];
        return new XagmanCollectionCapacityView
        {
            ItemId = first.ItemId,
            ItemName = first.ItemName,
            IsHq = first.IsHq,
            StackSize = first.StackSize,
            KnownEmptyStackSlots = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.KnownEmptyStackSlots)),
            EmptyStackCapacityQuantity = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.EmptyStackCapacityQuantity)),
            PartialStackHeadroom = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.PartialStackHeadroom)),
            TotalItemCapacityQuantity = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.TotalItemCapacityQuantity)),
            IncomingQuantity = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.IncomingQuantity)),
            CollectableQuantity = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.CollectableQuantity)),
            RemainingQuantity = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.RemainingQuantity)),
            NewStackSlotsUsed = regionalCapacity.Aggregate(
                0L,
                (total, capacity) => SaturatingXagmanCapacityAdd(total, capacity.NewStackSlotsUsed)),
        };
    }

    private static long SaturatingXagmanCapacityMultiply(long left, long right)
    {
        if (left <= 0 || right <= 0)
            return 0L;
        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }

    private static long SaturatingXagmanCapacityAdd(long left, long right)
    {
        left = Math.Max(0L, left);
        right = Math.Max(0L, right);
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static int SaturatingXagmanCountAdd(int left, int right)
    {
        left = Math.Max(0, left);
        right = Math.Max(0, right);
        return left > int.MaxValue - right ? int.MaxValue : left + right;
    }

    private List<XagmanForecastItemDefinition> BuildXagmanForecastItemDefinitions(IEnumerable<XagmanItemEntry> items)
    {
        return items
            .Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                && IsValidXagmanItemEntry(item)
                && (IsXagmanGilItem(item.ItemId) || IsXagmanForecastItemTradable(item.ItemId)))
            .GroupBy(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
            .Select(group =>
            {
                var item = group.First();
                return new XagmanForecastItemDefinition
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    IsHq = item.IsHq,
                    StackSize = GetXagmanForecastStackSize(item.ItemId),
                };
            })
            .ToList();
    }

    private Dictionary<string, XagmanForecastInventorySnapshot> BuildXagmanForecastInventorySnapshots(
        IReadOnlyCollection<string> characterKeys,
        IReadOnlyCollection<XagmanForecastItemDefinition> definitions)
    {
        var snapshots = new Dictionary<string, XagmanForecastInventorySnapshot>(StringComparer.OrdinalIgnoreCase);
        var remoteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var databaseAvailable = plugin.IpcClient.IsXaDatabaseAvailable();

        foreach (var characterKey in characterKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsXagmanCurrentLocalCharacter(characterKey) && Plugin.PlayerState.IsLoaded)
            {
                snapshots[characterKey] = BuildXagmanLiveForecastInventorySnapshot(definitions);
                continue;
            }

            var snapshot = new XagmanForecastInventorySnapshot();
            if (databaseAvailable
                && TryGetXagmanForecastCharacterInfo(characterKey, out var info)
                && info.MainInventoryTotalSlots > 0
                && IsXagmanTradeCapacityInventorySnapshotFresh(info, DateTime.UtcNow))
            {
                snapshot.IsKnown = true;
                snapshot.FreeSlots = Math.Max(0, info.MainInventoryFreeSlots);
                snapshot.Gil = Math.Max(0, info.Gil);
                remoteKeys.Add(characterKey);
            }
            snapshots[characterKey] = snapshot;
        }

        if (remoteKeys.Count == 0)
            return snapshots;

        var searchFailed = false;
        foreach (var definition in definitions.Where(definition => !IsXagmanGilItem(definition.ItemId)
                     && IsXagmanForecastItemTradable(definition.ItemId)))
        {
            var query = string.IsNullOrWhiteSpace(definition.ItemName)
                ? definition.ItemId.ToString(CultureInfo.InvariantCulture)
                : definition.ItemName;
            var matches = SearchXagmanTradeCapacityItems(query, out var searchSucceeded);
            if (!searchSucceeded)
            {
                searchFailed = true;
                continue;
            }
            foreach (var match in matches)
            {
                if (!remoteKeys.Contains(match.CharacterNameWorld)
                    || match.ItemId != definition.ItemId
                    || match.IsHq != definition.IsHq
                    || !IsXagmanSupportedItemContainer(match.ItemId, match.ContainerName)
                    || match.Quantity <= 0)
                {
                    continue;
                }
                snapshots[match.CharacterNameWorld].AddStack(definition.Key, match.Quantity);
            }
        }
        if (searchFailed)
        {
            foreach (var remoteKey in remoteKeys)
            {
                snapshots[remoteKey].IsKnown = false;
                snapshots[remoteKey].FreeSlots = 0;
                snapshots[remoteKey].Gil = 0;
                snapshots[remoteKey].Stacks.Clear();
            }
        }
        return snapshots;
    }

    private IReadOnlyList<XagmanDbSearchMatch> SearchXagmanTradeCapacityItems(string query, out bool succeeded)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            succeeded = false;
            return Array.Empty<XagmanDbSearchMatch>();
        }
        if (xagmanTradeCapacitySearchCache.TryGetValue(normalizedQuery, out var cached))
        {
            succeeded = cached.Succeeded;
            return cached.Results;
        }

        if (xagmanTradeCapacityQueryBudget <= 0)
        {
            xagmanTradeCapacityQueryDeferred = true;
            succeeded = false;
            return Array.Empty<XagmanDbSearchMatch>();
        }
        xagmanTradeCapacityQueryBudget--;

        var results = new List<XagmanDbSearchMatch>();
        succeeded = plugin.IpcClient.TrySearchItems(normalizedQuery, out var raw);
        if (succeeded)
        {
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseXagmanSearchMatch(line, out var match))
                {
                    succeeded = false;
                    results.Clear();
                    break;
                }
                results.Add(match);
            }
        }
        if (!succeeded)
            succeeded = TrySearchXagmanTradeCapacityItemsFromDatabase(normalizedQuery, out results);
        xagmanTradeCapacitySearchCache[normalizedQuery] = new XagmanForecastSearchCacheEntry
        {
            Succeeded = succeeded,
            Results = results,
        };
        return results;
    }

    private bool TrySearchXagmanTradeCapacityItemsFromDatabase(
        string query,
        out List<XagmanDbSearchMatch> results)
    {
        if (!xagmanTradeCapacityDatabaseFallbackAttempted)
        {
            xagmanTradeCapacityDatabaseFallbackAttempted = true;
            xagmanTradeCapacityDatabaseFallbackRows = TryLoadXagmanTradeCapacityDatabaseFallbackRows(out var loadedRows)
                ? loadedRows
                : null;
        }

        if (xagmanTradeCapacityDatabaseFallbackRows == null)
        {
            results = new List<XagmanDbSearchMatch>();
            return false;
        }
        results = xagmanTradeCapacityDatabaseFallbackRows
            .Where(match => match.ItemName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        return true;
    }

    private bool TryLoadXagmanTradeCapacityDatabaseFallbackRows(out List<XagmanDbSearchMatch> results)
    {
        results = new List<XagmanDbSearchMatch>();
        try
        {
            var dbPath = plugin.IpcClient.GetDbPath();
            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
                return false;

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT character_name, world, items_json FROM xa_characters";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var characterName = reader["character_name"]?.ToString() ?? string.Empty;
                var world = reader["world"]?.ToString() ?? string.Empty;
                var itemsJson = reader["items_json"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(characterName)
                    || string.IsNullOrWhiteSpace(world)
                    || string.IsNullOrWhiteSpace(itemsJson))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(itemsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("ItemName", out var itemNameElement))
                    {
                        continue;
                    }
                    var itemName = itemNameElement.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(itemName)
                        || !entry.TryGetProperty("ItemId", out var itemIdElement)
                        || !TryGetJsonInt64(itemIdElement, out var parsedItemId)
                        || parsedItemId <= 0
                        || parsedItemId > uint.MaxValue
                        || !entry.TryGetProperty("Quantity", out var quantityElement)
                        || !TryGetJsonInt32(quantityElement, out var quantity)
                        || quantity <= 0)
                    {
                        continue;
                    }

                    var containerName = entry.TryGetProperty("ContainerName", out var containerElement)
                        ? containerElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!IsXagmanSupportedItemContainer((uint)parsedItemId, containerName))
                        continue;
                    var isHq = entry.TryGetProperty("IsHq", out var hqElement)
                        && hqElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && hqElement.GetBoolean();
                    results.Add(new XagmanDbSearchMatch
                    {
                        CharacterNameWorld = $"{characterName}@{world}",
                        ContainerName = containerName,
                        ItemId = (uint)parsedItemId,
                        ItemName = itemName,
                        Quantity = quantity,
                        IsHq = isHq,
                    });
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[Xagman] XA Database fallback item search failed");
            results.Clear();
            return false;
        }
    }

    private unsafe XagmanForecastInventorySnapshot BuildXagmanLiveForecastInventorySnapshot(
        IReadOnlyCollection<XagmanForecastItemDefinition> definitions)
    {
        var snapshot = new XagmanForecastInventorySnapshot();
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return snapshot;
            snapshot.FreeSlots = Math.Max(0, (int)inventoryManager->GetEmptySlotsInBag());
            snapshot.Gil = (long)Math.Min((ulong)long.MaxValue, inventoryManager->GetGil());
            var requestedKeys = definitions
                .Where(definition => !IsXagmanGilItem(definition.ItemId))
                .Select(definition => definition.Key)
                .ToHashSet();
            if (requestedKeys.Count == 0)
            {
                snapshot.IsKnown = true;
                return snapshot;
            }
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            var loadedContainerCount = 0;
            var inventoryTypes = requestedKeys.Any(key => IsXagmanElementalCrystalItem(key.ItemId))
                ? XagmanMainInventoryTypes.Concat(XagmanCrystalInventoryTypes).ToArray()
                : XagmanMainInventoryTypes;

            foreach (var inventoryType in inventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded)
                    continue;
                loadedContainerCount++;
                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot == null || slot->ItemId == 0 || slot->IsSymbolic || slot->SpiritbondOrCollectability > 0)
                        continue;
                    var itemId = slot->GetBaseItemId();
                    var key = new XagmanForecastItemKey(itemId, slot->IsHighQuality());
                    if (!requestedKeys.Contains(key))
                        continue;
                    if (!itemSheet.TryGetRow(itemId, out var itemRow) || itemRow.IsUntradable)
                        continue;
                    snapshot.AddStack(key, (int)slot->Quantity);
                }
            }
            snapshot.IsKnown = loadedContainerCount == inventoryTypes.Length;
            if (!snapshot.IsKnown)
            {
                snapshot.FreeSlots = 0;
                snapshot.Gil = 0;
                snapshot.Stacks.Clear();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[Xagman] Could not read live inventory for the trade-capacity forecast");
            snapshot.IsKnown = false;
            snapshot.FreeSlots = 0;
            snapshot.Gil = 0;
            snapshot.Stacks.Clear();
        }
        return snapshot;
    }

    private bool TryGetXagmanForecastCharacterInfo(string characterKey, out ReloggerCharacterData info)
    {
        if (plugin.Configuration.ReloggerCharacterInfo.TryGetValue(characterKey, out var exact) && exact != null)
        {
            info = exact;
            return true;
        }
        foreach (var pair in plugin.Configuration.ReloggerCharacterInfo)
        {
            if (!pair.Key.Equals(characterKey, StringComparison.OrdinalIgnoreCase) || pair.Value == null)
                continue;
            info = pair.Value;
            return true;
        }
        info = new ReloggerCharacterData();
        return false;
    }

    private static bool IsXagmanMainInventoryContainer(string containerName)
    {
        return containerName.Equals("Inventory 1", StringComparison.OrdinalIgnoreCase)
            || containerName.Equals("Inventory 2", StringComparison.OrdinalIgnoreCase)
            || containerName.Equals("Inventory 3", StringComparison.OrdinalIgnoreCase)
            || containerName.Equals("Inventory 4", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsXagmanElementalCrystalItem(uint itemId)
    {
        return itemId >= XagmanFirstElementalCrystalItemId
            && itemId <= XagmanLastElementalCrystalItemId;
    }

    private static bool IsXagmanCrystalInventoryContainer(string containerName)
    {
        return containerName.Equals("Crystals", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsXagmanSupportedItemContainer(uint itemId, string containerName)
    {
        return IsXagmanElementalCrystalItem(itemId)
            ? IsXagmanCrystalInventoryContainer(containerName)
            : IsXagmanMainInventoryContainer(containerName);
    }

    private static bool IsXagmanTradeCapacityInventorySnapshotFresh(ReloggerCharacterData info, DateTime now)
    {
        if (info.XaDatabaseSnapshotUpdatedUtc <= DateTime.MinValue)
            return false;
        var ageDays = (now - info.XaDatabaseSnapshotUpdatedUtc).TotalDays;
        return ageDays >= -1.0 && ageDays <= XagmanTradeCapacityInventoryMaxAgeDays;
    }

    private int GetXagmanForecastStackSize(uint itemId, int fallback = 1)
    {
        if (IsXagmanGilItem(itemId))
            return 0;
        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            if (itemSheet.TryGetRow(itemId, out var itemRow))
                return Math.Max(1, (int)itemRow.StackSize);
        }
        catch
        {
        }
        return Math.Max(1, fallback);
    }

    private bool IsXagmanForecastItemTradable(uint itemId)
    {
        if (IsXagmanGilItem(itemId))
            return true;
        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            return itemSheet.TryGetRow(itemId, out var itemRow) && !itemRow.IsUntradable;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsXagmanTradeCapacityPeerFresh(XagmanPeerPresence peer, DateTime now)
    {
        return peer.LastSeenUtc > DateTime.MinValue
            && (now - peer.LastSeenUtc).TotalSeconds <= XagmanTradeCapacityPeerFreshSeconds;
    }

    private static bool IsXagmanTradeCapacityForecastFresh(XagmanTradeCapacityForecast forecast, DateTime now)
    {
        if (forecast.GeneratedAtUtc <= DateTime.MinValue)
            return false;
        var ageSeconds = (now - forecast.GeneratedAtUtc).TotalSeconds;
        return ageSeconds >= -300.0 && ageSeconds <= XagmanTradeCapacityForecastFreshSeconds;
    }

    private static string NormalizeXagmanForecastRegion(string region)
    {
        return WorldData.RegionOrder.Any(value => value.Equals(region, StringComparison.OrdinalIgnoreCase))
            ? WorldData.RegionOrder.First(value => value.Equals(region, StringComparison.OrdinalIgnoreCase))
            : "Unknown";
    }

    private static int GetXagmanForecastGroupSortIndex(string groupKey)
    {
        if (groupKey.Equals("All", StringComparison.OrdinalIgnoreCase))
            return -1;
        var index = Array.FindIndex(WorldData.RegionOrder, region => region.Equals(groupKey, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private void DrawXagmanOwnerForecasts(Configuration cfg)
    {
        var hasOwnerForecastMode = cfg.XagmanItems.Any(item =>
            item.Mode == XagmanItemMode.Give
            || (item.Mode == XagmanItemMode.Balance && item.Quantity > 0)
            || item.Mode == XagmanItemMode.Take);
        if (cfg.XagmanRole != XagmanRole.FranchiseOwner || !hasOwnerForecastMode)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SA0853EBE0D12C002"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S0E91610117029A62") + "##xagmanOwnerForecastRefresh")))
            RefreshXagmanTradeCapacityInventorySnapshots();
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD158ED75B40CD3A1"));

        if (xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S0898A4310D449238"));
            return;
        }

        var view = xagmanOwnerForecastView;
        if (view == null)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7EDB4D21D08E78F7"));
            return;
        }

        ImGui.TextDisabled(
            global::XASlave.Localization.UiLocale.FormatKey("SFECDE1B42B14934F", view.SelectedOwnerCount, view.KnownOwnerCount, view.UnknownOwnerCount, view.GeneratedAtUtc.ToLocalTime()));
        if (view.SelectedOwnerCount <= 0)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S84AC4E95251FEED7"));
            return;
        }
        if (view.InvalidPlanningItemCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S06931CDF7D2D06EB", view.InvalidPlanningItemCount));
        if (view.DuplicateItemKeyCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("SA0A336ED02A119F8", view.DuplicateItemKeyCount));
        if (view.IsTruncated)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.Get("SFBB7F313AECD9E87"));
        if (view.UnknownOwnerCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S9F78808BF74E1253", view.UnknownOwnerCount));
        if (view.UnknownConditionalPolicyOwnerCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("SBA36FF2C7C1A966F", view.UnknownConditionalPolicyOwnerCount));
        if (view.AllAvailableGiveItemCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S01FF0BCF0B7C41FA", view.AllAvailableGiveItemCount));
        if (view.AllAvailableTakeItemCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S197F510CCB6F6AC7", view.AllAvailableTakeItemCount));

        if (view.GiveItems.Count > 0)
            DrawXagmanOwnerGiveForecast(view);
        if (view.BalanceItems.Count > 0)
            DrawXagmanOwnerBalanceForecast(view);
        if (view.TakeItems.Count > 0 || view.AllAvailableTakeItemCount > 0)
            DrawXagmanOwnerTakeForecast(view);
        if (view.GiveItems.Count == 0 && view.BalanceItems.Count == 0 && view.TakeItems.Count == 0)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD3D5DEA433622BDA"));
    }

    private void DrawXagmanOwnerGiveForecast(XagmanOwnerForecastView view)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S332048F1B8B03694"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S316D8FB05DDA6B00"));
        var confirmedShortage = view.GiveItems.Sum(item => item.ConfirmedShortageQuantity);
        var maximumShortage = view.GiveItems.Sum(item => item.MaximumShortageQuantity);
        var configurationUncertainty = view.InvalidPlanningItemCount > 0
            || view.DuplicateItemKeyCount > 0
            || view.UnknownConditionalPolicyOwnerCount > 0
            || view.IsTruncated;
        if (confirmedShortage > 0 && confirmedShortage == maximumShortage && !configurationUncertainty)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("SF17837E6627D5D41", confirmedShortage));
        }
        else if (maximumShortage > 0 || configurationUncertainty)
        {
            var range = confirmedShortage == maximumShortage
                ? maximumShortage.ToString("N0", CultureInfo.InvariantCulture)
                : $"{confirmedShortage:N0}-{maximumShortage:N0}";
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("S5B301275272F64BF", range));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.Get("S7CF6DA9BD2340104"));
        }

        using (var table = ImRaii.Table("XagmanOwnerGiveForecastItems", 5,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
        if (!table)
            return;
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"), ImGuiTableColumnFlags.WidthStretch);
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SEF9CCA63A56C9B1A"), ImGuiTableColumnFlags.WidthFixed, Scale(105f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S58F5DF9B241AE8A8"), ImGuiTableColumnFlags.WidthFixed, Scale(65f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S18ABE5EB32BD27A5"), ImGuiTableColumnFlags.WidthFixed, Scale(105f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SE74E6C89734DBA9C"), ImGuiTableColumnFlags.WidthFixed, Scale(105f));
        global::XASlave.Localization.UiNative.TableHeadersRow();
        foreach (var item in view.GiveItems)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetXagmanOwnerForecastItemLabel(
                item.ItemName,
                item.IsHq,
                item.PolicyLabel));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.TargetQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.SelectedOwnerCount.ToString("N0", CultureInfo.InvariantCulture));
            if (item.UnknownOwnerCount > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S5540861DB1C7BEA7", item.KnownOwnerCount, item.UnknownOwnerCount));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.KnownToGiveQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            DrawXagmanOwnerForecastRange(item.ConfirmedShortageQuantity, item.MaximumShortageQuantity);
        }
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S2721972A5D38B357"));
    }

    private void DrawXagmanOwnerBalanceForecast(XagmanOwnerForecastView view)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S1E781F8248D91BFD"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SAF462EC80A13C784"));
        var confirmedNeeded = view.BalanceItems.Sum(item => item.ConfirmedNeededQuantity);
        var maximumNeeded = view.BalanceItems.Sum(item => item.MaximumNeededQuantity);
        var configurationUncertainty = view.InvalidPlanningItemCount > 0
            || view.DuplicateItemKeyCount > 0
            || view.UnknownConditionalPolicyOwnerCount > 0
            || view.IsTruncated;
        if (confirmedNeeded > 0 && confirmedNeeded == maximumNeeded && !configurationUncertainty)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("SC782EB69615759C6", confirmedNeeded));
        }
        else if (maximumNeeded > confirmedNeeded || configurationUncertainty)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("S7AAE002814A9B137", confirmedNeeded, maximumNeeded));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.Get("S4BA6A641BCF3BB64"));
        }

        using (var table = ImRaii.Table("XagmanOwnerBalanceForecastItems", 7,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
        if (!table)
            return;
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"), ImGuiTableColumnFlags.WidthStretch);
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SB21A27F01CC6C1F1"), ImGuiTableColumnFlags.WidthFixed, Scale(85f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S58F5DF9B241AE8A8"), ImGuiTableColumnFlags.WidthFixed, Scale(65f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S936AC261EEE0C74B"), ImGuiTableColumnFlags.WidthFixed, Scale(95f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S236017BC551BBD2C"), ImGuiTableColumnFlags.WidthFixed, Scale(95f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8C6A1DE9BBE4190A"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SC067A0C833BD7716"), ImGuiTableColumnFlags.WidthFixed, Scale(120f));
        global::XASlave.Localization.UiNative.TableHeadersRow();
        foreach (var item in view.BalanceItems)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetXagmanOwnerForecastItemLabel(
                item.ItemName,
                item.IsHq,
                item.PolicyLabel));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.PerOwnerTargetQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.SelectedOwnerCount.ToString("N0", CultureInfo.InvariantCulture));
            if (item.UnknownOwnerCount > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S5540861DB1C7BEA7", item.KnownOwnerCount, item.UnknownOwnerCount));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.TotalTargetQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.KnownFilledQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            var ownersShort = item.KnownOwnersShortCount == item.MaximumOwnersShortCount
                ? item.KnownOwnersShortCount.ToString("N0", CultureInfo.InvariantCulture)
                : $"{item.KnownOwnersShortCount:N0}-{item.MaximumOwnersShortCount:N0}";
            ImGui.TextDisabled(ownersShort);
            ImGui.TableNextColumn();
            DrawXagmanOwnerForecastRange(item.ConfirmedNeededQuantity, item.MaximumNeededQuantity);
        }
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8E78C9DE82C647B7"));
    }

    private void DrawXagmanOwnerTakeForecast(XagmanOwnerForecastView view)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S7858C7ABCF1E4D6F"));
        if (view.TakeItems.Count == 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                global::XASlave.Localization.UiLocale.Get("S4569A3FC6262EF0B"));
            return;
        }
        ImGui.TextDisabled(
            global::XASlave.Localization.UiLocale.FormatKey("S0C6BE12E86D57ED4", view.SelectedOwnerCount, view.KnownTakeBagReadyOwnerCount, view.KnownTakeNeedingSlotsOwnerCount, view.UnknownOwnerCount));
        var takeUncertainty = view.UnknownOwnerCount > 0
            || view.InvalidPlanningItemCount > 0
            || view.DuplicateItemKeyCount > 0
            || view.UnknownConditionalPolicyOwnerCount > 0
            || view.IsTruncated
            || view.AllAvailableTakeItemCount > 0;
        var slotSummary = (global::XASlave.Localization.UiMessage.Format("S226E6B2227A20E4A", view.KnownTakeRequiredSlots, view.KnownTakeFreeSlots, view.KnownTakeShortSlots)).Display;
        if (view.KnownTakeShortSlots > 0)
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), slotSummary);
        else if (takeUncertainty)
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("SC989466F02AF12D9", slotSummary));
        else
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S2D869A52750141C1", slotSummary));

        if (view.KnownTakeCrystalCapacityShortQuantity > 0)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("S483CADCE96EFDD7B", view.KnownTakeCrystalCapacityShortQuantity));
        }

        if (view.HasFiniteTakeGil)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.Get("S24C28ECF97696E99"));

        using (var table = ImRaii.Table("XagmanOwnerTakeForecastItems", 7,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
        if (!table)
            return;
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"), ImGuiTableColumnFlags.WidthStretch);
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SA9531F64027967A0"), ImGuiTableColumnFlags.WidthFixed, Scale(75f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S58F5DF9B241AE8A8"), ImGuiTableColumnFlags.WidthFixed, Scale(65f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SE551641242CE7246"), ImGuiTableColumnFlags.WidthFixed, Scale(55f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S25ACF838E92D7A54"), ImGuiTableColumnFlags.WidthFixed, Scale(115f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S75BE51916BF0368B"), ImGuiTableColumnFlags.WidthFixed, Scale(105f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SECE1E65C4DC4CB84"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
        global::XASlave.Localization.UiNative.TableHeadersRow();
        foreach (var item in view.TakeItems)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetXagmanOwnerForecastItemLabel(
                item.ItemName,
                item.IsHq,
                item.PolicyLabel));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.PerOwnerQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.SelectedOwnerCount.ToString("N0", CultureInfo.InvariantCulture));
            if (item.UnknownOwnerCount > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S5540861DB1C7BEA7", item.KnownOwnerCount, item.UnknownOwnerCount));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.IsGil ? global::XASlave.Localization.UiMessage.Key("SAB0FCEC08E18A7BC") : global::XASlave.Localization.UiMessage.Raw(item.StackSize.ToString("N0", CultureInfo.InvariantCulture)))).Display);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.IsGil ? global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9") : global::XASlave.Localization.UiMessage.Raw(item.KnownPartialFitQuantity.ToString("N0", CultureInfo.InvariantCulture)))).Display);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.KnownNewSlotsRequired.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.UsesCrystalPouch ? global::XASlave.Localization.UiMessage.Raw(item.KnownCrystalCapacityShortQuantity.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
        }
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S43E2E5A137134975"));
    }

    private static string GetXagmanOwnerForecastItemLabel(
        string itemName,
        bool isHq,
        string policyLabel)
    {
        var itemLabel = isHq ? global::XASlave.Localization.UiMessage.Format("S5864848AE53E7CAF", itemName) : global::XASlave.Localization.UiMessage.Raw(itemName);
        return (string.IsNullOrWhiteSpace(policyLabel)
            ? itemLabel
            : global::XASlave.Localization.UiMessage.Format("SFA4816846A52134E", itemLabel, global::XASlave.Localization.UiMessage.Authored(policyLabel))).Display;
    }

    private static void DrawXagmanOwnerForecastRange(long confirmed, long maximum)
    {
        if (confirmed != maximum)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S1101B7E441F216AE", confirmed, maximum));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S90AB1CBE5DFDF1BB"));
        }
        else if (confirmed > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), confirmed.ToString("N0", CultureInfo.InvariantCulture));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), (global::XASlave.Localization.UiMessage.Key("S5FECEB66FFC86F38")).Display);
        }
    }

    private void DrawXagmanTradeCapacityForecast(Configuration cfg)
    {
        if (cfg.XagmanOutsideNetworkHelper
            || cfg.XagmanRole != XagmanRole.Tony
            || !plugin.XagmanPeers.IsConnected
            || !plugin.XagmanPeers.Peers.Any(peer => peer.Role == XagmanRole.FranchiseOwner))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SCB92B0E791095341"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S0E91610117029A62") + "##xagmanTradeCapacityRefresh")))
            RefreshXagmanTradeCapacityInventorySnapshots();
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S677B7E62445F8B82"));

        var view = xagmanTradeCapacityView;
        if (view == null)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SB1D238D525213987"));
            return;
        }

        ImGui.TextDisabled(
            global::XASlave.Localization.UiLocale.FormatKey("SC705F6BB882BACFC", view.ConnectedOwnerPeerCount, view.SelectedOwnerCount, view.SelectedTonyCount, view.GeneratedAtUtc.ToLocalTime()));
        if (view.ShowCollectionFirstProjection)
        {
            ImGui.TextWrapped(
                global::XASlave.Localization.UiLocale.Get("S922A3D4FF39929D5"));
        }
        if (view.MissingForecastPeerCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("SC979A9FBA3650478", view.MissingForecastPeerCount));
        if (view.StaleForecastPeerCount > 0 || view.StalePresencePeerCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("SFECF12B530E9962D", view.StaleForecastPeerCount, view.StalePresencePeerCount));
        if (view.TruncatedForecastPeerCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S5A069C1992C995A3", view.TruncatedForecastPeerCount));
        if (view.UnknownOwnerCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S1A9DDCED07561FD7", view.UnknownOwnerCount));
        if (view.DuplicateOwnerKeys.Count > 0)
        {
            var preview = string.Join(", ", view.DuplicateOwnerKeys.Take(3));
            var remainder = ((view.DuplicateOwnerKeys.Count > 3 ? global::XASlave.Localization.UiMessage.Format("S13248AD5985692FB", view.DuplicateOwnerKeys.Count - 3) : global::XASlave.Localization.UiMessage.Raw(string.Empty))).Display;
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("SE4B422867A2A10CB", preview, remainder));
        }

        if (view.Groups.Count == 0)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SB8A41E471433BA21"));
            return;
        }

        if (view.ServerMatching && view.OverallCollectionCapacity != null)
        {
            var overallInputUncertainty = view.DuplicateOwnerKeys.Count > 0
                || view.UnknownOwnerCount > 0
                || view.MissingForecastPeerCount > 0
                || view.StaleForecastPeerCount > 0
                || view.StalePresencePeerCount > 0
                || view.TruncatedForecastPeerCount > 0
                || view.Groups.Any(group => group.UnknownOwnerCount > 0 || group.UnknownTonyCount > 0);
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SB3EE879E3793392E"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1C6B93FD8DE84CEE"));
            DrawXagmanCollectionCapacitySummary(view.OverallCollectionCapacity, overallInputUncertainty);
        }
        else if (view.ServerMatching && view.OverallCollectionItemTypeCount > 1)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SB3EE879E3793392E"));
            ImGui.TextWrapped(
                global::XASlave.Localization.UiLocale.FormatKey("S8FF0C504CC86132E", view.OverallCollectionItemTypeCount));
        }

        foreach (var group in view.Groups)
            DrawXagmanTradeCapacityGroup(view, group);
    }

    private void DrawXagmanTradeCapacityGroup(XagmanTradeCapacityView view, XagmanTradeCapacityViewGroup group)
    {
        ImGui.Spacing();
        var groupLabel = ((view.ServerMatching ? global::XASlave.Localization.UiMessage.Format("S84BDF8ACBFC44E37", group.GroupKey) : global::XASlave.Localization.UiMessage.Format("S16B9E43E0B7409C6", ((string.IsNullOrWhiteSpace(plugin.Configuration.XagmanTargetWorld) ? global::XASlave.Localization.UiMessage.Key("S8A92BCDD6DBDAA6E") : global::XASlave.Localization.UiMessage.Value(plugin.Configuration.XagmanTargetWorld)))))).Display;
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), groupLabel);
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SAD73BC22B38902E7", group.SelectedOwnerCount, group.SelectedTonyCount));

        var duplicateUncertainty = view.DuplicateOwnerKeys.Count > 0;
        var inputUncertainty = duplicateUncertainty
            || view.UnknownOwnerCount > 0
            || view.MissingForecastPeerCount > 0
            || view.StaleForecastPeerCount > 0
            || view.StalePresencePeerCount > 0
            || view.TruncatedForecastPeerCount > 0
            || group.UnknownOwnerCount > 0
            || group.UnknownTonyCount > 0;
        var collectionShortage = Math.Max(0L, group.RequiredCollectionSlots - group.KnownFreeSlots);
        if (collectionShortage > 0 && !inputUncertainty)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("S13BDAC4CEFE01EAA", group.RequiredCollectionSlots, group.KnownFreeSlots, collectionShortage));
        }
        else if (collectionShortage > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("S6A9983DC933E21A2", collectionShortage));
        }
        else
        {
            var color = inputUncertainty
                ? new Vector4(1.0f, 0.8f, 0.3f, 1.0f)
                : new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
            ImGui.TextColored(color,
                ((inputUncertainty ? global::XASlave.Localization.UiMessage.Format("S2C180ADA3E50F569", group.RequiredCollectionSlots, group.KnownFreeSlots) : global::XASlave.Localization.UiMessage.Format("S2D35DC239DF91FAA", group.RequiredCollectionSlots, group.KnownFreeSlots))).Display);
        }

        if (group.CollectionCapacity != null)
        {
            DrawXagmanCollectionCapacitySummary(group.CollectionCapacity, inputUncertainty);
        }
        else if (group.CollectionItemTypeCount > 1)
        {
            ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S1570095C82F7BF28"));
            ImGui.TextWrapped(
                global::XASlave.Localization.UiLocale.FormatKey("S896D2E7B20A361E5", group.KnownFreeSlots, group.CollectionItemTypeCount));
        }
        if (!view.ServerMatching && group.FixedWorldRegionCapacities.Count > 0)
            DrawXagmanFixedWorldRegionCapacities(group, inputUncertainty);

        if (group.SupplyShortageItemCount > 0 && !inputUncertainty)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("SDCC4C2EA74EE77A7", group.SupplyShortageUnits, group.SupplyShortageItemCount));
        }
        else if (group.SupplyShortageItemCount > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                global::XASlave.Localization.UiLocale.FormatKey("S8AC322450890D2AD", group.SupplyShortageUnits, group.SupplyShortageItemCount));
        }
        else
        {
            var color = inputUncertainty || group.AllAvailableRequestCount > 0
                ? new Vector4(1.0f, 0.8f, 0.3f, 1.0f)
                : new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
            ImGui.TextColored(color,
                ((inputUncertainty ? global::XASlave.Localization.UiMessage.Key("S4DBB42F4B1CD4B91") : global::XASlave.Localization.UiMessage.Key("SEE110F033051754C"))).Display);
        }
        if (view.ShowCollectionFirstProjection)
        {
            var projectionUncertain = inputUncertainty
                || collectionShortage > 0
                || group.AllAvailableRequestCount > 0;
            if (group.SupplyAfterCollectionShortageItemCount > 0 && !projectionUncertain)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                    global::XASlave.Localization.UiLocale.FormatKey("SFCAF2E2402C42704", group.SupplyAfterCollectionShortageUnits, group.SupplyAfterCollectionShortageItemCount));
            }
            else if (group.SupplyAfterCollectionShortageItemCount > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    global::XASlave.Localization.UiLocale.FormatKey("S8723DC57C5269BAD", group.SupplyAfterCollectionShortageUnits));
            }
            else
            {
                var projectionColor = projectionUncertain
                    ? new Vector4(1.0f, 0.8f, 0.3f, 1.0f)
                    : new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
                ImGui.TextColored(
                    projectionColor,
                    ((projectionUncertain ? global::XASlave.Localization.UiMessage.Key("SBF97441AE9D166D3") : global::XASlave.Localization.UiMessage.Key("S1EE66778C481C48D"))).Display);
            }
        }
        if (group.AllAvailableRequestCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("S238C8B2584889BE0", group.AllAvailableRequestCount));
        if (group.UnknownTonyCount > 0)
            DrawXagmanTradeCapacityWarning(global::XASlave.Localization.UiLocale.FormatKey("SD698B45E68975C3B", group.UnknownTonyCount));

        if (group.Items.Count == 0)
            return;
        var tableId = $"XagmanTradeCapacityItems##{group.GroupKey}";
        var tableColumnCount = view.ShowCollectionFirstProjection ? 9 : 7;
        using (var itemsTable = ImRaii.Table(tableId, tableColumnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
        if (!itemsTable)
            return;
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"), ImGuiTableColumnFlags.WidthStretch);
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SE551641242CE7246"), ImGuiTableColumnFlags.WidthFixed, Scale(55f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S08A0C33F913A1237"), ImGuiTableColumnFlags.WidthFixed, Scale(75f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S4FD72C06BCFF7971"), ImGuiTableColumnFlags.WidthFixed, Scale(55f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SF921375CB5109B5A"), ImGuiTableColumnFlags.WidthFixed, Scale(125f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFACD9B5DD3E38232"), ImGuiTableColumnFlags.WidthFixed, Scale(85f));
        if (view.ShowCollectionFirstProjection)
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SAC4914A9B95DB718"), ImGuiTableColumnFlags.WidthFixed, Scale(115f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S29347CA8AF9483F3"), ImGuiTableColumnFlags.WidthFixed, Scale(75f));
        if (view.ShowCollectionFirstProjection)
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SE3C0B1E74C4CE850"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
        global::XASlave.Localization.UiNative.TableHeadersRow();
        foreach (var item in group.Items)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(((item.IsHq ? global::XASlave.Localization.UiMessage.Format("S5864848AE53E7CAF", item.ItemName) : global::XASlave.Localization.UiMessage.Raw(item.ItemName))).Display);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.StackSize > 0 ? global::XASlave.Localization.UiMessage.Raw(item.StackSize.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.IncomingToTonyQuantity > 0 ? global::XASlave.Localization.UiMessage.Raw(item.IncomingToTonyQuantity.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.RequiredCollectionSlots > 0 ? global::XASlave.Localization.UiMessage.Raw(item.RequiredCollectionSlots.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
            ImGui.TableNextColumn();
            var supplyLabel = item.NeededFromTonyQuantity > 0
                ? item.NeededFromTonyQuantity.ToString("N0", CultureInfo.InvariantCulture)
                : "-";
            if (item.AllAvailableRequestCount > 0)
                supplyLabel += (global::XASlave.Localization.UiMessage.Format("S93EA9B958D254A7B", item.AllAvailableRequestCount)).Display;
            ImGui.TextDisabled(supplyLabel);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(((item.TonyStockQuantity > 0 ? global::XASlave.Localization.UiMessage.Raw(item.TonyStockQuantity.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
            if (view.ShowCollectionFirstProjection)
            {
                ImGui.TableNextColumn();
                ImGui.TextDisabled(((item.ProjectedTonyStockAfterCollection > 0 ? global::XASlave.Localization.UiMessage.Raw(item.ProjectedTonyStockAfterCollection.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
            }
            ImGui.TableNextColumn();
            if (item.SupplyShortageQuantity > 0 && !inputUncertainty)
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), item.SupplyShortageQuantity.ToString("N0", CultureInfo.InvariantCulture));
            else if (item.SupplyShortageQuantity > 0 || item.UnknownOwnerCount > 0 || item.AllAvailableRequestCount > 0 || duplicateUncertainty)
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), (global::XASlave.Localization.UiMessage.Key("S8A8DE823D5ED3E12")).Display);
            else
                ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9")).Display);
            if (view.ShowCollectionFirstProjection)
            {
                ImGui.TableNextColumn();
                var projectionUncertain = inputUncertainty
                    || collectionShortage > 0
                    || item.AllAvailableRequestCount > 0;
                if (item.ProjectedSupplyShortageAfterCollection > 0 && !projectionUncertain)
                {
                    ImGui.TextColored(
                        new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                        item.ProjectedSupplyShortageAfterCollection.ToString("N0", CultureInfo.InvariantCulture));
                }
                else if (item.ProjectedSupplyShortageAfterCollection > 0
                    || item.UnknownOwnerCount > 0
                    || projectionUncertain)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), (global::XASlave.Localization.UiMessage.Key("S8A8DE823D5ED3E12")).Display);
                }
                else
                {
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9")).Display);
                }
            }
        }
        }
        ImGui.TextDisabled(
            global::XASlave.Localization.UiLocale.Get("S7F623B83411D3652"));

        if (group.TonySupplyAvailability.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SD81F1DCD0E6A83D2"));
        var availabilityTableId = $"XagmanTonySupplyAvailability##{group.GroupKey}";
        using (var availabilityTable = ImRaii.Table(availabilityTableId, 4,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
        if (!availabilityTable)
            return;
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SC5A8D95238CD3EE8"), ImGuiTableColumnFlags.WidthStretch);
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"), ImGuiTableColumnFlags.WidthStretch);
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCD41DE668E2F141E"), ImGuiTableColumnFlags.WidthFixed, Scale(105f));
        global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S97F0220721840ABC"), ImGuiTableColumnFlags.WidthFixed, Scale(120f));
        global::XASlave.Localization.UiNative.TableHeadersRow();
        var anonymizeTonyCharacters = IsCharacterListAnonymizationEnabled();
        foreach (var availability in group.TonySupplyAvailability)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetDisplayCharacterKey(availability.TonyCharacter, anonymizeTonyCharacters));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(((availability.IsHq ? global::XASlave.Localization.UiMessage.Format("S5864848AE53E7CAF", availability.ItemName) : global::XASlave.Localization.UiMessage.Raw(availability.ItemName))).Display);
            ImGui.TableNextColumn();
            var pooledNeed = availability.PooledNeededQuantity > 0
                ? availability.PooledNeededQuantity.ToString("N0", CultureInfo.InvariantCulture)
                : string.Empty;
            if (availability.AllAvailableRequestCount > 0)
            {
                var allAvailableLabel = (global::XASlave.Localization.UiMessage.Format("S0558FC196C3BB350", availability.AllAvailableRequestCount)).Display;
                pooledNeed = string.IsNullOrEmpty(pooledNeed)
                    ? allAvailableLabel
                    : $"{pooledNeed} + {allAvailableLabel}";
            }
            ImGui.TextDisabled(pooledNeed);
            ImGui.TableNextColumn();
            if (availability.IsTonyInventoryKnown)
                ImGui.TextDisabled(availability.TonyAvailableQuantity.ToString("N0", CultureInfo.InvariantCulture));
            else
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), (global::XASlave.Localization.UiMessage.Key("S8A8DE823D5ED3E12")).Display);
        }
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SFC2AE8EDE11F1D3B"));
    }

    private static void DrawXagmanFixedWorldRegionCapacities(
        XagmanTradeCapacityViewGroup group,
        bool inputUncertainty)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SA395C39D3C23BB5A"));

        var fullyDemandedKnownPool = !inputUncertainty
            && group.CollectionCapacity != null
            && group.CollectionCapacity.IncomingQuantity >= group.CollectionCapacity.TotalItemCapacityQuantity;
        foreach (var region in group.FixedWorldRegionCapacities)
        {
            var knownLabel = ((region.UnknownTonyCount > 0 ? global::XASlave.Localization.UiMessage.Format("S8FD29C1604BB8FDD", region.SelectedTonyCount, region.KnownTonyCount, region.UnknownTonyCount) : global::XASlave.Localization.UiMessage.Format("SE82B5442BF9EE51E", region.SelectedTonyCount, region.KnownTonyCount))).Display;
            if (region.StackSize <= 0)
            {
                ImGui.TextWrapped(
                    global::XASlave.Localization.UiLocale.FormatKey("SE1B7BAC768171164", region.RegionKey, knownLabel, region.KnownEmptyStackSlots));
                continue;
            }

            var capacityText =
                (global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S0C12E054C356F98B", region.RegionKey, knownLabel, region.KnownEmptyStackSlots), global::XASlave.Localization.UiMessage.Format("S39C4A182FB2A48A8", region.StackSize, region.EmptyStackCapacityQuantity))).Display;
            if (region.PartialStackHeadroom > 0)
            {
                capacityText +=
                    (global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S880F3A8DABCDEF30", region.PartialStackHeadroom), global::XASlave.Localization.UiMessage.Format("SE649FAFB251B6AD0", region.TotalItemCapacityQuantity))).Display;
            }
            if (fullyDemandedKnownPool)
                capacityText += (global::XASlave.Localization.UiMessage.Format("SA04277B06A4D113C", region.TotalItemCapacityQuantity)).Display;
            ImGui.TextWrapped(capacityText + ".");
        }

        if (fullyDemandedKnownPool)
        {
            DrawWrappedDisabledText(
                global::XASlave.Localization.UiLocale.Get("SEF66DFDAE686FAAF"));
        }
        else if (group.CollectionCapacity != null)
        {
            DrawWrappedDisabledText(
                global::XASlave.Localization.UiLocale.Get("SC6F78EBC94444EDC"));
        }
        else if (group.CollectionItemTypeCount > 1)
        {
            DrawWrappedDisabledText(
                global::XASlave.Localization.UiLocale.Get("SA5AE29979452E861"));
        }
    }

    private static void DrawXagmanCollectionCapacitySummary(
        XagmanCollectionCapacityView capacity,
        bool inputUncertainty)
    {
        var itemLabel = ((capacity.IsHq ? global::XASlave.Localization.UiMessage.Format("S5864848AE53E7CAF", capacity.ItemName) : global::XASlave.Localization.UiMessage.Raw(capacity.ItemName))).Display;
        var capacityLabel = ((inputUncertainty ? global::XASlave.Localization.UiMessage.Key("S20A0295B00C5F526") : global::XASlave.Localization.UiMessage.Key("SE6BE05D10C4E0C9F"))).Display;
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S78502CBEFA9D3541", itemLabel));
        var capacityText =
            (global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S0D3DFAA025F9E845", capacityLabel, capacity.KnownEmptyStackSlots, capacity.StackSize), global::XASlave.Localization.UiMessage.Format("S0569C411F5D87BFD", capacity.EmptyStackCapacityQuantity))).Display;
        if (capacity.PartialStackHeadroom > 0)
        {
            capacityText +=
                (global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S880F3A8DABCDEF30", capacity.PartialStackHeadroom), global::XASlave.Localization.UiMessage.Format("S9CBC4D8B229BB62E", capacity.TotalItemCapacityQuantity))).Display;
        }
        else
        {
            capacityText += (global::XASlave.Localization.UiMessage.Key("SCDB4EE2AEA69CC6A")).Display;
        }
        ImGui.TextWrapped(capacityText);

        var collectLabel = ((inputUncertainty ? global::XASlave.Localization.UiMessage.Key("S275C7194912C93A6") : global::XASlave.Localization.UiMessage.Key("S4EA67B2DC3A0BEC0"))).Display;
        var collectColor = capacity.RemainingQuantity > 0
            ? new Vector4(1.0f, 0.8f, 0.3f, 1.0f)
            : new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
        using (ImRaii.PushColor(ImGuiCol.Text, collectColor))
        {
            ImGui.TextWrapped(
                global::XASlave.Localization.UiLocale.FormatKey("S5DAB970D0F72A01C", collectLabel, capacity.CollectableQuantity, capacity.IncomingQuantity, capacity.NewStackSlotsUsed, capacity.RemainingQuantity));
        }
    }

    private static void DrawXagmanTradeCapacityWarning(string text)
    {
        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), text);
    }
}
