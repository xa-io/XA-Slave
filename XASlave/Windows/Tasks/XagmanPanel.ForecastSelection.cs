using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private XagmanForecastItemKey xagmanTonyForecastSelectionItem;
    private int xagmanTonyForecastSelectionMinimum;
    private DateTime xagmanTonyForecastNextCheckUtc;
    private XagmanTonyForecastSelectionRequest? xagmanTonyForecastSelectionRequest;
    private UiMessage xagmanTonyForecastSelectionStatus = UiMessage.Raw(string.Empty);
    private readonly ConditionalWeakTable<object, XagmanForecastProviderIdentity> xagmanForecastProviderIdentities = new();

    private sealed class XagmanForecastProviderIdentity
    {
        public string Value { get; } = Guid.NewGuid().ToString("N");
    }

    private sealed class XagmanTonyForecastSelectionRequest
    {
        public DateTime RequestedUtc { get; init; }
        public string SetupKey { get; init; } = string.Empty;
        public XagmanForecastItemDefinition Item { get; init; } = new();
        public int Minimum { get; init; }
        public string? ProviderIdentity { get; set; }
        public string? EligibilityKey { get; set; }
    }

    // Framework-update use only. A loaded-instance identity also detects a reload of the same DLL.
    private string GetXagmanForecastInventoryProviderIdentity()
    {
        var available = plugin.IpcClient.IsXaDatabaseAvailable();
        var instance = AutoRetainerUiReflectionService.TryGetLoadedPluginInstance("XADatabase", "XADatabase.Plugin");
        var identity = instance == null ? "none"
            : xagmanForecastProviderIdentities.GetValue(instance, _ => new XagmanForecastProviderIdentity()).Value;
        return $"{available}:{identity}";
    }

    private bool CanEditXagmanTonyForecastSelection()
        => CanEditXagmanCharacterLists() && xagmanPendingMatchSelection == null
            && plugin.Configuration.XagmanRole == XagmanRole.Tony
            && !plugin.Configuration.XagmanOutsideNetworkHelper && plugin.XagmanPeers.IsConnected;

    private List<XagmanForecastItemDefinition> GetXagmanTonyForecastSelectionItems()
        => xagmanTradeCapacityView?.Groups.SelectMany(group => group.Items)
            .GroupBy(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
            .Select(group => group.First())
            .Where(item => item.ItemId != 0 && (IsXagmanGilItem(item.ItemId) || IsXagmanForecastItemTradable(item.ItemId)))
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.IsHq)
            .Select(item => new XagmanForecastItemDefinition
            {
                ItemId = item.ItemId, IsHq = item.IsHq, ItemName = item.ItemName, StackSize = item.StackSize,
            }).ToList() ?? new List<XagmanForecastItemDefinition>();

    private string GetXagmanTonyForecastSelectionSetupKey()
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Setup"] = GetXagmanTonyAutoSetupKey(),
            ["Item"] = xagmanTonyForecastSelectionItem,
            ["Minimum"] = xagmanTonyForecastSelectionMinimum,
            ["Items"] = GetXagmanTonyForecastSelectionItems().Select(item => new Dictionary<string, object?>
            {
                ["ItemId"] = item.ItemId, ["IsHq"] = item.IsHq, ["ItemName"] = item.ItemName,
            }).ToArray(),
        });

    private void CancelXagmanTonyForecastSelection(UiMessage message)
    {
        xagmanTonyForecastSelectionRequest = null;
        xagmanTonyForecastSelectionStatus = message;
    }

    private void CheckXagmanTonyForecastSelectionState(DateTime now)
    {
        var request = xagmanTonyForecastSelectionRequest;
        if (request == null)
            return;
        if (now < xagmanTonyForecastNextCheckUtc)
            return;
        xagmanTonyForecastNextCheckUtc = now.AddSeconds(0.25);
        if (!CanEditXagmanTonyForecastSelection()
            || !string.Equals(request.SetupKey, GetXagmanTonyForecastSelectionSetupKey(), StringComparison.Ordinal))
        {
            CancelXagmanTonyForecastSelection(UiMessage.Authored("Stock selection cancelled because the setup or inventory provider changed."));
            return;
        }
        if ((now - request.RequestedUtc).TotalSeconds > 60)
        {
            CancelXagmanTonyForecastSelection(UiMessage.Authored("Stock selection timed out. Refresh inventory and try again."));
            return;
        }
        try
        {
            var provider = GetXagmanForecastInventoryProviderIdentity();
            var cfg = plugin.Configuration;
            var eligibility = JsonSerializer.Serialize(cfg.XagmanTonyCharacters
                .Select(entry => IsAutoRetainerCharacterAllowed(entry.CharacterNameWorld, cfg.XagmanHonorArExclusions)).ToArray());
            request.ProviderIdentity ??= provider;
            request.EligibilityKey ??= eligibility;
            if (!string.Equals(request.ProviderIdentity, provider, StringComparison.Ordinal)
                || !string.Equals(request.EligibilityKey, eligibility, StringComparison.Ordinal))
                CancelXagmanTonyForecastSelection(UiMessage.Authored("Stock selection cancelled because the setup or inventory provider changed."));
        }
        catch
        {
            CancelXagmanTonyForecastSelection(UiMessage.Authored("Inventory lookup failed. The current selection was kept."));
        }
    }

    private void UpdateXagmanTonyForecastSelection(DateTime now)
    {
        xagmanTonyForecastNextCheckUtc = DateTime.MinValue;
        CheckXagmanTonyForecastSelectionState(now);
        var request = xagmanTonyForecastSelectionRequest;
        if (request == null)
            return;
        if (!IsXagmanTradeCapacityViewCurrent(now))
        {
            xagmanTradeCapacityQueryDeferred = true;
            return;
        }
        try
        {
            var cfg = plugin.Configuration;
            var roster = cfg.XagmanTonyCharacters;
            var eligible = roster.Select(entry => !string.IsNullOrWhiteSpace(entry.CharacterNameWorld)
                && IsAutoRetainerCharacterAllowed(entry.CharacterNameWorld, cfg.XagmanHonorArExclusions)).ToArray();
            // The full configured roster is intentional: a later threshold can reselect a removed Tony.
            var snapshots = BuildXagmanForecastInventorySnapshots(
                roster.Where((_, index) => eligible[index]).Select(entry => entry.CharacterNameWorld).ToArray(),
                new[] { request.Item });
            if (xagmanTradeCapacityQueryDeferred)
                return;
            var query = (string.IsNullOrWhiteSpace(request.Item.ItemName)
                ? request.Item.ItemId.ToString(CultureInfo.InvariantCulture) : request.Item.ItemName).Trim();
            if (!IsXagmanGilItem(request.Item.ItemId)
                && xagmanTradeCapacitySearchCache.TryGetValue(query, out var cached) && !cached.Succeeded)
            {
                CancelXagmanTonyForecastSelection(UiMessage.Authored("Inventory lookup failed. The current selection was kept."));
                return;
            }
            var candidates = roster.Select((entry, index) =>
            {
                snapshots.TryGetValue(entry.CharacterNameWorld, out var snapshot);
                return new XagmanForecastSelectionCandidate(index, entry.CharacterNameWorld, eligible[index],
                    snapshot?.IsKnown == true, snapshot == null ? 0L
                        : IsXagmanGilItem(request.Item.ItemId) ? Math.Max(0L, snapshot.Gil - GetXagmanTonyGilMinimum())
                        : snapshot.GetQuantity(request.Item.Key));
            }).ToArray();
            // Recheck immediately before committing; no partial result changes the checked roster.
            xagmanTonyForecastNextCheckUtc = DateTime.MinValue;
            CheckXagmanTonyForecastSelectionState(now);
            if (!ReferenceEquals(request, xagmanTonyForecastSelectionRequest))
                return;
            if (!XagmanForecastSelectionPolicy.TrySelectAtLeast(candidates, request.Minimum,
                    CanEditXagmanTonyForecastSelection() && IsXagmanTradeCapacityViewCurrent(now),
                    out var selected, out _, out var unknown))
            {
                CancelXagmanTonyForecastSelection(UiMessage.Authored("No known stock is available. The current selection was kept."));
                return;
            }
            xagmanTonyForecastSelectionRequest = null;
            StopXagmanTonyAutoSelection(UiMessage.Key("S7886807C2BF23CA0"));
            if (!xagmanTonySelectedIndices.SetEquals(selected))
            {
                xagmanTonySelectedIndices.Clear();
                xagmanTonySelectedIndices.UnionWith(selected);
                InvalidateXagmanTradeCapacityForecast();
            }
            xagmanTonyForecastSelectionStatus = UiMessage.Format(
                UiLocale.Key("Selected {0} Tonys; skipped {1} with unknown or stale stock."), selected.Count, unknown);
        }
        catch
        {
            CancelXagmanTonyForecastSelection(UiMessage.Authored("Inventory lookup failed. The current selection was kept."));
        }
    }

    private void DrawXagmanTonyForecastSelection(XagmanTradeCapacityView view)
    {
        var items = GetXagmanTonyForecastSelectionItems();
        if (items.Count == 0)
            return;
        if (!items.Any(item => item.Key == xagmanTonyForecastSelectionItem))
            xagmanTonyForecastSelectionItem = items[0].Key;
        var selectedItem = items.First(item => item.Key == xagmanTonyForecastSelectionItem);
        var canEdit = CanEditXagmanTonyForecastSelection() && IsXagmanTradeCapacityViewCurrent(DateTime.UtcNow);
        using (ImRaii.Disabled(!canEdit))
        {
            ImGui.SetNextItemWidth(Scale(250f));
            var label = selectedItem.IsHq ? UiMessage.Format("S5864848AE53E7CAF", selectedItem.ItemName).Display : selectedItem.ItemName;
            using (var combo = UiNative.ComboScope($"{UiLocale.Native(UiLocale.Key("Stock item"))}##xagmanTonyStockItem", label))
            {
                if (combo)
                {
                    foreach (var item in items)
                    {
                        var itemLabel = item.IsHq ? UiMessage.Format("S5864848AE53E7CAF", item.ItemName).Display : item.ItemName;
                        if (ImGui.Selectable($"{itemLabel}##stock{item.ItemId}_{item.IsHq}", item.Key == xagmanTonyForecastSelectionItem))
                            xagmanTonyForecastSelectionItem = item.Key;
                    }
                }
            }
            ImGui.SetNextItemWidth(Scale(150f));
            if (UiNative.InputInt($"{UiLocale.Native(UiLocale.Key("Minimum stock"))}##xagmanTonyStockMinimum", ref xagmanTonyForecastSelectionMinimum))
                xagmanTonyForecastSelectionMinimum = Math.Max(0, xagmanTonyForecastSelectionMinimum);
            using (ImRaii.Disabled(xagmanTonyForecastSelectionRequest != null))
            {
                if (UiNative.Button($"{UiLocale.Native(UiLocale.Key("Select matching stock"))}##xagmanTonyStockSelect"))
                {
                    StopXagmanTonyAutoSelection(UiMessage.Key("S7886807C2BF23CA0"));
                    var item = items.First(candidate => candidate.Key == xagmanTonyForecastSelectionItem);
                    xagmanTonyForecastSelectionRequest = new XagmanTonyForecastSelectionRequest
                    {
                        RequestedUtc = DateTime.UtcNow, SetupKey = GetXagmanTonyForecastSelectionSetupKey(),
                        Item = item, Minimum = xagmanTonyForecastSelectionMinimum,
                    };
                    xagmanTonyForecastSelectionStatus = UiMessage.Authored("Checking saved stock...");
                    xagmanTonyForecastNextCheckUtc = DateTime.MinValue;
                    InvalidateXagmanTradeCapacityForecast();
                }
            }
        }
        ImGui.TextWrapped(UiMessage.Authored("Selects from the full Tony roster using saved stock at or above the minimum. Unknown or stale stock is excluded. Save a named character list to keep the selection.").Display);
        if (IsXagmanGilItem(xagmanTonyForecastSelectionItem.ItemId))
            ImGui.TextWrapped(UiMessage.Authored("For gil, stock excludes the Tony gil reserve.").Display);
        if (xagmanTonyForecastSelectionStatus.Original.Length > 0)
            ImGui.TextWrapped(xagmanTonyForecastSelectionStatus.Display);
    }

    private void RemoveXagmanTonyFromForecastSelection(string characterKey)
    {
        if (!CanEditXagmanTonyForecastSelection() || !IsXagmanTradeCapacityViewCurrent(DateTime.UtcNow))
            return;
        StopXagmanTonyAutoSelection(UiMessage.Key("S7886807C2BF23CA0"));
        CancelXagmanTonyForecastSelection(UiMessage.Raw(string.Empty));
        if (XagmanForecastSelectionPolicy.RemoveCharacter(xagmanTonySelectedIndices,
                plugin.Configuration.XagmanTonyCharacters.Select(entry => entry.CharacterNameWorld).ToArray(), characterKey, true))
        {
            xagmanTonyForecastSelectionStatus = UiMessage.Authored("Tony removed from the current selection.");
            InvalidateXagmanTradeCapacityForecast();
        }
    }
}
