using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // This is a continuation of the live run, not a fresh Start. All item goals,
    // owner results and server/phase cursors remain owned by their existing fields.
    private enum XagmanNetworkRecoveryPhase { None, Preparing, StartingTony, AwaitingOwners, Held }
    private const int XagmanNetworkRecoveryRevision = 1;
    private const int XagmanNetworkFailureLimit = 3;
    private static readonly TimeSpan XagmanNetworkRecoveryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan XagmanNetworkRecoveryDeadline = TimeSpan.FromMinutes(20);
    private XagmanNetworkRecoveryPhase xagmanNetworkPhase;
    private string xagmanNetworkSession = string.Empty;
    private string xagmanNetworkCoordinator = string.Empty;
    private string xagmanNetworkIncident = string.Empty;
    private string xagmanNetworkLocalIncident = string.Empty;
    private int xagmanNetworkEpoch;
    private int xagmanNetworkFailureCount;
    private int xagmanNetworkReadyEpoch;
    private int xagmanNetworkResumeAckEpoch;
    private bool xagmanNetworkManualAttempt;
    private bool xagmanNetworkSendInFlight;
    private DateTime xagmanNetworkNotBeforeUtc;
    private DateTime xagmanNetworkStartedUtc;
    private DateTime xagmanNetworkLastSendUtc;
    private DateTime xagmanNetworkNextPollUtc;
    private DateTime xagmanNetworkLastLogUtc;
    private string xagmanNetworkHoldReason = string.Empty;
    private string xagmanNetworkLastWaitReason = string.Empty;
    private readonly HashSet<string> xagmanNetworkParticipants = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanNetworkSeenIncidents = new(StringComparer.Ordinal);
    private bool IsXagmanNetworkRecoveryHolding => xagmanNetworkPhase != XagmanNetworkRecoveryPhase.None;

    private void ResetXagmanNetworkRecovery()
    {
        xagmanNetworkPhase = XagmanNetworkRecoveryPhase.None;
        xagmanNetworkSession = xagmanNetworkCoordinator = xagmanNetworkIncident = xagmanNetworkLocalIncident = string.Empty;
        xagmanNetworkEpoch = xagmanNetworkFailureCount = xagmanNetworkReadyEpoch = xagmanNetworkResumeAckEpoch = 0;
        xagmanNetworkManualAttempt = false;
        xagmanNetworkSendInFlight = false;
        xagmanNetworkParticipants.Clear();
        xagmanNetworkSeenIncidents.Clear();
        xagmanNetworkNotBeforeUtc = xagmanNetworkStartedUtc = xagmanNetworkLastSendUtc = DateTime.MinValue;
        xagmanNetworkNextPollUtc = xagmanNetworkLastLogUtc = DateTime.MinValue;
        xagmanNetworkHoldReason = xagmanNetworkLastWaitReason = string.Empty;
        ResetXagmanNetworkCheckpointTransientState();
    }

    private void BeginXagmanNetworkSession()
    {
        ResetXagmanNetworkRecovery();
        xagmanNetworkSession = Guid.NewGuid().ToString("N");
        xagmanNetworkCoordinator = plugin.InstanceId;
    }

    // Returns true when the ordinary Start must not touch the retained run.
    private bool HandleXagmanNetworkStart(XagmanPeerMessage directive)
    {
        if (plugin.Configuration.XagmanRole != XagmanRole.FranchiseOwner) return false;
        if (IsXagmanNetworkRecoveryHolding)
        {
            plugin.TaskRunner.AddLog("Xagman: received Start while network recovery retains this run; waiting for Tony's recovery handshake.");
            PublishXagmanPresence();
            return true;
        }
        if (string.IsNullOrWhiteSpace(directive.NetworkRecoverySessionId)) return false;
        if (xagmanRunning && !string.IsNullOrWhiteSpace(xagmanNetworkSession)
            && (xagmanNetworkSession != directive.NetworkRecoverySessionId || xagmanNetworkCoordinator != directive.SenderInstanceId))
        {
            plugin.TaskRunner.AddLog("Xagman: ignored a new Start session because this owner still has an existing run. Resume the interrupted run or explicitly stop it first.");
            return true;
        }
        if (!xagmanRunning) ResetXagmanNetworkRecovery();
        xagmanNetworkSession = directive.NetworkRecoverySessionId;
        xagmanNetworkCoordinator = directive.SenderInstanceId;
        return false;
    }

    private void RememberXagmanNetworkParticipants(IEnumerable<XagmanPeerPresence> peers)
    {
        if (IsXagmanNetworkRecoveryHolding || xagmanActiveRole != XagmanRole.Tony) return;
        foreach (var peer in peers.Where(p => p.Role == XagmanRole.FranchiseOwner && !IsXagmanOwnerClientCompleted(p.InstanceId)))
            xagmanNetworkParticipants.Add(peer.InstanceId);
    }

    private string[] GetXagmanNetworkRequiredParticipants()
        => xagmanNetworkParticipants.Where(id => !IsXagmanOwnerClientCompleted(id)).ToArray();

    private bool TryHandleXagmanNetworkLogout()
    {
        // The numeric callback precedes the popup on the reported failure. Never
        // wait for UI recognition, and never count popup recognition a second time.
        if (plugin.LastClientStateLogoutCode != 90002 || !xagmanRunning
            || plugin.Configuration.XagmanOutsideNetworkHelper || string.IsNullOrWhiteSpace(xagmanNetworkSession)) return false;
        if (!CanCaptureXagmanNetworkCheckpoint(out var reason))
        {
            plugin.TaskRunner.AddLog($"Xagman: cannot retain network recovery checkpoint: {reason}");
            return false;
        }
        if (xagmanNetworkPhase is XagmanNetworkRecoveryPhase.Preparing or XagmanNetworkRecoveryPhase.Held)
            return true; // Repeated logout/popup notifications for the interrupted operation.
        var incident = Guid.NewGuid().ToString("N");
        if (xagmanActiveRole == XagmanRole.Tony)
            BeginXagmanCoordinatorRecovery(incident, "Tony received disconnect 90002", true);
        else
        {
            xagmanNetworkLocalIncident = incident;
            EnterXagmanOwnerNetworkHold("Disconnect 90002; waiting for Tony to coordinate recovery.");
        }
        return true;
    }

    private bool TryHoldXagmanOwnerForMissingTony()
    {
        if (xagmanActiveRole != XagmanRole.FranchiseOwner || string.IsNullOrWhiteSpace(xagmanNetworkSession)
            || !CanCaptureXagmanNetworkCheckpoint(out _)) return false;
        // A missing heartbeat is not another 90002 incident. Retain the ledger
        // and wait for the original coordinator; do not invent network failures.
        EnterXagmanOwnerNetworkHold("The supplying Tony is unavailable; unfinished work is retained for coordinated resume.");
        return true;
    }

    private void EnterXagmanOwnerNetworkHold(string reason)
    {
        xagmanNetworkPhase = XagmanNetworkRecoveryPhase.Preparing;
        xagmanNetworkReadyEpoch = xagmanNetworkResumeAckEpoch = 0;
        xagmanNetworkStartedUtc = DateTime.UtcNow;
        xagmanNetworkLastSendUtc = DateTime.MinValue;
        xagmanNetworkHoldReason = string.Empty;
        ResetXagmanNetworkCheckpointTransientState();
        TryQuiesceXagmanNetworkRun(out _);
        SetXagmanNetworkRecoveryStatus(reason);
        plugin.TaskRunner.AddLog($"Xagman: {reason}");
        PublishXagmanPresence();
    }

    private void BeginXagmanCoordinatorRecovery(string incident, string reason, bool countFailure)
    {
        if (countFailure && !xagmanNetworkSeenIncidents.Add(incident)) return;
        if (countFailure) xagmanNetworkFailureCount++;
        xagmanNetworkIncident = incident;
        xagmanNetworkEpoch++;
        xagmanNetworkPhase = XagmanNetworkRecoveryPhase.Preparing;
        xagmanNetworkReadyEpoch = xagmanNetworkResumeAckEpoch = 0;
        xagmanNetworkStartedUtc = DateTime.UtcNow;
        if (countFailure) xagmanNetworkNotBeforeUtc = xagmanNetworkStartedUtc + XagmanNetworkRecoveryDelay;
        xagmanNetworkLastSendUtc = DateTime.MinValue;
        xagmanNetworkHoldReason = string.Empty;
        if (countFailure) xagmanNetworkManualAttempt = false;
        ResetXagmanNetworkCheckpointTransientState();
        TryQuiesceXagmanNetworkRun(out _);
        SetXagmanNetworkRecoveryStatus($"Network failure {xagmanNetworkFailureCount}/{XagmanNetworkFailureLimit}: retaining the run and waiting for client readiness.");
        plugin.TaskRunner.AddLog($"Xagman: network recovery epoch {xagmanNetworkEpoch}, failure {xagmanNetworkFailureCount}/{XagmanNetworkFailureLimit}: {reason}. Resume no earlier than {xagmanNetworkNotBeforeUtc.ToLocalTime():HH:mm:ss}; progress is retained.");
        PublishXagmanPresence();
    }

    private void SetXagmanNetworkRecoveryStatus(string text, bool error = false)
    {
        xagmanStatus = error ? XagmanStatus.Error : XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
            global::XASlave.Localization.UiMessage.Raw(text));
    }

    private void HoldXagmanNetworkRecovery(string reason)
    {
        if (xagmanNetworkPhase == XagmanNetworkRecoveryPhase.Held && xagmanNetworkHoldReason == reason) return;
        var wasStarting = xagmanNetworkPhase is XagmanNetworkRecoveryPhase.StartingTony or XagmanNetworkRecoveryPhase.AwaitingOwners;
        if (xagmanActiveRole == XagmanRole.Tony && xagmanNetworkPhase == XagmanNetworkRecoveryPhase.AwaitingOwners)
        {
            // Some owners may already have committed. A higher prepare epoch
            // brings them back to a hold instead of letting old work run alone.
            xagmanNetworkEpoch++;
            xagmanNetworkReadyEpoch = xagmanNetworkResumeAckEpoch = 0;
            xagmanNetworkLastSendUtc = DateTime.MinValue;
        }
        if (wasStarting) ResetXagmanNetworkCheckpointTransientState();
        xagmanNetworkPhase = XagmanNetworkRecoveryPhase.Held;
        xagmanNetworkHoldReason = reason;
        TryQuiesceXagmanNetworkRun(out _);
        SetXagmanNetworkRecoveryStatus(reason, true);
        plugin.TaskRunner.AddLog($"Xagman: network recovery held: {reason} Saved work remains available; no new run was created.");
        PublishXagmanPresence();
    }

    private void RequestXagmanNetworkResume()
    {
        if (!IsXagmanNetworkRecoveryHolding || xagmanActiveRole != XagmanRole.Tony) return;
        if (xagmanNetworkPhase != XagmanNetworkRecoveryPhase.Held)
        {
            plugin.TaskRunner.AddLog("Xagman: the interrupted run is already recovering; retaining its five-minute wait and progress.");
            return;
        }
        // A deliberate manual request permits one attempt after exhaustion, but
        // does not replenish the automatic failure budget or clear run ledgers.
        xagmanNetworkManualAttempt = true;
        BeginXagmanCoordinatorRecovery(xagmanNetworkIncident, "manual resume requested", false);
    }

    private void DrawXagmanNetworkRecoveryControls()
    {
        if (!IsXagmanNetworkRecoveryHolding || xagmanActiveRole != XagmanRole.Tony) return;
        ImGui.BeginDisabled(xagmanNetworkPhase != XagmanNetworkRecoveryPhase.Held || !plugin.XagmanPeers.IsConnected);
        if (ImGui.Button("Resume interrupted run##xagmanNetworkResume")) RequestXagmanNetworkResume();
        ImGui.EndDisabled();
        ImGui.TextWrapped("Network recovery retains unfinished work. Stop abandons this continuation; Resume retries it without clearing progress.");
    }

    private void HandleXagmanNetworkRecoveryRequest(XagmanPeerMessage message)
    {
        _ = Plugin.RunOnGameThread(() => ApplyXagmanNetworkRecoveryRequest(message));
    }

    private void ApplyXagmanNetworkRecoveryRequest(XagmanPeerMessage message)
    {
        if (isDisposed) return;
        var directive = message.NetworkRecovery;
        if (directive == null || directive.Revision != XagmanNetworkRecoveryRevision || !xagmanRunning
            || directive.SessionId != xagmanNetworkSession || directive.CoordinatorInstanceId != xagmanNetworkCoordinator
            || !CanCaptureXagmanNetworkCheckpoint(out _)) return;
        if (directive.Action == XagmanNetworkRecoveryAction.ReportIncident)
        {
            if (xagmanActiveRole != XagmanRole.Tony || directive.Epoch != xagmanNetworkEpoch
                || !GetXagmanNetworkRequiredParticipants().Contains(message.SenderInstanceId)) return;
            var owner = plugin.XagmanPeers.Peers.FirstOrDefault(p => p.InstanceId == message.SenderInstanceId
                && p.Role == XagmanRole.FranchiseOwner && p.RecoverySessionId == xagmanNetworkSession && IsXagmanPeerFresh(p));
            if (owner == null || string.IsNullOrWhiteSpace(directive.IncidentId)) return;
            if (xagmanNetworkPhase is XagmanNetworkRecoveryPhase.Preparing or XagmanNetworkRecoveryPhase.Held)
            {
                xagmanNetworkSeenIncidents.Add(directive.IncidentId);
                return;
            }
            // During Tony's login, owners remain suspended. Their delayed reports
            // belong to the current outage until they acknowledge a resume.
            if (xagmanNetworkPhase == XagmanNetworkRecoveryPhase.StartingTony)
            {
                xagmanNetworkSeenIncidents.Add(directive.IncidentId);
                return;
            }
            BeginXagmanCoordinatorRecovery(directive.IncidentId, $"owner {owner.CharacterName} ({owner.InstanceId}) reported disconnect 90002", true);
            return;
        }
        if (xagmanActiveRole != XagmanRole.FranchiseOwner || message.SenderInstanceId != xagmanNetworkCoordinator
            || !directive.ParticipantInstanceIds.Contains(plugin.InstanceId) || directive.Epoch < xagmanNetworkEpoch) return;
        if (directive.Action == XagmanNetworkRecoveryAction.Prepare)
        {
            if (directive.Epoch == xagmanNetworkEpoch && (xagmanNetworkResumeAckEpoch == directive.Epoch || IsXagmanNetworkRecoveryHolding)) return;
            xagmanNetworkEpoch = directive.Epoch;
            xagmanNetworkIncident = directive.IncidentId;
            xagmanNetworkFailureCount = Math.Max(xagmanNetworkFailureCount, directive.FailureCount);
            xagmanNetworkNotBeforeUtc = directive.NotBeforeUtc;
            xagmanNetworkLocalIncident = string.Empty;
            EnterXagmanOwnerNetworkHold($"Tony requested network recovery epoch {directive.Epoch}; preserving this owner's unfinished run.");
            return;
        }
        if (directive.Action != XagmanNetworkRecoveryAction.Resume || directive.Epoch != xagmanNetworkEpoch
            || directive.IncidentId != xagmanNetworkIncident) return;
        // Completed owners only need their existing completion receipt relayed;
        // they must never rebuild a roster while Tony is still observing it.
        if (!string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken)) { PublishXagmanPresence(); return; }
        if (xagmanNetworkResumeAckEpoch == directive.Epoch) { PublishXagmanPresence(); return; }
        if (xagmanNetworkPhase != XagmanNetworkRecoveryPhase.Preparing || xagmanNetworkReadyEpoch != directive.Epoch
            || DateTime.UtcNow < xagmanNetworkNotBeforeUtc || !IsXagmanNetworkLocallyReady(out _)) return;
        var tony = plugin.XagmanPeers.Peers.FirstOrDefault(p => p.InstanceId == xagmanNetworkCoordinator && IsXagmanPeerFresh(p)
            && p.RecoverySessionId == xagmanNetworkSession && p.RecoveryEpoch == xagmanNetworkEpoch
            && IsXagmanPeerInCurrentRunPhase(p)
            && (!IsXagmanSupplyCycleActive() || CanResumeXagmanNetworkCapacityRestore(p)
                || (p.SupplyInventoryComplete && !string.IsNullOrWhiteSpace(p.SupplyPassId)))
            && p.Status == XagmanStatus.AtMeetSpot);
        if (tony == null) return;
        ResetXagmanNetworkTransientTimers();
        xagmanNetworkPhase = XagmanNetworkRecoveryPhase.None;
        if (!ResumeXagmanNetworkOwner(out var failure)) { HoldXagmanNetworkRecovery(failure); return; }
        xagmanNetworkResumeAckEpoch = directive.Epoch;
        xagmanNetworkLocalIncident = string.Empty;
        plugin.TaskRunner.AddLog($"Xagman: resumed retained owner work for network recovery epoch {directive.Epoch}; completed work and original trade goals are unchanged.");
        PublishXagmanPresence();
    }

    private XagmanNetworkRecoveryDirective CreateXagmanNetworkDirective(XagmanNetworkRecoveryAction action)
        => new()
        {
            Revision = XagmanNetworkRecoveryRevision, Action = action,
            SessionId = xagmanNetworkSession, CoordinatorInstanceId = xagmanNetworkCoordinator,
            IncidentId = action == XagmanNetworkRecoveryAction.ReportIncident ? xagmanNetworkLocalIncident : xagmanNetworkIncident,
            Epoch = xagmanNetworkEpoch, FailureCount = xagmanNetworkFailureCount,
            NotBeforeUtc = xagmanNetworkNotBeforeUtc, ParticipantInstanceIds = GetXagmanNetworkRequiredParticipants().ToList(),
        };

    private async System.Threading.Tasks.Task SendXagmanNetworkDirective(XagmanNetworkRecoveryAction action, string[] targets)
    {
        if (isDisposed || xagmanNetworkSendInFlight || targets.Length == 0) return;
        xagmanNetworkSendInFlight = true;
        xagmanNetworkLastSendUtc = DateTime.UtcNow;
        var directive = CreateXagmanNetworkDirective(action);
        try
        {
            // Sending is not an acknowledgment. Only fresh peer presence advances
            // the preparation and commit barriers below.
            await plugin.XagmanPeers.SendNetworkRecoveryAsync(directive, targets, workerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Xagman] Network recovery message could not be sent"); }
        finally
        {
            try
            {
                if (!isDisposed)
                    await Plugin.RunOnGameThread(() =>
                    {
                        if (!isDisposed && xagmanNetworkSession == directive.SessionId)
                            xagmanNetworkSendInFlight = false;
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Xagman] Network recovery send cleanup could not be scheduled"); }
        }
    }

    private bool IsXagmanNetworkPeerReady(XagmanPeerPresence peer, DateTime now)
        => IsXagmanPeerFresh(peer) && peer.NetworkRecoveryRevision == XagmanNetworkRecoveryRevision
            && peer.RecoverySessionId == xagmanNetworkSession && peer.RecoveryCoordinatorInstanceId == plugin.InstanceId
            && peer.RecoveryEpoch == xagmanNetworkEpoch && peer.RecoveryReadyEpoch == xagmanNetworkEpoch
            && peer.RecoverySuspended && peer.HasRecoveryCheckpoint && peer.CanResumeRecovery
            && !peer.RecoveryTaskRunning && peer.RecoveryLifestreamReadable && !peer.RecoveryLifestreamBusy
            && peer.RecoveryDropboxReadable && !peer.RecoveryDropboxBusy && !peer.RecoveryTradeVisible
            && (peer.RecoveryAtUsableTitle || peer.RecoverySafeIdle)
            && peer.RecoveryReadinessObservedAtUtc <= now.AddSeconds(5)
            && (now - peer.RecoveryReadinessObservedAtUtc).TotalSeconds <= 10;

    private void ApplyXagmanNetworkPresence(XagmanPeerPresence presence)
    {
        presence.NetworkRecoveryRevision = XagmanNetworkRecoveryRevision;
        presence.RecoverySessionId = xagmanNetworkSession;
        presence.RecoveryCoordinatorInstanceId = xagmanNetworkCoordinator;
        presence.RecoveryEpoch = xagmanNetworkEpoch;
        presence.RecoveryReadyEpoch = xagmanNetworkReadyEpoch;
        presence.RecoveryResumeAckEpoch = xagmanNetworkResumeAckEpoch;
        presence.RecoveryIncidentId = string.IsNullOrWhiteSpace(xagmanNetworkLocalIncident) ? xagmanNetworkIncident : xagmanNetworkLocalIncident;
        presence.RecoveryFailureCount = xagmanNetworkFailureCount;
        presence.RecoveryNotBeforeUtc = xagmanNetworkNotBeforeUtc;
        presence.RecoverySuspended = IsXagmanNetworkRecoveryHolding;
        presence.HasRecoveryCheckpoint = !string.IsNullOrWhiteSpace(xagmanNetworkSession) && CanCaptureXagmanNetworkCheckpoint(out _);
        presence.HasUnfinishedRecoveryWork = presence.HasRecoveryCheckpoint && string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken);
        presence.RecoveryTaskRunning = plugin.TaskRunner.IsRunning;
        if (xagmanNetworkPhase is not (XagmanNetworkRecoveryPhase.Preparing or XagmanNetworkRecoveryPhase.Held)) return;
        presence.RecoveryLifestreamReadable = plugin.IpcClient.TryGetLifestreamBusy(out var travelBusy);
        presence.RecoveryLifestreamBusy = travelBusy;
        presence.RecoveryDropboxReadable = TryReadXagmanNetworkDropboxBusy(out var dropboxBusy);
        presence.RecoveryDropboxBusy = dropboxBusy;
        presence.RecoveryTradeVisible = AddonHelper.IsAddonVisible("Trade");
        presence.RecoveryAtUsableTitle = !Plugin.ClientState.IsLoggedIn
            && AddonHelper.IsAddonReady("_TitleMenu") && AddonHelper.IsAddonVisible("_TitleMenu");
        presence.RecoverySafeIdle = Plugin.ClientState.IsLoggedIn && CharacterSafetyHelper.IsCharacterSafeWaitReady();
        presence.CanResumeRecovery = xagmanNetworkQuiesced && IsXagmanNetworkLocallyReady(out _);
        presence.RecoveryReadinessObservedAtUtc = DateTime.UtcNow;
        if (!presence.CanResumeRecovery) presence.RecoveryReadyEpoch = 0;
    }

    private bool AreXagmanNetworkParticipantsReady(DateTime now, bool requireResume, out string reason)
    {
        foreach (var id in GetXagmanNetworkRequiredParticipants())
        {
            var peer = plugin.XagmanPeers.Peers.FirstOrDefault(p => p.InstanceId == id && p.Role == XagmanRole.FranchiseOwner);
            if (peer == null || !IsXagmanPeerFresh(peer)) { reason = $"Waiting for client {id}: no fresh heartbeat."; return false; }
            if (peer.NetworkRecoveryRevision != XagmanNetworkRecoveryRevision)
            { reason = $"Client {id} does not support coordinated network recovery; all participating clients need the updated build."; return false; }
            if (requireResume)
            {
                if (peer.RecoverySessionId == xagmanNetworkSession && peer.RecoveryEpoch == xagmanNetworkEpoch
                    && peer.RecoveryResumeAckEpoch == xagmanNetworkEpoch && !peer.RecoverySuspended) continue;
            }
            else if (IsXagmanNetworkPeerReady(peer, now)) continue;
            reason = $"Waiting for client {id} ({peer.CharacterName}): {peer.StatusText}";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void LogXagmanNetworkWait(string reason)
    {
        if (reason == xagmanNetworkLastWaitReason && (DateTime.UtcNow - xagmanNetworkLastLogUtc).TotalSeconds < 30) return;
        xagmanNetworkLastWaitReason = reason;
        xagmanNetworkLastLogUtc = DateTime.UtcNow;
        plugin.TaskRunner.AddLog($"Xagman: network recovery: {reason}");
    }

    private bool TryUpdateXagmanNetworkOwnerCompletion()
    {
        if (string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken)) return false;
        if (!xagmanNetworkQuiesced || !IsXagmanNetworkLocallyReady(out _)) return true;
        // Pause the normal two-minute ack timeout during network recovery. Keep
        // the original token/pass and let Tony validate the full completion proof.
        xagmanOwnerCompletionRequestedAtUtc = DateTime.UtcNow;
        xagmanStatus = XagmanStatus.Paused;
        UpdateXagmanLocalOwnerCompletion();
        if (xagmanOwnerCompletionAckReceived)
        {
            xagmanNetworkPhase = XagmanNetworkRecoveryPhase.None;
            ResetXagmanNetworkCheckpointTransientState();
        }
        PublishXagmanPresence();
        return true;
    }

    private bool UpdateXagmanNetworkRecovery()
    {
        if (!IsXagmanNetworkRecoveryHolding) return false;
        var now = DateTime.UtcNow;
        if (now < xagmanNetworkNextPollUtc) return true;
        xagmanNetworkNextPollUtc = now.AddMilliseconds(250);
        if (xagmanNetworkPhase is XagmanNetworkRecoveryPhase.Preparing or XagmanNetworkRecoveryPhase.Held)
        {
            var ready = TryQuiesceXagmanNetworkRun(out var localReason) && IsXagmanNetworkLocallyReady(out localReason);
            xagmanNetworkReadyEpoch = ready ? xagmanNetworkEpoch : 0;
            if (!ready) LogXagmanNetworkWait(localReason);
        }
        if ((now - xagmanLastPresencePublishUtc).TotalSeconds >= 1) PublishXagmanPresence();
        if (xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            if (TryUpdateXagmanNetworkOwnerCompletion()) return true;
            if (!string.IsNullOrWhiteSpace(xagmanNetworkLocalIncident) && (now - xagmanNetworkLastSendUtc).TotalSeconds >= 5)
                _ = SendXagmanNetworkDirective(XagmanNetworkRecoveryAction.ReportIncident, new[] { xagmanNetworkCoordinator });
            if (xagmanNetworkPhase != XagmanNetworkRecoveryPhase.Held && now - xagmanNetworkStartedUtc > XagmanNetworkRecoveryDeadline)
                HoldXagmanNetworkRecovery("Timed out waiting for Tony's coordinated resume. Unfinished owner work is retained.");
            return true;
        }
        var required = GetXagmanNetworkRequiredParticipants();
        if ((now - xagmanNetworkLastSendUtc).TotalSeconds >= 5)
            _ = SendXagmanNetworkDirective(xagmanNetworkPhase == XagmanNetworkRecoveryPhase.AwaitingOwners
                ? XagmanNetworkRecoveryAction.Resume : XagmanNetworkRecoveryAction.Prepare, required);
        if (xagmanNetworkPhase == XagmanNetworkRecoveryPhase.Held) return true;
        if (now - xagmanNetworkStartedUtc > XagmanNetworkRecoveryDeadline)
        { HoldXagmanNetworkRecovery("Recovery did not complete within 20 minutes. Inspect client readiness, then use Resume interrupted run."); return true; }
        if (xagmanNetworkPhase == XagmanNetworkRecoveryPhase.Preparing)
        {
            if (xagmanNetworkFailureCount >= XagmanNetworkFailureLimit && !xagmanNetworkManualAttempt)
            { HoldXagmanNetworkRecovery("Three network failures reached; automatic recovery stopped. Use Resume interrupted run for a deliberate manual attempt."); return true; }
            if (now < xagmanNetworkNotBeforeUtc)
            {
                SetXagmanNetworkRecoveryStatus($"Network recovery {xagmanNetworkFailureCount}/{XagmanNetworkFailureLimit}: waiting {Math.Ceiling((xagmanNetworkNotBeforeUtc - now).TotalSeconds)}s before resuming.");
                return true;
            }
            if (!IsXagmanNetworkLocallyReady(out var reason) || !AreXagmanNetworkParticipantsReady(now, false, out reason))
            { LogXagmanNetworkWait(reason); return true; }
            ResetXagmanNetworkTransientTimers();
            xagmanNetworkPhase = XagmanNetworkRecoveryPhase.StartingTony;
            if (!ResumeXagmanNetworkTony(out reason)) HoldXagmanNetworkRecovery(reason);
            else plugin.TaskRunner.AddLog($"Xagman: all {required.Length} unfinished clients acknowledged recovery epoch {xagmanNetworkEpoch}; restoring Tony's saved character and meetup.");
            return true;
        }
        if (xagmanNetworkPhase == XagmanNetworkRecoveryPhase.StartingTony)
        {
            if (plugin.TaskRunner.IsRunning) return true;
            if (xagmanStatus != XagmanStatus.AtMeetSpot || !CharacterSafetyHelper.IsCharacterSafeWaitReady()
                || !plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy
                || !IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte())
                || (IsXagmanSupplyCycleActive() && !TryCaptureXagmanSupplyInventory(out _)))
            { HoldXagmanNetworkRecovery("Tony's recovery login or meetup could not be verified; retained run is held."); return true; }
            xagmanNetworkPhase = XagmanNetworkRecoveryPhase.AwaitingOwners;
            xagmanNetworkLastSendUtc = DateTime.MinValue;
            PublishXagmanPresence();
            return true;
        }
        if (xagmanNetworkPhase == XagmanNetworkRecoveryPhase.AwaitingOwners)
        {
            if (!AreXagmanNetworkParticipantsReady(now, true, out var reason)) { LogXagmanNetworkWait(reason); return true; }
            xagmanNetworkPhase = XagmanNetworkRecoveryPhase.None;
            xagmanNetworkResumeAckEpoch = xagmanNetworkEpoch;
            xagmanNetworkManualAttempt = false;
            ResetXagmanNetworkTransientTimers();
            plugin.TaskRunner.AddLog($"Xagman: every unfinished client acknowledged resume epoch {xagmanNetworkEpoch}; continuing the saved run after {xagmanNetworkFailureCount} network failure(s).");
            PublishXagmanPresence();
        }
        return true;
    }
}
