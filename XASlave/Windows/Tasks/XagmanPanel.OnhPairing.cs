using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    // Separate from capacity/control-signal revisions: old invitation peers cannot
    // accidentally negotiate this owner-led collection contract.
    private const int XagmanOnhPairingRevision = 1;
    private const int XagmanOnhRegionalPairingRevision = 2;
    private string onhPairRequest = string.Empty;
    private string onhPairSession = string.Empty;
    private string onhPairPartnerCharacter = string.Empty;
    private uint onhPairPartnerEntityId;
    private uint onhPairLocalEntityId;
    private DateTime onhPairCreatedUtc;
    private DateTime onhPairExpiresUtc;
    private DateTime onhPairNextSendUtc;
    private bool onhPairConfirmed;
    private bool onhPairReady;
    private readonly Dictionary<string, DateTime> onhDeferredTonys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> onhClosedPairRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> onhPairLastReplyUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Request, string Session, uint LocalEntity, uint PartnerEntity, string Slave, string Dropbox)>
        onhCompletedPairReceipts = new(StringComparer.OrdinalIgnoreCase);
    private bool onhOwnerCompletionAcknowledged;
    private DateTime onhOwnerCompletionDeadlineUtc;
    private DateTime onhOwnerCompletionNextSendUtc;
    private DateTime onhLastTerminalOwnerUtc;

    private bool IsXagmanOnhPairReady => !onhCollectionHeld && !onhIncomingRefusalPending
        && onhPairReady && onhPairRequest.Length != 0
        && onhPairSession == onhSession && onhPairPartnerEntityId == onhVersionPartnerEntityId
        && onhPairPartnerCharacter.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)
        && Plugin.ObjectTable.LocalPlayer?.EntityId == onhPairLocalEntityId;

    private bool CanRepeatXagmanOnhReady => IsXagmanOnhPairReady
        && xagmanOnhPhase == XagmanOnhPhase.TonyReceiving
        && xagmanDropboxAutoAcceptState == true && !xagmanBusyTradeAdmissionPending;

    private void ResetXagmanOnhPairing(bool newRun = false)
    {
        if (onhPairRequest.Length != 0 && onhPairPartnerCharacter.Length != 0)
            onhClosedPairRequests[onhPairPartnerCharacter] = onhPairRequest;
        onhPairRequest = onhPairSession = onhPairPartnerCharacter = string.Empty;
        onhPairPartnerEntityId = onhPairLocalEntityId = 0;
        onhPairCreatedUtc = onhPairExpiresUtc = onhPairNextSendUtc = DateTime.MinValue;
        onhPairConfirmed = onhPairReady = false;
        onhOwnerCompletionAcknowledged = false;
        onhOwnerCompletionDeadlineUtc = onhOwnerCompletionNextSendUtc = DateTime.MinValue;
        // Deferral is temporary and is deliberately retained by exchange resets.
        // Otherwise the nearest busy Tony would immediately win discovery again.
        if (!newRun) return;
        onhDeferredTonys.Clear();
        onhClosedPairRequests.Clear();
        onhPairLastReplyUtc.Clear();
        onhCompletedPairReceipts.Clear();
        onhLastTerminalOwnerUtc = DateTime.MinValue;
    }

    private static string GetXagmanOnhActorKey(IPlayerCharacter actor)
        => actor.HomeWorld.RowId == 0 ? string.Empty : $"{actor.Name.TextValue}@{actor.HomeWorld.Value.Name}";

    private static bool IsXagmanOnhFullKey(string key)
        => !string.IsNullOrWhiteSpace(key) && key.IndexOf('@') > 0
            && key.LastIndexOf('@') == key.IndexOf('@') && !key.EndsWith('@');

    private bool IsXagmanOnhTonyDeferred(string key)
    {
        if (!onhDeferredTonys.TryGetValue(key, out var until)) return false;
        if (DateTime.UtcNow < until) return true;
        onhDeferredTonys.Remove(key);
        return false;
    }

    private bool FocusXagmanOnhPartner(string key)
    {
        var actor = FindXagmanOnhPlayer(key);
        if (actor == null || actor.EntityId == 0
            || (key.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)
                && onhPairPartnerEntityId != 0 && actor.EntityId != onhPairPartnerEntityId)) return false;
        Plugin.TargetManager.Target = actor;
        Plugin.TargetManager.FocusTarget = actor;
        return Plugin.TargetManager.Target?.EntityId == actor.EntityId
            && Plugin.TargetManager.FocusTarget?.EntityId == actor.EntityId;
    }

    private bool IsXagmanOnhPartnerInRange(string key)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        var actor = FindXagmanOnhPlayer(key);
        return local != null && actor != null && Vector3.Distance(local.Position, actor.Position)
            - local.HitboxRadius - actor.HitboxRadius <= XagmanOnhTradeStopDistance;
    }

    private bool ApproachXagmanOnhPartner(string key)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        var actor = FindXagmanOnhPlayer(key);
        if (local == null || actor == null || !FocusXagmanOnhPartner(key)) return false;
        if (IsXagmanOnhPartnerInRange(key))
        {
            if (!IsXagmanMovementActive()) return true;
            plugin.IpcClient.VnavStop();
            return false;
        }
        if (!IsXagmanMovementActive() && plugin.IpcClient.VnavIsReady())
            plugin.IpcClient.VnavPathfindAndMoveCloseTo(actor.Position, false, XagmanOnhTradeStopDistance);
        return false;
    }

    private bool TryGetXagmanOnhPairingSender(PlayerPayload sender, out IPlayerCharacter actor, out string key)
    {
        actor = null!;
        key = string.Empty;
        if (!xagmanRunning || !plugin.Configuration.XagmanOutsideNetworkHelper
            || xagmanActiveRole is not (XagmanRole.Tony or XagmanRole.FranchiseOwner)
            || Plugin.ObjectTable.LocalPlayer is not { } local
            || !xagmanActiveCharacter.Equals(MonthlyReloggerTask.GetCurrentCharacterNameWorld(), StringComparison.OrdinalIgnoreCase)) return false;
        var matches = Plugin.ObjectTable.OfType<IPlayerCharacter>().Where(p => p.IsTargetable && p.EntityId != 0
            && p.HomeWorld.RowId == sender.World.RowId
            && p.Name.TextValue.Equals(sender.PlayerName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1 || Vector3.Distance(local.Position, matches[0].Position) > XagmanOnhScanRadius) return false;
        actor = matches[0];
        var actorKey = GetXagmanOnhActorKey(actor);
        key = actorKey;
        var roster = GetXagmanOnhPartnerRoster();
        return IsXagmanOnhFullKey(actorKey) && IsXagmanOnhPartnerInActiveRegion(actorKey)
            && roster.Any(entry => entry.Trim().Equals(actorKey, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetXagmanOnhRegionalPairRoute(out string region, out uint worldId)
    {
        region = GetXagmanRegionOfChar(xagmanActiveCharacter);
        var world = WorldData.Worlds.FirstOrDefault(w => w.Name.Equals(GetXagmanActiveMeetWorld(), StringComparison.OrdinalIgnoreCase));
        worldId = world?.Id ?? 0;
        return world != null && world.Region.Equals(region, StringComparison.OrdinalIgnoreCase)
            && Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId == worldId
            && IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte());
    }

    private bool SendXagmanOnhPairingTell(string operation, string request, string session, string recipient)
    {
        if (onhCollectionHeld || onhIncomingRefusalPending || !TryRequireXagmanOnhProviderIdentity(out _)) return false;
        var actor = FindXagmanOnhPlayer(recipient);
        var local = Plugin.ObjectTable.LocalPlayer;
        if (actor == null || local == null || local.EntityId == 0 || actor.EntityId == 0
            || !TryGetXagmanTradeVersions(out var slave, out var dropbox, out _)) return false;
        if (onhPairLastReplyUtc.TryGetValue(recipient, out var last) && (DateTime.UtcNow - last).TotalSeconds < 1) return false;
        var revision = xagmanOnhRegionalMatching ? XagmanOnhRegionalPairingRevision : XagmanOnhPairingRevision;
        var text = FormattableString.Invariant($"/xa onh-pair {revision} {operation} {request} {session} {local.EntityId} {actor.EntityId} {slave} {dropbox}");
        if (xagmanOnhRegionalMatching)
        {
            if (!IsXagmanOnhPartnerInActiveRegion(recipient)
                || !TryGetXagmanOnhRegionalPairRoute(out var region, out var worldId)) return false;
            text += FormattableString.Invariant($" {region} {worldId}");
        }
        onhPairLastReplyUtc[recipient] = DateTime.UtcNow;
        return ChatHelper.TrySend($"/tell {GetXagmanOnhActorKey(actor)} {text}");
    }

    private void BeginXagmanOnhOwnerPairing(string key)
    {
        if (!TryRequireXagmanOnhProviderIdentity(out _)) return;
        var actor = FindXagmanOnhPlayer(key);
        var local = Plugin.ObjectTable.LocalPlayer;
        if (actor == null || local == null || onhRetiredTonys.Contains(key)) return;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false,
            global::XASlave.Localization.UiMessage.Authored("ONH collection negotiation"))) return;
        ResetXagmanOnhExchange();
        xagmanOnhEngagedPartner = xagmanActiveTradePartner = GetXagmanOnhActorKey(actor);
        onhPairPartnerCharacter = xagmanOnhEngagedPartner;
        onhPairRequest = Guid.NewGuid().ToString("N");
        onhPairLocalEntityId = local.EntityId;
        onhPairPartnerEntityId = actor.EntityId;
        onhPairCreatedUtc = DateTime.UtcNow;
        onhPairExpiresUtc = onhPairCreatedUtc.AddSeconds(20);
        onhPairNextSendUtc = DateTime.MinValue;
        xagmanStatus = XagmanStatus.Queued;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
            global::XASlave.Localization.UiMessage.Authored("Requesting an ONH collection exchange."));
        SetXagmanOnhPhase(XagmanOnhPhase.FoAwaitReservation);
    }

    private void DeferXagmanOnhTony()
    {
        var partner = xagmanOnhEngagedPartner;
        if (!onhPairReady && onhPairRequest.Length != 0 && partner.Length != 0)
            SendXagmanOnhPairingTell("abandon", onhPairRequest,
                onhPairSession.Length == 0 ? "-" : onhPairSession, partner);
        if (partner.Length != 0) onhDeferredTonys[partner] = DateTime.UtcNow.AddSeconds(10);
        ReleaseXagmanOnhOwnerPartner(false);
    }

    private void DriveXagmanOnhOwnerPairing()
    {
        var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        if (actor == null || actor.EntityId != onhPairPartnerEntityId
            || Plugin.ObjectTable.LocalPlayer?.EntityId != onhPairLocalEntityId)
        { DeferXagmanOnhTony(); return; }
        if (onhPairReady)
        {
            if (!TryRequireXagmanOnhTradeVersions(out var failure))
            { RejectXagmanOnhTradeVersions(failure); return; }
            if (!ApproachXagmanOnhPartner(xagmanOnhEngagedPartner)) return;
            var steps = BuildXagmanOnhTransfer(XagmanOnhGiveRemaining(), false);
            if (TryStartXagmanOnhSubTask(steps))
            {
                xagmanStatus = XagmanStatus.Trading;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
                    global::XASlave.Localization.UiMessage.Authored("ONH collection is in progress."));
                SetXagmanOnhPhase(XagmanOnhPhase.FoGiving);
            }
            return;
        }
        if (DateTime.UtcNow >= onhPairExpiresUtc) { DeferXagmanOnhTony(); return; }
        if (DateTime.UtcNow < onhPairNextSendUtc) return;
        onhPairNextSendUtc = DateTime.UtcNow.AddSeconds(2);
        SendXagmanOnhPairingTell(onhPairSession.Length == 0 ? "request" : "confirm", onhPairRequest,
            onhPairSession.Length == 0 ? "-" : onhPairSession, xagmanOnhEngagedPartner);
    }

    private void DriveXagmanOnhTonyPairing()
    {
        var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        if (actor == null || actor.EntityId != onhPairPartnerEntityId
            || Plugin.ObjectTable.LocalPlayer?.EntityId != onhPairLocalEntityId
            || DateTime.UtcNow >= onhPairExpiresUtc)
        {
            if (!TrySetXagmanDropboxAutoAcceptOrStop(false,
                global::XASlave.Localization.UiMessage.Authored("ONH partner negotiation reset"))) return;
            ResetXagmanOnhExchange();
            xagmanOnhEngagedPartner = xagmanActiveTradePartner = string.Empty;
            SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
            return;
        }
        if (onhPairConfirmed)
        {
            if (!TryRequireXagmanOnhTradeVersions(out var failure))
            { RejectXagmanOnhTradeVersions(failure); return; }
            if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Authored("ONH collection receiver"))) return;
            if (xagmanDropboxAutoAcceptState != true || xagmanBusyTradeAdmissionPending) return;
            onhPairReady = true;
            onhLastActivity = DateTime.UtcNow;
            xagmanStatus = XagmanStatus.Trading;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
                global::XASlave.Localization.UiMessage.Authored("ONH collection is in progress."));
            SetXagmanOnhPhase(XagmanOnhPhase.TonyReceiving);
            SendXagmanOnhPairingTell("ready", onhPairRequest, onhPairSession, xagmanOnhEngagedPartner);
            return;
        }
        if (DateTime.UtcNow < onhPairNextSendUtc) return;
        onhPairNextSendUtc = DateTime.UtcNow.AddSeconds(2);
        SendXagmanOnhPairingTell("reserve", onhPairRequest, onhPairSession, xagmanOnhEngagedPartner);
    }

    private void BeginXagmanOnhCompletionConfirmation(bool owner)
    {
        onhOwnerCompletionAcknowledged = false;
        onhOwnerCompletionDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
        onhOwnerCompletionNextSendUtc = DateTime.MinValue;
        var phase = owner ? XagmanOnhPhase.FoConfirmCompletion : XagmanOnhPhase.TonyAwaitOwnerCompletion;
        xagmanStatus = XagmanStatus.Queued;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, GetXagmanOnhPhaseMessage(phase));
        SetXagmanOnhPhase(phase);
    }

    private void DriveXagmanOnhCompletionConfirmation()
    {
        if (xagmanOnhPhase == XagmanOnhPhase.FoConfirmCompletion && onhOwnerCompletionAcknowledged)
        {
            AdvanceXagmanOnhCharacter(false);
            return;
        }
        if (DateTime.UtcNow >= onhOwnerCompletionDeadlineUtc)
        {
            if (!plugin.TaskRunner.IncompleteCharacters.Contains(xagmanActiveCharacter))
                plugin.TaskRunner.RecordIncompleteCharacter(xagmanActiveCharacter);
            HoldXagmanOnhRegionalWork(global::XASlave.Localization.UiMessage.Authored("ONH completion was not confirmed; remaining regional work has not been marked complete."));
            return;
        }
        if (xagmanOnhPhase != XagmanOnhPhase.FoConfirmCompletion || DateTime.UtcNow < onhOwnerCompletionNextSendUtc) return;
        onhOwnerCompletionNextSendUtc = DateTime.UtcNow.AddSeconds(2);
        SendXagmanOnhPairingTell("complete", onhPairRequest, onhPairSession, xagmanOnhEngagedPartner);
    }

    private bool TryReceiveXagmanOnhPairingTell(PlayerPayload sender, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "/xa" || parts[1] != "onh-pair") return false;
        if (!Plugin.Framework.IsInFrameworkUpdateThread)
        {
            Plugin.RunOnGameThread(() => TryReceiveXagmanOnhPairingTell(sender, text));
            return true;
        }
        var expectedRevision = xagmanOnhRegionalMatching ? XagmanOnhRegionalPairingRevision : XagmanOnhPairingRevision;
        if (onhCollectionHeld || onhIncomingRefusalPending || !TryRequireXagmanOnhProviderIdentity(out _)
            || parts.Length != (xagmanOnhRegionalMatching ? 12 : 10)
            || parts[2] != expectedRevision.ToString(CultureInfo.InvariantCulture)
            || !XagmanTradeCapacityProtocol.IsRequestIdValid(parts[4])
            || !uint.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var senderEntity)
            || !uint.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out var receiverEntity)
            || !TryGetXagmanOnhPairingSender(sender, out var actor, out var key)
            || actor.EntityId != senderEntity || Plugin.ObjectTable.LocalPlayer?.EntityId != receiverEntity
            || !XagmanTradeVersionPolicy.TryNormalize(parts[8], out var slave)
            || !XagmanTradeVersionPolicy.TryNormalize(parts[9], out var dropbox)) return true;
        if (xagmanOnhRegionalMatching
            && (!TryGetXagmanOnhRegionalPairRoute(out var region, out var worldId)
                || !parts[10].Equals(region, StringComparison.Ordinal)
                || !uint.TryParse(parts[11], NumberStyles.None, CultureInfo.InvariantCulture, out var advertisedWorld)
                || advertisedWorld != worldId || actor.CurrentWorld.RowId != worldId)) return true;
        var operation = parts[3];
        var request = parts[4];
        var session = parts[5];
        if (operation is "request" or "busy" or "mismatch")
        {
            if (session != "-") return true;
        }
        else if (operation == "abandon" && session == "-") { }
        else if (!XagmanTradeCapacityProtocol.IsRequestIdValid(session)) return true;

        // A lost final reply may be retried after this Tony has released the exchange.
        // Only the exact completed request, actors, route and versions may replay its receipt.
        if (xagmanOnhRegionalMatching && operation == "complete" && xagmanActiveRole == XagmanRole.Tony
            && onhCompletedPairReceipts.TryGetValue(key, out var receipt)
            && receipt.Request == request && receipt.Session == session
            && receipt.LocalEntity == receiverEntity && receipt.PartnerEntity == senderEntity
            && receipt.Slave == slave && receipt.Dropbox == dropbox
            && TryGetXagmanTradeVersions(out var currentSlave, out var currentDropbox, out _)
            && currentSlave == receipt.Slave && currentDropbox == receipt.Dropbox)
        {
            SendXagmanOnhPairingTell("complete-ack", request, session, key);
            return true;
        }

        if (operation == "request" && xagmanActiveRole == XagmanRole.Tony)
        {
            if (request == onhPairRequest && key.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)
                && actor.EntityId == onhPairPartnerEntityId)
            {
                if (!onhPairReady && DateTime.UtcNow >= onhPairExpiresUtc) return true;
                if (xagmanOnhPhase == XagmanOnhPhase.TonyAwaitConfirmation || CanRepeatXagmanOnhReady)
                    SendXagmanOnhPairingTell(CanRepeatXagmanOnhReady ? "ready" : "reserve", request, onhPairSession, key);
                return true;
            }
            if (onhClosedPairRequests.TryGetValue(key, out var closed) && closed == request) return true;
            if (xagmanOnhPhase != XagmanOnhPhase.TonySearch || xagmanOnhEngagedPartner.Length != 0
                || plugin.TaskRunner.IsRunning || OnhTradeOpen || !plugin.IpcClient.DropboxTryGetBusy(out var busy) || busy
                || xagmanOnhCompletedPartners.Contains(key) || GetXagmanOwnGil() < OnhGilFloor + 3)
            { SendXagmanOnhPairingTell("busy", request, "-", key); return true; }
            if (!TryGetXagmanTradeVersions(out var localSlave, out var localDropbox, out var failure))
            { RejectXagmanOnhTradeVersions(failure); return true; }
            if (!TryMatchXagmanTradeVersions(localSlave, localDropbox, slave, dropbox, out _))
            { SendXagmanOnhPairingTell("mismatch", request, "-", key); return true; }
            if (!TrySetXagmanDropboxAutoAcceptOrStop(false,
                global::XASlave.Localization.UiMessage.Authored("ONH collection negotiation"))) return true;
            ResetXagmanOnhExchange();
            xagmanOnhEngagedPartner = xagmanActiveTradePartner = key;
            onhPairPartnerCharacter = key;
            onhPairRequest = request;
            onhPairSession = onhSession = Guid.NewGuid().ToString("N");
            onhPairLocalEntityId = receiverEntity;
            onhPairPartnerEntityId = senderEntity;
            onhPairCreatedUtc = DateTime.UtcNow;
            onhPairExpiresUtc = onhPairCreatedUtc.AddSeconds(20);
            onhPairNextSendUtc = DateTime.MinValue;
            if (!TryPinXagmanOnhTonyVersions(onhSession, localSlave, localDropbox, sender, out failure))
            { RejectXagmanOnhTradeVersions(failure); return true; }
            xagmanStatus = XagmanStatus.Queued;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
                global::XASlave.Localization.UiMessage.Authored("Waiting for ONH collection confirmation."));
            SetXagmanOnhPhase(XagmanOnhPhase.TonyAwaitConfirmation);
            return true;
        }

        if (request != onhPairRequest || !key.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)
            || senderEntity != onhPairPartnerEntityId || receiverEntity != onhPairLocalEntityId) return true;
        if (xagmanOnhRegionalMatching && session == onhPairSession)
        {
            if (operation == "complete" && xagmanActiveRole == XagmanRole.Tony
                && xagmanOnhPhase == XagmanOnhPhase.TonyAwaitOwnerCompletion)
            {
                if (DateTime.UtcNow >= onhOwnerCompletionDeadlineUtc) return true;
                if (!TryAcceptXagmanOnhPeerVersions(session, slave, dropbox, sender, false, out var failure))
                { RejectXagmanOnhTradeVersions(failure); return true; }
                if (!SendXagmanOnhPairingTell("complete-ack", request, session, key)) return true;
                onhCompletedPairReceipts[key] = (Request: request, Session: session, LocalEntity: receiverEntity,
                    PartnerEntity: senderEntity, Slave: slave, Dropbox: dropbox);
                FinishXagmanOnhTonyEngagement();
                return true;
            }
            if (operation == "complete-ack" && xagmanActiveRole == XagmanRole.FranchiseOwner
                && xagmanOnhPhase == XagmanOnhPhase.FoConfirmCompletion)
            {
                if (DateTime.UtcNow >= onhOwnerCompletionDeadlineUtc) return true;
                if (!TryAcceptXagmanOnhPeerVersions(session, slave, dropbox, sender, false, out var failure))
                { RejectXagmanOnhTradeVersions(failure); return true; }
                onhOwnerCompletionAcknowledged = true;
                return true;
            }
        }
        if (operation == "abandon" && xagmanActiveRole == XagmanRole.Tony
            && (session == onhPairSession || (session == "-" && !onhPairConfirmed))
            && xagmanOnhPhase is XagmanOnhPhase.TonyAwaitConfirmation or XagmanOnhPhase.TonyReceiving
            && onhTradeStarted < onhPairCreatedUtc && !OnhTradeOpen && !IsXagmanTradeCapacitySenderActive()
            && plugin.IpcClient.DropboxTryGetBusy(out var abandonedBusy) && !abandonedBusy)
        {
            // Coordination cancellation is not native trade cancellation. It can
            // release only a reservation that never began an item/control trade.
            if (!TrySetXagmanDropboxAutoAcceptOrStop(false,
                global::XASlave.Localization.UiMessage.Authored("ONH partner negotiation reset"))) return true;
            ResetXagmanOnhExchange();
            xagmanOnhEngagedPartner = xagmanActiveTradePartner = string.Empty;
            SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
            return true;
        }
        if (!onhPairReady && DateTime.UtcNow >= onhPairExpiresUtc) return true;
        if (xagmanActiveRole == XagmanRole.FranchiseOwner && xagmanOnhPhase == XagmanOnhPhase.FoAwaitReservation)
        {
            if (operation == "busy") { DeferXagmanOnhTony(); return true; }
            if (operation == "mismatch")
            {
                if (!TryGetXagmanTradeVersions(out var localSlave, out var localDropbox, out var failure)
                    || !TryMatchXagmanTradeVersions(slave, dropbox, localSlave, localDropbox, out failure))
                    RejectXagmanOnhTradeVersions(failure);
                return true;
            }
            if (operation == "reserve")
            {
                if (onhPairSession.Length != 0 && onhPairSession != session) return true;
                if (!TryAcceptXagmanOnhPeerVersions(session, slave, dropbox, sender,
                    initialTonyOffer: onhPairSession.Length == 0, out var failure))
                { RejectXagmanOnhTradeVersions(failure); return true; }
                onhPairSession = onhSession = session;
                onhPairNextSendUtc = DateTime.MinValue;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
                    global::XASlave.Localization.UiMessage.Authored("Waiting for ONH receiver readiness."));
                return true;
            }
            if (operation == "ready" && onhPairSession == session)
            {
                if (!TryAcceptXagmanOnhPeerVersions(session, slave, dropbox, sender, false, out var failure))
                { RejectXagmanOnhTradeVersions(failure); return true; }
                onhPairReady = true;
            }
            return true;
        }
        if (xagmanActiveRole == XagmanRole.Tony && operation == "confirm" && session == onhPairSession
            && (xagmanOnhPhase == XagmanOnhPhase.TonyAwaitConfirmation || CanRepeatXagmanOnhReady))
        {
            if (!TryAcceptXagmanOnhPeerVersions(session, slave, dropbox, sender, false, out var failure))
            { RejectXagmanOnhTradeVersions(failure); return true; }
            onhPairConfirmed = true;
            if (CanRepeatXagmanOnhReady) SendXagmanOnhPairingTell("ready", request, session, key);
        }
        return true;
    }
}
