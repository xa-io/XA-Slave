using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Windows;

// Xagman "Server Matching" support.
//
// Terminology (user vocabulary -> code):
//   Region = NA / EU / JP / OCE            (WorldData.Region)
//   Server = data center: Aether, Crystal, Dynamis, Primal, ...  (WorldData.DataCenter)
//   World  = Siren, Adamantoise, ...        (WorldData.Name)
//
// In Server Matching mode the Tony picks one meet World per Server and one shared meet location,
// then sweeps Server-by-Server within a Region (Aether -> Crystal -> Dynamis -> Primal), region by
// region (NA -> EU -> JP -> OCE). Franchise Owners only ever World-travel inside their own Server to
// the Tony's selected meet World for that Server; they never DC-travel. Owners whose Server has not
// been reached yet wait; owners whose Server is passed without service are skipped (purple).
public partial class SlaveWindow
{
    // How long the current server must look drained (no queue, no pending owners) before the Tony
    // advances to the next server. Gives gated owners time to relog/travel after the Tony arrives.
    private const double XagmanSweepServerSettleSeconds = 6.0;

    // How long to keep idling in discovery before giving up when owners report only regions that
    // have no selected Tony (so the run does not hang forever waiting for owners it cannot serve).
    private const double XagmanSweepDiscoveryGiveUpSeconds = 25.0;

    // -- Run state (Tony sweep) --
    private bool xagmanServerMatchingActive;
    // While true, the Tony is idling in discovery: it waits to see which servers Franchise Owners are
    // on (from peer presence) before committing to a region/server, instead of blindly visiting
    // configured/ordered servers that may have no owners.
    private bool xagmanSweepAwaitingStart;
    private DateTime xagmanSweepDiscoveryStartedUtc = DateTime.MinValue;
    private string xagmanSweepRegion = string.Empty;
    private string xagmanSweepDataCenter = string.Empty;
    private DateTime xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
    private DateTime xagmanInvalidPendingServerPeerSinceUtc = DateTime.MinValue;
    private bool xagmanMeetRouteSnapshotPinned;
    private string xagmanFixedMeetWorldSnapshot = string.Empty;
    private string xagmanMeetAetheryteSnapshot = string.Empty;
    private readonly Dictionary<string, string> xagmanServerMeetWorldsSnapshot =
        new(StringComparer.OrdinalIgnoreCase);

    // -- Run state (Franchise Owner) --
    private string xagmanOwnerSweepPendingDataCenter = string.Empty;
    private readonly HashSet<string> xagmanSkippedCharacters = new(StringComparer.OrdinalIgnoreCase);

    // -- Post-completion snapshot (keep red/purple order lists visible after the run ends) --
    private bool xagmanHasLastRunSnapshot;
    private XagmanRole xagmanLastRunRole = XagmanRole.Tony;
    private IReadOnlyList<string> xagmanLastRunOwnerPlan = Array.Empty<string>();
    private IReadOnlyList<string> xagmanLastRunTonyPlan = Array.Empty<string>();
    private int xagmanLastRunOwnerCompleted;
    private int xagmanLastRunTonyCompleted;
    private readonly HashSet<string> xagmanLastRunFailedCharacters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanLastRunSkippedCharacters = new(StringComparer.OrdinalIgnoreCase);

    // Per-character active processing time: started at the /ays relog login attempt (which happens
    // AFTER the wait-for-server gate, so the idle "waiting for Tony to reach my server" time is not
    // counted) and ended when the character finishes (completed/failed). This times only the time we
    // are actually able to process a character; the ETA uses this average x remaining.
    private readonly Dictionary<string, DateTime> xagmanCharStartUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> xagmanCharDurationSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> xagmanLastRunCharDurations = new(StringComparer.OrdinalIgnoreCase);

    private enum XagmanOwnerServerGate
    {
        Proceed,
        Wait,
        Skip,
        RouteError,
    }

    private enum XagmanSweepStep
    {
        NotServerMatching,
        WaitingForOwners,
        Settling,
        Advanced,
        Finished,
        Blocked,
        Error,
    }

    private enum XagmanSweepCommitResult
    {
        NoCandidate,
        Committed,
        Error,
    }

    // Franchise Owner characters this client has actually completed (traded), used to tell apart
    // completed (green) from skipped (purple) when finalizing a server-matching run.
    private readonly HashSet<string> xagmanOwnerCompletedKeys = new(StringComparer.OrdinalIgnoreCase);

    // ---- Configuration helpers ----

    private bool HasXagmanServerMatchingMeetConfig()
    {
        var cfg = plugin.Configuration;
        if (xagmanMeetRouteSnapshotPinned)
        {
            return !string.IsNullOrWhiteSpace(xagmanMeetAetheryteSnapshot)
                && xagmanServerMeetWorldsSnapshot.Values.Any(world => !string.IsNullOrWhiteSpace(world));
        }

        return cfg.XagmanServerMatchingEnabled
            && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte)
            && cfg.XagmanServerMeetWorlds != null
            && cfg.XagmanServerMeetWorlds.Values.Any(world => !string.IsNullOrWhiteSpace(world));
    }

    private void PinXagmanMeetRouteSnapshot(bool serverMatching)
    {
        xagmanFixedMeetWorldSnapshot = serverMatching
            ? string.Empty
            : (plugin.Configuration.XagmanTargetWorld ?? string.Empty).Trim();
        xagmanMeetAetheryteSnapshot =
            (plugin.Configuration.XagmanTargetAetheryte ?? string.Empty).Trim();
        xagmanServerMeetWorldsSnapshot.Clear();
        if (!serverMatching)
        {
            xagmanMeetRouteSnapshotPinned = true;
            return;
        }

        var configuredMeetWorlds = plugin.Configuration.XagmanServerMeetWorlds;
        foreach (var configured in configuredMeetWorlds)
            xagmanServerMeetWorldsSnapshot[configured.Key] = (configured.Value ?? string.Empty).Trim();
        xagmanMeetRouteSnapshotPinned = true;
    }

    private void ResetXagmanMeetRouteSnapshot()
    {
        xagmanMeetRouteSnapshotPinned = false;
        xagmanFixedMeetWorldSnapshot = string.Empty;
        xagmanMeetAetheryteSnapshot = string.Empty;
        xagmanServerMeetWorldsSnapshot.Clear();
    }

    private string GetXagmanFixedMeetWorld()
        => xagmanMeetRouteSnapshotPinned
            ? xagmanFixedMeetWorldSnapshot
            : (plugin.Configuration.XagmanTargetWorld ?? string.Empty);

    private string GetXagmanServerMeetWorld(string dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
            return string.Empty;
        var map = xagmanMeetRouteSnapshotPinned
            ? xagmanServerMeetWorldsSnapshot
            : plugin.Configuration.XagmanServerMeetWorlds;
        return map != null && map.TryGetValue(dataCenter, out var world)
            ? (world ?? string.Empty)
            : string.Empty;
    }

    private string GetXagmanSharedMeetLocation()
        => xagmanMeetRouteSnapshotPinned
            ? xagmanMeetAetheryteSnapshot
            : (plugin.Configuration.XagmanTargetAetheryte ?? string.Empty);

    // Servers (data centers) that have a selected meet world, in Region -> Server sweep order.
    private List<string> GetXagmanConfiguredSweepServers()
    {
        var result = new List<string>();
        foreach (var (_, dc) in WorldData.EnumerateDataCentersInSweepOrder())
        {
            if (!string.IsNullOrWhiteSpace(GetXagmanServerMeetWorld(dc)))
                result.Add(dc);
        }

        return result;
    }

    private List<string> GetXagmanConfiguredSweepServersForRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return new List<string>();
        return GetXagmanConfiguredSweepServers()
            .Where(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), region, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string GetXagmanDataCenterOfChar(string characterNameWorld)
        => WorldData.GetDataCenterOfWorld(GetWorldFromKey(characterNameWorld)) ?? string.Empty;

    private string GetXagmanRegionOfChar(string characterNameWorld)
    {
        var dc = GetXagmanDataCenterOfChar(characterNameWorld);
        return string.IsNullOrWhiteSpace(dc) ? string.Empty : (WorldData.GetRegionOfDataCenter(dc) ?? string.Empty);
    }

    private bool TryValidateXagmanServerMatchingTravelPlan(
        IReadOnlyList<XagmanTonyCharacterEntry> selectedTonys,
        out string reason)
    {
        foreach (var tony in selectedTonys)
        {
            var homeWorld = GetWorldFromKey(tony.CharacterNameWorld);
            if (WorldData.GetByName(homeWorld) != null)
                continue;

            reason = $"selected Tony {tony.CharacterNameWorld} has an unknown home world '{homeWorld}'";
            return false;
        }

        var configuredMeetWorlds = xagmanMeetRouteSnapshotPinned
            ? xagmanServerMeetWorldsSnapshot
            : plugin.Configuration.XagmanServerMeetWorlds;
        if (configuredMeetWorlds.Count == 0)
        {
            reason = "no Server Matching meet worlds were captured for this run";
            return false;
        }

        foreach (var configured in configuredMeetWorlds)
        {
            if (string.IsNullOrWhiteSpace(configured.Value))
                continue;

            var configuredRegion = WorldData.GetRegionOfDataCenter(configured.Key);
            if (string.IsNullOrWhiteSpace(configuredRegion))
            {
                reason = $"configured server '{configured.Key}' is not in the XA world map";
                return false;
            }

            var meetWorld = WorldData.GetByName(configured.Value.Trim());
            if (meetWorld == null)
            {
                reason = $"meet world '{configured.Value}' for server {configured.Key} is unknown";
                return false;
            }
            if (!meetWorld.DataCenter.Equals(configured.Key, StringComparison.OrdinalIgnoreCase))
            {
                reason =
                    $"meet world {meetWorld.Name} belongs to {meetWorld.DataCenter}, not configured server {configured.Key}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateXagmanServerMatchingTonyRoute(
        string tonyCharacter,
        string region,
        string dataCenter,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(dataCenter))
        {
            reason = "the active sweep region or server is blank";
            return false;
        }

        var serverRegion = WorldData.GetRegionOfDataCenter(dataCenter);
        if (string.IsNullOrWhiteSpace(serverRegion)
            || !serverRegion.Equals(region, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"server {dataCenter} does not belong to active region {region}";
            return false;
        }

        var meetWorld = GetXagmanServerMeetWorld(dataCenter);
        var meetWorldInfo = string.IsNullOrWhiteSpace(meetWorld)
            ? null
            : WorldData.GetByName(meetWorld.Trim());
        if (meetWorldInfo == null)
        {
            reason = $"server {dataCenter} has no known configured meet world";
            return false;
        }
        if (!meetWorldInfo.DataCenter.Equals(dataCenter, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                $"configured meet world {meetWorldInfo.Name} belongs to {meetWorldInfo.DataCenter}, not {dataCenter}";
            return false;
        }
        if (string.IsNullOrWhiteSpace(GetXagmanSharedMeetLocation()))
        {
            reason = "the shared meet aetheryte is blank";
            return false;
        }

        var tonyRegion = GetXagmanRegionOfChar(tonyCharacter);
        if (string.IsNullOrWhiteSpace(tonyRegion)
            || !tonyRegion.Equals(region, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                $"Tony {tonyCharacter} belongs to {(string.IsNullOrWhiteSpace(tonyRegion) ? "an unknown region" : tonyRegion)}, not active region {region}";
            return false;
        }
        if (!TryValidateXagmanMeetTravel(tonyCharacter, meetWorldInfo.Name, out reason))
            return false;

        reason = string.Empty;
        return true;
    }

    private List<string> OrderXagmanKeysBySweep(IEnumerable<string> keys)
        => keys
            .OrderBy(key => WorldData.GetSweepOrdinalForWorld(GetWorldFromKey(key)))
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- Sweep state (Tony) ----

    private int GetXagmanCurrentSweepOrdinal()
        => string.IsNullOrWhiteSpace(xagmanSweepDataCenter) ? -1 : WorldData.GetSweepOrdinal(xagmanSweepDataCenter);

    private string GetXagmanTonySweepMeetWorld() => GetXagmanServerMeetWorld(xagmanSweepDataCenter);

    private void ApplyXagmanTonySweepMeetDestination()
        => SetXagmanActiveMeetDestination(GetXagmanTonySweepMeetWorld(), GetXagmanSharedMeetLocation());

    // Arm Server Matching for the run. The actual region/server is NOT chosen here - the Tony idles
    // in discovery (xagmanSweepAwaitingStart) and only commits once Franchise Owner presence reveals
    // which servers actually have owners. This avoids visiting servers/regions that have no owners.
    private void InitXagmanSweepState(IReadOnlyList<string> orderedTonyKeys)
    {
        xagmanServerMatchingActive = HasXagmanServerMatchingMeetConfig();
        xagmanSweepRegion = string.Empty;
        xagmanSweepDataCenter = string.Empty;
        xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
        xagmanInvalidPendingServerPeerSinceUtc = DateTime.MinValue;
        xagmanSweepAwaitingStart = xagmanServerMatchingActive;
        xagmanSweepDiscoveryStartedUtc = xagmanServerMatchingActive ? DateTime.UtcNow : DateTime.MinValue;
    }

    private void ResetXagmanServerMatchingRunState()
    {
        xagmanServerMatchingActive = false;
        xagmanSweepAwaitingStart = false;
        xagmanSweepDiscoveryStartedUtc = DateTime.MinValue;
        xagmanSweepRegion = string.Empty;
        xagmanSweepDataCenter = string.Empty;
        xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
        xagmanInvalidPendingServerPeerSinceUtc = DateTime.MinValue;
        xagmanOwnerSweepPendingDataCenter = string.Empty;
        xagmanSkippedCharacters.Clear();
        xagmanOwnerCompletedKeys.Clear();
    }

    // ---- FO-presence-driven server selection ----

    // Servers (data centers) that currently have at least one pending Franchise Owner AND a configured
    // meet world, in Region -> Server sweep order. This is the live frontier the Tony actually needs to
    // visit; servers with no owners are never visited.
    private List<string> GetXagmanFoNeededServers()
    {
        var configured = new HashSet<string>(GetXagmanConfiguredSweepServers(), StringComparer.OrdinalIgnoreCase);
        if (configured.Count == 0)
            return new List<string>();
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner && peer.XagmanEnabled && IsXagmanPeerFresh(peer))
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Select(peer => peer.ServerMatchingPendingDataCenter)
            .Where(dc => !string.IsNullOrWhiteSpace(dc) && configured.Contains(dc))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dc => WorldData.GetSweepOrdinal(dc))
            .ToList();
    }

    private List<XagmanPeerPresence> GetXagmanInvalidPendingServerPeers()
    {
        var configured = new HashSet<string>(
            GetXagmanConfiguredSweepServers(),
            StringComparer.OrdinalIgnoreCase);
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner
                && peer.XagmanEnabled
                && !peer.PhaseComplete
                && IsXagmanPeerFresh(peer))
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => string.IsNullOrWhiteSpace(peer.ServerMatchingPendingDataCenter)
                || WorldData.GetRegionOfDataCenter(peer.ServerMatchingPendingDataCenter) == null
                || !configured.Contains(peer.ServerMatchingPendingDataCenter))
            .ToList();
    }

    private static string DescribeXagmanInvalidPendingServerPeers(
        IReadOnlyList<XagmanPeerPresence> peers)
    {
        return string.Join(
            ", ",
            peers.Select(peer =>
                $"{peer.InstanceId}:{(string.IsNullOrWhiteSpace(peer.ServerMatchingPendingDataCenter) ? "<blank>" : peer.ServerMatchingPendingDataCenter)}"));
    }

    private bool XagmanRunListHasRegionTony(string region)
        => !string.IsNullOrWhiteSpace(region)
            && xagmanTonyRunList.Any(key => GetXagmanRegionOfChar(key).Equals(region, StringComparison.OrdinalIgnoreCase));

    private string? GetXagmanNextRunListTonyInRegion(string region)
        => xagmanTonyRunList.FirstOrDefault(key => GetXagmanRegionOfChar(key).Equals(region, StringComparison.OrdinalIgnoreCase));

    // Consume (silently, as "successful but not needed") any run-list Tonys whose region sorts before
    // the given region - those regions have no owners, so their Tonys are skipped without travel.
    private void ConsumeXagmanRunListTonysBeforeRegion(string region)
    {
        var targetIdx = Array.IndexOf(WorldData.RegionOrder, region);
        if (targetIdx < 0)
            return;
        var toConsume = xagmanTonyRunList
            .Where(key =>
            {
                var idx = Array.IndexOf(WorldData.RegionOrder, GetXagmanRegionOfChar(key));
                return idx >= 0 && idx < targetIdx;
            })
            .ToList();
        foreach (var key in toConsume)
        {
            plugin.TaskRunner.AddLog($"Xagman: Tony {key} not needed (no Franchise Owners in region {GetXagmanRegionOfChar(key)}); skipping it.");
            MarkXagmanTonyConsumed(key);
        }
    }

    // Relog the active Tony to the first run-list Tony in the target region and point the sweep at the
    // given server. Mirrors RotateXagmanTony but targets a specific region/server.
    private bool CommitXagmanSweepToRegion(string region, string server)
    {
        var tonyKey = GetXagmanNextRunListTonyInRegion(region);
        if (string.IsNullOrWhiteSpace(tonyKey))
        {
            ReportXagmanTravelRouteError(
                $"Server Matching cannot commit {region}/{server}: no same-region Tony remains.");
            return false;
        }
        if (!TryValidateXagmanServerMatchingTonyRoute(tonyKey, region, server, out var routeFailure))
        {
            ReportXagmanTravelRouteError(
                $"Server Matching cannot commit {region}/{server} with {tonyKey}: {routeFailure}");
            return false;
        }

        var meetWorld = GetXagmanServerMeetWorld(server);
        var meetAetheryte = GetXagmanSharedMeetLocation();
        var entry = plugin.Configuration.XagmanTonyCharacters
                .FirstOrDefault(e => e.CharacterNameWorld.Equals(tonyKey, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry { CharacterNameWorld = tonyKey, Mode = xagmanTonyMode };
        xagmanSweepRegion = region;
        xagmanSweepDataCenter = server;
        xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
        SetXagmanActiveMeetDestination(meetWorld, meetAetheryte);
        ResetXagmanTonyMeetRetryState();
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        ResetXagmanTonySellLocation();
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"Tony sweep commit to {region}/{server}"))
            return false;
        xagmanPreferredTonyCharacter = tonyKey;
        StartXagmanTonyStartup(entry, true);
        return xagmanStatus != XagmanStatus.Error;
    }

    // Pick the first region (sweep order, optionally excluding one) that has pending owners AND a Tony,
    // consume earlier unneeded Tonys, and commit the sweep + Tony to it. Returns false if no such region.
    private XagmanSweepCommitResult TryCommitXagmanSweepToFirstNeededRegion(string? excludeRegion)
    {
        var needed = GetXagmanFoNeededServers();
        if (needed.Count == 0)
            return XagmanSweepCommitResult.NoCandidate;
        foreach (var region in WorldData.RegionOrder)
        {
            if (excludeRegion != null && region.Equals(excludeRegion, StringComparison.OrdinalIgnoreCase))
                continue;
            var regionNeeded = needed.FirstOrDefault(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), region, StringComparison.OrdinalIgnoreCase));
            if (regionNeeded == null)
                continue;
            if (!XagmanRunListHasRegionTony(region))
            {
                plugin.TaskRunner.AddLog($"Xagman: region {region} has Franchise Owners but no selected Tony; they will be skipped.");
                continue;
            }

            ConsumeXagmanRunListTonysBeforeRegion(region);
            plugin.TaskRunner.AddLog($"Xagman: Franchise Owners detected in region {region}; starting Tony sweep at server {regionNeeded} ({GetXagmanServerMeetWorld(regionNeeded)}).");
            if (CommitXagmanSweepToRegion(region, regionNeeded))
                return XagmanSweepCommitResult.Committed;
            return XagmanSweepCommitResult.Error;
        }

        return XagmanSweepCommitResult.NoCandidate;
    }

    // Discovery loop: the Tony idles until it can see which servers owners are on, then commits.
    private void TryBeginXagmanSweepFromDiscovery()
    {
        var commitResult = TryCommitXagmanSweepToFirstNeededRegion(null);
        if (commitResult == XagmanSweepCommitResult.Error)
        {
            xagmanSweepAwaitingStart = false;
            return;
        }
        if (commitResult == XagmanSweepCommitResult.Committed)
        {
            xagmanSweepAwaitingStart = false;
            return;
        }

        var needed = GetXagmanFoNeededServers();
        var invalidPendingPeers = GetXagmanInvalidPendingServerPeers();
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = needed.Count == 0 && invalidPendingPeers.Count == 0
            ? "Server Matching: waiting to see which servers Franchise Owners need..."
            : invalidPendingPeers.Count > 0
                ? "Server Matching: waiting for Franchise Owners to publish known configured home servers..."
            : "Server Matching: Franchise Owners are on regions with no selected Tony; waiting...";

        // If owners are present but none are on a region we have a Tony for, give up after a grace
        // period so the run does not hang. Owners on unservable regions are skipped on completion.
        if ((needed.Count > 0 || invalidPendingPeers.Count > 0)
            && xagmanSweepDiscoveryStartedUtc > DateTime.MinValue
            && (DateTime.UtcNow - xagmanSweepDiscoveryStartedUtc).TotalSeconds >= XagmanSweepDiscoveryGiveUpSeconds)
        {
            xagmanSweepAwaitingStart = false;
            if (IsXagmanCollectionFirstCollectionPhase())
            {
                var invalidDetail = invalidPendingPeers.Count == 0
                    ? string.Empty
                    : $" Invalid pending FO servers: {DescribeXagmanInvalidPendingServerPeers(invalidPendingPeers)}.";
                ReportXagmanTravelRouteError(
                    "collection pass cannot continue because one or more waiting FO regions have no selected Tony or no known configured home server."
                    + invalidDetail);
                return;
            }
            plugin.TaskRunner.AddLog("Xagman: no selected Tony covers the regions where Franchise Owners are waiting; completing (those owners will be marked skipped).");
            StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: true, completedWithWarnings: true, broadcastPeerCompletion: true);
        }
    }

    // ---- Tony sweep advance ----

    // True when the current sweep server has no live owners left to serve (queue empty, no fresh
    // owner peer still pending on this server, and no active trade).
    private bool IsXagmanCurrentSweepServerDrained()
    {
        if (!xagmanServerMatchingActive || string.IsNullOrWhiteSpace(xagmanSweepDataCenter))
            return false;
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return false;
        if (GetXagmanQueueForTony(xagmanActiveCharacter).Count > 0)
            return false;

        var pendingOnServer = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner && peer.XagmanEnabled && IsXagmanPeerFresh(peer))
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Any(peer => string.Equals(peer.ServerMatchingPendingDataCenter, xagmanSweepDataCenter, StringComparison.OrdinalIgnoreCase));
        return !pendingOnServer;
    }

    private bool TryHoldXagmanCollectionSweepForUnavailableExpectedPeers()
    {
        if (!IsXagmanCollectionFirstCollectionPhase())
            return false;

        var availableExpectedIds = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(peer => IsXagmanPeerInRun(peer, xagmanRunId, XagmanRunPhase.Collection))
            .Where(peer => peer.Status != XagmanStatus.Error)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Select(peer => peer.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailable = xagmanExpectedFranchiseOwnerInstanceIds
            .Where(instanceId => !xagmanCollectionPhaseAcknowledgedInstanceIds.Contains(instanceId))
            .Where(instanceId => !availableExpectedIds.Contains(instanceId))
            .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unavailable.Count == 0)
        {
            xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
            return false;
        }

        if (xagmanMissingCohortPeerSinceUtc == DateTime.MinValue)
        {
            xagmanMissingCohortPeerSinceUtc = DateTime.UtcNow;
            plugin.TaskRunner.AddLog(
                $"Xagman: Server Matching collection sweep is holding before advance because {unavailable.Count} unacknowledged expected FO client(s) are unavailable: {string.Join(", ", unavailable)}.");
        }

        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText =
            $"Collection sweep paused for {unavailable.Count} unavailable expected FO client(s); no server or region advance is allowed.";
        if ((DateTime.UtcNow - xagmanMissingCohortPeerSinceUtc).TotalSeconds
            < XagmanMissingCohortPeerFailSeconds)
        {
            return true;
        }

        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText =
            $"Collection sweep stopped because {unavailable.Count} expected FO client(s) stayed unavailable.";
        plugin.TaskRunner.AddLog(
            $"Xagman: collection-first run {xagmanRunId} failed closed before Server Matching advance after waiting {XagmanMissingCohortPeerFailSeconds:0} seconds for: {string.Join(", ", unavailable)}. Restock was not started.");
        PublishXagmanPresence();
        return true;
    }

    // Drive the Tony sweep one step when the current server's queue is empty. The caller (Tony
    // runtime) only invokes this in server-matching mode when there is no active trade and the
    // queue is empty.
    private XagmanSweepStep StepXagmanSweep()
    {
        if (!xagmanServerMatchingActive)
            return XagmanSweepStep.NotServerMatching;
        if (TryHoldXagmanCollectionSweepForUnavailableExpectedPeers())
            return XagmanSweepStep.Blocked;

        if (IsXagmanCollectionFirstCollectionPhase())
        {
            var invalidPendingPeers = GetXagmanInvalidPendingServerPeers();
            if (invalidPendingPeers.Count > 0)
            {
                if (xagmanInvalidPendingServerPeerSinceUtc == DateTime.MinValue)
                {
                    xagmanInvalidPendingServerPeerSinceUtc = DateTime.UtcNow;
                    plugin.TaskRunner.AddLog(
                        "Xagman: collection sweep is holding before server advance because FO clients published blank, unknown, or unconfigured pending servers: "
                        + DescribeXagmanInvalidPendingServerPeers(invalidPendingPeers));
                }

                xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText =
                    "Collection sweep is holding for Franchise Owners to publish known configured home servers.";
                if ((DateTime.UtcNow - xagmanInvalidPendingServerPeerSinceUtc).TotalSeconds
                    < XagmanSweepDiscoveryGiveUpSeconds)
                {
                    return XagmanSweepStep.Blocked;
                }

                ReportXagmanTravelRouteError(
                    "collection sweep cannot advance while expected FO clients have blank, unknown, or unconfigured pending servers: "
                    + DescribeXagmanInvalidPendingServerPeers(invalidPendingPeers));
                return XagmanSweepStep.Error;
            }

            xagmanInvalidPendingServerPeerSinceUtc = DateTime.MinValue;
        }

        if (!IsXagmanCurrentSweepServerDrained())
        {
            xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
            return XagmanSweepStep.WaitingForOwners;
        }

        if (xagmanSweepServerDrainedSinceUtc == DateTime.MinValue)
        {
            xagmanSweepServerDrainedSinceUtc = DateTime.UtcNow;
            return XagmanSweepStep.Settling;
        }

        if ((DateTime.UtcNow - xagmanSweepServerDrainedSinceUtc).TotalSeconds < XagmanSweepServerSettleSeconds)
            return XagmanSweepStep.Settling;

        xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;

        // Next server in the CURRENT region that still has pending owners (FO-driven, not config order).
        var nextInRegion = GetXagmanFoNeededServers()
            .FirstOrDefault(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), xagmanSweepRegion, StringComparison.OrdinalIgnoreCase)
                && !dc.Equals(xagmanSweepDataCenter, StringComparison.OrdinalIgnoreCase));
        if (nextInRegion != null)
        {
            var drainedServer = xagmanSweepDataCenter;
            if (!TryValidateXagmanServerMatchingTonyRoute(
                    xagmanActiveCharacter,
                    xagmanSweepRegion,
                    nextInRegion,
                    out var routeFailure))
            {
                ReportXagmanTravelRouteError(
                    $"Server Matching cannot advance {xagmanActiveCharacter} from {drainedServer} to {nextInRegion}: {routeFailure}");
                return XagmanSweepStep.Error;
            }

            xagmanSweepDataCenter = nextInRegion;
            SetXagmanActiveMeetDestination(
                GetXagmanServerMeetWorld(nextInRegion),
                GetXagmanSharedMeetLocation());
            ResetXagmanTonyMeetRetryState();
            plugin.TaskRunner.AddLog(
                $"Xagman: server {drainedServer} fully processed; advancing Tony {xagmanActiveCharacter} sweep to {xagmanSweepDataCenter} ({GetXagmanTonySweepMeetWorld()}).");
            PublishXagmanPresence();
            return XagmanSweepStep.Advanced;
        }

        // No more owners in this region -> region complete -> move to the next region that has owners.
        return TryAdvanceXagmanSweepToNextNeededRegion("fully served")
            ? XagmanSweepStep.Advanced
            : XagmanSweepStep.Finished;
    }

    // Region-aware rotation used by the Tony full/deplete paths. Returns true when a rotation or
    // region advance was performed (caller returns); false when no Tony capacity remains anywhere
    // (caller should finalize the run with a warning summary).
    private bool TryRotateXagmanTonyForCapacityExhaustion(string contextLog)
    {
        var active = xagmanActiveCharacter;
        if (xagmanServerMatchingActive)
        {
            var hasSameRegionAlternate = xagmanTonyRunList.Any(key =>
                !key.Equals(active, StringComparison.OrdinalIgnoreCase)
                && GetXagmanRegionOfChar(key).Equals(xagmanSweepRegion, StringComparison.OrdinalIgnoreCase));
            if (hasSameRegionAlternate)
            {
                if (!string.IsNullOrWhiteSpace(contextLog))
                    plugin.TaskRunner.AddLog($"{contextLog} Rotating to the next Tony in region {xagmanSweepRegion}.");
                RotateXagmanTony();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(contextLog))
                plugin.TaskRunner.AddLog(contextLog);
            if (IsXagmanCollectionFirstCollectionPhase())
            {
                ReportXagmanTravelRouteError(
                    $"Collection pass cannot leave region {xagmanSweepRegion} after its last usable Tony was exhausted; all required collection work must finish before restock.");
                return true;
            }
            return TryAdvanceXagmanSweepToNextNeededRegion("ran out of Tony capacity; remaining owners on it will be skipped");
        }

        var hasAlternateTony = xagmanTonyRunList.Any(key => !key.Equals(active, StringComparison.OrdinalIgnoreCase));
        if (hasAlternateTony)
        {
            if (!string.IsNullOrWhiteSpace(contextLog))
                plugin.TaskRunner.AddLog($"{contextLog} Rotating to the next Tony.");
            RotateXagmanTony();
            return true;
        }

        return false;
    }

    private string GetXagmanPeerDataCenter(XagmanPeerPresence peer)
    {
        var world = !string.IsNullOrWhiteSpace(peer.ActiveCharacter)
            ? GetWorldFromKey(peer.ActiveCharacter)
            : peer.HomeWorld;
        return WorldData.GetDataCenterOfWorld(world) ?? string.Empty;
    }

    // When a server-matching Franchise Owner snapshot is captured, preserve every unresolved character as
    // skipped (purple) in the snapshot without flooding the task log with one line per character.
    private void FinalizeXagmanOwnerSkippedRemainder()
    {
        if (!xagmanServerMatchingActive || xagmanActiveRole != XagmanRole.FranchiseOwner)
            return;
        var newlySkipped = 0;
        foreach (var key in xagmanOwnerRunPlan)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (xagmanOwnerCompletedKeys.Contains(key))
                continue;
            if (plugin.TaskRunner.FailedCharacters.Contains(key))
                continue;
            if (xagmanSkippedCharacters.Add(key))
                newlySkipped++;
        }
        if (newlySkipped > 0)
        {
            InvalidateXagmanTradeCapacityForecast();
            plugin.TaskRunner.AddLog($"Xagman: {newlySkipped} remaining owner(s) were not served; marked them skipped in the run summary.");
        }
    }

    // Leave the current region (fully served or out of Tony capacity) and commit to the next region
    // that still has pending owners and a selected Tony. The current region's remaining Tonys are
    // consumed silently (successful but not needed); owners on skipped regions fall through to skip.
    private bool TryAdvanceXagmanSweepToNextNeededRegion(string reasonText)
    {
        var leavingRegion = xagmanSweepRegion;
        var invalidPendingPeers = GetXagmanInvalidPendingServerPeers();
        if (IsXagmanCollectionFirstCollectionPhase() && invalidPendingPeers.Count > 0)
        {
            var discoveryAgeSeconds = xagmanSweepDiscoveryStartedUtc == DateTime.MinValue
                ? 0.0
                : (DateTime.UtcNow - xagmanSweepDiscoveryStartedUtc).TotalSeconds;
            if (discoveryAgeSeconds < XagmanSweepDiscoveryGiveUpSeconds)
            {
                xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText =
                    "Collection sweep is holding for Franchise Owners to publish known configured home servers.";
                return true;
            }

            ReportXagmanTravelRouteError(
                "collection sweep cannot advance while expected FO clients have blank, unknown, or unconfigured pending servers: "
                + DescribeXagmanInvalidPendingServerPeers(invalidPendingPeers));
            return true;
        }

        // The current region is done; any remaining Tonys in it are no longer needed.
        var redundantTonys = xagmanTonyRunList
            .Where(key => GetXagmanRegionOfChar(key).Equals(leavingRegion, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in redundantTonys)
            MarkXagmanTonyConsumed(key);

        plugin.TaskRunner.AddLog($"Xagman: region {leavingRegion} {reasonText}.");
        var commitResult = TryCommitXagmanSweepToFirstNeededRegion(leavingRegion);
        return commitResult is XagmanSweepCommitResult.Committed or XagmanSweepCommitResult.Error;
    }

    // ---- Franchise Owner gate ----

    private XagmanPeerPresence? GetXagmanServerMatchingTonyPeer()
    {
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.Tony && peer.XagmanEnabled && peer.ServerMatchingEnabled && IsXagmanPeerFresh(peer))
            .Where(peer => peer.Status != XagmanStatus.Error)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .OrderByDescending(peer => IsXagmanServerMatchingTonyMeetReady(peer, out _))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
    }

    private bool IsXagmanOwnerServerMatchingActive() => GetXagmanServerMatchingTonyPeer() != null;

    private bool IsXagmanServerMatchingTonyMeetReady(XagmanPeerPresence tony, out string reason)
    {
        if (tony.Status == XagmanStatus.Error)
        {
            reason = "Tony reported a travel or coordination error";
            return false;
        }

        if (tony.ServerMatchingSweepOrdinal < 0
            || string.IsNullOrWhiteSpace(tony.ServerMatchingActiveDataCenter))
        {
            reason = "waiting for the Tony sweep to start";
            return false;
        }

        if (!HasCompleteXagmanMeetDestination(tony.MeetWorld, tony.MeetAetheryte))
        {
            reason = "waiting for Tony to publish a complete meet world and aetheryte";
            return false;
        }

        var activeRegion = WorldData.GetRegionOfDataCenter(tony.ServerMatchingActiveDataCenter);
        if (string.IsNullOrWhiteSpace(activeRegion))
        {
            reason = $"Tony published unknown active server {tony.ServerMatchingActiveDataCenter}";
            return false;
        }

        var expectedOrdinal = WorldData.GetSweepOrdinal(tony.ServerMatchingActiveDataCenter);
        if (tony.ServerMatchingSweepOrdinal != expectedOrdinal)
        {
            reason =
                $"Tony's sweep ordinal {tony.ServerMatchingSweepOrdinal} does not match active server {tony.ServerMatchingActiveDataCenter} ({expectedOrdinal})";
            return false;
        }

        var tonyHomeRegion = GetXagmanRegionOfChar(tony.ActiveCharacter);
        if (string.IsNullOrWhiteSpace(tonyHomeRegion)
            || !tonyHomeRegion.Equals(activeRegion, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                $"Tony {tony.ActiveCharacter} is not a home-region match for active region {activeRegion}";
            return false;
        }

        var advertisedDataCenter = WorldData.GetDataCenterOfWorld(tony.MeetWorld);
        if (string.IsNullOrWhiteSpace(advertisedDataCenter)
            || !advertisedDataCenter.Equals(tony.ServerMatchingActiveDataCenter, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"waiting for a meet world on Tony's active server {tony.ServerMatchingActiveDataCenter}";
            return false;
        }

        if (!TryValidateXagmanMeetTravel(tony.ActiveCharacter, tony.MeetWorld, out reason))
            return false;

        reason = string.Empty;
        return true;
    }

    private XagmanOwnerServerGate EvaluateXagmanOwnerServerGate(string characterNameWorld, out string meetWorld, out string meetAetheryte, out string reason)
    {
        meetWorld = string.Empty;
        meetAetheryte = string.Empty;
        reason = string.Empty;

        var tony = GetXagmanServerMatchingTonyPeer();
        if (tony == null)
        {
            reason = "waiting for a Server Matching Tony";
            return XagmanOwnerServerGate.Wait;
        }

        if (!IsXagmanServerMatchingTonyMeetReady(tony, out reason))
            return XagmanOwnerServerGate.Wait;

        var myDataCenter = GetXagmanDataCenterOfChar(characterNameWorld);
        if (string.IsNullOrWhiteSpace(myDataCenter))
        {
            reason = "home server is unknown, so Server Matching cannot route this owner safely";
            return IsXagmanCollectionFirstRunActive()
                ? XagmanOwnerServerGate.RouteError
                : XagmanOwnerServerGate.Skip;
        }

        if (string.Equals(tony.ServerMatchingActiveDataCenter, myDataCenter, StringComparison.OrdinalIgnoreCase))
        {
            meetWorld = tony.MeetWorld;
            meetAetheryte = tony.MeetAetheryte;
            reason = $"Tony is processing {myDataCenter}";
            return XagmanOwnerServerGate.Proceed;
        }

        var myOrdinal = WorldData.GetSweepOrdinal(myDataCenter);
        if (tony.ServerMatchingSweepOrdinal < myOrdinal)
        {
            reason = $"waiting for server {myDataCenter} (Tony on {tony.ServerMatchingActiveDataCenter})";
            return XagmanOwnerServerGate.Wait;
        }

        reason = $"server {myDataCenter} already processed (Tony on {tony.ServerMatchingActiveDataCenter})";
        return XagmanOwnerServerGate.Skip;
    }

    // True once the sweeping Tony has moved past this owner character's server without serving it.
    private bool IsXagmanOwnerCharPassedBySweep(string characterNameWorld)
    {
        var tony = GetXagmanServerMatchingTonyPeer();
        if (tony == null || tony.ServerMatchingSweepOrdinal < 0)
            return false;
        var myDataCenter = GetXagmanDataCenterOfChar(characterNameWorld);
        if (string.IsNullOrWhiteSpace(myDataCenter))
            return false;
        if (string.Equals(tony.ServerMatchingActiveDataCenter, myDataCenter, StringComparison.OrdinalIgnoreCase))
            return false;
        return tony.ServerMatchingSweepOrdinal > WorldData.GetSweepOrdinal(myDataCenter);
    }

    private void MarkXagmanOwnerSkipped(string characterNameWorld, string reason)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return;
        if (xagmanSkippedCharacters.Add(characterNameWorld))
        {
            InvalidateXagmanTradeCapacityForecast();
            plugin.TaskRunner.AddLog($"Xagman: owner {characterNameWorld} skipped - {reason}.");
        }
    }

    // ---- Per-character timing + ETA ----

    // Start the active-processing timer for a character at its /ays relog login attempt.
    private void RecordXagmanOwnerProcessingStart(string characterNameWorld)
    {
        if (!string.IsNullOrWhiteSpace(characterNameWorld))
            xagmanCharStartUtc[characterNameWorld] = DateTime.UtcNow;
    }

    // End the active-processing timer for a finished character (completed or failed).
    private void RecordXagmanOwnerProcessingEnd(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return;
        if (xagmanCharStartUtc.TryGetValue(characterNameWorld, out var start) && !xagmanCharDurationSeconds.ContainsKey(characterNameWorld))
            xagmanCharDurationSeconds[characterNameWorld] = Math.Max(0, (DateTime.UtcNow - start).TotalSeconds);
    }

    private void ResetXagmanOwnerTimings()
    {
        xagmanCharStartUtc.Clear();
        xagmanCharDurationSeconds.Clear();
    }

    private static string FormatXagmanDuration(double seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds));
        if (total < 60)
            return $"{total}s";
        var minutes = total / 60;
        var secs = total % 60;
        if (minutes < 60)
            return $"{minutes}m {secs:00}s";
        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours}h {minutes:00}m";
    }

    // Rolling completion estimate from the average processed-character time times the remaining count.
    private string GetXagmanOwnerEtaLabel(IReadOnlyList<string> runPlan, IReadOnlyDictionary<string, double> durations)
    {
        if (runPlan == null || runPlan.Count == 0 || durations == null || durations.Count == 0)
            return string.Empty;
        var avg = durations.Values.Average();
        var processed = durations.Count;
        var remaining = Math.Max(0, runPlan.Count - processed);
        if (remaining == 0)
            return $"Avg {FormatXagmanDuration(avg)}/char.";
        return $"Avg {FormatXagmanDuration(avg)}/char, {remaining} remaining, ETA ~{FormatXagmanDuration(avg * remaining)}.";
    }

    // ---- Post-completion snapshot ----

    private void CaptureXagmanRunSnapshot()
    {
        FinalizeXagmanOwnerSkippedRemainder();
        xagmanLastRunRole = xagmanActiveRole;
        xagmanLastRunCharDurations.Clear();
        foreach (var entry in xagmanCharDurationSeconds)
            xagmanLastRunCharDurations[entry.Key] = entry.Value;
        xagmanLastRunOwnerPlan = IsXagmanCollectionFirstRunActive() && xagmanCollectionFirstOwnerFullPlan.Count > 0
            ? xagmanCollectionFirstOwnerFullPlan.ToList()
            : xagmanOwnerRunPlan.ToList();
        xagmanLastRunTonyPlan = xagmanTonyRunPlan.ToList();
        xagmanLastRunOwnerCompleted = GetXagmanLocalOwnerCompletedCharacters();
        xagmanLastRunTonyCompleted = GetXagmanLocalTonyCompletedCharacters();
        xagmanLastRunFailedCharacters.Clear();
        foreach (var failed in plugin.TaskRunner.FailedCharacters)
        {
            if (!string.IsNullOrWhiteSpace(failed))
                xagmanLastRunFailedCharacters.Add(failed);
        }
        foreach (var failed in xagmanCollectionFirstFailedCharacters)
        {
            if (!string.IsNullOrWhiteSpace(failed))
                xagmanLastRunFailedCharacters.Add(failed);
        }

        xagmanLastRunSkippedCharacters.Clear();
        foreach (var skipped in xagmanSkippedCharacters)
            xagmanLastRunSkippedCharacters.Add(skipped);

        xagmanHasLastRunSnapshot = (xagmanLastRunOwnerPlan.Count + xagmanLastRunTonyPlan.Count) > 0;
    }

    private void ClearXagmanRunSnapshot()
    {
        xagmanHasLastRunSnapshot = false;
        xagmanLastRunOwnerPlan = Array.Empty<string>();
        xagmanLastRunTonyPlan = Array.Empty<string>();
        xagmanLastRunOwnerCompleted = 0;
        xagmanLastRunTonyCompleted = 0;
        xagmanLastRunFailedCharacters.Clear();
        xagmanLastRunSkippedCharacters.Clear();
        xagmanLastRunCharDurations.Clear();
    }
}
