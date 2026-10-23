using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private XagmanEtaReport? xagmanPublishedEta;

    private XagmanEtaReport? BuildXagmanEtaReport()
    {
        if (!xagmanRunning || xagmanStatus == XagmanStatus.Error || plugin.Configuration.XagmanOutsideNetworkHelper) return null;
        var phase = IsXagmanCollectionFirstRunActive() ? xagmanRunPhase : XagmanRunPhase.Legacy;
        var now = DateTime.UtcNow;
        if (xagmanActiveRole == XagmanRole.Tony)
        {
            var peers = plugin.XagmanPeers.Peers.Where(peer => peer.XagmanEnabled
                && peer.Role == XagmanRole.FranchiseOwner && peer.TotalCharacters > 0
                && IsXagmanPeerInCurrentRunPhase(peer) && IsXagmanPeerInSupplyCoordinatorScope(peer)
                && (!IsXagmanCollectionFirstRunActive() || xagmanExpectedFranchiseOwnerInstanceIds.Contains(peer.InstanceId)))
                .GroupBy(peer => peer.InstanceId).Select(group => group.OrderByDescending(peer => peer.LastSeenUtc).First()).ToList();
            var expected = new HashSet<string>(xagmanSupplyOwnerCohort);
            if (IsXagmanCollectionFirstRunActive()) expected.UnionWith(xagmanExpectedFranchiseOwnerInstanceIds);
            expected.RemoveWhere(IsXagmanOwnerClientCompleted);
            var complete = expected.All(id => peers.Any(peer => peer.InstanceId == id));
            var reports = new List<XagmanEtaReport>();
            foreach (var peer in peers)
            {
                if (!IsXagmanPeerFresh(peer) || !XagmanEtaEstimator.IsUsable(peer.Eta, now, phase, plugin.InstanceId))
                { complete = false; continue; }
                reports.Add(peer.Eta!);
            }
            return XagmanEtaEstimator.Aggregate(reports, complete, now, plugin.InstanceId, phase,
                xagmanServerMatchingActive ? xagmanSweepDataCenter : string.Empty);
        }

        var plan = xagmanOwnerRunPlan.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var keys = new HashSet<string>(plan, StringComparer.OrdinalIgnoreCase);
        var samples = xagmanCharDurationSeconds.Where(pair => keys.Contains(pair.Key)
            && !plugin.TaskRunner.FailedCharacters.Contains(pair.Key) && !xagmanSkippedCharacters.Contains(pair.Key)
            && double.IsFinite(pair.Value) && pair.Value > 0 && pair.Value <= 86400).Select(pair => pair.Value).ToList();
        var average = samples.Count > 0 ? samples.Average() : XagmanEtaEstimator.DefaultSecondsPerCharacter;
        var remaining = plan.Where(key => !plugin.TaskRunner.FailedCharacters.Contains(key)
            && !xagmanCollectionFirstFailedCharacters.Contains(key) && !xagmanSkippedCharacters.Contains(key)
            && !xagmanOwnerCompletedKeys.Contains(key)
            && (!IsXagmanCollectionFirstCollectionPhase() || !xagmanCharDurationSeconds.ContainsKey(key))).ToList();
        var routes = remaining.GroupBy(GetXagmanDataCenterOfChar, StringComparer.OrdinalIgnoreCase)
            .Select(group => new XagmanEtaRoute
            {
                DataCenter = group.Key, RemainingCharacters = group.Count(), RemainingSeconds = group.Count() * average,
            }).ToList();
        return new XagmanEtaReport
        {
            CapturedUtc = now, CoordinatorInstanceId = GetXagmanOwnerSupplyCoordinatorInstance(), Phase = phase,
            Complete = (plan.Count > 0 || xagmanPhaseComplete)
                && routes.All(route => WorldData.GetSweepOrdinal(route.DataCenter) < 900),
            UsesDefaultRate = samples.Count == 0, HasUnfinishedWork = xagmanPartialOwners.Count > 0,
            AverageSecondsPerCharacter = average, SampleCount = samples.Count,
            RemainingSeconds = routes.Sum(route => route.RemainingSeconds), Routes = routes,
        };
    }

    private global::XASlave.Localization.UiMessage GetXagmanTonyEtaLabel()
    {
        var report = xagmanPublishedEta;
        var phase = IsXagmanCollectionFirstRunActive() ? xagmanRunPhase : XagmanRunPhase.Legacy;
        if (!XagmanEtaEstimator.IsUsable(report, DateTime.UtcNow, phase, plugin.InstanceId))
            return global::XASlave.Localization.UiMessage.Key("S3B91622B73DD4F4B");
        var qualifier = (report!.UsesDefaultRate ? global::XASlave.Localization.UiMessage.Key("SEA0BE3C1D016B160") : global::XASlave.Localization.UiMessage.Raw(string.Empty));
        if (report.HasUnfinishedWork) qualifier = global::XASlave.Localization.UiMessage.Concat(qualifier, global::XASlave.Localization.UiMessage.Key("S8953925348017BE6"));
        var scope = (IsXagmanCollectionFirstRunActive() ? global::XASlave.Localization.UiMessage.Format("S948934D2F002791A", GetXagmanPhaseMessage(phase)) : global::XASlave.Localization.UiMessage.Raw(string.Empty));
        return global::XASlave.Localization.UiMessage.Format("S17CBD879D3214D1D", scope, FormatXagmanDurationMessage(report.RemainingSeconds), qualifier);
    }

    private bool TryGetXagmanOwnerRouteWait(out global::XASlave.Localization.UiMessage state, out global::XASlave.Localization.UiMessage estimate)
    {
        state = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        estimate = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        if (!xagmanRunning || plugin.Configuration.XagmanOutsideNetworkHelper
            || xagmanActiveRole != XagmanRole.FranchiseOwner || xagmanStatus != XagmanStatus.Paused) return false;
        var tony = GetXagmanSupplyTonyPeer();
        // The supply frontier can be filtered to Tony's current region. Keep the owner's
        // explicit next route first so an EU-only owner waiting on NA still gets a wait ETA.
        var destination = !string.IsNullOrWhiteSpace(xagmanOwnerSweepPendingDataCenter)
            ? xagmanOwnerSweepPendingDataCenter
            : IsXagmanSupplyCycleActive()
                ? GetXagmanSupplyPendingDataCenters().FirstOrDefault() ?? string.Empty
                : GetXagmanActiveTradeCoordinationDataCenter();
        if (tony == null || !IsXagmanPeerFresh(tony) || tony.Status == XagmanStatus.Error || !tony.ServerMatchingEnabled
            || string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(tony.ServerMatchingActiveDataCenter)
            || destination.Equals(tony.ServerMatchingActiveDataCenter, StringComparison.OrdinalIgnoreCase)) return false;
        var differentRegion = !string.Equals(WorldData.GetRegionOfDataCenter(destination),
            WorldData.GetRegionOfDataCenter(tony.ServerMatchingActiveDataCenter), StringComparison.OrdinalIgnoreCase);
        state = (differentRegion ? global::XASlave.Localization.UiMessage.Key("S049CBB216199D4F1") : global::XASlave.Localization.UiMessage.Key("S4B027EC29B7FFA92"));
        if (WorldData.GetSweepOrdinal(destination) < WorldData.GetSweepOrdinal(tony.ServerMatchingActiveDataCenter))
            state = (differentRegion ? global::XASlave.Localization.UiMessage.Key("SB0F0E82AA549083C") : global::XASlave.Localization.UiMessage.Key("SC3D9D0E4FBEF0D7D"));
        var phase = IsXagmanCollectionFirstRunActive() ? xagmanRunPhase : XagmanRunPhase.Legacy;
        if (!XagmanEtaEstimator.IsUsable(tony.Eta, DateTime.UtcNow, phase, tony.InstanceId))
        { estimate = global::XASlave.Localization.UiMessage.Key("SD7D120621FB5D60C"); return true; }
        if (!XagmanEtaEstimator.TryEstimateWait(tony.Eta!, destination, out var characters, out var seconds))
        { estimate = global::XASlave.Localization.UiMessage.Key("S57C09BEDE615CD7F"); return true; }
        var rate = (tony.Eta!.UsesDefaultRate ? global::XASlave.Localization.UiMessage.Key("S4E40351AE461A631") : global::XASlave.Localization.UiMessage.Format("S77F0705495B5EC16", FormatXagmanDurationMessage(tony.Eta.AverageSecondsPerCharacter)));
        estimate = global::XASlave.Localization.UiMessage.Format("SDB9AB18C4167FFD5", characters, FormatXagmanDurationMessage(seconds), rate);
        if (IsXagmanCollectionFirstRunActive()) estimate = global::XASlave.Localization.UiMessage.Format("S31DBC1ABDB977186", GetXagmanPhaseMessage(phase), estimate);
        if (tony.Eta.HasUnfinishedWork) estimate = global::XASlave.Localization.UiMessage.Concat(estimate, global::XASlave.Localization.UiMessage.Key("S0E20B8F4457A8CDA"));
        return true;
    }
}
