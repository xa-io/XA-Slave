using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class XagmanTonyAutoDemand
    {
        public string GroupKey { get; init; } = string.Empty;
        public int OwnerCount { get; init; }
        public List<XagmanTonyAutoDemandItem> Items { get; init; } = new();
    }

    private sealed class XagmanTonyAutoDemandItem
    {
        public uint ItemId { get; init; }
        public bool IsHq { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public int StackSize { get; init; }
        public long IncomingToTonyQuantity { get; init; }
        public long NeededFromTonyQuantity { get; init; }
    }

    private sealed class XagmanTonyAutoCandidate
    {
        public int Index { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public int FreeSlots { get; init; }
        public long Gil { get; init; }
        public Dictionary<XagmanForecastItemKey, long> Stock { get; init; } = new();
        public Dictionary<XagmanForecastItemKey, long> Headroom { get; init; } = new();
    }

    private sealed class XagmanTonyAutoPlan
    {
        public HashSet<int> SelectedIndices { get; } = new();
        public Dictionary<string, int> SelectedByGroup { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Shortfalls { get; } = new();
        public bool Success => Shortfalls.Count == 0;
    }

    private sealed class XagmanTonyAutoDeficits
    {
        public bool MissingCoordinator { get; init; }
        public long BagSlots { get; init; }
        public Dictionary<XagmanForecastItemKey, long> BagIncomingUnits { get; } = new();
        public Dictionary<XagmanForecastItemKey, long> ReceivingUnits { get; } = new();
        public Dictionary<XagmanForecastItemKey, long> SupplyUnits { get; } = new();
    }

    // The controller owns freshness, route validity, and snapshot queries. This planner only
    // reads detached known inputs; it never changes the active roster or reserves inventory.
    private static XagmanTonyAutoPlan PlanXagmanTonyAutoSelection(
        IReadOnlyCollection<XagmanTonyAutoDemand> demands,
        IReadOnlyCollection<XagmanTonyAutoCandidate> candidates,
        bool creditIncomingSupply,
        long gilReserve,
        long gilReceivingLimit)
    {
        var result = new XagmanTonyAutoPlan();
        var eligibleCandidates = candidates
            .Where(candidate => candidate.Index >= 0
                && !string.IsNullOrWhiteSpace(candidate.Key)
                && candidate.Gil >= 5_000)
            .OrderByDescending(candidate => Math.Max(0, candidate.FreeSlots))
            .ThenBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Index)
            .GroupBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        foreach (var demandGroup in demands.GroupBy(demand => demand.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var groupKey = demandGroup.Key;
            var items = demandGroup.SelectMany(demand => demand.Items)
                .GroupBy(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
                .Select(group => new XagmanTonyAutoDemandItem
                {
                    ItemId = group.Key.ItemId,
                    IsHq = group.Key.IsHq,
                    ItemName = group.First().ItemName,
                    StackSize = Math.Max(1, group.First().StackSize),
                    IncomingToTonyQuantity = group.Aggregate(0L, (sum, item) =>
                        AddXagmanTonyAutoQuantity(sum, item.IncomingToTonyQuantity)),
                    NeededFromTonyQuantity = group.Aggregate(0L, (sum, item) =>
                        AddXagmanTonyAutoQuantity(sum, item.NeededFromTonyQuantity)),
                })
                .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ThenBy(item => item.IsHq)
                .ToList();
            if (!demandGroup.Any(demand => demand.OwnerCount > 0)
                && !items.Any(item => item.IncomingToTonyQuantity > 0 || item.NeededFromTonyQuantity > 0))
            {
                continue;
            }

            if (!IsXagmanTonyAutoGroupKnown(groupKey))
            {
                result.Shortfalls.Add("An owner region is unknown; its Tony requirement cannot be selected.");
                continue;
            }

            var groupCandidates = eligibleCandidates.Where(candidate =>
                    !result.SelectedIndices.Contains(candidate.Index)
                    && (groupKey.Equals("All", StringComparison.OrdinalIgnoreCase)
                        || candidate.Region.Equals(groupKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var selected = new List<XagmanTonyAutoCandidate>();
            var deficits = EvaluateXagmanTonyAutoDeficits(items, selected, creditIncomingSupply, gilReserve, gilReceivingLimit);
            foreach (var candidate in groupCandidates)
            {
                var trial = selected.Append(candidate).ToList();
                var after = EvaluateXagmanTonyAutoDeficits(items, trial, creditIncomingSupply, gilReserve, gilReceivingLimit);
                if (!ImprovesXagmanTonyAutoDeficits(deficits, after))
                    continue;
                selected.Add(candidate);
                deficits = after;
                if (AreXagmanTonyAutoDeficitsCovered(deficits))
                    break;
            }

            // A stock supplier can make an earlier coordinator redundant. Remove lower-ranked
            // choices first, while never worsening a shortfall or leaving owners without Tony.
            for (var index = selected.Count - 1; index >= 0; index--)
            {
                var trial = selected.Where((_, selectedIndex) => selectedIndex != index).ToList();
                var after = EvaluateXagmanTonyAutoDeficits(items, trial, creditIncomingSupply, gilReserve, gilReceivingLimit);
                if (!AreXagmanTonyAutoDeficitsNoWorse(deficits, after))
                    continue;
                selected.RemoveAt(index);
                deficits = after;
            }

            result.SelectedIndices.UnionWith(selected.Select(candidate => candidate.Index));
            result.SelectedByGroup[groupKey] = selected.Count;
            if (deficits.MissingCoordinator)
                result.Shortfalls.Add($"{groupKey}: an eligible Tony with at least 5,000 gil is needed.");
            if (deficits.BagSlots > 0)
                result.Shortfalls.Add($"{groupKey}: more eligible Tonys are needed for {deficits.BagSlots:N0} additional free inventory slot(s).");
            foreach (var item in items)
            {
                var key = new XagmanForecastItemKey(item.ItemId, item.IsHq);
                var name = string.IsNullOrWhiteSpace(item.ItemName) ? $"Item {item.ItemId}" : item.ItemName;
                var label = item.IsHq ? $"{name} (HQ)" : name;
                if (deficits.ReceivingUnits.TryGetValue(key, out var receiving) && receiving > 0)
                {
                    var capacity = item.ItemId == 1 ? "gil receiving capacity" : $"{label} crystal pouch capacity";
                    result.Shortfalls.Add($"{groupKey}: more eligible Tonys are needed for {receiving:N0} additional units of {capacity}.");
                }
                if (deficits.SupplyUnits.TryGetValue(key, out var supply) && supply > 0)
                {
                    var projection = creditIncomingSupply ? " after collection" : string.Empty;
                    result.Shortfalls.Add($"{groupKey}: eligible Tony stock is short {supply:N0} {label}{projection}; add Tonys holding the missing supply.");
                }
            }
        }

        if (result.SelectedByGroup.Count == 0 && result.Shortfalls.Count == 0)
            result.Shortfalls.Add("No selected Franchise Owner requirements are available for Tony Auto selection.");
        return result;
    }

    private static XagmanTonyAutoDeficits EvaluateXagmanTonyAutoDeficits(
        IReadOnlyCollection<XagmanTonyAutoDemandItem> items,
        IReadOnlyCollection<XagmanTonyAutoCandidate> selected,
        bool creditIncomingSupply,
        long gilReserve,
        long gilReceivingLimit)
    {
        // Runtime pauses all receiving at either capacity threshold. Such Tonys may still
        // supply stock, but their partial stacks, crystal pouches and gil room cannot collect.
        var receivers = selected.Where(candidate => candidate.FreeSlots > 0
            && candidate.Gil < gilReceivingLimit).ToList();
        var freeSlots = receivers.Aggregate(0L, (sum, candidate) =>
            AddXagmanTonyAutoQuantity(sum, Math.Max(0, candidate.FreeSlots)));
        var requiredSlots = 0L;
        var bagIncomingUnits = new Dictionary<XagmanForecastItemKey, long>();
        var receivingUnits = new Dictionary<XagmanForecastItemKey, long>();
        var supplyUnits = new Dictionary<XagmanForecastItemKey, long>();
        foreach (var item in items)
        {
            var key = new XagmanForecastItemKey(item.ItemId, item.IsHq);
            var isGil = item.ItemId == 1;
            var isCrystal = item.ItemId >= XagmanFirstElementalCrystalItemId
                && item.ItemId <= XagmanLastElementalCrystalItemId;
            var incoming = Math.Max(0L, item.IncomingToTonyQuantity);
            var headroom = receivers.Aggregate(0L, (sum, candidate) =>
                AddXagmanTonyAutoQuantity(sum, isGil
                    ? Math.Max(0L, Math.Max(0L, gilReceivingLimit) - Math.Max(0L, candidate.Gil))
                    : GetXagmanTonyAutoQuantity(candidate.Headroom, key)));
            var unfilledIncoming = Math.Max(0L, incoming - headroom);
            if (isGil || isCrystal)
            {
                receivingUnits[key] = unfilledIncoming;
            }
            else if (unfilledIncoming > 0)
            {
                var stackSize = Math.Max(1, item.StackSize);
                requiredSlots = AddXagmanTonyAutoQuantity(requiredSlots, 1L + (unfilledIncoming - 1L) / stackSize);
                bagIncomingUnits[key] = unfilledIncoming;
            }

            var stock = selected.Aggregate(0L, (sum, candidate) =>
                AddXagmanTonyAutoQuantity(sum, isGil
                    ? Math.Max(0L, Math.Max(0L, candidate.Gil) - Math.Max(0L, gilReserve))
                    : GetXagmanTonyAutoQuantity(candidate.Stock, key)));
            if (creditIncomingSupply)
            {
                // Incoming gil may first have to restore a selected character's configured
                // reserve. Do not promise those reserved funds as spendable supply.
                var availableIncoming = isGil
                    ? GetXagmanTonyAutoIncomingGilSupply(receivers, incoming, gilReserve, gilReceivingLimit)
                    : incoming;
                stock = AddXagmanTonyAutoQuantity(stock, availableIncoming);
            }
            supplyUnits[key] = Math.Max(0L, Math.Max(0L, item.NeededFromTonyQuantity) - stock);
        }

        var result = new XagmanTonyAutoDeficits
        {
            MissingCoordinator = selected.Count == 0,
            BagSlots = Math.Max(0L, requiredSlots - freeSlots),
        };
        // Several partial stacks may jointly remove one required bag slot. Keep their unit
        // progress visible while space is short, so the greedy pass does not skip every one.
        if (result.BagSlots > 0)
        {
            foreach (var entry in bagIncomingUnits)
                result.BagIncomingUnits[entry.Key] = entry.Value;
        }
        foreach (var entry in receivingUnits)
            result.ReceivingUnits[entry.Key] = entry.Value;
        foreach (var entry in supplyUnits)
            result.SupplyUnits[entry.Key] = entry.Value;
        return result;
    }

    private static bool IsXagmanTonyAutoGroupKnown(string groupKey) =>
        groupKey.Equals("NA", StringComparison.OrdinalIgnoreCase)
        || groupKey.Equals("EU", StringComparison.OrdinalIgnoreCase)
        || groupKey.Equals("JP", StringComparison.OrdinalIgnoreCase)
        || groupKey.Equals("OCE", StringComparison.OrdinalIgnoreCase)
        || groupKey.Equals("All", StringComparison.OrdinalIgnoreCase);

    private static long GetXagmanTonyAutoIncomingGilSupply(
        IReadOnlyCollection<XagmanTonyAutoCandidate> selected,
        long incoming,
        long gilReserve,
        long gilReceivingLimit)
    {
        var result = 0L;
        // Gil is pooled just like item stock. Fund the smallest remaining reserve first;
        // introducing another low-gil candidate must never reduce already available supply.
        foreach (var capacity in selected.Select(candidate => new
                 {
                     Reserve = Math.Max(0L, Math.Max(0L, gilReserve) - Math.Max(0L, candidate.Gil)),
                     Room = Math.Max(0L, Math.Max(0L, gilReceivingLimit) - Math.Max(0L, candidate.Gil)),
                 })
                 .Where(capacity => capacity.Room > capacity.Reserve)
                 .OrderBy(capacity => capacity.Reserve)
                 .ThenByDescending(capacity => capacity.Room))
        {
            var received = Math.Min(Math.Max(0L, incoming), capacity.Room);
            result = AddXagmanTonyAutoQuantity(result, Math.Max(0L, received - capacity.Reserve));
            incoming -= received;
            if (incoming <= 0)
                break;
        }
        return result;
    }

    private static long GetXagmanTonyAutoQuantity(
        IReadOnlyDictionary<XagmanForecastItemKey, long> quantities,
        XagmanForecastItemKey key) =>
        quantities.TryGetValue(key, out var quantity) ? Math.Max(0L, quantity) : 0L;

    private static long AddXagmanTonyAutoQuantity(long left, long right)
    {
        left = Math.Max(0L, left);
        right = Math.Max(0L, right);
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static bool AreXagmanTonyAutoDeficitsCovered(XagmanTonyAutoDeficits deficits) =>
        !deficits.MissingCoordinator
        && deficits.BagSlots == 0
        && deficits.ReceivingUnits.Values.All(quantity => quantity == 0)
        && deficits.SupplyUnits.Values.All(quantity => quantity == 0);

    private static bool ImprovesXagmanTonyAutoDeficits(XagmanTonyAutoDeficits before, XagmanTonyAutoDeficits after) =>
        (before.MissingCoordinator && !after.MissingCoordinator)
        || after.BagSlots < before.BagSlots
        || before.BagIncomingUnits.Any(entry => GetXagmanTonyAutoQuantity(after.BagIncomingUnits, entry.Key) < entry.Value)
        || before.ReceivingUnits.Any(entry => GetXagmanTonyAutoQuantity(after.ReceivingUnits, entry.Key) < entry.Value)
        || before.SupplyUnits.Any(entry => GetXagmanTonyAutoQuantity(after.SupplyUnits, entry.Key) < entry.Value);

    private static bool AreXagmanTonyAutoDeficitsNoWorse(XagmanTonyAutoDeficits before, XagmanTonyAutoDeficits after) =>
        (!after.MissingCoordinator || before.MissingCoordinator)
        && after.BagSlots <= before.BagSlots
        && after.BagIncomingUnits.All(entry => entry.Value <= GetXagmanTonyAutoQuantity(before.BagIncomingUnits, entry.Key))
        && after.ReceivingUnits.All(entry => entry.Value <= GetXagmanTonyAutoQuantity(before.ReceivingUnits, entry.Key))
        && after.SupplyUnits.All(entry => entry.Value <= GetXagmanTonyAutoQuantity(before.SupplyUnits, entry.Key));
}
