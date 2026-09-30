using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int XagmanOwnerCompletionRevision = 1;
    private readonly Dictionary<string, string> xagmanCompletedOwnerClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> xagmanOwnerCompletionAckTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanOwnerCompletionDelivered = new(StringComparer.OrdinalIgnoreCase);
    private string xagmanOwnerCompletionToken = string.Empty;
    private bool xagmanOwnerCompletionAckReceived;
    private DateTime xagmanOwnerCompletionRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanOwnerCompletionReceivedAtUtc = DateTime.MinValue;

    private void ResetXagmanOwnerCompletion()
    {
        xagmanCompletedOwnerClients.Clear();
        xagmanOwnerCompletionAckTimes.Clear();
        xagmanOwnerCompletionDelivered.Clear();
        xagmanOwnerCompletionToken = string.Empty;
        xagmanOwnerCompletionAckReceived = false;
        xagmanOwnerCompletionRequestedAtUtc = xagmanOwnerCompletionReceivedAtUtc = DateTime.MinValue;
    }

    private bool IsXagmanOwnerClientCompleted(string instanceId) => xagmanCompletedOwnerClients.ContainsKey(instanceId);

    private bool TryFinishXagmanLocalOwnerRun()
    {
        if (!IsXagmanSupplyCycleActive() || xagmanActiveRole != XagmanRole.FranchiseOwner
            || IsXagmanCapacityRecoveryActive() || IsXagmanCapacityDrainActive() || xagmanTravelRouteFatalError || xagmanStatus == XagmanStatus.Error
            || xagmanOwnerStandbyPending || xagmanPartialOwners.Count > 0
            || !xagmanOwnerRunPlan.All(xagmanOwnerCompletedKeys.Contains)
            || plugin.TaskRunner.FailedCharacters.Count > 0 || xagmanSkippedCharacters.Count > 0
            || xagmanCollectionFirstFailedCharacters.Count > 0
            || plugin.IpcClient.DropboxIsBusy() || AddonHelper.IsAddonVisible("Trade")) return false;
        var tony = GetXagmanSupplyTonyPeer();
        // Older coordinators keep the established connected pass barrier.
        if (tony == null || !IsXagmanPeerFresh(tony) || tony.OwnerCompletionRevision != XagmanOwnerCompletionRevision
            || string.IsNullOrWhiteSpace(xagmanOwnerSupplyPassId)) return false;
        if (string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken))
        {
            xagmanOwnerCompletionToken = Guid.NewGuid().ToString("N");
            xagmanOwnerCompletionRequestedAtUtc = DateTime.UtcNow;
        }
        xagmanOwnerSupplyWaiting = true;
        xagmanOwnerSupplyContinuation = false;
        xagmanOwnerSupplyPassComplete = true;
        xagmanOwnerSweepPendingDataCenter = string.Empty;
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
        if (IsXagmanCollectionFirstRestockPhase())
        {
            FinalizeXagmanCollectionFirstOwnerSelections();
            xagmanPhaseResolvedCharacters = xagmanPhaseTotalCharacters;
            xagmanPhaseComplete = true;
        }
        xagmanOwnerCompletedCharacters = xagmanOwnerCompletedKeys.Count;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = "All local owners completed; waiting for Tony to acknowledge this client's completion before disconnecting.";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
        return true;
    }

    private bool UpdateXagmanLocalOwnerCompletion()
    {
        if (string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken)) return false;
        // A final coordinator can already be in cleanup while relaying its retained ack.
        var tony = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.InstanceId == xagmanOwnerSupplyTonyInstance
            && peer.Role == XagmanRole.Tony);
        if (!xagmanOwnerCompletionAckReceived && tony != null && IsXagmanPeerFresh(tony)
            && tony.InstanceId == xagmanOwnerSupplyTonyInstance && IsXagmanPeerInCurrentRunPhase(tony)
            && tony.OwnerCompletionRevision == XagmanOwnerCompletionRevision
            && tony.OwnerCompletionAcknowledgements.TryGetValue(plugin.InstanceId, out var token)
            && token == xagmanOwnerCompletionToken)
        {
            xagmanOwnerCompletionAckReceived = true;
            xagmanOwnerCompletionReceivedAtUtc = DateTime.UtcNow;
            plugin.TaskRunner.AddLog("Xagman: Tony acknowledged all local owners complete; disconnecting before Task Completion.");
            PublishXagmanPresence();
        }
        if (xagmanOwnerCompletionAckReceived)
        {
            // Relay receipt before a hub-hosting FO stops its listener. The other local
            // clients can re-elect a hub without losing the coordinator's completion ledger.
            if ((DateTime.UtcNow - xagmanOwnerCompletionReceivedAtUtc).TotalSeconds >= 2.5)
                StartXagmanFranchiseCompletionTask("Xagman: all local owners completed successfully.", localOwnersFinished: true);
        }
        else if ((DateTime.UtcNow - xagmanOwnerCompletionRequestedAtUtc).TotalSeconds > 120)
            FailXagmanSupplyCycle("Tony did not acknowledge this client's completed owner run; kept the connection and withheld Task Completion.");
        return true;
    }

    private void ObserveXagmanOwnerClientCompletions()
    {
        if (!IsXagmanSupplyCycleActive() || xagmanActiveRole != XagmanRole.Tony
            || IsXagmanCapacityRecoveryActive() || IsXagmanCapacityDrainActive() || xagmanStatus == XagmanStatus.Error) return;
        var changed = false;
        foreach (var peer in plugin.XagmanPeers.Peers)
        {
            if (peer.Role != XagmanRole.FranchiseOwner || !IsXagmanPeerFresh(peer)
                || peer.OwnerCompletionRevision != XagmanOwnerCompletionRevision
                || !IsXagmanPeerInCurrentRunPhase(peer) || peer.SupplyCoordinatorInstanceId != plugin.InstanceId
                || !xagmanSupplyRunPassIds.Contains(peer.SupplyPassId)
                || !xagmanSupplyOwnerCohort.Contains(peer.InstanceId)
                || string.IsNullOrWhiteSpace(peer.OwnerCompletionToken)) continue;
            if (xagmanCompletedOwnerClients.TryGetValue(peer.InstanceId, out var priorToken))
            {
                if (priorToken == peer.OwnerCompletionToken && peer.OwnerCompletionAckReceived)
                    xagmanOwnerCompletionDelivered.Add(peer.InstanceId);
                continue;
            }
            if (!peer.XagmanEnabled || peer.Status != XagmanStatus.Paused || !peer.SupplyPassComplete
                || peer.TotalCharacters < 0 || peer.CompletedCharacters != peer.TotalCharacters
                || peer.PartialOwners.Count > 0 || peer.RequestedItems.Count > 0
                || peer.SupplyPendingDataCenters.Count > 0 || peer.QueueRequestedAtUtc != DateTime.MinValue
                || !string.IsNullOrWhiteSpace(peer.ActiveTradePartner)
                || (IsXagmanCollectionFirstRestockPhase() && !peer.PhaseComplete)) continue;
            xagmanCompletedOwnerClients.Add(peer.InstanceId, peer.OwnerCompletionToken);
            xagmanOwnerCompletionAckTimes.Add(peer.InstanceId, DateTime.UtcNow);
            if (IsXagmanCollectionFirstRestockPhase()) xagmanRestockPhaseAcknowledgedInstanceIds.Add(peer.InstanceId);
            foreach (var owner in xagmanSupplyPartialOwnerInstances.Where(entry => entry.Value == peer.InstanceId).Select(entry => entry.Key).ToList())
            {
                xagmanPartialOwners.Remove(owner);
                xagmanSupplyPartialOwnerInstances.Remove(owner);
            }
            if (xagmanActiveTradePartnerInstanceId == peer.InstanceId)
                xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
            plugin.TaskRunner.AddLog($"Xagman: owner client {peer.InstanceId} completed all {peer.TotalCharacters} local owner(s); acknowledged independent disconnect and Task Completion.");
            changed = true;
        }
        if (changed) PublishXagmanPresence();
    }

    private bool HoldXagmanFinalCompletionDelivery()
    {
        foreach (var entry in xagmanOwnerCompletionAckTimes)
        {
            if (xagmanOwnerCompletionDelivered.Contains(entry.Key)) continue;
            var elapsed = (DateTime.UtcNow - entry.Value).TotalSeconds;
            // Missing peers can mean hub reconnection, never proof that an ack arrived.
            if (elapsed > 60)
                FailXagmanSupplyCycle("Tony could not confirm delivery of a completed owner's acknowledgement; Tony remains connected for recovery.");
            return true;
        }
        return false;
    }
}
