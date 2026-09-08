using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XASlave.Services;

internal readonly record struct SublootStockItem(uint ItemId, long UnitValue, int Quantity);

internal sealed class SublootValueSelectionResult
{
    public SublootValueSelectionResult(
        long targetValue,
        long selectedValue,
        long totalAvailableValue,
        IReadOnlyDictionary<uint, int> quantities)
    {
        TargetValue = targetValue;
        SelectedValue = selectedValue;
        TotalAvailableValue = totalAvailableValue;
        Quantities = quantities;
    }

    public long TargetValue { get; }
    public long SelectedValue { get; }
    public long TotalAvailableValue { get; }
    public IReadOnlyDictionary<uint, int> Quantities { get; }
    public bool TargetReached => SelectedValue >= TargetValue;
    public long Overflow => TargetReached ? SelectedValue - TargetValue : 0;
    public long Shortfall => TargetReached ? 0 : TargetValue - SelectedValue;
    public int SelectedItemCount => Quantities.Values.Sum();
}

internal static class SublootValueSelector
{
    private readonly record struct QuantityGroup(int StockIndex, int Quantity, int ValueUnits);

    public static bool TryParseTarget(string arguments, out long targetValue)
    {
        targetValue = 0;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        var normalized = arguments.Trim()
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out targetValue)
               && targetValue > 0;
    }

    public static SublootValueSelectionResult Select(
        IReadOnlyCollection<SublootStockItem> stock,
        long targetValue)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (targetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetValue), "The target value must be positive.");

        var duplicateItemId = stock
            .GroupBy(item => item.ItemId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateItemId != null)
            throw new ArgumentException($"Duplicate subloot item ID {duplicateItemId.Key}.", nameof(stock));

        foreach (var item in stock)
        {
            if (item.UnitValue <= 0)
                throw new ArgumentOutOfRangeException(nameof(stock), $"Item {item.ItemId} has a non-positive unit value.");
            if (item.Quantity < 0)
                throw new ArgumentOutOfRangeException(nameof(stock), $"Item {item.ItemId} has a negative quantity.");
        }

        var available = stock
            .Where(item => item.Quantity > 0)
            .OrderByDescending(item => item.UnitValue)
            .ThenBy(item => item.ItemId)
            .ToArray();
        var totalAvailableValue = CalculateTotalAvailableValue(available);

        if (totalAvailableValue <= targetValue)
        {
            return new SublootValueSelectionResult(
                targetValue,
                totalAvailableValue,
                totalAvailableValue,
                available.ToDictionary(item => item.ItemId, item => item.Quantity));
        }

        var valueGcd = available.Select(item => item.UnitValue).Aggregate(GreatestCommonDivisor);
        var targetUnits = targetValue / valueGcd + (targetValue % valueGcd == 0 ? 0 : 1);
        var totalAvailableUnits = totalAvailableValue / valueGcd;
        var maxUnitValue = available.Max(item => item.UnitValue / valueGcd);
        var upperBoundUnits = targetUnits > long.MaxValue - (maxUnitValue - 1)
            ? long.MaxValue
            : targetUnits + maxUnitValue - 1;
        var searchLimitUnits = Math.Min(totalAvailableUnits, upperBoundUnits);
        if (searchLimitUnits > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"The minimum-overflow search requires {searchLimitUnits:N0} value states, which exceeds the supported limit.");
        }

        var groups = BuildQuantityGroups(available, valueGcd, (int)searchLimitUnits);
        var reachable = new bool[(int)searchLimitUnits + 1];
        var previousSum = new int[reachable.Length];
        var previousGroup = new int[reachable.Length];
        Array.Fill(previousSum, -1);
        Array.Fill(previousGroup, -1);
        reachable[0] = true;

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            for (var sum = reachable.Length - 1 - group.ValueUnits; sum >= 0; sum--)
            {
                if (!reachable[sum] || reachable[sum + group.ValueUnits])
                    continue;

                var nextSum = sum + group.ValueUnits;
                reachable[nextSum] = true;
                previousSum[nextSum] = sum;
                previousGroup[nextSum] = groupIndex;
            }
        }

        var selectedUnits = -1;
        for (var sum = (int)targetUnits; sum < reachable.Length; sum++)
        {
            if (!reachable[sum])
                continue;

            selectedUnits = sum;
            break;
        }

        if (selectedUnits < 0)
            throw new InvalidOperationException("No minimum-overflow subloot selection was found within the proven search bound.");

        var selectedQuantities = new int[available.Length];
        for (var sum = selectedUnits; sum > 0; sum = previousSum[sum])
        {
            var groupIndex = previousGroup[sum];
            if (groupIndex < 0)
                throw new InvalidOperationException("The subloot selection path could not be reconstructed.");

            var group = groups[groupIndex];
            selectedQuantities[group.StockIndex] += group.Quantity;
        }

        var quantities = new Dictionary<uint, int>();
        for (var index = 0; index < available.Length; index++)
        {
            if (selectedQuantities[index] > 0)
                quantities[available[index].ItemId] = selectedQuantities[index];
        }

        return new SublootValueSelectionResult(
            targetValue,
            selectedUnits * valueGcd,
            totalAvailableValue,
            quantities);
    }

    private static long CalculateTotalAvailableValue(IEnumerable<SublootStockItem> stock)
    {
        try
        {
            return stock.Aggregate(0L, (total, item) => checked(total + checked(item.UnitValue * item.Quantity)));
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("The available subloot value exceeds Int64 capacity.", nameof(stock), ex);
        }
    }

    private static List<QuantityGroup> BuildQuantityGroups(
        IReadOnlyList<SublootStockItem> stock,
        long valueGcd,
        int searchLimitUnits)
    {
        var groups = new List<QuantityGroup>();
        for (var stockIndex = 0; stockIndex < stock.Count; stockIndex++)
        {
            var item = stock[stockIndex];
            var remaining = item.Quantity;
            var bundleSize = 1;
            while (remaining > 0)
            {
                var quantity = Math.Min(bundleSize, remaining);
                var valueUnits = checked((item.UnitValue / valueGcd) * quantity);
                if (valueUnits <= searchLimitUnits)
                    groups.Add(new QuantityGroup(stockIndex, quantity, (int)valueUnits));

                remaining -= quantity;
                bundleSize = bundleSize <= int.MaxValue / 2 ? bundleSize * 2 : remaining;
            }
        }

        return groups;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }
}
