using System;
using System.Collections.Generic;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int MaxDebugCallbackValues = 32;

    internal enum DebugCallbackValueKind
    {
        Null,
        Int,
        UInt,
        Bool,
    }

    internal readonly record struct DebugCallbackValue(
        DebugCallbackValueKind Kind,
        int IntValue,
        uint UIntValue,
        bool BoolValue,
        string Display);

    internal static bool TryParseDebugCallbackValues(
        string rawValues,
        out List<DebugCallbackValue> values,
        out string error)
    {
        values = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValues))
            return true;

        var tokens = rawValues.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > MaxDebugCallbackValues)
        {
            error = $"At most {MaxDebugCallbackValues} callback values are allowed.";
            return false;
        }

        foreach (var token in tokens)
        {
            if (token.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                values.Add(new DebugCallbackValue(DebugCallbackValueKind.Null, 0, 0, false, "null"));
                continue;
            }

            if (bool.TryParse(token, out var boolValue))
            {
                values.Add(new DebugCallbackValue(
                    DebugCallbackValueKind.Bool,
                    boolValue ? 1 : 0,
                    0,
                    boolValue,
                    boolValue ? "true" : "false"));
                continue;
            }

            if (token.StartsWith("uint:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
            {
                var separatorIndex = token.IndexOf(':');
                var uintText = separatorIndex >= 0 ? token[(separatorIndex + 1)..] : string.Empty;
                if (uint.TryParse(uintText, out var uintValue))
                {
                    values.Add(new DebugCallbackValue(
                        DebugCallbackValueKind.UInt,
                        0,
                        uintValue,
                        false,
                        $"uint:{uintValue}"));
                    continue;
                }

                error = $"'{token}' is not a valid uint token.";
                return false;
            }

            if (int.TryParse(token, out var intValue))
            {
                values.Add(new DebugCallbackValue(
                    DebugCallbackValueKind.Int,
                    intValue,
                    0,
                    false,
                    intValue.ToString()));
                continue;
            }

            error = $"Unsupported token '{token}'. Use signed integers, true/false, null, or uint:<value>.";
            return false;
        }

        return true;
    }
}
