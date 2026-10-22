using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace XASlave.Services;

[Serializable]
public sealed class SubmarineConsumptionRate
{
    public decimal TanksPerDay { get; set; }
    public decimal KitsPerDay { get; set; }
}

internal static class SubmarineBuildSupply
{
    // Mammet's Fleet upkeep, confirmed 2026-10-21: rank 90, route O -> J, per submarine/day.
    // Default resend interval: 24h if the voyage fits, otherwise 36h; repair all parts when the first breaks.
    // Rates use the displayed two-decimal values: https://mammets-fleet.web.app/tools/upkeep
    private static readonly Dictionary<string, (decimal Tanks, decimal Kits)> DefaultRates = new(StringComparer.Ordinal)
    {
        ["WSUC"] = (9.0m, 1.67m),
        ["SSUC"] = (9.0m, 1.33m),
        ["W+S+U+C+"] = (9.0m, 3.43m),
        ["S+S+S+C+"] = (9.0m, 3.43m),
        ["YUUW"] = (6.0m, 1.14m),
        ["Y+U+U+W+"] = (6.0m, 2.29m),
        ["WCSU"] = (6.0m, 1.11m),
        ["WUSS"] = (6.0m, 0.78m),
        ["W+U+S+S+"] = (6.0m, 2.29m),
        ["YSYC"] = (6.0m, 1.67m),
        ["Y+S+Y+C+"] = (6.0m, 2.29m),
        ["S+S+U+C"] = (9.0m, 3.14m),
        ["S+S+U+C+"] = (9.0m, 3.43m),
        ["WCYC"] = (6.0m, 1.52m),
        ["WUWC"] = (6.0m, 1.14m),
        ["W+U+W+C+"] = (6.0m, 2.29m),
        ["YSCU"] = (6.0m, 1.33m),
        ["SCUS"] = (6.0m, 0.89m),
        ["S+C+U+S+"] = (6.0m, 2.29m),
    };

    internal static bool IsDefaultBuild(string build) => DefaultRates.ContainsKey(build);

    internal static bool TryNormalizeBuild(string? value, out string build)
    {
        build = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (build.Length == 6 && build.EndsWith("++", StringComparison.Ordinal))
            build = string.Concat(build[..4].Select(part => $"{part}+"));

        var parts = 0;
        for (var i = 0; i < build.Length; i++)
        {
            if (build[i] is not ('S' or 'U' or 'W' or 'C' or 'Y'))
                return false;
            parts++;
            if (i + 1 < build.Length && build[i + 1] == '+')
                i++;
        }
        return parts == 4;
    }

    internal static bool IsValidRate(decimal tanks, decimal kits)
        => tanks > 0m && kits > 0m && tanks <= int.MaxValue && kits <= int.MaxValue;

    internal static bool TryParseRate(string text, out decimal value)
        => decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out value) && value > 0m && value <= int.MaxValue;

    internal static IReadOnlyDictionary<string, (decimal Tanks, decimal Kits)> GetEffectiveRates(
        IReadOnlyDictionary<string, SubmarineConsumptionRate>? overrides)
    {
        var rates = new Dictionary<string, (decimal Tanks, decimal Kits)>(DefaultRates, StringComparer.Ordinal);
        if (overrides == null)
            return rates;

        foreach (var (key, value) in overrides.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!TryNormalizeBuild(key, out var build) || value == null || !IsValidRate(value.TanksPerDay, value.KitsPerDay))
                continue;
            // An exact canonical key takes precedence over aliases in manually edited JSON.
            if (key != build && overrides.TryGetValue(build, out var canonical) && canonical != null
                && IsValidRate(canonical.TanksPerDay, canonical.KitsPerDay))
                continue;
            rates[build] = (value.TanksPerDay, value.KitsPerDay);
        }
        return rates;
    }

    internal static bool TryCalculateStock(string build, string daysText,
        IReadOnlyDictionary<string, (decimal Tanks, decimal Kits)> rates, out int tanks, out int kits)
    {
        tanks = kits = 0;
        if (!TryNormalizeBuild(build, out var normalizedBuild) || !rates.TryGetValue(normalizedBuild, out var rate)
            || !IsValidRate(rate.Tanks, rate.Kits)
            || !int.TryParse(daysText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var days) || days <= 0)
            return false;

        // One farmer's full fleet: round the combined four-submarine stock upward.
        var tankStock = decimal.Ceiling(days * 4m * rate.Tanks);
        var kitStock = decimal.Ceiling(days * 4m * rate.Kits);
        if (tankStock > int.MaxValue || kitStock > int.MaxValue)
            return false;
        tanks = (int)tankStock;
        kits = (int)kitStock;
        return true;
    }

    internal static List<string> ReadBuilds(JsonElement character)
    {
        var builds = new List<string>();
        JsonElement registered;
        if ((!character.TryGetProperty("OfflineSubmarineData", out registered) || registered.ValueKind != JsonValueKind.Array)
            && (!character.TryGetProperty("SubmarineData", out registered) || registered.ValueKind != JsonValueKind.Array)
            && (!character.TryGetProperty("Submarines", out registered) || registered.ValueKind != JsonValueKind.Array))
            return builds;

        character.TryGetProperty("AdditionalSubmarineData", out var additional);
        foreach (var submarine in registered.EnumerateArray())
        {
            if (builds.Count == 4)
                break;
            var name = ReadName(submarine);
            var parts = default(JsonElement);
            if (!string.IsNullOrEmpty(name) && additional.ValueKind == JsonValueKind.Object)
            {
                // Match either dictionary key or nested Name, as Sub Manager does.
                foreach (var candidate in additional.EnumerateObject())
                {
                    if (candidate.Value.ValueKind == JsonValueKind.Object
                        && (candidate.Name == name || ReadName(candidate.Value) == name))
                    {
                        parts = candidate.Value;
                        break;
                    }
                }
            }
            var build = string.Empty;
            for (var i = 1; i <= 4; i++)
            {
                var id = 0;
                if (parts.ValueKind == JsonValueKind.Object
                    && parts.TryGetProperty($"Part{i}", out var part) && part.ValueKind == JsonValueKind.Number)
                    part.TryGetInt32(out id);
                build += PartCode(id);
            }
            builds.Add(build);
        }
        return builds;
    }

    private static string ReadName(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
        ? name.GetString() ?? string.Empty : string.Empty;

    private static string PartCode(int id) => id switch
    {
        >= 21792 and <= 21795 => "S",
        >= 21796 and <= 21799 => "U",
        >= 22526 and <= 22529 => "W",
        >= 23903 and <= 23906 => "C",
        >= 24344 and <= 24347 => "Y",
        >= 24348 and <= 24351 => "S+",
        >= 24352 and <= 24355 => "U+",
        >= 24356 and <= 24359 => "W+",
        >= 24360 and <= 24363 => "C+",
        >= 24364 and <= 24367 => "Y+",
        _ => "?",
    };

    internal static int? CalculateDays(int submarines, IReadOnlyList<string>? builds, int tanks, int kits,
        IReadOnlyDictionary<string, (decimal Tanks, decimal Kits)> rates, out int fallbackCount)
    {
        fallbackCount = 0;
        if (submarines <= 0)
            return null;
        decimal tanksPerDay = 0m, kitsPerDay = 0m;
        for (var i = 0; i < Math.Min(submarines, 4); i++)
        {
            var build = builds != null && i < builds.Count ? builds[i] : null;
            if (!TryNormalizeBuild(build, out var normalizedBuild) || !rates.TryGetValue(normalizedBuild, out var rate)
                || !IsValidRate(rate.Tanks, rate.Kits))
            {
                rate = (9m, 1.33m);
                fallbackCount++;
            }
            tanksPerDay += rate.Tanks;
            kitsPerDay += rate.Kits;
        }
        return (int)decimal.Floor(Math.Min(DaysForStock(tanks, tanksPerDay), DaysForStock(kits, kitsPerDay)));
    }

    private static decimal DaysForStock(int stock, decimal dailyRate)
    {
        var available = (decimal)Math.Max(0, stock);
        // Bound before dividing so tiny valid custom rates cannot overflow decimal or Int32.
        return available >= dailyRate * int.MaxValue ? int.MaxValue : available / dailyRate;
    }
}
