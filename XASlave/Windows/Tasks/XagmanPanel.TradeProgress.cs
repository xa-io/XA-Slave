using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using XASlave.Data;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class XagmanTradeWaitState
    {
        internal readonly XagmanTradeProgressMonitor Monitor = new();
        internal readonly string Session = Guid.NewGuid().ToString("N");
        internal readonly HashSet<(uint ItemId, bool IsHq)> Watched = new();
        internal long RunId;
        internal int Step;
        internal ulong ContentId;
        internal string Scope = string.Empty;
        internal string Partner = string.Empty;
        internal string PartnerInstance = string.Empty;
        internal bool Outgoing;
        internal float LastElapsed;
        internal float GraceSeconds;
        internal float UnboundSince;
        internal float SettlementSince;
        internal float? StopStartedAt;
        internal float FinalReconciledAt;
        internal DateTime QuietSince;
        internal DateTime LastScanUtc;
        internal object? Provider;
        internal bool Settling;
        internal bool Settled;
        internal bool Completed;
        internal bool CaptureFailed;
        internal bool UsingLocalInventory;
        internal bool ReportedSnapshotFailure;
        internal bool FinalReconciled;
        internal bool PeerReadyToSettle;
        internal string RemoteSession = string.Empty;
    }

    private XagmanTradeWaitState? xagmanTradeWait;
    private XagmanTradeProgressPresence? xagmanSettledTradeProgress;
    private DateTime xagmanSettledTradeProgressExpiresUtc;

    private Func<float, bool> CreateXagmanTradeTimeout(bool outgoing)
    {
        XagmanTradeWaitState? state = null;
        var graceSeconds = XagmanTradeBatchTimeoutSeconds;
        var waitForQueue = CreateXagmanQueueTimeout(3600f);
        var waitingForOtherOwner = false;
        return elapsed =>
        {
            if (plugin.Configuration.XagmanOutsideNetworkHelper) return elapsed > 3600f;
            if (state == null || state.RunId != plugin.TaskRunner.CurrentRunId
                || state.Step != plugin.TaskRunner.CurrentStep || elapsed < state.LastElapsed
                || state.Scope != GetXagmanTradeCapacityScope() || state.ContentId != Plugin.PlayerState.ContentId
                || state.Partner != xagmanActiveTradePartner || state.PartnerInstance != xagmanActiveTradePartnerInstanceId)
            {
                state = new XagmanTradeWaitState
                {
                    RunId = plugin.TaskRunner.CurrentRunId, Step = plugin.TaskRunner.CurrentStep,
                    ContentId = Plugin.PlayerState.ContentId, Scope = GetXagmanTradeCapacityScope(),
                    Partner = xagmanActiveTradePartner, PartnerInstance = xagmanActiveTradePartnerInstanceId,
                    Outgoing = outgoing, UnboundSince = elapsed, GraceSeconds = graceSeconds,
                };
                foreach (var entry in plugin.Configuration.XagmanItems.Where(entry => entry.ItemId > 0))
                    state.Watched.Add((entry.ItemId, entry.IsHq));
                foreach (var entry in xagmanOwnerRequestedItems.Where(entry => entry.ItemId > 0))
                    state.Watched.Add((entry.ItemId, entry.IsHq));
                if (plugin.DropboxQueue.TryCaptureXagmanQueue(out var queue, out _))
                    foreach (var entry in queue) state.Watched.Add((entry.ItemId, entry.IsHq));
                plugin.DropboxQueue.TryCaptureXagmanProviderIdentity(out state.Provider, out _);
            }
            xagmanTradeWait = state;
            state.LastElapsed = elapsed;
            if (state.Settled) return !state.Completed;
            if (state.Settling) return false;

            XagmanPeerPresence? peer = null;
            var active = TryGetXagmanTradeCapacityLocal(out var local)
                && TryGetXagmanTradeCapacityConnectedPeer(local, out peer, out _);
            ObserveXagmanPeerTradeSettlement(state, active ? peer : null);
            if (state.Settling) return false;
            state.Monitor.Tick(elapsed, active);
            if (!active)
            {
                // A fresh Tony serving another owner is ordinary queue waiting.
                var tony = xagmanActiveRole == XagmanRole.FranchiseOwner ? GetXagmanLiveTonyPeer() : null;
                if (tony != null && IsXagmanPeerFresh(tony) && tony.Status is (XagmanStatus.Trading or XagmanStatus.Called)
                    && !string.IsNullOrWhiteSpace(tony.ActiveTradePartner)
                    && tony.ActiveTradePartnerInstanceId != plugin.InstanceId)
                {
                    if (!waitingForOtherOwner) waitForQueue = CreateXagmanQueueTimeout(3600f);
                    waitingForOtherOwner = true;
                    state.UnboundSince = elapsed;
                    if (!waitForQueue(elapsed)) return false;
                }
                else
                {
                    waitingForOtherOwner = false;
                    if (elapsed - state.UnboundSince < 120f) return false;
                }
                BeginXagmanTradeTimeoutSettlement(state, captureFailed: true);
                return false;
            }
            waitingForOtherOwner = false;
            state.UnboundSince = elapsed;
            if (!state.Monitor.ScanDue) return false;
            if (state.Provider == null && !plugin.DropboxQueue.TryCaptureXagmanProviderIdentity(out state.Provider, out _))
            {
                HoldXagmanTradeProgress("The original Dropbox provider is unavailable.");
                return false;
            }
            var snapshot = CaptureXagmanTradeProgressSnapshot(state);
            if (snapshot != null && plugin.Configuration.XagmanItems.Any(entry => IsXagmanGreenValueSelector(entry.SelectorKind)))
                foreach (var key in snapshot.Quantities.Keys.Where(key => key.ItemId > 19)) state.Watched.Add(key);
            state.Monitor.Observe(snapshot, state.GraceSeconds, state.Outgoing, state.Watched);
            if (snapshot != null) state.LastScanUtc = DateTime.UtcNow;
            plugin.TaskRunner.AddLog($"Xagman: trade inventory scan {state.Monitor.ScanSequence}, active {state.Monitor.ActiveSeconds:0}s, progress {state.Monitor.ProgressSequence}, unchanged {state.Monitor.IdleScans}/5, failed captures {state.Monitor.FailedScans}/5.");
            PublishXagmanPresence();
            if (state.Monitor.Stalled || state.Monitor.CaptureFailed)
                BeginXagmanTradeTimeoutSettlement(state, state.Monitor.CaptureFailed);
            return false;
        };
    }

    private unsafe XagmanTradeInventorySnapshot? CaptureXagmanTradeProgressSnapshot(XagmanTradeWaitState state)
    {
        bool CurrentCharacter() => state.ContentId != 0 && Plugin.PlayerState.IsLoaded
            && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.ContentId == state.ContentId
            && Plugin.ObjectTable.LocalPlayer != null && !Plugin.Condition[ConditionFlag.BetweenAreas]
            && !Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (!CurrentCharacter()) return null;
        if (state.UsingLocalInventory && CaptureLocal() is { } live)
            return CurrentCharacter() ? live : null;
        var requestedAt = DateTime.UtcNow;
        var json = plugin.IpcClient.GetTradeInventorySnapshotJson(state.ContentId, out var error);
        if (XagmanTradeInventorySnapshot.TryRead(json, state.ContentId, requestedAt, out var snapshot))
        {
            if (!CurrentCharacter()) return null;
            if (state.UsingLocalInventory) state.Monitor.ResetBaseline();
            state.UsingLocalInventory = false;
            return snapshot;
        }
        if (!state.ReportedSnapshotFailure)
        {
            plugin.TaskRunner.AddLog($"Xagman: XA Database trade snapshot unavailable ({error ?? "empty or invalid snapshot"}); checking live local inventory.");
            state.ReportedSnapshotFailure = true;
        }
        var result = state.UsingLocalInventory ? null : CaptureLocal();
        if (result == null || !CurrentCharacter()) return null;
        if (!state.UsingLocalInventory) state.Monitor.ResetBaseline();
        state.UsingLocalInventory = true;
        return result;

        XagmanTradeInventorySnapshot? CaptureLocal()
        {
            var manager = InventoryManager.Instance();
            if (manager == null || !NativeArrayAccess.TryGetInventoryContainer(manager, InventoryType.Currency, out var currency)
                || currency->Size <= 0 || !TryGetXagmanLiveGil(out var gil) || gil is < 0 or > 999999999) return null;
            var local = new XagmanTradeInventorySnapshot();
            local.Quantities[(1, false)] = gil;
            var green = plugin.Configuration.XagmanItems.Any(entry => IsXagmanGreenValueSelector(entry.SelectorKind));
            IEnumerable<InventoryType> containers = Array.Empty<InventoryType>();
            if (green) containers = XagmanGreenValueInventoryTypes;
            else if (state.Watched.Any(key => key.ItemId > 19)) containers = XagmanMainInventoryTypes;
            if (state.Watched.Any(key => key.ItemId is >= 2 and <= 19))
                containers = containers.Concat(XagmanCrystalInventoryTypes);
            foreach (var type in containers)
            {
                if (!NativeArrayAccess.TryGetInventoryContainer(manager, type, out var bag) || bag->Size <= 0) return null;
                for (var i = 0; i < bag->Size; i++)
                {
                    if (!NativeArrayAccess.TryGetInventorySlot(bag, i, out var item)) return null;
                    if (item->ItemId == 0 || item->IsSymbolic) continue;
                    var key = (item->GetBaseItemId(), item->IsHighQuality());
                    local.Quantities[key] = local.Quantities.GetValueOrDefault(key) + item->Quantity;
                }
            }
            return local;
        }
    }

    private void BeginXagmanTradeTimeoutSettlement(XagmanTradeWaitState state, bool captureFailed, bool peerRequested = false)
    {
        state.Settling = true;
        state.CaptureFailed = captureFailed;
        state.SettlementSince = state.LastElapsed;
        plugin.TaskRunner.AddLog(peerRequested ? "Xagman: the trading partner requested settlement of the current transfer." : captureFailed
            ? "Xagman: inventory capture or reciprocal trade ownership is unavailable; settling the current transfer before holding."
            : "Xagman: five fresh inventory scans showed no trade progress; settling the current transfer before timeout.");
        PublishXagmanPresence();
    }

    private void ObserveXagmanPeerTradeSettlement(XagmanTradeWaitState state, XagmanPeerPresence? peer)
    {
        var remote = peer?.TradeProgress;
        if (peer == null || remote == null || string.IsNullOrEmpty(remote.Session)
            || remote.Character != state.Partner || remote.PartnerInstanceId != plugin.InstanceId
            || remote.Scope != state.Scope || remote.Outgoing == state.Outgoing) return;
        // A receipt from the preceding Give/Take step cannot bind the next transfer.
        if (!remote.Settled && !remote.Completed && (string.IsNullOrEmpty(remote.PartnerSession)
            || remote.PartnerSession == state.Session))
            state.RemoteSession = remote.Session;
        if (remote.Session != state.RemoteSession || remote.PartnerSession != state.Session) return;
        if (remote.Settling || remote.Settled || remote.Completed)
            state.PeerReadyToSettle = true;
        if (remote.Settling && !state.Settling)
            BeginXagmanTradeTimeoutSettlement(state, captureFailed: false, peerRequested: true);
    }

    // Runs before the ordinary completion poll, so a stopped queue cannot falsely finish the batch.
    private bool TryPollXagmanTradeTimeoutSettlement(out bool completed)
    {
        completed = false;
        var state = xagmanTradeWait;
        if (state == null || state.RunId != plugin.TaskRunner.CurrentRunId || state.Step != plugin.TaskRunner.CurrentStep)
            return false;
        if (TryGetXagmanTradeCapacityLocal(out var local)
            && TryGetXagmanTradeCapacityConnectedPeer(local, out var peer, out _))
            ObserveXagmanPeerTradeSettlement(state, peer);
        if (!state.Settling) return false;
        completed = state.Completed;
        if (state.Settled) return true;
        if (state.ContentId != Plugin.PlayerState.ContentId || state.Scope != GetXagmanTradeCapacityScope()
            || state.Partner != xagmanActiveTradePartner || state.PartnerInstance != xagmanActiveTradePartnerInstanceId)
        {
            HoldXagmanTradeProgress("The trade identity changed before timeout cleanup settled.");
            return true;
        }
        if (state.Provider == null || !plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(state.Provider, out _))
        {
            HoldXagmanTradeProgress("Dropbox changed before timeout cleanup could settle the original provider.");
            return true;
        }
        if (state.FinalReconciled)
        {
            FinishXagmanTradeTimeoutSettlement(state);
            completed = state.Completed;
            return true;
        }
        // Tell the peer to enter settlement before stopping its observed queue.
        if (!state.PeerReadyToSettle && state.LastElapsed - state.SettlementSince < 15)
            return true;
        state.StopStartedAt ??= state.LastElapsed;
        var stopped = TryStopXagmanDropboxTradeQueue(preserveCapacityBatch: true);
        var acceptanceDisabled = TrySetXagmanDropboxAutoAccept(false);
        if (AddonHelper.IsAddonVisible("Trade")) TryAbortXagmanTradeWindow();
        var quiet = stopped && acceptanceDisabled && !plugin.IpcClient.DropboxIsBusy() && !AddonHelper.IsAddonVisible("Trade");
        if (!quiet)
        {
            state.QuietSince = default;
            if (state.LastElapsed - state.StopStartedAt.Value > 15)
                HoldXagmanTradeProgress("The trade did not settle within 15 seconds; progress is retained.");
            return true;
        }
        if (state.QuietSince == default) state.QuietSince = DateTime.UtcNow;
        if ((DateTime.UtcNow - state.QuietSince).TotalSeconds < 1) return true;
        var finalSnapshot = CaptureXagmanTradeProgressSnapshot(state);
        if (finalSnapshot == null)
        {
            HoldXagmanTradeProgress("The final live inventory snapshot is unavailable; progress is retained.");
            return true;
        }
        if (!ReconcileXagmanTradeBatchForTimeout()
            || !plugin.DropboxQueue.TryClearStoppedXagmanQueue(state.Provider, stopConfirmed: true, out _))
        {
            HoldXagmanTradeProgress("The stopped Dropbox selection could not be reconciled; progress is retained.");
            return true;
        }
        state.Monitor.Observe(finalSnapshot, state.GraceSeconds, state.Outgoing, state.Watched);
        state.LastScanUtc = DateTime.UtcNow;
        if (xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            if (state.Outgoing)
                state.Completed = HasXagmanOwnerCollectionTradeCompleted(plugin.Configuration.XagmanItems, xagmanActiveCharacter);
            else
            {
                var remaining = BuildXagmanOwnerTradeRequests(plugin.Configuration.XagmanItems, xagmanActiveCharacter, false);
                SetXagmanOwnerRequestedItemsIfChanged(remaining, xagmanActiveCharacter);
                state.Completed = remaining.Count == 0 && xagmanStatus != XagmanStatus.Error;
            }
        }
        state.FinalReconciled = true;
        state.FinalReconciledAt = state.LastElapsed;
        RetainXagmanTradeSettlementReceipt();
        PublishXagmanPresence();
        FinishXagmanTradeTimeoutSettlement(state);
        completed = state.Completed;
        return true;
    }

    private void FinishXagmanTradeTimeoutSettlement(XagmanTradeWaitState state)
    {
        if (!state.PeerReadyToSettle)
        {
            HoldXagmanTradeProgress("The peer did not acknowledge the current transfer's settlement; final inventory was retained.");
            return;
        }
        if (xagmanActiveRole == XagmanRole.Tony)
        {
            var owner = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.InstanceId == state.PartnerInstance);
            var progress = owner?.TradeSettlement;
            if (progress?.Session != state.RemoteSession) progress = owner?.TradeProgress;
            var acknowledged = owner != null && IsXagmanPeerFresh(owner) && IsXagmanPeerInCurrentRunPhase(owner)
                && IsXagmanPeerInSupplyCoordinatorScope(owner) && progress != null
                && !string.IsNullOrEmpty(state.RemoteSession) && progress.Session == state.RemoteSession
                && progress.PartnerSession == state.Session && progress.Outgoing != state.Outgoing
                && progress.Character == state.Partner && progress.PartnerInstanceId == plugin.InstanceId
                && progress.Scope == state.Scope && (progress.Settled || progress.Completed);
            if (!acknowledged)
            {
                if (state.LastElapsed - state.FinalReconciledAt > 15)
                    HoldXagmanTradeProgress("The owner did not confirm final inventory reconciliation within 15 seconds.");
                return;
            }
            state.Completed = progress!.Completed;
        }
        state.Settled = true;
        PublishXagmanPresence();
        plugin.TaskRunner.AddLog($"Xagman: final settled snapshot reconciled; local goals complete={state.Completed}, progress={state.Monitor.ProgressSequence}.");
        if (state.CaptureFailed && !state.Completed)
            HoldXagmanTradeProgress("Trade observation failed; final inventory was reconciled and remaining work is retained.");
    }

    private void HoldXagmanTradeProgress(string diagnostic)
    {
        plugin.TaskRunner.AddLog("Xagman: " + diagnostic);
        xagmanStatus = XagmanStatus.Error;
        var message = UiMessage.Key(UiLocale.Key("Trade monitoring stopped. Check XA Database and the trading partner, then resume the remaining work."));
        xagmanStatusText = UiMessage.Store(ref xagmanStatusTextUiMessage, message);
        plugin.TaskRunner.RequestHalt(message.Original);
        PublishXagmanPresence();
    }

    private Func<float, bool> CreateXagmanQueueTimeout(float timeoutSeconds)
    {
        var session = string.Empty;
        long sequence = 0;
        float lastProgressAt = 0;
        float previousElapsed = 0;
        var initialized = false;
        return elapsed =>
        {
            if (!initialized || elapsed < previousElapsed)
            {
                initialized = true;
                lastProgressAt = elapsed;
                session = string.Empty;
                sequence = 0;
            }
            previousElapsed = elapsed;
            var tony = GetXagmanLiveTonyPeer();
            var progress = tony?.TradeProgress;
            if (tony != null && progress != null && IsXagmanPeerFresh(tony)
                && tony.Status is (XagmanStatus.Trading or XagmanStatus.Called)
                && IsXagmanPeerInCurrentRunPhase(tony) && IsXagmanPeerInSupplyCoordinatorScope(tony)
                && progress.Scope == GetXagmanTradeCapacityPeerScope(tony)
                && progress.PartnerInstanceId == tony.ActiveTradePartnerInstanceId
                && progress.Character == tony.ActiveCharacter && progress.ScanSequence > 0
                && double.IsFinite(progress.ScanAgeSeconds) && progress.ScanAgeSeconds >= 0
                && progress.ScanAgeSeconds <= 75)
            {
                if (session != progress.Session) { session = progress.Session; sequence = 0; }
                if (progress.ProgressSequence > sequence)
                {
                    sequence = progress.ProgressSequence;
                    lastProgressAt = elapsed;
                }
            }
            return elapsed - lastProgressAt > timeoutSeconds;
        };
    }

    private XagmanTradeProgressPresence? GetXagmanTradeProgressPresence()
        => GetCurrentXagmanTradeProgressPresence();

    private XagmanTradeProgressPresence? GetXagmanTradeSettlementReceipt()
        => DateTime.UtcNow <= xagmanSettledTradeProgressExpiresUtc ? xagmanSettledTradeProgress?.Copy() : null;

    private XagmanTradeProgressPresence? GetCurrentXagmanTradeProgressPresence()
    {
        var state = xagmanTradeWait;
        if (state == null || !xagmanRunning || (!plugin.TaskRunner.IsRunning && !state.Completed)
            || state.RunId != plugin.TaskRunner.CurrentRunId || (state.Step != plugin.TaskRunner.CurrentStep && !state.Completed)
            || state.ContentId != Plugin.PlayerState.ContentId || state.Scope != GetXagmanTradeCapacityScope()
            || state.PartnerInstance != xagmanActiveTradePartnerInstanceId) return null;
        return new XagmanTradeProgressPresence
        {
            Session = state.Session, PartnerSession = state.RemoteSession, Outgoing = state.Outgoing,
            Scope = state.Scope, Character = xagmanActiveCharacter,
            PartnerInstanceId = state.PartnerInstance, ProgressSequence = state.Monitor.ProgressSequence,
            ScanSequence = state.Monitor.ScanSequence, ObservedAtUtc = state.LastScanUtc,
            ScanAgeSeconds = (DateTime.UtcNow - state.LastScanUtc).TotalSeconds,
            Completed = state.Completed,
            Settling = state.Settling, Settled = state.FinalReconciled,
        };
    }

    private void MarkXagmanTradeProgressComplete()
    {
        if (xagmanTradeWait is not { } state || state.RunId != plugin.TaskRunner.CurrentRunId
            || state.Step != plugin.TaskRunner.CurrentStep) return;
        state.Completed = true;
        RetainXagmanTradeSettlementReceipt();
        PublishXagmanPresence();
    }

    private void RetainXagmanTradeSettlementReceipt()
    {
        xagmanSettledTradeProgress = GetCurrentXagmanTradeProgressPresence();
        xagmanSettledTradeProgressExpiresUtc = DateTime.UtcNow.AddSeconds(20);
    }
}
