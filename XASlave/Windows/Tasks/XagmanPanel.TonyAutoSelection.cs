using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using XASlave.Data;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool xagmanTonyAutoPending;
    private bool xagmanTonyAutoTracking;
    private DateTime xagmanTonyAutoRequestedUtc;
    private DateTime xagmanTonyAutoValidUntilUtc;
    private DateTime xagmanTonyAutoNextSetupCheckUtc;
    private string xagmanTonyAutoSetupKey = string.Empty;
    private string xagmanTonyAutoInputKey = string.Empty;
    private string xagmanTonyAutoStatus = string.Empty;
    private int xagmanTonyAutoStatusKind; // 0: advisory, 1: covered, 2: shortfall

    private bool CanSelectXagmanTonyAutomatically()
        => !xagmanRunning && !plugin.TaskRunner.IsRunning && !xagmanTradeSafetySessionActive
            && System.Threading.Volatile.Read(ref xagmanManualXaDatabasePullRunning) == 0 && xagmanPendingMatchSelection == null
            && plugin.Configuration.XagmanRole == XagmanRole.Tony
            && !plugin.Configuration.XagmanOutsideNetworkHelper && plugin.XagmanPeers.IsConnected;

    private string GetXagmanTonyAutoSetupKey()
    {
        var cfg = plugin.Configuration;
        return JsonSerializer.Serialize(new
        {
            cfg.XagmanRole,
            cfg.XagmanOutsideNetworkHelper,
            cfg.XagmanServerMatchingEnabled,
            cfg.XagmanTargetWorld,
            cfg.XagmanTargetAetheryte,
            cfg.XagmanTonyGilMinimum,
            cfg.XagmanHonorArExclusions,
            Routes = cfg.XagmanServerMeetWorlds.OrderBy(pair => pair.Key).ToArray(),
            Roster = cfg.XagmanTonyCharacters.Select(entry => entry.CharacterNameWorld).ToArray(),
            Selected = xagmanTonySelectedIndices.OrderBy(index => index).ToArray(),
        });
    }

    private void StopXagmanTonyAutoSelection(string message)
    {
        xagmanTonyAutoPending = false;
        xagmanTonyAutoTracking = false;
        xagmanTonyAutoStatus = message;
        xagmanTonyAutoStatusKind = 0;
    }

    private void CheckXagmanTonyAutoSelectionState(DateTime now)
    {
        if (!xagmanTonyAutoPending && !xagmanTonyAutoTracking)
            return;
        if (now < xagmanTonyAutoNextSetupCheckUtc)
            return;
        xagmanTonyAutoNextSetupCheckUtc = now.AddSeconds(0.25);
        if (!CanSelectXagmanTonyAutomatically()
            || !GetXagmanTonyAutoSetupKey().Equals(xagmanTonyAutoSetupKey, StringComparison.Ordinal))
        {
            StopXagmanTonyAutoSelection("Auto: selection or setup changed. Run Auto again when idle to check coverage.");
        }
        else if (xagmanTonyAutoPending && (now - xagmanTonyAutoRequestedUtc).TotalSeconds > 60)
        {
            StopXagmanTonyAutoSelection("Auto: inventory forecasting did not finish. Existing selection kept; refresh inventory and try again.");
        }
    }

    private void DrawXagmanTonyAutoSelection()
    {
        CheckXagmanTonyAutoSelectionState(DateTime.UtcNow);
        ImGui.SameLine();
        using (ImRaii.Disabled(xagmanTonyAutoPending || !CanSelectXagmanTonyAutomatically()))
        {
            if (ImGui.Button("Auto##xagmanTonyAuto"))
            {
                xagmanTonyAutoSetupKey = GetXagmanTonyAutoSetupKey();
                xagmanTonyAutoRequestedUtc = DateTime.UtcNow;
                xagmanTonyAutoNextSetupCheckUtc = DateTime.MinValue;
                xagmanTonyAutoPending = true;
                xagmanTonyAutoTracking = false;
                xagmanTonyAutoStatusKind = 0;
                xagmanTonyAutoStatus = "Auto: checking connected FO forecasts and Tony inventories...";
                InvalidateXagmanTradeCapacityForecast();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Attempt to select enough Tonys for connected FO collection and supply forecasts. Prefer the most free inventory spaces and require at least 5,000 gil. Uses the full eligible Tony roster, including characters hidden by Region, Search or Selected Only; Honor Exclusions still applies. Replaces the selection when forecasting is complete; reports missing space or stock if more eligible Tonys are needed. Available while connected and idle.");
        if (string.IsNullOrWhiteSpace(xagmanTonyAutoStatus))
            return;
        var current = !xagmanTonyAutoTracking || DateTime.UtcNow <= xagmanTonyAutoValidUntilUtc;
        var kind = current ? xagmanTonyAutoStatusKind : 0;
        var color = kind == 1 ? new Vector4(0.4f, 0.9f, 0.4f, 1f)
            : kind == 2 ? new Vector4(1f, 0.4f, 0.4f, 1f) : new Vector4(1f, 0.8f, 0.3f, 1f);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(current ? xagmanTonyAutoStatus : "Auto: refreshing forecasts before confirming coverage...");
    }

    private bool TryGetXagmanTonyAutoDemands(DateTime now, IReadOnlyList<XagmanPeerPresence> peers,
        out List<XagmanTonyAutoDemand> demands, out string error, out DateTime validUntil)
    {
        demands = new List<XagmanTonyAutoDemand>();
        error = string.Empty;
        validUntil = now.AddSeconds(XagmanTradeCapacityPeerFreshSeconds);
        var view = xagmanTradeCapacityView;
        if (view == null || peers.Count == 0)
            error = "connect the FO clients and select their owners first";
        else if (view.MissingForecastPeerCount > 0 || view.StaleForecastPeerCount > 0
            || view.StalePresencePeerCount > 0 || peers.Any(peer => !IsXagmanTradeCapacityPeerFresh(peer, now)
                || peer.TradeCapacityForecast == null || !IsXagmanTradeCapacityForecastFresh(peer.TradeCapacityForecast, now)))
            error = "waiting for fresh forecasts from every connected FO client";
        else if (view.TruncatedForecastPeerCount > 0 || view.DuplicateOwnerKeys.Count > 0)
            error = "FO forecasts contain truncated data or duplicate owners";
        else if (peers.Any(peer => peer.TradeCapacityForecast is { } forecast
            && (!forecast.SchedulingPoliciesComplete || forecast.UnknownOwnerCount != 0
                || forecast.KnownOwnerCount != forecast.SelectedOwnerCount
                || forecast.SelectedOwnerKeys == null || forecast.Items == null
                || forecast.SelectedOwnerKeys.Count != forecast.SelectedOwnerCount
                || forecast.SelectedOwnerKeys.Any(string.IsNullOrWhiteSpace)
                || forecast.Items.Any(row => row.ItemId == 0 || row.UnknownOwnerCount != 0
                    || row.IncomingToTonyQuantity < 0 || row.NeededFromTonyQuantity < 0
                    || !IsXagmanForecastItemTradable(row.ItemId)))))
            error = "FO inventory or item policies are incomplete; refresh their forecasts";
        else if (view.Groups.Any(group => group.AllAvailableRequestCount > 0))
            error = "Take-all requests have no finite target; choose a quantity before using Auto";
        else if (view.SelectedOwnerCount == 0)
            error = "no FO owners are selected";
        else if (string.IsNullOrWhiteSpace(plugin.Configuration.XagmanTargetAetheryte))
            error = "configure a meetup aetheryte first";
        else if (peers.SelectMany(peer => peer.TradeCapacityForecast!.SelectedOwnerKeys)
            .Any(key => NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(key)) == "Unknown")
            || (view.ServerMatching && view.Groups.Any(group => group.GroupKey == "Unknown")))
            error = "an FO home region is unknown";

        if (error.Length > 0)
            return false;
        // Match runtime routing. Fixed-world collection may pool only characters that can visit it.
        if (view!.ServerMatching)
        {
            if (!HasXagmanServerMatchingMeetConfig()
                || !TryValidateXagmanServerMatchingTravelPlan(Array.Empty<XagmanTonyCharacterEntry>(), out _))
                error = "configure valid Server Matching meet worlds first";
            else if (peers.SelectMany(peer => peer.TradeCapacityForecast!.SelectedOwnerKeys)
                .Any(key => string.IsNullOrWhiteSpace(GetXagmanServerMeetWorld(GetXagmanDataCenterOfChar(key)))))
                error = "a selected FO server has no configured meetup world";
        }
        else if (peers.SelectMany(peer => peer.TradeCapacityForecast!.SelectedOwnerKeys)
            .Any(key => !TryValidateXagmanMeetTravel(key, GetXagmanFixedMeetWorld(), out _)))
            error = "selected FO owners cannot all travel to the fixed meetup world; use Server Matching or adjust the roster";
        if (error.Length > 0)
            return false;

        foreach (var peer in peers)
        {
            var peerUntil = peer.LastSeenUtc.AddSeconds(XagmanTradeCapacityPeerFreshSeconds);
            var forecastUntil = peer.TradeCapacityForecast!.GeneratedAtUtc.AddSeconds(XagmanTradeCapacityForecastFreshSeconds);
            if (peerUntil < validUntil) validUntil = peerUntil;
            if (forecastUntil < validUntil) validUntil = forecastUntil;
        }
        demands = view.Groups.Select(group => new XagmanTonyAutoDemand
        {
            GroupKey = group.GroupKey,
            OwnerCount = group.SelectedOwnerCount,
            Items = group.Items.Select(item => new XagmanTonyAutoDemandItem
            {
                ItemId = item.ItemId, IsHq = item.IsHq, ItemName = item.ItemName,
                StackSize = item.StackSize, IncomingToTonyQuantity = item.IncomingToTonyQuantity,
                NeededFromTonyQuantity = item.NeededFromTonyQuantity,
            }).ToList(),
        }).ToList();
        return true;
    }

    private void UpdateXagmanTonyAutoSelection(DateTime now, IReadOnlyList<XagmanPeerPresence> ownerPeers)
    {
        // Recheck immediately before an update can apply; the UI/status poll is throttled.
        xagmanTonyAutoNextSetupCheckUtc = DateTime.MinValue;
        CheckXagmanTonyAutoSelectionState(now);
        if (!xagmanTonyAutoPending && !xagmanTonyAutoTracking)
            return;
        if (!TryGetXagmanTonyAutoDemands(now, ownerPeers, out var demands, out var error, out var validUntil))
        {
            StopXagmanTonyAutoSelection($"Auto: {error}. Existing selection kept.");
            return;
        }
        var definitions = demands.SelectMany(demand => demand.Items)
            .GroupBy(item => new XagmanForecastItemKey(item.ItemId, item.IsHq))
            .Select(group => group.First())
            .Select(item => new XagmanForecastItemDefinition
            {
                ItemId = item.ItemId, IsHq = item.IsHq, ItemName = item.ItemName, StackSize = item.StackSize,
            }).ToList();
        var cfg = plugin.Configuration;
        var roster = plugin.Configuration.XagmanTonyCharacters;
        var snapshots = BuildXagmanForecastInventorySnapshots(
            roster.Select(entry => entry.CharacterNameWorld)
                .Where(key => !string.IsNullOrWhiteSpace(key) && IsAutoRetainerCharacterAllowed(key, cfg.XagmanHonorArExclusions))
                .ToList(), definitions);
        if (xagmanTradeCapacityQueryDeferred)
        {
            xagmanTonyAutoValidUntilUtc = DateTime.MinValue;
            return;
        }
        if (definitions.Any(definition => !IsXagmanGilItem(definition.ItemId)
            && xagmanTradeCapacitySearchCache.TryGetValue(
                (string.IsNullOrWhiteSpace(definition.ItemName) ? definition.ItemId.ToString() : definition.ItemName).Trim(),
                out var cached) && !cached.Succeeded))
        {
            StopXagmanTonyAutoSelection("Auto: Tony item inventory could not be read. Existing selection kept; refresh inventory and try again.");
            return;
        }
        var candidates = new List<XagmanTonyAutoCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = 0;
        var belowGilMinimum = 0;
        for (var index = 0; index < roster.Count; index++)
        {
            var key = roster[index].CharacterNameWorld;
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)
                || !IsAutoRetainerCharacterAllowed(key, cfg.XagmanHonorArExclusions)) continue;
            var region = NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(key));
            if (region == "Unknown" || (!xagmanTradeCapacityView!.ServerMatching
                && !TryValidateXagmanMeetTravel(key, GetXagmanFixedMeetWorld(), out _))) continue;
            if (!snapshots.TryGetValue(key, out var snapshot) || !snapshot.IsKnown)
            {
                unknown++;
                continue;
            }
            if (snapshot.Gil < 5000)
            {
                belowGilMinimum++;
                continue;
            }
            var candidate = new XagmanTonyAutoCandidate
            {
                Index = index, Key = key, Region = region,
                FreeSlots = snapshot.FreeSlots, Gil = snapshot.Gil,
            };
            foreach (var definition in definitions)
            {
                candidate.Stock[definition.Key] = snapshot.GetQuantity(definition.Key);
                candidate.Headroom[definition.Key] = snapshot.GetPartialStackHeadroom(definition.Key, definition.StackSize);
            }
            candidates.Add(candidate);
        }
        // Ignore timestamps/revisions: only changed requirements or candidate inventory invalidate a plan.
        var inputKey = JsonSerializer.Serialize(new
        {
            Demands = demands,
            Candidates = candidates.Select(candidate => new
            {
                candidate.Index, candidate.Key, candidate.Region, candidate.FreeSlots, candidate.Gil,
                Stock = candidate.Stock.OrderBy(pair => pair.Key.ItemId).ThenBy(pair => pair.Key.IsHq).ToArray(),
                Headroom = candidate.Headroom.OrderBy(pair => pair.Key.ItemId).ThenBy(pair => pair.Key.IsHq).ToArray(),
            }).ToArray(),
            Projection = xagmanTradeCapacityView!.ShowCollectionFirstProjection,
            Unknown = unknown, BelowGilMinimum = belowGilMinimum,
        });
        if (xagmanTonyAutoTracking)
        {
            if (!inputKey.Equals(xagmanTonyAutoInputKey, StringComparison.Ordinal))
                StopXagmanTonyAutoSelection("Auto: FO requirements or Tony inventories changed. Run Auto again to update the selection.");
            else
                xagmanTonyAutoValidUntilUtc = validUntil;
            return;
        }
        var result = PlanXagmanTonyAutoSelection(demands, candidates,
            xagmanTradeCapacityView.ShowCollectionFirstProjection, GetXagmanTonyGilMinimum(), XagmanTonySellGilLimit);
        xagmanTonySelectedIndices.Clear();
        xagmanTonySelectedIndices.UnionWith(result.SelectedIndices);
        InvalidateXagmanTradeCapacityForecast();
        xagmanTonyAutoPending = false;
        xagmanTonyAutoTracking = true;
        xagmanTonyAutoSetupKey = GetXagmanTonyAutoSetupKey();
        xagmanTonyAutoInputKey = inputKey;
        xagmanTonyAutoValidUntilUtc = validUntil;
        xagmanTonyAutoStatusKind = result.Success ? 1 : 2;
        var counts = string.Join(", ", result.SelectedByGroup.Select(pair => $"{pair.Key}: {pair.Value}"));
        xagmanTonyAutoStatus = result.Success
            ? $"Auto: all required Tonys selected ({counts}). Collection and supply forecasts covered."
            : $"Auto: more eligible Tonys or stock needed ({counts}). {string.Join(" ", result.Shortfalls)}";
        if (!result.Success && (unknown > 0 || belowGilMinimum > 0))
            xagmanTonyAutoStatus += $" Excluded: {unknown} with unknown inventory, {belowGilMinimum} below 5,000 gil.";
    }
}
