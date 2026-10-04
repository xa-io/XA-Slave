using System;
using System.Collections.Generic;
using System.Linq;
using Item = Lumina.Excel.Sheets.Item;

namespace XASlave.Services;

internal sealed record ViewSingleItemSearchResult(uint ItemId, string Name);

// Search keeps managed item IDs/names and cached visual availability. Captures
// still use the existing guarded item route after an explicit selection.
internal sealed class ViewSingleItemCatalogSearch
{
    private List<ViewSingleItemSearchResult>? catalog;
    private readonly Dictionary<uint, bool> supportedItems = new();
    private readonly Dictionary<uint, bool> kitArtwork = new();
    private string? cachedQuery;
    private IReadOnlyList<ViewSingleItemSearchResult> results = Array.Empty<ViewSingleItemSearchResult>();

    internal IReadOnlyList<ViewSingleItemSearchResult> Search(string query)
    {
        var normalized = query.Trim();
        if (string.Equals(cachedQuery, normalized, StringComparison.OrdinalIgnoreCase)) return results;
        var next = normalized.Length < 2 ? Array.Empty<ViewSingleItemSearchResult>()
            : GetCatalog().Where(entry => Matches(entry.Name, normalized))
                .Where(entry => IsSupported(entry.ItemId))
                .OrderBy(entry => Rank(entry.Name, normalized))
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ItemId).ToArray();
        // Do not cache a failed sheet read as a successful empty result.
        cachedQuery = normalized;
        results = next;
        return results;
    }

    internal static bool Matches(string name, string query)
    {
        if (query.Length < 2) return false;
        if (query.Length == 2)
            return name.Length == 2 && string.Equals(name, query, StringComparison.OrdinalIgnoreCase);
        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        return name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private bool IsSupported(uint itemId)
    {
        if (supportedItems.TryGetValue(itemId, out var cached)) return cached;
        if (!ViewSingleItemShopSource.TryDescribe(itemId, out var entry, out _))
        {
            supportedItems[itemId] = false;
            return false;
        }
        // Either plate or portrait artwork is previewable. Kit zero is valid.
        var supported = entry.Key != 105
            || (entry.FramerKitId is { } kitId && HasKitArtwork(kitId));
        supportedItems[itemId] = supported;
        return supported;
    }

    private bool HasKitArtwork(uint kitId)
    {
        if (kitArtwork.TryGetValue(kitId, out var cached)) return cached;
        var available = ViewSingleItemFramerKit.Load(kitId).Count > 0;
        kitArtwork[kitId] = available;
        return available;
    }

    private List<ViewSingleItemSearchResult> GetCatalog()
    {
        if (catalog != null) return catalog;
        var loaded = new List<ViewSingleItemSearchResult>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
        {
            var name = item.Name.ToString().Trim();
            if (item.RowId == 0 || string.IsNullOrWhiteSpace(name)) continue;
            // Keep every named candidate, including untradeable items. Resolve
            // visual availability only for matching names and cache that result.
            loaded.Add(new(item.RowId, name));
        }
        catalog = loaded;
        return catalog;
    }
}
