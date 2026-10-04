using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed partial class ViewItemWindow
{
    private const int CatalogPageSize = 100;
    private readonly ViewSingleItemCatalogSearch catalogSearch = new();
    private bool showCatalogSearch = true;
    private string catalogQuery = string.Empty;
    private string catalogSearchError = string.Empty;
    private int catalogPage;

    private void DrawCatalogSearch()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##ViewItemSearch", "Find an item by name...", ref catalogQuery, 128))
        {
            catalogPage = 0;
            catalogSearchError = string.Empty;
        }
        ImGui.TextDisabled("Type 3+ characters, or an exact 2-character item name.");
        ImGui.TextDisabled("Only items with supported previews are shown.");
        IReadOnlyList<ViewSingleItemSearchResult> results = Array.Empty<ViewSingleItemSearchResult>();
        if (string.IsNullOrEmpty(catalogSearchError))
        {
            try { results = catalogSearch.Search(catalogQuery); }
            catch (Exception error) { catalogSearchError = "Item search unavailable: " + error.Message; }
        }
        if (!string.IsNullOrEmpty(catalogSearchError))
        {
            ImGui.TextWrapped(catalogSearchError);
            if (ImGui.Button("Retry item search")) catalogSearchError = string.Empty;
            return;
        }
        if (results.Count == 0)
        {
            if (catalogQuery.Trim().Length >= 2) ImGui.TextDisabled("No matching items with a supported preview.");
            return;
        }

        catalogPage = Math.Clamp(catalogPage, 0, (results.Count - 1) / CatalogPageSize);
        var start = catalogPage * CatalogPageSize;
        var end = Math.Min(start + CatalogPageSize, results.Count);
        ImGui.TextDisabled($"{start + 1:N0}-{end:N0} of {results.Count:N0} items. Select an item to preview it.");
        if (ImGui.BeginChild("##ViewItemSearchResults", new Vector2(0, 170 * ImGuiHelpers.GlobalScale), true))
        {
            for (var index = start; index < end; index++)
            {
                var item = results[index];
                if (ImGui.Selectable($"{item.Name} ({item.ItemId})##ViewItemSearch{item.ItemId}"))
                {
                    service.QueueCatalogItem(item.ItemId);
                    showCatalogSearch = false;
                }
            }
        }
        ImGui.EndChild();
        if (catalogPage > 0 && ImGui.Button("Previous##ViewItemSearchPage")) catalogPage--;
        if (catalogPage > 0) ImGui.SameLine();
        if (end < results.Count && ImGui.Button("Next##ViewItemSearchPage")) catalogPage++;
        ImGui.TextDisabled("Framer's Kits include adventurer plate and portrait artwork.");
    }
}
