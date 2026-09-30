using System;
using System.Collections.Generic;

namespace XASlave.Services;

internal readonly record struct WeatherForecastRate(uint WeatherId, int Rate);

/// <summary>Deterministic outdoor weather. Live and quest overrides are separate.</summary>
internal static class WeatherForecast
{
    internal const long PeriodSeconds = 1400;
    internal const int FuturePeriods = 6;

    internal static long PeriodStart(long unixSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unixSeconds);
        return unixSeconds - unixSeconds % PeriodSeconds;
    }

    internal static int CalculateTarget(long unixSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unixSeconds);
        var bell = unixSeconds / 175;
        var increment = (uint)((bell + 8 - bell % 8) % 24);
        var totalDays = (uint)(unixSeconds / 4200);
        // The game's hash wraps as an unsigned 32-bit value; the timestamp does not.
        var calcBase = unchecked(totalDays * 100 + increment);
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    internal static uint ResolveWeather(IReadOnlyList<WeatherForecastRate> rates, int target)
    {
        if (target < 0 || target >= 100)
            return 0;

        var total = 0;
        uint selected = 0;
        foreach (var entry in rates)
        {
            if (entry.Rate < 0 || entry.Rate > 100 || (entry.Rate > 0 && entry.WeatherId == 0))
                return 0;
            total += entry.Rate;
            if (selected == 0 && target < total)
                selected = entry.WeatherId;
        }

        return total == 100 ? selected : 0;
    }
}
