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
    private global::XASlave.Localization.UiMessage? xagmanTonyAutoStatusUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
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

    private void StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage messagePresentation)
    {
        var message = messagePresentation.Original;
        xagmanTonyAutoPending = false;
        xagmanTonyAutoTracking = false;
        xagmanTonyAutoStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanTonyAutoStatusUiMessage, messagePresentation);
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
            StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("S7886807C2BF23CA0"));
        }
        else if (xagmanTonyAutoPending && (now - xagmanTonyAutoRequestedUtc).TotalSeconds > 60)
        {
            StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("S30FC4E21C3E61419"));
        }
    }

    private void DrawXagmanTonyAutoSelection()
    {
        CheckXagmanTonyAutoSelectionState(DateTime.UtcNow);
        ImGui.SameLine();
        using (ImRaii.Disabled(xagmanTonyAutoPending || !CanSelectXagmanTonyAutomatically()))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0286249762F7C943") + "##xagmanTonyAuto")))
            {
                xagmanTonyAutoSetupKey = GetXagmanTonyAutoSetupKey();
                xagmanTonyAutoRequestedUtc = DateTime.UtcNow;
                xagmanTonyAutoNextSetupCheckUtc = DateTime.MinValue;
                xagmanTonyAutoPending = true;
                xagmanTonyAutoTracking = false;
                xagmanTonyAutoStatusKind = 0;
                xagmanTonyAutoStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanTonyAutoStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S0638E2DCE2943E9B"));
                InvalidateXagmanTradeCapacityForecast();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SBA73242EC0DE1E5F"));
        if (string.IsNullOrWhiteSpace(xagmanTonyAutoStatus))
            return;
        var current = !xagmanTonyAutoTracking || DateTime.UtcNow <= xagmanTonyAutoValidUntilUtc;
        var kind = current ? xagmanTonyAutoStatusKind : 0;
        var color = kind == 1 ? new Vector4(0.4f, 0.9f, 0.4f, 1f)
            : kind == 2 ? new Vector4(1f, 0.4f, 0.4f, 1f) : new Vector4(1f, 0.8f, 0.3f, 1f);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(current ? global::XASlave.Localization.UiMessage.Resolve(ref xagmanTonyAutoStatusUiMessage, xagmanTonyAutoStatus) : global::XASlave.Localization.UiLocale.Get("SCA0BB67DBEB6C574"));
    }

    private bool TryGetXagmanTonyAutoDemands(DateTime now, IReadOnlyList<XagmanPeerPresence> peers,
        out List<XagmanTonyAutoDemand> demands, out global::XASlave.Localization.UiMessage error, out DateTime validUntil)
    {
        demands = new List<XagmanTonyAutoDemand>();
        error = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        validUntil = now.AddSeconds(XagmanTradeCapacityPeerFreshSeconds);
        var view = xagmanTradeCapacityView;
        if (view == null || peers.Count == 0)
            error = global::XASlave.Localization.UiMessage.Key("S5BCAD16F9A7FB292");
        else if (view.MissingForecastPeerCount > 0 || view.StaleForecastPeerCount > 0
            || view.StalePresencePeerCount > 0 || peers.Any(peer => !IsXagmanTradeCapacityPeerFresh(peer, now)
                || peer.TradeCapacityForecast == null || !IsXagmanTradeCapacityForecastFresh(peer.TradeCapacityForecast, now)))
            error = global::XASlave.Localization.UiMessage.Key("S3C1FF753C349B735");
        else if (view.TruncatedForecastPeerCount > 0 || view.DuplicateOwnerKeys.Count > 0)
            error = global::XASlave.Localization.UiMessage.Key("S278CF55801360C38");
        else if (peers.Any(peer => peer.TradeCapacityForecast is { } forecast
            && (!forecast.SchedulingPoliciesComplete || forecast.UnknownOwnerCount != 0
                || forecast.KnownOwnerCount != forecast.SelectedOwnerCount
                || forecast.SelectedOwnerKeys == null || forecast.Items == null
                || forecast.SelectedOwnerKeys.Count != forecast.SelectedOwnerCount
                || forecast.SelectedOwnerKeys.Any(string.IsNullOrWhiteSpace)
                || forecast.Items.Any(row => row.ItemId == 0 || row.UnknownOwnerCount != 0
                    || row.IncomingToTonyQuantity < 0 || row.NeededFromTonyQuantity < 0
                    || !IsXagmanForecastItemTradable(row.ItemId)))))
            error = global::XASlave.Localization.UiMessage.Key("S8D80B63A447054E0");
        else if (view.Groups.Any(group => group.AllAvailableRequestCount > 0))
            error = global::XASlave.Localization.UiMessage.Key("SDA4554708B3FE530");
        else if (view.SelectedOwnerCount == 0)
            error = global::XASlave.Localization.UiMessage.Key("SAD35C020E58B29A7");
        else if (string.IsNullOrWhiteSpace(plugin.Configuration.XagmanTargetAetheryte))
            error = global::XASlave.Localization.UiMessage.Key("SBDD4D6693223B872");
        else if (peers.SelectMany(peer => peer.TradeCapacityForecast!.SelectedOwnerKeys)
            .Any(key => NormalizeXagmanForecastRegion(GetXagmanRegionOfChar(key)) == "Unknown")
            || (view.ServerMatching && view.Groups.Any(group => group.GroupKey == "Unknown")))
            error = global::XASlave.Localization.UiMessage.Key("S52AC2BBD580E4612");

        if (error.Original.Length > 0)
            return false;
        // Match runtime routing. Fixed-world collection may pool only characters that can visit it.
        if (view!.ServerMatching)
        {
            if (!HasXagmanServerMatchingMeetConfig()
                || !TryValidateXagmanServerMatchingTravelPlan(Array.Empty<XagmanTonyCharacterEntry>(), out _))
                error = global::XASlave.Localization.UiMessage.Key("S6026C2EB973E2621");
            else if (peers.SelectMany(peer => peer.TradeCapacityForecast!.SelectedOwnerKeys)
                .Any(key => string.IsNullOrWhiteSpace(GetXagmanServerMeetWorld(GetXagmanDataCenterOfChar(key)))))
                error = global::XASlave.Localization.UiMessage.Key("S2753157DEEBE35AB");
        }
        else if (peers.SelectMany(peer => peer.TradeCapacityForecast!.SelectedOwnerKeys)
            .Any(key => !TryValidateXagmanMeetTravel(key, GetXagmanFixedMeetWorld(), out _)))
            error = global::XASlave.Localization.UiMessage.Key("S3277779A32574007");
        if (error.Original.Length > 0)
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
            StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Format("S4E9660AA7A1043E2", error));
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
            StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("S44D64F34BC3AC56B"));
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
                StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("S45908F9F3E7479A2"));
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
        xagmanTonyAutoStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanTonyAutoStatusUiMessage, (result.Success ? global::XASlave.Localization.UiMessage.Format("S6727CAF8FB8CD52B", counts) : global::XASlave.Localization.UiMessage.Format("S1846ECDCAF31277B", counts, global::XASlave.Localization.UiMessage.Join(" ", result.Shortfalls))));
        if (!result.Success && (unknown > 0 || belowGilMinimum > 0))
            xagmanTonyAutoStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanTonyAutoStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref xagmanTonyAutoStatusUiMessage, xagmanTonyAutoStatus), global::XASlave.Localization.UiMessage.Format("S1C705FDFA1638FC2", unknown, belowGilMinimum)));
    }
}
