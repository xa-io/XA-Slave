using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XASlave.Services;

public enum ExpertDeliveryOutcome { Completed, ProtectedOnly, SealCapBlocked, Cancelled, Failed }

public sealed record ExpertDeliveryResult(long RunId, ExpertDeliveryOutcome Outcome, int Delivered, int Protected, int CapBlocked, long ElapsedMilliseconds, string Message);

internal static class ExpertDeliveryPolicy
{
    // Persisted values: 0 normal, 1 fast, 2 conservative. Unknown values use normal.
    internal static (int Action, int Scan) Timing(int profile) => profile switch
    {
        1 => (100, 50),
        2 => (250, 100),
        _ => (150, 100),
    };

    internal static bool Fits(uint current, uint maximum, uint reward)
        => current <= maximum && reward <= maximum - current;

    internal static bool TryParseProtectedIds(string? text, out HashSet<uint> ids)
    {
        ids = new();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Length > 4096) return false;
        foreach (var token in text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 || id >= 1_000_000)
                return false;
            ids.Add(id);
        }
        return true;
    }

    internal static string NormalizePrompt(string? text)
    {
        var result = new StringBuilder();
        foreach (var c in text ?? string.Empty)
            if (!char.IsWhiteSpace(c)) result.Append(c);
        return result.ToString();
    }

    internal static bool PromptMatches(string actual, string expected)
        => !string.IsNullOrWhiteSpace(expected) && NormalizePrompt(actual).Equals(NormalizePrompt(expected), StringComparison.Ordinal);

    internal static bool IsValidCommand(string? command)
        => !string.IsNullOrWhiteSpace(command) && command.StartsWith('/') && Encoding.UTF8.GetByteCount(command) <= 500
           && !command.Contains('\n') && !command.Contains('\r') && !command.Contains('\0');
}
