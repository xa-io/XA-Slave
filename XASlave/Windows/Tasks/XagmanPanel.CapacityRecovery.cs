using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private enum XagmanCapacityRecoveryStage
    {
        None,
        WaitingForOwnerPause,
        Draining,
        WaitingForOwnerRestore,
        OwnerWaitingForDrain,
        OwnerDraining,
        OwnerWaitingForCollectionRelease,
        Completing,
        Failed,
    }

    private sealed class XagmanCapacityOwnerCheckpoint
    {
        public List<string> Plan { get; init; } = new();
        public List<string> RunList { get; init; } = new();
        public int Index { get; init; }
        public int Total { get; init; }
        public int Completed { get; init; }
        public int PhaseTotal { get; init; }
        public int PhaseResolved { get; init; }
        public bool PhaseComplete { get; init; }
        public string Character { get; init; } = string.Empty;
        public string PendingDataCenter { get; init; } = string.Empty;
        public List<XagmanTradeRequestEntry> ActivePendingGiveItems { get; init; } = new();
    }

    private string xagmanCapacityDrainId = string.Empty;
    private int xagmanCapacityRecoveryEpoch;
    private string xagmanCapacityRecoveryCoordinatorInstanceId = string.Empty;
    private string xagmanCapacityRecoveryRegion = string.Empty;
    private bool xagmanCapacityDrainReady;
    private bool xagmanCapacityCollectionRestored;
    private int xagmanCapacityRestoreRequestedEpoch;
    private string xagmanCapacityWarningReason = string.Empty;
    private XagmanCapacityRecoveryStage xagmanCapacityRecoveryStage;
    private XagmanCapacityOwnerCheckpoint? xagmanCapacityOwnerCheckpoint;
    private List<string> xagmanCapacityCollectionTonyRemainder = new();
    private string xagmanCapacityCollectionDataCenter = string.Empty;
    private int xagmanCapacityCollectionPhaseTotal;
    private int xagmanCapacityCollectionPhaseResolved;
    private bool xagmanCapacityBlockedOnItems;
    private bool xagmanCapacityBlockedOnGil;
    private readonly HashSet<string> xagmanCapacityRecoveredTonys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanCapacityDrainFilledOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<(uint ItemId, bool IsHq), int>> xagmanCapacityInitialStock = new(StringComparer.OrdinalIgnoreCase);
    private DateTime xagmanCapacityTransitionStartedUtc = DateTime.MinValue;
    private DateTime xagmanCapacitySafeSinceUtc = DateTime.MinValue;
    private DateTime xagmanCapacityMissingPeerSinceUtc = DateTime.MinValue;
    private const double XagmanCapacityTransitionTimeoutSeconds = 900;

    private bool IsXagmanCapacityDrainActive() => !string.IsNullOrWhiteSpace(xagmanCapacityDrainId);

    private bool IsXagmanCapacityRecoveryActive()
        => xagmanCapacityRecoveryStage != XagmanCapacityRecoveryStage.None;

    private bool IsXagmanCapacityWarningCompletion()
        => IsXagmanCapacityDrainActive() && xagmanRunPhase == XagmanRunPhase.Collection
            && xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.Completing;

    private bool IsXagmanCapacityDrainOwnerFilled(string owner)
        => IsXagmanCapacityDrainActive() && xagmanCapacityDrainFilledOwners.Contains(owner);

    private void RecordXagmanCapacityDrainOwnerFilled(string owner)
    {
        if (IsXagmanCapacityDrainActive()) xagmanCapacityDrainFilledOwners.Add(owner);
    }

    private void ResetXagmanCapacityRecovery()
    {
        xagmanCapacityDrainId = xagmanCapacityRecoveryCoordinatorInstanceId = xagmanCapacityRecoveryRegion = string.Empty;
        xagmanCapacityRecoveryEpoch = 0;
        xagmanCapacityRestoreRequestedEpoch = 0;
        xagmanCapacityWarningReason = string.Empty;
        xagmanCapacityDrainReady = xagmanCapacityCollectionRestored = false;
        xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.None;
        xagmanCapacityOwnerCheckpoint = null;
        xagmanCapacityCollectionTonyRemainder.Clear();
        xagmanCapacityCollectionDataCenter = string.Empty;
        xagmanCapacityCollectionPhaseTotal = xagmanCapacityCollectionPhaseResolved = 0;
        xagmanCapacityBlockedOnItems = xagmanCapacityBlockedOnGil = false;
        xagmanCapacityRecoveredTonys.Clear();
        xagmanCapacityDrainFilledOwners.Clear();
        xagmanCapacityInitialStock.Clear();
        xagmanCapacityTransitionStartedUtc = xagmanCapacitySafeSinceUtc = xagmanCapacityMissingPeerSinceUtc = DateTime.MinValue;
    }

    // Collection keeps its RunId, raw phase and frozen client cohort throughout recovery.
    // False leaves ordinary collection rotation/failure handling in charge.
    private bool TryBeginXagmanCapacityRecovery()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || !IsXagmanCollectionFirstRunActive()
            || xagmanRunPhase != XagmanRunPhase.Collection) return false;
        if (xagmanCapacityRecoveryStage != XagmanCapacityRecoveryStage.None) return true;
        if (plugin.TaskRunner.IsRunning || !IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)) return false;
        var region = GetXagmanRegionOfChar(xagmanActiveCharacter);
        if (string.IsNullOrWhiteSpace(region)) return false;
        if (xagmanTonyRunList.Any(key => !key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
            && !plugin.TaskRunner.FailedCharacters.Contains(key)
            && GetXagmanRegionOfChar(key).Equals(region, StringComparison.OrdinalIgnoreCase))) return false;
        if (!TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots)
            || !TryGetXagmanLiveGil(out var currentGil))
        {
            FailXagmanCapacityRecovery($"Tony {xagmanActiveCharacter}'s receiving capacity could not be verified; collection recovery was withheld.");
            return true;
        }
        var fullInventory = freeSlots == 0;
        var gilCap = currentGil >= XagmanTonySellGilLimit;
        if (!fullInventory && !gilCap) return false;
        if (xagmanExpectedFranchiseOwnerInstanceIds.Count == 0)
        {
            FailXagmanCapacityRecovery("Collection capacity recovery has no frozen Franchise Owner cohort; no supply pass was started.");
            return true;
        }
        if (xagmanCapacityRecoveryEpoch >= int.MaxValue - 1)
        {
            FailXagmanCapacityRecovery("Collection capacity recovery exhausted its transition generation; restart the run explicitly.");
            return true;
        }
        xagmanCapacityCollectionTonyRemainder = xagmanTonyRunList.ToList();
        xagmanCapacityCollectionDataCenter = xagmanSweepDataCenter;
        xagmanCapacityCollectionPhaseTotal = xagmanPhaseTotalCharacters;
        xagmanCapacityCollectionPhaseResolved = xagmanPhaseResolvedCharacters;
        xagmanCapacityBlockedOnItems = fullInventory;
        xagmanCapacityBlockedOnGil = gilCap;
        xagmanCapacityRecoveredTonys.Clear();
        xagmanCapacityInitialStock.Clear();
        xagmanCapacityDrainFilledOwners.Clear();
        xagmanCapacityDrainId = Guid.NewGuid().ToString("N");
        xagmanCapacityRecoveryEpoch++;
        xagmanCapacityRecoveryCoordinatorInstanceId = plugin.InstanceId;
        xagmanCapacityRecoveryRegion = region;
        xagmanCapacityDrainReady = xagmanCapacityCollectionRestored = false;
        xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.WaitingForOwnerPause;
        xagmanCapacityTransitionStartedUtc = DateTime.UtcNow;
        xagmanCapacitySafeSinceUtc = DateTime.MinValue;
        xagmanSupplyOwnerCohort.Clear();
        xagmanSupplyOwnerCohort.UnionWith(xagmanExpectedFranchiseOwnerInstanceIds);
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = $"Collection capacity exhausted in {region}; waiting for owners to safely save collection progress before stocked Tonys deliver supplies.";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
        return true;
    }

    // Run before normal owner/Tony runtime, including while the task runner is active.
    // True holds ordinary dispatch, but does not interrupt an in-flight native operation.
    private bool UpdateXagmanCapacityRecovery()
    {
        if (!xagmanRunning || !IsXagmanCollectionFirstRunActive() || xagmanRunPhase != XagmanRunPhase.Collection) return false;
        if (xagmanCapacityRecoveryStage is XagmanCapacityRecoveryStage.Failed or XagmanCapacityRecoveryStage.Completing) return true;
        if (xagmanActiveRole == XagmanRole.FranchiseOwner) return UpdateXagmanOwnerCapacityRecovery();
        if (xagmanActiveRole != XagmanRole.Tony || xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.None) return false;
        if (xagmanStatus == XagmanStatus.Error) return true;
        if (xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.Draining)
        {
            CaptureXagmanCapacityInitialStock();
            return false;
        }
        var peers = GetXagmanCapacityOwnerPeers();
        if (peers.Any(peer => peer.Status == XagmanStatus.Error))
        {
            FailXagmanCapacityRecovery("A frozen owner client reported an error during collection capacity recovery; saved work remains pending.");
            return true;
        }
        var expectedPresent = xagmanExpectedFranchiseOwnerInstanceIds.All(id => peers.Any(peer => peer.InstanceId == id));
        var restoring = xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.WaitingForOwnerRestore;
        var acknowledged = expectedPresent && peers.All(peer => peer.CapacityRecoveryEpoch == xagmanCapacityRecoveryEpoch
            && peer.CapacityRecoveryCoordinatorInstanceId == plugin.InstanceId
            && peer.CapacityRecoveryRegion == xagmanCapacityRecoveryRegion
            && (restoring ? string.IsNullOrWhiteSpace(peer.CapacityDrainId) && peer.CapacityCollectionRestored
                : peer.CapacityDrainId == xagmanCapacityDrainId && peer.CapacityDrainReady));
        if (!acknowledged || !IsXagmanCapacityTransitionSafe())
        {
            CheckXagmanCapacityTransitionTimeout(restoring ? "restore their saved collection cursors" : "safely pause collection");
            return true;
        }
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "collection capacity transition")) return true;
        ClearXagmanDropbox();
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        xagmanCapacitySafeSinceUtc = DateTime.MinValue;
        if (restoring)
        {
            xagmanCapacityCollectionRestored = true;
            xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.None;
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = "All owners restored collection progress; resuming collection with a Tony that recovered capacity.";
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            PublishXagmanPresence();
            StartXagmanCapacityTony(xagmanTonyRunList.First(key => GetXagmanRegionOfChar(key)
                .Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase)));
            return true;
        }
        var firstDataCenter = peers.SelectMany(peer => peer.SupplyPendingDataCenters)
            .Where(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase))
            .OrderBy(WorldData.GetSweepOrdinal).FirstOrDefault();
        // Even a region with no receivers joins an empty exact supplier pass. Every frozen
        // owner must reach the menu before definitive capacity exhaustion can clean up.
        var emptySupplyPass = firstDataCenter == null;
        xagmanTonyRunList = emptySupplyPass ? new List<string> { xagmanActiveCharacter }
            : xagmanTonyRunPlan.Where(key => !plugin.TaskRunner.FailedCharacters.Contains(key)
                && GetXagmanRegionOfChar(key).Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase)).ToList();
        if (xagmanTonyRunList.Count == 0)
        {
            FailXagmanCapacityRecovery($"No nonfailed stocked Tony remains in {xagmanCapacityRecoveryRegion} for capacity recovery.");
            return true;
        }
        xagmanTonyCompletedCharacters = 0;
        xagmanCurrentTonyIndex = 0;
        xagmanSweepRegion = xagmanCapacityRecoveryRegion;
        if (xagmanServerMatchingActive) xagmanSweepDataCenter = firstDataCenter ?? xagmanCapacityCollectionDataCenter;
        xagmanSweepAwaitingStart = false;
        xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
        xagmanCapacityDrainReady = true;
        xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.Draining;
        plugin.TaskRunner.AddLog(emptySupplyPass
            ? $"Xagman: no receiving owner policies can drain the stocked Tonys in {xagmanCapacityRecoveryRegion}; completing an empty supplier pass at the main-menu barrier before warning cleanup."
            : $"Xagman: collection is saved on every owner client; revisiting {xagmanTonyRunList.Count} stocked Tony(s) in {xagmanCapacityRecoveryRegion} for outgoing supplies.");
        StartXagmanCapacityTony(xagmanTonyRunList[0]);
        return true;
    }

    private List<XagmanPeerPresence> GetXagmanCapacityOwnerPeers() => plugin.XagmanPeers.Peers
        .Where(peer => peer.Role == XagmanRole.FranchiseOwner && peer.XagmanEnabled && IsXagmanPeerFresh(peer)
            && xagmanExpectedFranchiseOwnerInstanceIds.Contains(peer.InstanceId)
            && IsXagmanPeerInRun(peer, xagmanRunId, XagmanRunPhase.Collection)).ToList();

    private bool UpdateXagmanOwnerCapacityRecovery()
    {
        var tony = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.Role == XagmanRole.Tony && peer.XagmanEnabled
            && IsXagmanPeerFresh(peer) && IsXagmanPeerInRun(peer, xagmanRunId, XagmanRunPhase.Collection)
            && peer.CapacityRecoveryCoordinatorInstanceId == peer.InstanceId
            && (string.IsNullOrWhiteSpace(xagmanCapacityRecoveryCoordinatorInstanceId)
                || peer.InstanceId == xagmanCapacityRecoveryCoordinatorInstanceId));
        if (tony == null)
        {
            if (xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.None) return false;
            if (xagmanCapacityMissingPeerSinceUtc == DateTime.MinValue) xagmanCapacityMissingPeerSinceUtc = DateTime.UtcNow;
            if ((DateTime.UtcNow - xagmanCapacityMissingPeerSinceUtc).TotalSeconds > 120)
                FailXagmanCapacityRecovery("The bound capacity-recovery Tony disconnected; saved collection and partial supply work remain pending.");
            return true;
        }
        xagmanCapacityMissingPeerSinceUtc = DateTime.MinValue;
        if (xagmanCapacityRecoveryStage != XagmanCapacityRecoveryStage.None
            && tony.CapacityRecoveryRegion != xagmanCapacityRecoveryRegion)
        {
            FailXagmanCapacityRecovery("Tony changed the capacity-recovery region without a new saved collection checkpoint.");
            return true;
        }
        if (tony.Status == XagmanStatus.Error && xagmanCapacityRecoveryStage != XagmanCapacityRecoveryStage.None)
        {
            FailXagmanCapacityRecovery($"Tony capacity recovery stopped: {tony.StatusText}");
            return true;
        }
        if (xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.None)
        {
            if (string.IsNullOrWhiteSpace(tony.CapacityDrainId) || tony.CapacityRecoveryEpoch <= xagmanCapacityRecoveryEpoch) return false;
            xagmanCapacityTransitionStartedUtc = DateTime.UtcNow;
            xagmanCapacityRecoveryCoordinatorInstanceId = tony.InstanceId;
            xagmanCapacityRecoveryRegion = tony.CapacityRecoveryRegion;
            xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.OwnerWaitingForDrain;
            xagmanCapacitySafeSinceUtc = DateTime.MinValue;
        }
        if (xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.OwnerWaitingForDrain)
        {
            if (!xagmanCapacityDrainReady)
            {
                if (string.IsNullOrWhiteSpace(tony.CapacityDrainId) || tony.CapacityRecoveryEpoch <= xagmanCapacityRecoveryEpoch)
                {
                    FailXagmanCapacityRecovery("Tony changed the capacity-recovery directive before this owner saved collection progress.");
                    return true;
                }
                if (!IsXagmanCapacityTransitionSafe())
                {
                    CheckXagmanCapacityTransitionTimeout("reach a safe native-action boundary before pausing collection");
                    return true;
                }
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "save collection before capacity drain")) return true;
                // Reconcile the last outgoing inventory delta before a drain can add stock
                // to this same owner and obscure progress against its original Give goal.
                if (IsXagmanCurrentLocalCharacter(xagmanActiveCharacter))
                {
                    BuildXagmanPendingGiveItems(xagmanActiveCharacter);
                    if (xagmanStatus == XagmanStatus.Error) return true;
                }
                SaveXagmanCapacityOwnerCheckpoint();
                if (plugin.TaskRunner.IsRunning) plugin.TaskRunner.Cancel();
                xagmanCapacityDrainId = tony.CapacityDrainId;
                xagmanCapacityRecoveryEpoch = tony.CapacityRecoveryEpoch;
                xagmanCapacityDrainReady = true;
                xagmanCapacityCollectionRestored = false;
                xagmanCapacityDrainFilledOwners.Clear();
                ClearXagmanCapacityOwnerTraversal();
                xagmanOwnerRunPlan = xagmanCollectionFirstRestockPlan.Where(owner => GetXagmanRegionOfChar(owner)
                    .Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase)).ToList();
                xagmanOwnerRunList = xagmanOwnerRunPlan.ToList();
                xagmanOwnerCurrentCharacterIndex = 0;
                xagmanOwnerTotalCharacters = xagmanOwnerRunPlan.Count;
                xagmanOwnerCompletedCharacters = 0;
                xagmanOwnerSupplyRegion = xagmanCapacityRecoveryRegion;
                xagmanPhaseComplete = false;
                xagmanOwnerSweepPendingDataCenter = xagmanOwnerRunPlan.Select(GetXagmanDataCenterOfChar).FirstOrDefault() ?? string.Empty;
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = $"Collection progress saved; preparing {xagmanOwnerRunPlan.Count} owner(s) to receive supplies from stocked Tonys.";
                plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
                PublishXagmanPresence();
            }
            if (tony.CapacityDrainId != xagmanCapacityDrainId || tony.CapacityRecoveryEpoch != xagmanCapacityRecoveryEpoch
                || !tony.CapacityDrainReady || string.IsNullOrWhiteSpace(tony.SupplyPassId))
            {
                CheckXagmanCapacityTransitionTimeout("receive the acknowledged temporary supplier pass");
                return true;
            }
            xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.OwnerDraining;
            xagmanCapacitySafeSinceUtc = DateTime.MinValue;
            xagmanOwnerSupplyTonyInstance = tony.InstanceId;
            xagmanPreferredTonyCharacter = tony.ActiveCharacter;
            SetXagmanActiveMeetDestination(tony.MeetWorld, tony.MeetAetheryte);
            xagmanOwnerStartRequested = true;
            if (xagmanOwnerRunPlan.Count == 0)
            {
                xagmanOwnerSupplyPassId = tony.SupplyPassId;
                FinishXagmanOwnerSupplyPass();
                StartXagmanSupplyMenuWait();
            }
            else StartXagmanCapacityOwnerSteps(xagmanOwnerRunList, 0);
            return true;
        }
        if (xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.OwnerDraining)
        {
            if (tony.CapacityDrainId == xagmanCapacityDrainId && tony.CapacityRecoveryEpoch == xagmanCapacityRecoveryEpoch) return false;
            if (!string.IsNullOrWhiteSpace(tony.CapacityDrainId) || tony.CapacityRecoveryEpoch != xagmanCapacityRecoveryEpoch + 1
                || tony.SupplyPassId != xagmanOwnerSupplyPassId
                || !xagmanOwnerSupplyPassComplete || !xagmanOwnerSupplyAtMenu)
            {
                FailXagmanCapacityRecovery("Tony attempted to restore collection without this owner's exact completed supplier pass.");
                return true;
            }
            if (xagmanCapacityRestoreRequestedEpoch != tony.CapacityRecoveryEpoch)
            {
                xagmanCapacityRestoreRequestedEpoch = tony.CapacityRecoveryEpoch;
                xagmanCapacityTransitionStartedUtc = DateTime.UtcNow;
                xagmanCapacitySafeSinceUtc = DateTime.MinValue;
            }
            if (!IsXagmanCapacityTransitionSafe() || plugin.TaskRunner.IsRunning)
            {
                CheckXagmanCapacityTransitionTimeout("finish the supplier pass safely before restoring collection");
                return true;
            }
            if (!RestoreXagmanCapacityOwnerCheckpoint()) return true;
            xagmanCapacityDrainId = string.Empty;
            xagmanCapacityRecoveryEpoch = tony.CapacityRecoveryEpoch;
            xagmanCapacityDrainReady = false;
            xagmanCapacityCollectionRestored = true;
            xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.OwnerWaitingForCollectionRelease;
            xagmanCapacityTransitionStartedUtc = DateTime.UtcNow;
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = "Collection cursor restored; waiting for every owner acknowledgement before collection resumes.";
            PublishXagmanPresence();
            return true;
        }
        if (xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.OwnerWaitingForCollectionRelease)
        {
            if (!string.IsNullOrWhiteSpace(tony.CapacityDrainId) || tony.CapacityRecoveryEpoch != xagmanCapacityRecoveryEpoch
                || !tony.CapacityCollectionRestored)
            {
                CheckXagmanCapacityTransitionTimeout("receive the collection-resume acknowledgement");
                return true;
            }
            xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.None;
            xagmanCapacityOwnerCheckpoint = null;
            xagmanPreferredTonyCharacter = tony.ActiveCharacter;
            xagmanOwnerStartRequested = true;
            SetXagmanActiveMeetDestination(tony.MeetWorld, tony.MeetAetheryte);
            if (!xagmanPhaseComplete && xagmanOwnerCurrentCharacterIndex < xagmanOwnerRunList.Count)
                StartXagmanCapacityOwnerSteps(xagmanOwnerRunList, Math.Max(0, xagmanOwnerCurrentCharacterIndex));
            else PublishXagmanPresence();
            return true;
        }
        return false;
    }

    private void SaveXagmanCapacityOwnerCheckpoint()
    {
        xagmanCapacityOwnerCheckpoint = new XagmanCapacityOwnerCheckpoint
        {
            Plan = xagmanOwnerRunPlan.ToList(), RunList = xagmanOwnerRunList.ToList(),
            Index = Math.Max(0, xagmanOwnerCurrentCharacterIndex), Total = xagmanOwnerTotalCharacters,
            Completed = xagmanOwnerCompletedCharacters, PhaseTotal = xagmanPhaseTotalCharacters,
            PhaseResolved = xagmanPhaseResolvedCharacters, PhaseComplete = xagmanPhaseComplete,
            Character = xagmanActiveCharacter, PendingDataCenter = xagmanOwnerSweepPendingDataCenter,
            ActivePendingGiveItems = IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)
                ? CloneXagmanTradeRequests(BuildXagmanPendingGiveItems(xagmanActiveCharacter)) : new(),
        };
    }

    private bool RestoreXagmanCapacityOwnerCheckpoint()
    {
        var checkpoint = xagmanCapacityOwnerCheckpoint;
        if (checkpoint == null) { FailXagmanCapacityRecovery("Saved collection cursor is missing; automatic resume was withheld."); return false; }
        ClearXagmanCapacityOwnerTraversal();
        xagmanOwnerRunPlan = checkpoint.Plan.ToList();
        xagmanOwnerRunList = checkpoint.RunList.ToList();
        xagmanOwnerCurrentCharacterIndex = checkpoint.Index;
        xagmanOwnerTotalCharacters = checkpoint.Total;
        xagmanOwnerCompletedCharacters = checkpoint.Completed;
        xagmanPhaseTotalCharacters = checkpoint.PhaseTotal;
        xagmanPhaseResolvedCharacters = checkpoint.PhaseResolved;
        xagmanPhaseComplete = checkpoint.PhaseComplete;
        xagmanActiveCharacter = checkpoint.Character;
        xagmanOwnerSweepPendingDataCenter = checkpoint.PendingDataCenter;
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = checkpoint.Completed;
        // Finite Give/Take ledgers, partial requests and all three frozen plans are deliberately retained.
        return true;
    }

    private void ClearXagmanCapacityOwnerTraversal()
    {
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanOwnerStandbyPending = xagmanOwnerPauseForTonyRotationRequested = false;
        xagmanOwnerStandbyTonyCharacter = xagmanOwnerStandbyTonyInstanceId = string.Empty;
        xagmanOwnerStandbyPriorTonyCallReleased = true;
        xagmanQueueRequestedAtUtc = xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
        xagmanOwnerSupplyPassId = string.Empty;
        xagmanOwnerSupplyPassComplete = xagmanOwnerSupplyWaiting = xagmanOwnerSupplyAtMenu = xagmanOwnerSupplyContinuation = false;
        xagmanSupplyVisitedOwners.Clear();
        xagmanSupplyOwnerStockAtVisit.Clear();
        ResetXagmanTravelFailureMonitor();
        ClearXagmanExpectedTravelLogoutWindow();
    }

    private void StartXagmanCapacityOwnerSteps(IReadOnlyList<string> owners, int index)
    {
        var steps = BuildXagmanFranchiseSteps(owners.ToList(), index);
        plugin.TaskRunner.Start("Xagman", steps, onFinished: OnXagmanFranchiseTaskFinished,
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), preserveRunHistory: true);
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = GetXagmanLocalOwnerCompletedCharacters();
    }

    private void StartXagmanCapacityTony(string key)
    {
        var entry = plugin.Configuration.XagmanTonyCharacters.FirstOrDefault(candidate => candidate.CharacterNameWorld.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry { CharacterNameWorld = key, Mode = xagmanTonyMode };
        StartXagmanTonyStartup(entry, true);
    }

    private bool IsXagmanCapacityTransitionSafe()
    {
        var safe = (!plugin.TaskRunner.IsRunning || plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            && !plugin.IpcClient.DropboxIsBusy() && !AddonHelper.IsAddonVisible("Trade")
            && !AddonHelper.IsAddonVisible("SelectYesno") && !AddonHelper.IsAddonVisible("SelectOk")
            && !plugin.IpcClient.LifestreamIsBusy() && !plugin.IpcClient.VnavPathIsRunning()
            && !plugin.IpcClient.VnavSimpleMovePathfindInProgress()
            && (Plugin.ClientState.IsLoggedIn ? CharacterSafetyHelper.IsCharacterSafeWaitReady()
                : AddonHelper.IsAddonReady("_TitleMenu") && AddonHelper.IsAddonVisible("_TitleMenu"));
        if (!safe) { xagmanCapacitySafeSinceUtc = DateTime.MinValue; return false; }
        if (xagmanCapacitySafeSinceUtc == DateTime.MinValue) xagmanCapacitySafeSinceUtc = DateTime.UtcNow;
        return (DateTime.UtcNow - xagmanCapacitySafeSinceUtc).TotalSeconds >= 2;
    }

    private void CheckXagmanCapacityTransitionTimeout(string action)
    {
        if ((DateTime.UtcNow - xagmanCapacityTransitionStartedUtc).TotalSeconds > XagmanCapacityTransitionTimeoutSeconds)
            FailXagmanCapacityRecovery($"Collection capacity recovery timed out waiting for the frozen owner clients to {action}; saved work remains pending.");
    }

    private void CaptureXagmanCapacityInitialStock()
    {
        if (string.IsNullOrWhiteSpace(xagmanActiveCharacter) || xagmanCapacityInitialStock.ContainsKey(xagmanActiveCharacter)
            || !IsXagmanCurrentLocalCharacter(xagmanActiveCharacter) || plugin.IpcClient.DropboxIsBusy()
            || AddonHelper.IsAddonVisible("Trade") || !TryCaptureXagmanSupplyInventory(out var stock)) return;
        xagmanCapacityInitialStock[xagmanActiveCharacter] = CaptureXagmanSupplyStock(stock);
    }

    // Called only after the ordinary exact-pass, full-cohort, main-menu and useful-recall barriers.
    // False permits another Tony's ordinary supply pass. True owns terminal drain handling.
    private bool TryFinishXagmanCapacityRecoverySupplyPass(IReadOnlyList<XagmanPeerPresence> owners)
    {
        if (!IsXagmanCapacityDrainActive() || xagmanActiveRole != XagmanRole.Tony) return false;
        if (xagmanCapacityRecoveryStage != XagmanCapacityRecoveryStage.Draining) return true;
        if (owners.Any(peer => peer.CapacityDrainId != xagmanCapacityDrainId
            || peer.CapacityRecoveryEpoch != xagmanCapacityRecoveryEpoch
            || peer.CapacityRecoveryCoordinatorInstanceId != plugin.InstanceId
            || peer.SupplyPassId != xagmanSupplyPassId || !peer.SupplyPassComplete)) return true;
        if (!IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)
            || !TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots)
            || !TryCaptureXagmanSupplyInventory(out var stock)
            || !TryGetXagmanLiveGil(out var currentGil))
        {
            FailXagmanCapacityRecovery($"Tony {xagmanActiveCharacter}'s inventory could not be verified after its capacity drain; collection was not resumed.");
            return true;
        }
        var after = CaptureXagmanSupplyStock(stock);
        var madeProgress = xagmanCapacityInitialStock.TryGetValue(xagmanActiveCharacter, out var before)
            && before.Any(item => after.GetValueOrDefault(item.Key) < item.Value);
        var capacityRecovered = (!xagmanCapacityBlockedOnItems || freeSlots > 0)
            && (!xagmanCapacityBlockedOnGil || currentGil < XagmanTonySellGilLimit);
        if (madeProgress && capacityRecovered) xagmanCapacityRecoveredTonys.Add(xagmanActiveCharacter);
        var nextTony = xagmanTonyRunList.Any(key => !key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
            && !plugin.TaskRunner.FailedCharacters.Contains(key)
            && GetXagmanRegionOfChar(key).Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase));
        var pendingSupply = owners.Any(peer => peer.SupplyPendingDataCenters.Any(dc => string.Equals(
                WorldData.GetRegionOfDataCenter(dc), xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase))
            || peer.PartialOwners.Any(state => state.RequestedItems.Count > 0
                && GetXagmanRegionOfChar(state.CharacterNameWorld).Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase)));
        if (nextTony && pendingSupply) return false;
        if (xagmanCapacityRecoveredTonys.Count == 0)
        {
            var partialDetails = owners.SelectMany(peer => peer.PartialOwners)
                .Select(state => $"{state.CharacterNameWorld}: {GetXagmanPartialOwnerDetail(state)}").ToList();
            var detail = partialDetails.Count == 0 ? "No requested outgoing supply freed usable receiving capacity."
                : "Unfilled supplies: " + string.Join("; ", partialDetails);
            var tonys = string.Join(", ", xagmanTonyRunPlan.Where(key => GetXagmanRegionOfChar(key)
                .Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase)));
            xagmanCapacityWarningReason = $"Collection capacity recovery exhausted the stocked Tonys in {xagmanCapacityRecoveryRegion}: {tonys}. {detail} Unfinished collection and supply work remain in the warning results.";
            xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.Completing;
            plugin.TaskRunner.AddLog($"Xagman: completion warning: {xagmanCapacityWarningReason}");
            StartXagmanTonyCompletionTask(string.Empty, completedWithWarnings: true, broadcastPeerCompletion: true);
            return true;
        }
        xagmanTonyRunList = xagmanTonyRunPlan.Where(key => !plugin.TaskRunner.FailedCharacters.Contains(key)
            && (xagmanCapacityRecoveredTonys.Contains(key) || (xagmanCapacityCollectionTonyRemainder.Contains(key, StringComparer.OrdinalIgnoreCase)
                && !GetXagmanRegionOfChar(key).Equals(xagmanCapacityRecoveryRegion, StringComparison.OrdinalIgnoreCase)))).ToList();
        xagmanTonyCompletedCharacters = Math.Max(0, xagmanTonyRunPlan.Count - xagmanTonyRunList.Count);
        xagmanCurrentTonyIndex = 0;
        xagmanPhaseTotalCharacters = xagmanCapacityCollectionPhaseTotal;
        xagmanPhaseResolvedCharacters = xagmanCapacityCollectionPhaseResolved;
        xagmanSweepRegion = xagmanCapacityRecoveryRegion;
        xagmanSweepDataCenter = xagmanCapacityCollectionDataCenter;
        xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
        xagmanCapacityDrainId = string.Empty;
        xagmanCapacityRecoveryEpoch++;
        xagmanCapacityDrainReady = xagmanCapacityCollectionRestored = false;
        xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.WaitingForOwnerRestore;
        xagmanCapacityTransitionStartedUtc = DateTime.UtcNow;
        xagmanCapacitySafeSinceUtc = DateTime.MinValue;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = $"Supply deliveries recovered capacity on {xagmanCapacityRecoveredTonys.Count} Tony(s); waiting for saved collection cursors to be restored.";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
        return true;
    }

    private void FailXagmanCapacityRecovery(string reason)
    {
        xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.Failed;
        xagmanCapacityDrainReady = false;
        xagmanCapacityCollectionRestored = false;
        FailXagmanSupplyCycle(reason);
    }

    private bool MatchesXagmanCapacityWarningCompletion(XagmanPeerMessage directive)
        => IsXagmanCollectionFirstRunActive() && xagmanRunPhase == XagmanRunPhase.Collection
            && xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.OwnerDraining
            && IsXagmanCapacityDrainActive()
            && directive.RunPhase == XagmanRunPhase.Collection
            && directive.CapacityDrainId == xagmanCapacityDrainId
            && directive.CapacityRecoveryEpoch == xagmanCapacityRecoveryEpoch
            && directive.SenderInstanceId == xagmanCapacityRecoveryCoordinatorInstanceId
            && directive.SenderInstanceId == xagmanOwnerSupplyTonyInstance
            && !string.IsNullOrWhiteSpace(directive.SupplyPassId)
            && directive.SupplyPassId == xagmanOwnerSupplyPassId
            && xagmanExpectedFranchiseOwnerInstanceIds.SetEquals(directive.ExpectedFranchiseOwnerInstanceIds)
            && xagmanOwnerSupplyPassComplete && xagmanOwnerSupplyAtMenu
            && !plugin.TaskRunner.IsRunning && !Plugin.ClientState.IsLoggedIn
            && AddonHelper.IsAddonReady("_TitleMenu") && AddonHelper.IsAddonVisible("_TitleMenu")
            && !AddonHelper.IsAddonVisible("Trade") && !AddonHelper.IsAddonVisible("SelectYesno")
            && !AddonHelper.IsAddonVisible("SelectOk") && !plugin.IpcClient.DropboxIsBusy()
            && !plugin.IpcClient.LifestreamIsBusy();

    private bool PrepareXagmanCapacityOwnerWarningCompletion()
    {
        var checkpoint = xagmanCapacityOwnerCheckpoint;
        if (checkpoint == null)
        {
            FailXagmanCapacityRecovery("Capacity warning cleanup cannot find the saved collection plan; incomplete work was not marked complete.");
            return false;
        }
        // A Give-only owner may never appear in the temporary receiver traversal. Preserve
        // its unfinished collection result, including the live remaining Give snapshot when
        // available, without inventing quantities for characters that were never logged in.
        foreach (var owner in checkpoint.RunList.Skip(checkpoint.Index))
        {
            if (xagmanOwnerCollectionCompletedKeys.Contains(owner) || plugin.TaskRunner.FailedCharacters.Contains(owner)
                || xagmanSkippedCharacters.Contains(owner)) continue;
            if (!xagmanPartialOwners.TryGetValue(owner, out var state))
            {
                state = new XagmanPartialOwnerState { CharacterNameWorld = owner };
                xagmanPartialOwners[owner] = state;
            }
            if (owner.Equals(checkpoint.Character, StringComparison.OrdinalIgnoreCase)
                && state.PendingGiveItems.Count == 0)
                state.PendingGiveItems = CloneXagmanTradeRequests(checkpoint.ActivePendingGiveItems);
            state.Reason = "collection unfinished because the selected Tonys could not recover receiving capacity";
            plugin.TaskRunner.AddLog($"Xagman: completion warning: {owner}: {GetXagmanPartialOwnerDetail(state)}");
        }
        xagmanOwnerRunPlan = xagmanCollectionFirstOwnerFullPlan.ToList();
        xagmanOwnerTotalCharacters = xagmanOwnerRunPlan.Count;
        xagmanCapacityRecoveryStage = XagmanCapacityRecoveryStage.Completing;
        xagmanPhaseComplete = false;
        return true;
    }
}
