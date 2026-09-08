using System;
using System.Globalization;

namespace XASlave.Services;

internal static class ResolutionCommandParser
{
    public static bool TryParseDimensions(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(
            ['x', 'X', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
    }
}
