using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Services;

internal static class XagmanEtaEstimator
{
    internal const double DefaultSecondsPerCharacter = 120;

    // The representative DC is an ETA-only region key. It must never select a travel world.
    internal static string GetRegionalRouteDataCenter(string dataCenter)
    {
        var region = WorldData.GetRegionOfDataCenter(dataCenter);
        return region != null && WorldData.DataCenterOrder.TryGetValue(region, out var dataCenters)
            ? dataCenters.FirstOrDefault() ?? string.Empty : string.Empty;
    }

    internal static bool IsUsable(XagmanEtaReport? report, DateTime now, XagmanRunPhase phase, string coordinator)
        => report is { Revision: 1, Complete: true, Routes: not null }
            && report.Phase == phase && report.CoordinatorInstanceId == coordinator
            && Math.Abs((now - report.CapturedUtc).TotalSeconds) <= 10
            && double.IsFinite(report.RemainingSeconds) && report.RemainingSeconds >= 0 && report.RemainingSeconds <= 1009152000
            && double.IsFinite(report.AverageSecondsPerCharacter) && report.AverageSecondsPerCharacter > 0
            && report.AverageSecondsPerCharacter <= 86400
            && report.SampleCount >= 0 && report.SampleCount <= 100000 && report.Routes.Count <= 32
            && report.Routes.All(route => route != null && !string.IsNullOrWhiteSpace(route.DataCenter)
                && WorldData.GetSweepOrdinal(route.DataCenter) < 900
                && route.RemainingCharacters >= 0 && route.RemainingCharacters <= 100000
                && double.IsFinite(route.RemainingSeconds) && route.RemainingSeconds >= 0
                && route.RemainingSeconds <= 31536000)
            && report.Routes.Select(route => route.DataCenter).Distinct(StringComparer.OrdinalIgnoreCase).Count() == report.Routes.Count;

    internal static XagmanEtaReport Aggregate(IReadOnlyList<XagmanEtaReport> owners, bool complete,
        DateTime now, string coordinator, XagmanRunPhase phase, string activeDataCenter)
    {
        var samples = owners.Sum(owner => (double)owner.SampleCount);
        return new XagmanEtaReport
        {
            CapturedUtc = now, CoordinatorInstanceId = coordinator, Phase = phase,
            Complete = complete && owners.Count > 0,
            ActiveDataCenter = activeDataCenter,
            UsesDefaultRate = owners.Any(owner => owner.UsesDefaultRate),
            HasUnfinishedWork = owners.Any(owner => owner.HasUnfinishedWork),
            SampleCount = (int)Math.Min(int.MaxValue, samples),
            AverageSecondsPerCharacter = samples > 0
                ? owners.Sum(owner => owner.AverageSecondsPerCharacter * owner.SampleCount) / samples
                : DefaultSecondsPerCharacter,
            // Accounts process concurrently: Tony's requested overall estimate is the longest account.
            RemainingSeconds = owners.Count == 0 ? 0 : owners.Max(owner => owner.RemainingSeconds),
            // Combine each account's home DCs before taking the regional maximum across
            // concurrent accounts. Tony's active DC remains the actual meetup destination.
            Routes = owners.SelectMany(owner => owner.Routes
                .GroupBy(route => string.IsNullOrWhiteSpace(activeDataCenter)
                    ? route.DataCenter : GetRegionalRouteDataCenter(route.DataCenter), StringComparer.OrdinalIgnoreCase)
                .Select(group => new XagmanEtaRoute
                {
                    DataCenter = group.Key,
                    RemainingCharacters = (int)Math.Min(int.MaxValue, group.Sum(route => (long)route.RemainingCharacters)),
                    RemainingSeconds = group.Sum(route => route.RemainingSeconds),
                }))
                .GroupBy(route => route.DataCenter, StringComparer.OrdinalIgnoreCase)
                .Select(group => new XagmanEtaRoute
                {
                    DataCenter = group.Key,
                    RemainingCharacters = (int)Math.Min(int.MaxValue, group.Sum(route => (long)route.RemainingCharacters)),
                    RemainingSeconds = group.Max(route => route.RemainingSeconds),
                }).OrderBy(route => WorldData.GetSweepOrdinal(route.DataCenter)).ToList(),
        };
    }

    internal static bool TryEstimateWait(XagmanEtaReport report, string destination, out int characters, out double seconds)
    {
        characters = 0;
        seconds = 0;
        var current = WorldData.GetSweepOrdinal(report.ActiveDataCenter) / 100;
        var target = WorldData.GetSweepOrdinal(destination) / 100;
        // A return to an already-passed route requires a new supply sweep; its order is not yet known.
        if (!report.Complete || current >= WorldData.RegionOrder.Length
            || target >= WorldData.RegionOrder.Length || target <= current) return false;
        foreach (var route in report.Routes)
        {
            var ordinal = WorldData.GetSweepOrdinal(route.DataCenter) / 100;
            if (ordinal < current || ordinal >= target) continue;
            characters = (int)Math.Min(int.MaxValue, (long)characters + route.RemainingCharacters);
            // Regions are serial; accounts within each region run concurrently.
            seconds += route.RemainingSeconds;
        }
        return true;
    }
}
