using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal static class PluginBuilderCommandRules
{
    internal const int MaxNameLength = 64;

    internal static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength
            && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

    // An empty saved name disables the shortcut. Never silently alter a name into another window's shortcut.
    internal static bool ValidateName(IEnumerable<PluginBuilderWindowDefinition> windows, Guid windowId, string name, out string error)
    {
        error = string.Empty;
        if (name.Length == 0) return true;
        if (!IsValidName(name))
        {
            error = "Use up to 64 letters, numbers, underscores or hyphens, without spaces.";
            return false;
        }
        if (windows.Any(window => window.Id != windowId && string.Equals(window.CommandName, name, StringComparison.OrdinalIgnoreCase)))
        {
            error = "Another saved window already uses this command name.";
            return false;
        }
        return true;
    }

    internal static bool TryFindWindow(IEnumerable<PluginBuilderWindowDefinition> windows, string name,
        out PluginBuilderWindowDefinition? window, out string error)
    {
        window = null;
        if (!IsValidName(name))
        {
            error = "Usage: /xa win <name>. Set a name without spaces in Plugin Builder > Saved windows.";
            return false;
        }
        var matches = windows.Where(item => string.Equals(item.CommandName, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            error = matches.Length == 0
                ? "No saved window uses that command name. Check Plugin Builder > Saved windows."
                : "That command name is assigned to more than one window. Give each saved window a unique name.";
            return false;
        }
        window = matches[0];
        error = string.Empty;
        return true;
    }
}
