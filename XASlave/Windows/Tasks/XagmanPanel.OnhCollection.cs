using System;
using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool onhCollectionTransferActive;
    private bool onhCollectionCancellationObserved;
    private bool onhCollectionPartnerUnavailable;
    private bool onhCollectionSettlementConfirmed;
    private bool onhCollectionLocalCleanup;
    private bool onhCollectionTradeAdmitted;
    private bool onhCollectionHeld;
    private bool onhCollectionObserverSubscribed;
    private object? onhAdmissionProvider;
    private bool onhIncomingRefusalPending;
    private DateTime onhIncomingRefusalSince;
    private uint onhCollectionEntityId;
    private long onhCollectionTaskRunId;
    private string onhCollectionSession = string.Empty;
    private string onhCollectionCharacter = string.Empty;
    private string onhCollectionPartner = string.Empty;
    private string onhTradeCancelledMessage = string.Empty;
    private string onhTradeCompletedMessage = string.Empty;
    private DateTime onhCollectionAdmittedAt;
    private string onhSaleReturnRecipient = string.Empty;
    private string onhSaleReturnLocalCharacter = string.Empty;
    private string onhSaleReturnText = string.Empty;
    private DateTime onhSaleReturnUntil;
    private bool onhSaleReturnUnavailable;
    private bool onhSaleReturnAcknowledged;
    private string onhSaleAckRecipient = string.Empty;
    private string onhSaleAckLocalCharacter = string.Empty;
    private string onhSaleAckNoticeText = string.Empty;
    private string onhSaleAckText = string.Empty;
    private uint onhSaleAckPartnerEntityId;
    private uint onhSaleAckLocalEntityId;
    private DateTime onhSaleAckUntil;

    private bool IsXagmanOnhCollectionTransfer => xagmanRunning
        && plugin.Configuration.XagmanOutsideNetworkHelper
        && xagmanActiveRole == XagmanRole.FranchiseOwner
        && xagmanOnhPhase == XagmanOnhPhase.FoGiving
        && onhCollectionTransferActive && !onhCollectionHeld
        && onhOutgoingSignalAmount == 0;

    private bool TryStartXagmanOnhCollectionObservation()
    {
        StopXagmanOnhCollectionObservation();
        try
        {
            // LogMessage 36/38 are the game's cancellation/completion chat records (base kind 57).
            // Default sheet language is the current game client language, not the plugin UI language.
            var sheet = Plugin.DataManager.GetExcelSheet<LogMessage>();
            if (!sheet.TryGetRow(36, out var cancelled) || !sheet.TryGetRow(38, out var completed)) return false;
            onhTradeCancelledMessage = cancelled.Text.ExtractText().Trim();
            onhTradeCompletedMessage = completed.Text.ExtractText().Trim();
            if (onhTradeCancelledMessage.Length == 0 || onhTradeCompletedMessage.Length == 0
                || onhTradeCancelledMessage == onhTradeCompletedMessage) return false;
            if (!plugin.DropboxQueue.TryCaptureXagmanProviderIdentity(out var provider, out _)) return false;
            onhAdmissionProvider = provider;
            plugin.MessageLog.MessageObserved += ObserveXagmanOnhCollectionMessage;
            onhCollectionObserverSubscribed = true;
            return true;
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "[Xagman ONH] Could not resolve localized trade message identities.");
            return false;
        }
    }

    private void StopXagmanOnhCollectionObservation()
    {
        if (onhCollectionObserverSubscribed)
            plugin.MessageLog.MessageObserved -= ObserveXagmanOnhCollectionMessage;
        onhCollectionObserverSubscribed = false;
        // A failed safety shutdown can be retried after the run itself is reset.
        // Keep its provider pin so that retry cannot silently take over a replacement.
        if (!xagmanTradeSafetySessionActive) onhAdmissionProvider = null;
        onhIncomingRefusalPending = false;
    }

    private void ResetXagmanOnhCollection()
    {
        onhCollectionTransferActive = onhCollectionCancellationObserved = false;
        onhCollectionPartnerUnavailable = onhCollectionSettlementConfirmed = false;
        onhCollectionLocalCleanup = onhCollectionTradeAdmitted = false;
        onhCollectionSession = onhCollectionCharacter = onhCollectionPartner = string.Empty;
        onhCollectionEntityId = 0;
    }

    private void MarkXagmanOnhLocalTradeCleanup()
    {
        if (plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            // Never erase a cancellation already observed before our cleanup started.
            onhCollectionLocalCleanup = true;
            onhCollectionTradeAdmitted = false;
        }
    }

    private bool CanMutateXagmanOnhProvider(out string failure)
    {
        failure = string.Empty;
        // The pin is captured before ONH admission is installed. Startup before that point
        // and connected mode keep their existing behavior; a pinned replacement is never owned.
        return onhAdmissionProvider == null
            || plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(onhAdmissionProvider, out failure);
    }

    private void ObserveXagmanOnhCollectionTradeStart(uint entityId)
    {
        if (!IsXagmanOnhCollectionTransfer || !IsXagmanOnhPairReady
            || onhCollectionPartnerUnavailable || onhCollectionCancellationObserved
            || onhCollectionLocalCleanup || !plugin.TaskRunner.IsRunning) return;
        var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        if (actor == null || actor.EntityId != entityId) return;
        onhCollectionTradeAdmitted = true;
        onhCollectionEntityId = entityId;
        onhCollectionTaskRunId = plugin.TaskRunner.CurrentRunId;
        onhCollectionSession = onhSession;
        onhCollectionCharacter = xagmanActiveCharacter;
        onhCollectionPartner = xagmanOnhEngagedPartner;
        onhCollectionAdmittedAt = DateTime.UtcNow;
    }

    private void ObserveXagmanOnhCollectionMessage(MessageLogEntry entry)
    {
        // Consume only fresh delivered events, never the retained chat snapshot or visible _TextError.
        if (!Plugin.Framework.IsInFrameworkUpdateThread || !IsXagmanOnhCollectionTransfer
            || !onhCollectionTradeAdmitted || onhCollectionLocalCleanup
            || ((int)entry.ChatType & 0x7f) != 57 || entry.CapturedUtc < onhCollectionAdmittedAt
            || !plugin.TaskRunner.IsRunning || plugin.TaskRunner.CurrentRunId != onhCollectionTaskRunId
            || onhSession != onhCollectionSession
            || !xagmanActiveCharacter.Equals(onhCollectionCharacter, StringComparison.OrdinalIgnoreCase)
            || !xagmanOnhEngagedPartner.Equals(onhCollectionPartner, StringComparison.OrdinalIgnoreCase)) return;
        var actor = FindXagmanOnhPlayer(onhCollectionPartner);
        if (actor == null || actor.EntityId != onhCollectionEntityId) return;
        if (entry.Message.Equals(onhTradeCompletedMessage, StringComparison.Ordinal))
        {
            onhCollectionTradeAdmitted = false;
            return;
        }
        if (!entry.Message.Equals(onhTradeCancelledMessage, StringComparison.Ordinal)) return;
        onhCollectionCancellationObserved = true;
        onhCollectionTradeAdmitted = false;
        // Stop queued dispatch in this event, before the next TaskRunner poll. Preserve the
        // bounded batch so that its normal quiet settlement can reconcile actual inventory.
        if (!TryStopXagmanDropboxTradeQueue(preserveCapacityBatch: true)
            || !TrySetXagmanDropboxAutoAccept(false))
        {
            HoldXagmanOnhCollection("Could not stop the admitted collection after cancellation.");
            return;
        }
        plugin.TaskRunner.AddLog($"Xagman ONH: collection cancellation observed for {onhCollectionPartner}; settling before retirement.");
    }

    private void HoldXagmanOnhCollection(string detail)
    {
        // Keep the active run's goals and retirement set. The operator's explicit Stop still resets them.
        onhCollectionHeld = true;
        MarkXagmanOnhLocalTradeCleanup();
        if (onhAdmissionProvider != null && plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(onhAdmissionProvider, out _))
        {
            TryStopXagmanDropboxTradeQueue();
            TrySetXagmanDropboxAutoAccept(false);
            if (OnhTradeOpen) TryAbortXagmanTradeWindow();
        }
        else CancelXagmanTradeBatch(); // Never mutate a replacement provider.
        plugin.TaskRunner.AddLog("Xagman ONH: collection held: " + detail);
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
            global::XASlave.Localization.UiMessage.Authored("ONH collection is paused because trade cleanup could not be confirmed. Stop the run after reviewing the task log."));
        plugin.TaskRunner.RequestHalt(detail);
    }

    private bool SuppressXagmanOnhIncomingTrade()
    {
        // Called before the native incoming status update. Both Dropbox's lock and yes/no
        // confirmation paths require Active or a busy task manager; suppress and read back both.
        onhIncomingRefusalPending = true;
        onhIncomingRefusalSince = DateTime.UtcNow;
        MarkXagmanOnhLocalTradeCleanup();
        if (onhAdmissionProvider == null || !plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(onhAdmissionProvider, out _)
            || !TryStopXagmanDropboxTradeQueue(preserveCapacityBatch: true)
            || !TrySetXagmanDropboxAutoAccept(false)
            || !plugin.DropboxQueue.TryConfirmXagmanAcceptanceSuppressed(onhAdmissionProvider, out _))
        {
            HoldXagmanOnhCollection("Could not suppress provider confirmation for a refused incoming trade.");
            return false;
        }
        if (onhCollectionTransferActive || xagmanTradeBatch != null || plugin.TaskRunner.IsRunning)
            HoldXagmanOnhCollection("An unrelated incoming trade interrupted an active transfer; goals are retained for review.");
        return true;
    }

    private bool UpdateXagmanOnhIncomingRefusal()
    {
        if (!onhIncomingRefusalPending) return true;
        if (onhCollectionHeld) return false;
        if (OnhTradeOpen || Plugin.Condition[ConditionFlag.TradeOpen]
            || !plugin.IpcClient.DropboxTryGetBusy(out var busy) || busy)
        {
            if ((DateTime.UtcNow - onhIncomingRefusalSince).TotalSeconds > 15)
                HoldXagmanOnhCollection("A refused incoming trade did not settle.");
            return false;
        }
        if (onhAdmissionProvider == null || !plugin.DropboxQueue.TryConfirmXagmanAcceptanceSuppressed(onhAdmissionProvider, out _))
        { HoldXagmanOnhCollection("The refused trade's provider state could not be confirmed."); return false; }
        onhIncomingRefusalPending = false;
        if (IsXagmanOnhPairReady && xagmanOnhPhase is XagmanOnhPhase.TonyReceiving or XagmanOnhPhase.FoReceiving
            or XagmanOnhPhase.FoSettle or XagmanOnhPhase.FoAwaitSale)
            return TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Authored("ONH receiver ready"));
        return true;
    }

    private bool TryRetireXagmanOnhCollectionPartner()
    {
        if (!onhCollectionPartnerUnavailable) return false;
        if (!onhCollectionSettlementConfirmed || OnhTradeOpen || Plugin.Condition[ConditionFlag.TradeOpen]
            || !plugin.IpcClient.DropboxTryGetBusy(out var busy) || busy
            || !TrySetXagmanDropboxAutoAccept(false))
        {
            HoldXagmanOnhCollection("The old collection exchange is not confirmed closed and idle.");
            return true;
        }
        ReleaseXagmanOnhOwnerPartner(true);
        return true;
    }

    private bool TryReceiveXagmanOnhSaleReturn(string[] parts, Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload sender)
    {
        if (parts.Length != 7 || parts[0] != "/xa" || parts[1] != "onh"
            || parts[3] is not ("returned" or "unavailable" or "returned-ack" or "unavailable-ack")) return false;
        if (onhCollectionHeld || onhIncomingRefusalPending
            || !TryRequireXagmanOnhProviderIdentity(out _)
            || !TryGetXagmanOnhPairingSender(sender, out var actor, out var key)
            || parts[4] != XagmanTradeVersionPolicy.Revision.ToString(CultureInfo.InvariantCulture)
            || !TryGetXagmanTradeVersions(out var localSlave, out var localDropbox, out _)
            || !TryMatchXagmanTradeVersions(parts[5], parts[6], localSlave, localDropbox, out _)) return true;
        var text = string.Join(' ', parts);
        if (parts[3] is "returned-ack" or "unavailable-ack")
        {
            if (xagmanActiveRole != XagmanRole.Tony || xagmanOnhPhase != XagmanOnhPhase.TonyAwaitSaleResolution
                || onhSaleReturnText.Length == 0 || DateTime.UtcNow >= onhSaleReturnUntil || parts[2] != onhSession
                || !key.Equals(onhSaleReturnRecipient, StringComparison.OrdinalIgnoreCase)
                || parts[3] != (onhSaleReturnUnavailable ? "unavailable-ack" : "returned-ack")
                || !TryAcceptXagmanOnhPeerVersions(parts[2], parts[5], parts[6], sender, false, out _)) return true;
            onhSaleReturnAcknowledged = true;
            return true;
        }
        // A duplicate notice can outlive the old exchange. Replay only its exact bounded
        // receipt; never apply it to a newly paired owner or mutate that exchange.
        if (xagmanActiveRole == XagmanRole.FranchiseOwner && text == onhSaleAckNoticeText
            && DateTime.UtcNow < onhSaleAckUntil && xagmanActiveCharacter == onhSaleAckLocalCharacter
            && key.Equals(onhSaleAckRecipient, StringComparison.OrdinalIgnoreCase)
            && actor.EntityId == onhSaleAckPartnerEntityId && Plugin.ObjectTable.LocalPlayer?.EntityId == onhSaleAckLocalEntityId)
        {
            SendXagmanOnhSaleResolutionTell(onhSaleAckRecipient, onhSaleAckText);
            return true;
        }
        if (xagmanActiveRole != XagmanRole.FranchiseOwner || parts[2] != onhSession
            || xagmanOnhPhase != XagmanOnhPhase.FoAwaitSale || !onhPartnerSelling
            || !key.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)
            || onhCollectionCancellationObserved || onhCollectionPartnerUnavailable
            || onhRetiredTonys.Contains(xagmanOnhEngagedPartner)
            || !TryAcceptXagmanOnhPeerVersions(parts[2], parts[5], parts[6], sender, false, out _, returningTony: true)) return true;
        // Seller cleanup was negotiated before travel. Confirm the old receiver is idle,
        // then resolve that session without reusing its transient entity IDs for native trade.
        if (plugin.TaskRunner.IsRunning || OnhTradeOpen || Plugin.Condition[ConditionFlag.TradeOpen]
            || !plugin.IpcClient.DropboxTryGetBusy(out var busy) || busy) return true;
        if (!TryGetXagmanOnhVersionSuffix(out var suffix, out _)) return true;
        var receipt = $"/xa onh {onhSession} {parts[3]}-ack {suffix}";
        onhPartnerSelling = false;
        ReleaseXagmanOnhOwnerPartner(parts[3] == "unavailable");
        if (onhCollectionHeld) return true;
        onhSaleAckRecipient = key;
        onhSaleAckLocalCharacter = xagmanActiveCharacter;
        onhSaleAckNoticeText = text;
        onhSaleAckText = receipt;
        onhSaleAckPartnerEntityId = actor.EntityId;
        onhSaleAckLocalEntityId = Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0;
        onhSaleAckUntil = DateTime.UtcNow.AddSeconds(35);
        SendXagmanOnhSaleResolutionTell(key, receipt);
        return true;
    }

    private void AnnounceXagmanOnhSaleReturn(bool unavailable = false)
    {
        if (!TryGetXagmanOnhVersionSuffix(out var suffix, out var failure))
        { RejectXagmanOnhTradeVersions(failure); return; }
        if (OnhTradeOpen || Plugin.Condition[ConditionFlag.TradeOpen]
            || !plugin.IpcClient.DropboxTryGetBusy(out var busy) || busy
            || !TrySetXagmanDropboxAutoAccept(false))
        { HoldXagmanOnhCollection("Sale return could not confirm the old trade is closed and idle."); return; }
        onhSaleReturnRecipient = xagmanOnhEngagedPartner;
        onhSaleReturnLocalCharacter = xagmanActiveCharacter;
        onhSaleReturnText = $"/xa onh {onhSession} {(unavailable ? "unavailable" : "returned")} {suffix}";
        onhSaleReturnUnavailable = unavailable;
        onhSaleReturnAcknowledged = false;
        onhSaleReturnUntil = DateTime.UtcNow.AddSeconds(30);
        SetXagmanOnhPhase(XagmanOnhPhase.TonyAwaitSaleResolution);
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
            global::XASlave.Localization.UiMessage.Authored("Waiting for ONH sale-return confirmation."));
    }

    private void UpdateXagmanOnhSaleReturnNotice()
    {
        if (onhSaleReturnText.Length == 0 || onhSaleReturnAcknowledged) return;
        if (DateTime.UtcNow >= onhSaleReturnUntil || xagmanActiveCharacter != onhSaleReturnLocalCharacter)
        {
            onhSaleReturnText = string.Empty;
            HoldXagmanOnhCollection("The owner did not confirm the sale-return result; the old exchange remains unresolved.");
            return;
        }
        if (onhTellOutbox.Count != 0 || (DateTime.UtcNow - onhLastTellSent).TotalSeconds < 2) return;
        SendXagmanOnhSaleResolutionTell(onhSaleReturnRecipient, onhSaleReturnText);
    }

    private void SendXagmanOnhSaleResolutionTell(string recipient, string text)
    {
        if (onhCollectionHeld || onhIncomingRefusalPending || !TryRequireXagmanOnhProviderIdentity(out _)
            || (DateTime.UtcNow - onhLastTellSent).TotalSeconds < 1) return;
        var actor = FindXagmanOnhPlayer(recipient);
        if (actor == null || text.Length == 0) return;
        onhLastTellSent = DateTime.UtcNow;
        ChatHelper.TrySend($"/tell {GetXagmanOnhActorKey(actor)} {text}");
    }

    private void ResetXagmanOnhSaleResolution()
    {
        onhSaleReturnText = onhSaleReturnRecipient = onhSaleReturnLocalCharacter = string.Empty;
        onhSaleAckRecipient = onhSaleAckLocalCharacter = onhSaleAckNoticeText = onhSaleAckText = string.Empty;
        onhSaleReturnUnavailable = onhSaleReturnAcknowledged = false;
        onhSaleAckPartnerEntityId = onhSaleAckLocalEntityId = 0;
        onhSaleReturnUntil = onhSaleAckUntil = DateTime.MinValue;
    }
}
