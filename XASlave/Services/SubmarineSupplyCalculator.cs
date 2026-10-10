using System;
using System.Globalization;

namespace XASlave.Services;

internal static class SubmarineSupplyCalculator
{
    internal const uint CeruleumTankItemId = 10155;
    internal const uint MagitekRepairKitItemId = 10373;
    private const decimal Submarines = 4m;
    private const decimal TanksPerSubmarinePerDay = 9m;
    private const decimal KitsPerSubmarinePerDay = 1.33m;

    internal static int? CalculateRestockDays(int submarines, int tanks, int repairKits)
    {
        if (submarines <= 0)
            return null;

        // XA Sub Manager defaults; the first supply to run out limits whole days.
        var tankDays = Math.Max(0, tanks) / (submarines * TanksPerSubmarinePerDay);
        var kitDays = Math.Max(0, repairKits) / (submarines * KitsPerSubmarinePerDay);
        return (int)decimal.Floor(Math.Min(tankDays, kitDays));
    }

    internal static bool TryCalculate(string arguments, out int tanks, out int repairKits)
    {
        tanks = 0;
        repairKits = 0;
        if (string.IsNullOrWhiteSpace(arguments)
            || !decimal.TryParse(arguments.Trim(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var days)
            || days <= 0 || days > int.MaxValue / (Submarines * TanksPerSubmarinePerDay))
            return false;

        // Round the combined FC requirements, not each submarine or each day.
        var tankTotal = decimal.Ceiling(days * Submarines * TanksPerSubmarinePerDay);
        var kitTotal = decimal.Ceiling(days * Submarines * KitsPerSubmarinePerDay);
        // The existing manual queue reports its combined item count as Int32.
        if (tankTotal + kitTotal > int.MaxValue)
            return false;

        tanks = (int)tankTotal;
        repairKits = (int)kitTotal;
        return true;
    }
}
