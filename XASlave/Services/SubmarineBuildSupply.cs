using System;
using System.Collections.Generic;
using System.Text.Json;

namespace XASlave.Services;

internal static class SubmarineBuildSupply
{
    // XA Sub Manager src/core/Config.cpp defaults (2026-10-09).
    private static readonly Dictionary<string, (decimal Tanks, decimal Kits)> Rates = new(StringComparer.Ordinal)
    {
        ["WSUC"] = (9.0m, 1.33m),
        ["SSUC"] = (9.0m, 1.33m),
        ["W+S+U+C+"] = (9.0m, 3.43m),
        ["S+S+S+C+"] = (9.0m, 3.43m),
        ["YUUW"] = (7.5m, 1.40m),
        ["Y+U+U+W+"] = (10.0m, 3.07m),
        ["WCSU"] = (10.0m, 1.67m),
        ["WUSS"] = (10.0m, 1.67m),
        ["W+U+S+S+"] = (10.0m, 3.20m),
        ["YSYC"] = (10.0m, 2.50m),
        ["Y+S+Y+C+"] = (10.0m, 3.20m),
        ["S+S+U+C"] = (14.0m, 3.67m),
        ["S+S+U+C+"] = (14.0m, 4.00m),
        ["WCYC"] = (10.5m, 2.00m),
        ["WUWC"] = (10.5m, 2.00m),
        ["W+U+W+C+"] = (10.5m, 3.00m),
        ["YSCU"] = (9.0m, 1.67m),
        ["SCUS"] = (9.0m, 1.67m),
        ["S+C+U+S+"] = (13.5m, 4.00m),
    };

    internal static IEnumerable<string> SupportedBuilds => Rates.Keys;

    internal static bool TryCalculateStock(string build, string daysText, out int tanks, out int kits)
    {
        tanks = kits = 0;
        if (!Rates.TryGetValue(build, out var rate)
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

    internal static int? CalculateDays(int submarines, IReadOnlyList<string>? builds, int tanks, int kits, out int fallbackCount)
    {
        fallbackCount = 0;
        if (submarines <= 0)
            return null;
        decimal tanksPerDay = 0m, kitsPerDay = 0m;
        for (var i = 0; i < Math.Min(submarines, 4); i++)
        {
            var build = builds != null && i < builds.Count ? builds[i] : null;
            if (build == null || !Rates.TryGetValue(build, out var rate))
            {
                rate = (9m, 1.33m);
                fallbackCount++;
            }
            tanksPerDay += rate.Tanks;
            kitsPerDay += rate.Kits;
        }
        return (int)decimal.Floor(Math.Min(Math.Max(0, tanks) / tanksPerDay, Math.Max(0, kits) / kitsPerDay));
    }
}
