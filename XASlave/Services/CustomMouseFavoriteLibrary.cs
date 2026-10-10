using System;
using System.Collections.Generic;
using System.IO;

namespace XASlave.Services;

/// <summary>Persistent local cursor favorites, independent of appearance settings and mod presets.</summary>
public sealed class CustomMouseFavoriteLibrary
{
    public List<string> Paths { get; set; } = [];
    public bool ExistingAssignmentsImported { get; set; }

    /// <summary>Import configured files once; later resets, presets and removed favorites never repeat the import.</summary>
    internal bool ImportExistingOnce(CustomMouseSettings? settings)
    {
        var repairedList = Paths == null;
        Paths ??= [];
        if (ExistingAssignmentsImported) return repairedList;
        if (settings != null)
            foreach (var role in Enum.GetValues<CustomMouseArtworkRole>())
                _ = Add(settings.GetArtworkPath(role));
        ExistingAssignmentsImported = true;
        return true;
    }

    internal bool Add(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0) return false;
        Paths ??= [];
        if (Paths.Exists(existing => string.Equals(NormalizePath(existing), normalized, StringComparison.OrdinalIgnoreCase))) return false;
        Paths.Add(normalized);
        return true;
    }

    /// <summary>Only an explicit library action removes entries; existence checks never prune saved paths.</summary>
    internal bool Remove(string path)
    {
        if (Paths == null) return false;
        var normalized = NormalizePath(path);
        return Paths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)
            || (normalized.Length != 0 && string.Equals(NormalizePath(existing), normalized, StringComparison.OrdinalIgnoreCase))) != 0;
    }

    internal static string NormalizePath(string? path)
    {
        if (path is not { Length: <= 1024 }) return string.Empty;
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(trimmed)) return string.Empty;
            var absolute = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(absolute);
            // Match the loader's local drive syntax without filesystem/network I/O in the menu.
            if (root == null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':') return string.Empty;
            var extension = Path.GetExtension(trimmed);
            if (!extension.Equals(".ani", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".cur", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return absolute.Length <= 1024 ? absolute : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
