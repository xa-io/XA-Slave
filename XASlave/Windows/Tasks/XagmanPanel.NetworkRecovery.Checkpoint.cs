using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // The checkpoint is the retained live run, not the display-only last-run snapshot.
    // These fields own cleanup for one recovery epoch; none is a delivery ledger.
    private bool xagmanNetworkQuiesced;
    private string xagmanNetworkCleanupFailure = string.Empty;
    private bool xagmanNetworkQuiesceStarted;
    private bool xagmanNetworkOwnsLifestream;
    private bool xagmanNetworkLifestreamAbortSent;
    private bool xagmanNetworkOwnsMovement;
    private bool xagmanNetworkMovementStopSent;
    private bool xagmanNetworkDropboxStopSent;
    private DateTime xagmanNetworkNextTradeCloseUtc = DateTime.MinValue;
    private DateTime xagmanNetworkNextTitleNavigationUtc = DateTime.MinValue;

    private bool CanCaptureXagmanNetworkCheckpoint(out string reason)
    {
        reason = string.Empty;
        if (!xagmanRunning || plugin.Configuration.XagmanOutsideNetworkHelper)
            reason = "There is no active connected Xagman run to retain.";
        else if ((xagmanStatus == XagmanStatus.Completed
                && !(xagmanActiveRole == XagmanRole.FranchiseOwner && IsXagmanCollectionFirstCollectionPhase() && !xagmanPhaseComplete))
            || xagmanCompletionDirectiveAcknowledged || xagmanOwnerCompletionAckReceived
            || xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.Completing)
            reason = "This client has already entered completion; it is not an unfinished recovery participant.";
        else if (xagmanTravelRouteFatalError || xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.Failed)
            reason = "An existing route or capacity failure requires its own recovery; network retry cannot clear it.";
        else if (xagmanActiveRole == XagmanRole.Tony
            && (string.IsNullOrWhiteSpace(xagmanActiveCharacter)
                || !xagmanTonyRunPlan.Contains(xagmanActiveCharacter, StringComparer.OrdinalIgnoreCase)))
            reason = "The current Tony is not present in the retained run plan.";
        else if (xagmanActiveRole == XagmanRole.FranchiseOwner && xagmanOwnerRunPlan.Count == 0
            && !(IsXagmanCollectionFirstRunActive() && (xagmanPhaseComplete
                || (xagmanCapacityOwnerCheckpoint != null
                    && (xagmanCapacityRecoveryStage is XagmanCapacityRecoveryStage.OwnerWaitingForDrain
                        or XagmanCapacityRecoveryStage.OwnerDraining or XagmanCapacityRecoveryStage.OwnerWaitingForCollectionRelease)))))
            reason = "The retained owner roster is unavailable.";
        return reason.Length == 0;
    }

    private void ResetXagmanNetworkCheckpointTransientState()
    {
        xagmanNetworkQuiesced = xagmanNetworkQuiesceStarted = false;
        xagmanNetworkCleanupFailure = string.Empty;
        xagmanNetworkOwnsLifestream = xagmanNetworkLifestreamAbortSent = false;
        xagmanNetworkOwnsMovement = xagmanNetworkMovementStopSent = false;
        xagmanNetworkDropboxStopSent = false;
        xagmanNetworkNextTradeCloseUtc = DateTime.MinValue;
        xagmanNetworkNextTitleNavigationUtc = DateTime.MinValue;
        if (xagmanLobbyOperation == null)
            plugin.LobbyErrorAutoClose.SetRequiredByXagman(false);
    }

    private bool TryQuiesceXagmanNetworkRun(out string reason)
    {
        reason = string.Empty;
        if (!CanCaptureXagmanNetworkCheckpoint(out reason)) return false;
        try
        {
            if (!xagmanNetworkQuiesceStarted)
            {
                var ownsRunner = plugin.TaskRunner.IsRunning
                    && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase);
                xagmanNetworkOwnsLifestream = xagmanLobbyOperation != null || xagmanTravelFailureToken != 0
                    || IsXagmanExpectedTravelLogoutWindowActive()
                    || xagmanTonyMeetCommandPhase != XagmanTonyMeetCommandPhase.None;
                xagmanNetworkOwnsMovement = xagmanCustomMoveOwned || (ownsRunner
                    && xagmanStatus is XagmanStatus.Traveling or XagmanStatus.ReadyForQueue
                        or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.Called or XagmanStatus.Trading);
                xagmanNetworkQuiesceStarted = true;
                if (ownsRunner) plugin.TaskRunner.Cancel(); // Cancel never calls completion continuations.
                EndXagmanLobbyOperation();
                ResetXagmanTravelFailureMonitor();
                ClearXagmanExpectedTravelLogoutWindow();
                xagmanExpectedLogout = false;
                xagmanOwnerRelogLogoutFailure = null;
                xagmanNpcSell?.Abort("Xagman network recovery");
                xagmanNpcSell?.Dispose();
                xagmanNpcSell = null;
                ClearXagmanFocusTarget();
                // Rebuilt sequences establish fresh calls. Keep quantities, identities, plans and cursors.
                xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanOwnerPauseForTonyRotationRequested = false;
                xagmanTonyRotationRequestedByOwnerStandby = false;
                ResetXagmanNetworkTransientTimers();
            }
            plugin.LobbyErrorAutoClose.SetRequiredByXagman(true);
            if (xagmanNetworkQuiesced)
                return IsXagmanNetworkLocallyReady(out reason);

            var failures = new List<string>();
            if (plugin.TaskRunner.IsRunning)
                failures.Add("Another task is running; network recovery will not cancel it.");
            if (!plugin.IpcClient.TryGetLifestreamBusy(out var travelBusy))
                failures.Add("Lifestream busy state is unreadable.");
            else if (travelBusy)
            {
                if (!xagmanNetworkOwnsLifestream)
                    failures.Add("Lifestream is busy without a retained Xagman travel operation.");
                else if (!xagmanNetworkLifestreamAbortSent)
                {
                    xagmanNetworkLifestreamAbortSent = plugin.IpcClient.LifestreamAbort();
                    failures.Add(xagmanNetworkLifestreamAbortSent
                        ? "Waiting for the owned Lifestream operation to stop." : "Lifestream abort was not accepted.");
                }
                else failures.Add("Waiting for the owned Lifestream operation to stop.");
            }
            else xagmanNetworkOwnsLifestream = false;

            if (!TryReadXagmanNetworkMovement(out var moving))
                failures.Add("Navigation state is unreadable.");
            else if (moving)
            {
                if (!xagmanNetworkOwnsMovement)
                    failures.Add("Navigation is active without retained Xagman ownership.");
                else if (!xagmanNetworkMovementStopSent)
                {
                    xagmanNetworkMovementStopSent = plugin.IpcClient.VnavStop();
                    failures.Add(xagmanNetworkMovementStopSent
                        ? "Waiting for owned navigation to stop." : "Owned navigation stop was not accepted.");
                }
                else failures.Add("Waiting for owned navigation to stop.");
            }
            else
            {
                xagmanNetworkOwnsMovement = false;
                StopXagmanCustomMeetingMovement();
            }

            if (!TrySetXagmanDropboxAutoAccept(false))
                failures.Add("Dropbox auto-accept could not be confirmed off.");
            if (!xagmanNetworkDropboxStopSent)
            {
                xagmanNetworkDropboxStopSent = TryStopXagmanDropboxTradeQueue();
                if (!xagmanNetworkDropboxStopSent) failures.Add("Dropbox queue stop was not accepted.");
            }
            var tradeVisible = AddonHelper.IsAddonVisible("Trade");
            if (tradeVisible)
            {
                if (DateTime.UtcNow >= xagmanNetworkNextTradeCloseUtc)
                {
                    xagmanNetworkNextTradeCloseUtc = DateTime.UtcNow.AddSeconds(1);
                    if (!TryAbortXagmanTradeWindow()) failures.Add("The Trade window close request failed.");
                }
                failures.Add("Waiting for the Trade window to close.");
            }
            if (!TryReadXagmanNetworkDropboxBusy(out var dropboxBusy))
                failures.Add("Dropbox busy state is unreadable.");
            else if (dropboxBusy) failures.Add("Waiting for Dropbox trading to stop.");
            else if (!tradeVisible && xagmanNetworkDropboxStopSent
                && !TryClearXagmanDropbox(out var queueFailure))
                failures.Add("Dropbox queue clear failed: " + queueFailure);

            if (failures.Count > 0)
            {
                reason = xagmanNetworkCleanupFailure = string.Join(" ", failures);
                return false;
            }
            xagmanObservedDropboxBusy = false;
            ReturnXagmanNetworkRecoveryToTitle();
            if (!IsXagmanNetworkLocallyReady(out reason))
            {
                xagmanNetworkCleanupFailure = reason;
                return false;
            }
            xagmanNetworkQuiesced = true;
            xagmanNetworkCleanupFailure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = xagmanNetworkCleanupFailure = "Network recovery cleanup failed: " + ex.Message;
            return false;
        }
    }

    private void ReturnXagmanNetworkRecoveryToTitle()
    {
        if (Plugin.ClientState.IsLoggedIn || DateTime.UtcNow < xagmanNetworkNextTitleNavigationUtc
            || !plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy
            || new[] { "Dialogue", "SelectOk", "SelectYesno", "NowLoading", "FadeMiddle", "FadeBack" }.Any(AddonHelper.IsAddonVisible)
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]) return;
        var characterSelect = AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu");
        var dataCenterSelect = AddonHelper.IsAddonVisible("TitleDCWorldMap");
        if (!characterSelect && !dataCenterSelect) return;
        xagmanNetworkNextTitleNavigationUtc = DateTime.UtcNow.AddSeconds(2);
        if (characterSelect) AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
        else KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
    }

    private bool IsXagmanNetworkLocallyReady(out string reason)
    {
        reason = string.Empty;
        try
        {
            if (plugin.TaskRunner.IsRunning) reason = "A task is still running.";
            else if (!plugin.IpcClient.TryGetLifestreamBusy(out var travelBusy)) reason = "Lifestream busy state is unreadable.";
            else if (travelBusy) reason = "Lifestream is still busy.";
            else if (!TryReadXagmanNetworkDropboxBusy(out var dropboxBusy)) reason = "Dropbox busy state is unreadable.";
            else if (dropboxBusy) reason = "Dropbox is still trading.";
            else if (!TryReadXagmanNetworkDropboxQueue(out var autoAccept, out var queued)) reason = "Dropbox queue or auto-accept state is unreadable.";
            else if (autoAccept || queued) reason = "Dropbox still has auto-accept or queued quantities enabled.";
            else if (!TryReadXagmanNetworkMovement(out var moving)) reason = "Navigation state is unreadable.";
            else if (moving) reason = "Navigation has not stopped.";
            else if (new[] { "Trade", "Dialogue", "SelectOk", "SelectYesno", "NowLoading", "FadeMiddle", "FadeBack" }
                .Any(AddonHelper.IsAddonVisible) || Plugin.Condition[ConditionFlag.BetweenAreas]
                || Plugin.Condition[ConditionFlag.BetweenAreas51] || plugin.LobbyErrorAutoClose.HasSupportedError)
                reason = "A trade, dialogue, loading screen or area transition is still active.";
            else if (Plugin.ClientState.IsLoggedIn)
            {
                if (!CharacterSafetyHelper.IsCharacterSafeWaitReady()) reason = "The logged-in character is not safely usable yet.";
            }
            else if (!AddonHelper.IsAddonReady("_TitleMenu") || !AddonHelper.IsAddonVisible("_TitleMenu")
                || new[] { "CharaSelect", "_CharaSelectListMenu", "TitleDCWorldMap" }.Any(AddonHelper.IsAddonVisible))
                reason = "Waiting for a usable title menu.";
            return reason.Length == 0;
        }
        catch (Exception ex)
        {
            reason = "Recovery readiness could not be read: " + ex.Message;
            return false;
        }
    }

    private static bool TryReadXagmanNetworkDropboxBusy(out bool busy)
    {
        busy = false;
        try { busy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Dropbox.IsBusy").InvokeFunc(); return true; }
        catch { return false; }
    }

    private static bool TryReadXagmanNetworkMovement(out bool moving)
    {
        moving = false;
        try
        {
            // Read all endpoints even if the first reports activity; unreadable is never idle.
            var path = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc();
            var nav = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc();
            var simple = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc();
            moving = path || nav || simple;
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadXagmanNetworkDropboxQueue(out bool autoAccept, out bool queued)
    {
        autoAccept = queued = false;
        try
        {
            var dropbox = GetXagmanDropboxPlugin();
            if (dropbox == null) return false;
            var config = GetXagmanDropboxPluginConfig(dropbox);
            if (config == null) return false;
            const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var active = config.GetType().GetField("Active", instanceFlags)?.GetValue(config)
                ?? config.GetType().GetProperty("Active", instanceFlags)?.GetValue(config);
            if (active is not bool enabled) return false;
            autoAccept = enabled;
            var queueType = dropbox.GetType().Assembly.GetType("Dropbox.ItemQueueUI");
            if (queueType?.GetField("ItemQuantities", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not IDictionary quantities)
                return false;
            foreach (DictionaryEntry entry in quantities)
            {
                if (entry.Value == null) return false;
                var value = entry.Value is int ? entry.Value
                    : entry.Value.GetType().GetField("Value", instanceFlags)?.GetValue(entry.Value)
                        ?? entry.Value.GetType().GetProperty("Value", instanceFlags)?.GetValue(entry.Value);
                if (value is not int quantity) return false;
                queued |= quantity != 0; // Dropbox's UI may recreate zero-valued boxes after a clear.
            }
            return true;
        }
        catch { return false; }
    }

    private bool ResumeXagmanNetworkTony(out string reason)
    {
        if (!CanCaptureXagmanNetworkCheckpoint(out reason) || xagmanActiveRole != XagmanRole.Tony)
        { if (reason.Length == 0) reason = "This checkpoint is not a Tony run."; return false; }
        if (!xagmanNetworkQuiesced || !IsXagmanNetworkLocallyReady(out reason))
        { if (reason.Length == 0) reason = "Network cleanup is not complete."; return false; }
        var character = xagmanActiveCharacter;
        var meetWorld = GetXagmanActiveMeetWorld();
        if (!HasCompleteXagmanActiveMeetDestination())
        { reason = "The retained Tony meetup destination is incomplete."; return false; }
        if (!TryValidateXagmanMeetTravelMessage(character, meetWorld, out var travelFailure))
        { reason = travelFailure.Original; return false; }
        if (xagmanServerMatchingActive && !TryValidateXagmanServerMatchingTonyRouteMessage(
            character, xagmanSweepRegion, xagmanSweepDataCenter, out var serverFailure))
        { reason = serverFailure.Original; return false; }
        ResetXagmanNetworkTransientTimers();
        ResetXagmanTonyMeetRetryState();
        xagmanPreferredTonyCharacter = character;
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanStatus = XagmanStatus.Paused;
        BeginXagmanTonySupplyPass();
        // Do not use StartXagmanTonyStartup: its new-supplier reset discards capacity/drain state.
        var entry = new XagmanTonyCharacterEntry { CharacterNameWorld = character, Mode = xagmanTonyMode };
        var steps = BuildXagmanTonyStartupSteps(entry, true);
        if (!plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps,
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true, preserveRunHistory: true))
        { reason = "TaskRunner refused the retained Tony restart."; return false; }
        UpdateXagmanTonyTaskRunnerProgress();
        PublishXagmanPresence();
        reason = string.Empty;
        return true;
    }

    private bool ResumeXagmanNetworkOwner(out string reason)
    {
        if (!CanCaptureXagmanNetworkCheckpoint(out reason) || xagmanActiveRole != XagmanRole.FranchiseOwner)
        { if (reason.Length == 0) reason = "This checkpoint is not an owner run."; return false; }
        if (!xagmanNetworkQuiesced || !IsXagmanNetworkLocallyReady(out reason))
        { if (reason.Length == 0) reason = "Network cleanup is not complete."; return false; }
        var tony = GetXagmanSupplyTonyPeer();
        if (tony == null || !IsXagmanPeerFresh(tony) || tony.Status is XagmanStatus.Error or XagmanStatus.Paused
            or XagmanStatus.Preflight or XagmanStatus.Relogging or XagmanStatus.Traveling or XagmanStatus.ReturningHome)
        { reason = "The retained coordinator has not published fresh ready state."; return false; }
        ResetXagmanNetworkTransientTimers();
        xagmanPreferredTonyCharacter = tony.ActiveCharacter;
        SetXagmanActiveMeetDestination(tony.MeetWorld, tony.MeetAetheryte);
        xagmanOwnerStartRequested = true;
        xagmanOwnerStandbyPending = xagmanOwnerPauseForTonyRotationRequested = false;
        xagmanOwnerStandbyTonyCharacter = xagmanOwnerStandbyTonyInstanceId = string.Empty;
        xagmanOwnerStandbyPriorTonyCallReleased = true;
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanStatus = XagmanStatus.Paused;
        // Capacity phase transitions already own a saved collection cursor and must finish it themselves.
        if ((xagmanCapacityRecoveryStage is XagmanCapacityRecoveryStage.OwnerWaitingForDrain
            or XagmanCapacityRecoveryStage.OwnerWaitingForCollectionRelease)
            || CanResumeXagmanNetworkCapacityRestore(tony))
        { PublishXagmanPresence(); reason = string.Empty; return true; }

        var collectionOnly = IsXagmanCollectionFirstCollectionPhase();
        List<string> owners;
        var startIndex = 0;
        if (collectionOnly)
        {
            // Preserve original ordinal accounting in collection; completed entries precede the cursor.
            owners = xagmanOwnerRunList.ToList();
            startIndex = Math.Clamp(xagmanOwnerCurrentCharacterIndex, 0, owners.Count);
            while (startIndex < owners.Count && (xagmanOwnerCollectionCompletedKeys.Contains(owners[startIndex])
                || xagmanOwnerCompletedKeys.Contains(owners[startIndex])
                || plugin.TaskRunner.FailedCharacters.Contains(owners[startIndex])
                || xagmanSkippedCharacters.Contains(owners[startIndex]))) startIndex++;
        }
        else
        {
            owners = xagmanOwnerRunPlan.Concat(xagmanOwnerRunList).Concat(xagmanPartialOwners.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(IsXagmanSupplyOwnerUnresolved).ToList();
            // Keep the interrupted character first, but never reinsert a completed or failed owner.
            var interrupted = owners.FindIndex(owner => owner.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase));
            if (interrupted > 0) { var owner = owners[interrupted]; owners.RemoveAt(interrupted); owners.Insert(0, owner); }
            xagmanOwnerSupplyPassId = string.Empty;
            if (!TryBindXagmanOwnerSupplyPass())
            { reason = "Waiting for the restarted coordinator's readable supply pass."; return false; }
            xagmanOwnerRunList = owners;
            xagmanOwnerCurrentCharacterIndex = 0;
        }
        xagmanOwnerSupplyWaiting = xagmanOwnerSupplyPassComplete = xagmanOwnerSupplyAtMenu = xagmanOwnerSupplyContinuation = false;
        if (startIndex >= owners.Count && collectionOnly && xagmanPhaseComplete)
        { PublishXagmanPresence(); reason = string.Empty; return true; }
        var steps = BuildXagmanFranchiseSteps(owners, startIndex);
        if (!plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps,
            onFinished: OnXagmanFranchiseTaskFinished, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), preserveRunHistory: true))
        { reason = "TaskRunner refused the retained owner restart."; return false; }
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = GetXagmanLocalOwnerCompletedCharacters();
        PublishXagmanPresence();
        reason = string.Empty;
        return true;
    }

    private bool CanResumeXagmanNetworkCapacityRestore(XagmanPeerPresence tony)
        // Tony may already have committed the drain -> collection transition when the
        // outage reaches an owner still holding the completed drain's pass/menu ack.
        // Preserve that exact ack so the existing capacity handler can restore its cursor.
        => xagmanActiveRole == XagmanRole.FranchiseOwner && IsXagmanCollectionFirstRunActive()
            && xagmanRunPhase == XagmanRunPhase.Collection
            && xagmanCapacityRecoveryStage == XagmanCapacityRecoveryStage.OwnerDraining
            && xagmanCapacityOwnerCheckpoint != null && !string.IsNullOrWhiteSpace(xagmanCapacityDrainId)
            && tony.Role == XagmanRole.Tony && tony.XagmanEnabled && IsXagmanPeerFresh(tony)
            && IsXagmanPeerInCurrentRunPhase(tony)
            && tony.InstanceId == xagmanCapacityRecoveryCoordinatorInstanceId
            && tony.CapacityRecoveryCoordinatorInstanceId == tony.InstanceId
            && tony.CapacityRecoveryRegion == xagmanCapacityRecoveryRegion
            && string.IsNullOrWhiteSpace(tony.CapacityDrainId)
            && tony.CapacityRecoveryEpoch == (long)xagmanCapacityRecoveryEpoch + 1
            && !string.IsNullOrWhiteSpace(xagmanOwnerSupplyPassId)
            && tony.SupplyPassId == xagmanOwnerSupplyPassId
            && xagmanOwnerSupplyPassComplete && xagmanOwnerSupplyAtMenu;

    private void ResetXagmanNetworkTransientTimers()
    {
        var now = DateTime.UtcNow;
        xagmanSupplyBarrierSinceUtc = xagmanSupplyRecallSinceUtc = xagmanSupplyMissingPeerSinceUtc = DateTime.MinValue;
        xagmanSupplyInitialCohortSinceUtc = xagmanSupplyUnboundCohortSinceUtc = xagmanOwnerSupplyBindSinceUtc = DateTime.MinValue;
        xagmanSupplyInventoryUnreadableSinceUtc = xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
        xagmanSupplyInventoryCachedCharacter = string.Empty;
        xagmanSupplyInventoryCache.Clear();
        xagmanOwnerSupplyWaitSinceUtc = now;
        if (xagmanDeferredOwnerSinceUtc != DateTime.MinValue) xagmanDeferredOwnerSinceUtc = now;
        xagmanTonyAllOwnersCompletedObservedAtUtc = xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
        xagmanSweepServerDrainedSinceUtc = xagmanInvalidPendingServerPeerSinceUtc = DateTime.MinValue;
        if (xagmanSweepDiscoveryStartedUtc != DateTime.MinValue) xagmanSweepDiscoveryStartedUtc = now;
        xagmanCollectionBarrierStartedAtUtc = xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
        xagmanCapacitySafeSinceUtc = xagmanCapacityMissingPeerSinceUtc = DateTime.MinValue;
        if (xagmanCapacityTransitionStartedUtc != DateTime.MinValue) xagmanCapacityTransitionStartedUtc = now;
        if (xagmanSupplyDrainStartedAtUtc != DateTime.MinValue) xagmanSupplyDrainStartedAtUtc = now;
        xagmanSupplyDrainNextPollAtUtc = xagmanSupplyDrainQuietSinceUtc = xagmanSupplyReceivingPolicyWaitSinceUtc = DateTime.MinValue;
        ResetXagmanTonyApproachWait();
        xagmanLastTonyActionAtUtc = now;
        // Finished-client acknowledgements remain authoritative; pause only undelivered time bounds.
        foreach (var key in xagmanOwnerCompletionAckTimes.Keys.ToList())
            if (!xagmanOwnerCompletionDelivered.Contains(key)) xagmanOwnerCompletionAckTimes[key] = now;
    }
}
