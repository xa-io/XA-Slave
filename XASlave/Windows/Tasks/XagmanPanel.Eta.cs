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

    private string GetXagmanTonyEtaLabel()
    {
        var report = xagmanPublishedEta;
        var phase = IsXagmanCollectionFirstRunActive() ? xagmanRunPhase : XagmanRunPhase.Legacy;
        if (!XagmanEtaEstimator.IsUsable(report, DateTime.UtcNow, phase, plugin.InstanceId))
            return "ETA: waiting for fresh timing reports from all owner clients";
        var qualifier = report!.UsesDefaultRate ? "; includes assumed 2m/char" : string.Empty;
        if (report.HasUnfinishedWork) qualifier += "; unfinished work may require more passes";
        var scope = IsXagmanCollectionFirstRunActive() ? $"{phase} phase " : string.Empty;
        return $"{scope}ETA ~{FormatXagmanDuration(report.RemainingSeconds)} (longest owner account{qualifier})";
    }

    private bool TryGetXagmanOwnerRouteWait(out string state, out string estimate)
    {
        state = string.Empty;
        estimate = string.Empty;
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
        state = differentRegion ? "Waiting for Tony to finish previous region..." : "Waiting for Tony to finish previous world...";
        if (WorldData.GetSweepOrdinal(destination) < WorldData.GetSweepOrdinal(tony.ServerMatchingActiveDataCenter))
            state = differentRegion ? "Waiting for Tony to return to this region..." : "Waiting for Tony to return to this world...";
        var phase = IsXagmanCollectionFirstRunActive() ? xagmanRunPhase : XagmanRunPhase.Legacy;
        if (!XagmanEtaEstimator.IsUsable(tony.Eta, DateTime.UtcNow, phase, tony.InstanceId))
        { estimate = "Wait ETA: waiting for fresh timing reports from all owner clients"; return true; }
        if (!XagmanEtaEstimator.TryEstimateWait(tony.Eta!, destination, out var characters, out var seconds))
        { estimate = "Wait ETA: unavailable until Tony schedules the next sweep"; return true; }
        var rate = tony.Eta!.UsesDefaultRate ? "includes assumed 2m/char" : $"avg {FormatXagmanDuration(tony.Eta.AverageSecondsPerCharacter)}/char";
        estimate = $"{characters} characters ahead; wait ~{FormatXagmanDuration(seconds)} ({rate}; parallel accounts)";
        if (IsXagmanCollectionFirstRunActive()) estimate = $"{phase} phase: {estimate}";
        if (tony.Eta.HasUnfinishedWork) estimate += "; additional supply passes may extend this";
        return true;
    }
}
