using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal readonly record struct XagmanTradeBatchItem(uint ItemId, bool IsHq, int Quantity);
internal readonly record struct XagmanTradeBatchStack(uint ItemId, bool IsHq, int Quantity);

internal sealed record XagmanTradeBatchPlan(
    IReadOnlyList<XagmanTradeBatchItem> Selected,
    int PhysicalOfferSlots,
    int BagSlots,
    string FailureReason)
{
    internal bool IsValid => FailureReason.Length == 0;
}

/// <summary>Limits one Dropbox Begin call to one receiver-safe physical trade.</summary>
internal static class XagmanTradeBatchPlanner
{
    internal const int MaximumPhysicalOfferSlots = 5;
    internal const int MaximumGilPerTrade = 1_000_000;

    internal static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;

    internal static XagmanTradeBatchPlan Plan(
        IReadOnlyList<XagmanTradeBatchItem> selection,
        IReadOnlyList<XagmanTradeBatchStack> stacks,
        int receiverFreeSlots,
        int maxOfferSlots = MaximumPhysicalOfferSlots,
        int maxGil = MaximumGilPerTrade)
    {
        if (selection == null || stacks == null || receiverFreeSlots is < 0 or > 140
            || maxOfferSlots is < 0 or > MaximumPhysicalOfferSlots || maxGil <= 0)
            return Invalid("Invalid trade capacity or source snapshot.");

        var keys = new HashSet<(uint, bool)>();
        foreach (var item in selection)
        {
            if (!IsValid(item.ItemId, item.IsHq, item.Quantity) || !keys.Add((item.ItemId, item.IsHq)))
                return Invalid("Invalid or duplicate selected item.");
        }

        var available = new Dictionary<(uint, bool), List<int>>();
        foreach (var stack in stacks)
        {
            if (!IsValid(stack.ItemId, stack.IsHq, 1) || stack.Quantity < 0)
                return Invalid("Invalid physical source stack.");
            if (!available.TryGetValue((stack.ItemId, stack.IsHq), out var quantities))
                available[(stack.ItemId, stack.IsHq)] = quantities = new List<int>();
            quantities.Add(stack.Quantity);
        }
        foreach (var quantities in available.Values)
            quantities.Sort();

        var selected = new List<XagmanTradeBatchItem>();
        var offerSlots = 0;
        var bagSlots = 0;
        foreach (var item in selection)
        {
            if (!available.TryGetValue((item.ItemId, item.IsHq), out var quantities))
                continue;
            if (item.ItemId == 1)
            {
                var gil = (int)Math.Min(Math.Min(item.Quantity, Math.Min(maxGil, MaximumGilPerTrade)), quantities.Sum(value => (long)value));
                if (gil > 0)
                    selected.Add(item with { Quantity = gil });
                continue;
            }

            var isCrystal = IsCrystal(item.ItemId);
            var budget = Math.Min(maxOfferSlots - offerSlots, isCrystal ? maxOfferSlots : receiverFreeSlots - bagSlots);
            if (budget <= 0)
                continue;

            // Dropbox chooses physical stacks itself. The sum of the b smallest
            // eligible stacks is a lower bound for EVERY possible b-stack prefix,
            // so this quantity cannot cause more than b offers, regardless of order.
            // Include armoury stacks in the capture, and never credit receiver merges.
            long quantityBound = 0;
            var physicalSlots = 0;
            while (physicalSlots < budget && physicalSlots < quantities.Count && quantityBound < item.Quantity)
                quantityBound += quantities[physicalSlots++];
            var quantity = (int)Math.Min(item.Quantity, quantityBound);
            if (quantity <= 0)
                continue;

            selected.Add(item with { Quantity = quantity });
            offerSlots += physicalSlots;
            if (!isCrystal)
                bagSlots += physicalSlots;
        }

        return new XagmanTradeBatchPlan(selected.ToArray(), offerSlots, bagSlots, string.Empty);
    }

    private static bool IsValid(uint itemId, bool isHq, int quantity)
        => itemId is > 0 and <= int.MaxValue && quantity > 0 && (itemId > 19 || !isHq);

    private static XagmanTradeBatchPlan Invalid(string reason)
        => new(Array.Empty<XagmanTradeBatchItem>(), 0, 0, reason);
}

internal sealed record XagmanTradeBatchReconciliation(
    IReadOnlyList<XagmanTradeBatchItem> Remaining,
    long DeliveredQuantity,
    string FailureReason)
{
    internal bool IsValid => FailureReason.Length == 0;
}

/// <summary>Validates the entire observed batch before changing any finite remaining goal.</summary>
internal static class XagmanTradeBatchAccounting
{
    internal static XagmanTradeBatchReconciliation Reconcile(
        IReadOnlyList<XagmanTradeBatchItem> remaining,
        IReadOnlyList<XagmanTradeBatchItem> offered,
        IReadOnlyDictionary<(uint, bool), int> before,
        IReadOnlyDictionary<(uint, bool), int> after)
    {
        if (remaining == null || offered == null || before == null || after == null)
            return Invalid("Trade accounting snapshots are missing.");
        if (!TryReadItems(remaining, out var goals) || !TryReadItems(offered, out var offers)
            || !TryReadTotals(before, out var oldTotals) || !TryReadTotals(after, out var newTotals))
            return Invalid("Trade accounting contains duplicate or invalid quantities.");

        foreach (var (key, quantity) in offers)
        {
            if (!goals.TryGetValue(key, out var target) || quantity > target
                || !oldTotals.TryGetValue(key, out var held) || quantity > held)
                return Invalid("A bounded offer exceeds its remaining goal or pre-trade inventory.");
        }

        var decreases = new Dictionary<(uint, bool), int>();
        long delivered = 0;
        foreach (var key in oldTotals.Keys.Union(newTotals.Keys))
        {
            var delta = (long)oldTotals.GetValueOrDefault(key) - newTotals.GetValueOrDefault(key);
            if (delta == 0)
                continue;
            if (delta < 0 || !offers.TryGetValue(key, out var offeredQuantity) || delta > offeredQuantity)
                return Invalid("Inventory changed outside the bounded offer; delivery accounting is uncertain.");
            decreases.Add(key, (int)delta);
            delivered += delta;
        }

        // Commit a new value only after every key passed. A rejected later entry
        // must not leave earlier goals partially decremented in the caller.
        var next = remaining.Select(item => item with
            { Quantity = item.Quantity - decreases.GetValueOrDefault((item.ItemId, item.IsHq)) })
            .Where(item => item.Quantity > 0).ToArray();
        return new(next, delivered, string.Empty);
    }

    private static bool TryReadItems(IReadOnlyList<XagmanTradeBatchItem> items, out Dictionary<(uint, bool), int> result)
    {
        result = new();
        foreach (var item in items)
        {
            if (!IsValidKey(item.ItemId, item.IsHq) || item.Quantity <= 0
                || !result.TryAdd((item.ItemId, item.IsHq), item.Quantity))
                return false;
        }
        return true;
    }

    private static bool TryReadTotals(IReadOnlyDictionary<(uint, bool), int> totals, out Dictionary<(uint, bool), int> result)
    {
        result = new();
        foreach (var (key, quantity) in totals)
        {
            if (!IsValidKey(key.Item1, key.Item2) || quantity < 0 || !result.TryAdd(key, quantity))
                return false;
        }
        return true;
    }

    private static bool IsValidKey(uint itemId, bool isHq)
        => itemId is > 0 and <= int.MaxValue && (itemId > 19 || !isHq);

    private static XagmanTradeBatchReconciliation Invalid(string reason)
        => new(Array.Empty<XagmanTradeBatchItem>(), 0, reason);
}
