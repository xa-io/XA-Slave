using System;

namespace XASlave.Services;

internal static class IpcCommandPolicy
{
    public static string NormalizeTaskName(string? taskName)
        => (taskName ?? string.Empty).Trim();

    public static string NormalizeCommand(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return string.Empty;

        var trimmed = commandText.Trim();
        if (trimmed.StartsWith("/xa", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])))
        {
            return trimmed.Length == 3 ? string.Empty : trimmed[3..].Trim();
        }

        return trimmed;
    }
}
