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
    private global::XASlave.Localization.UiMessage? catalogSearchErrorUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private int catalogPage;

    private void DrawCatalogSearch()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##ViewItemSearch", global::XASlave.Localization.UiLocale.Get("S656D278DBC71D748"), ref catalogQuery, 128))
        {
            catalogPage = 0;
            catalogSearchError = global::XASlave.Localization.UiMessage.Store(ref catalogSearchErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7C04074DCEC493EE"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SC50327252EA27F25"));
        IReadOnlyList<ViewSingleItemSearchResult> results = Array.Empty<ViewSingleItemSearchResult>();
        if (string.IsNullOrEmpty(catalogSearchError))
        {
            try { results = catalogSearch.Search(catalogQuery); }
            catch (Exception error) { catalogSearchError = global::XASlave.Localization.UiMessage.Store(ref catalogSearchErrorUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SCFF34D5ECCEB4A07"), global::XASlave.Localization.UiMessage.FromException(error))); }
        }
        if (!string.IsNullOrEmpty(catalogSearchError))
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref catalogSearchErrorUiMessage, catalogSearchError));
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SCC6ECDD128D87B10"))) catalogSearchError = global::XASlave.Localization.UiMessage.Store(ref catalogSearchErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
            return;
        }
        if (results.Count == 0)
        {
            if (catalogQuery.Trim().Length >= 2) ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SC9660E47B7C4C08C"));
            return;
        }

        catalogPage = Math.Clamp(catalogPage, 0, (results.Count - 1) / CatalogPageSize);
        var start = catalogPage * CatalogPageSize;
        var end = Math.Min(start + CatalogPageSize, results.Count);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S66382812743DBB36", start + 1, end, results.Count));
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
        if (catalogPage > 0 && global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SA57B08A480B822A0") + "##ViewItemSearchPage"))) catalogPage--;
        if (catalogPage > 0) ImGui.SameLine();
        if (end < results.Count && global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S1FF57A29D7C9D11B") + "##ViewItemSearchPage"))) catalogPage++;
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S974C9F66C5473826"));
    }
}
