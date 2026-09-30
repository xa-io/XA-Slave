using System;
using System.Globalization;
using System.Numerics;

namespace XASlave.Services;

internal static class XagmanMeetingCoordinates
{
    internal static bool TryParse(string? input, out Vector3 position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 160)
            return false;
        // Keep empty comma components so "1,,2,3" cannot silently become a valid point.
        var parts = input.Contains(',') ? input.Split(',')
            : input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;
        position = new Vector3(x, y, z);
        return true;
    }

    internal static bool IsTerritoryMatch(uint configured, uint expected, uint actual)
        => configured != 0 && expected != 0 && configured == expected && actual == expected;
}
