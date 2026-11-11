using System;
using System.Globalization;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private enum XagmanTradeCapacityState { Pending, Ready, Invalid }
    // Covers 15 seconds for capacity acknowledgement, a 60-second offer, 15 seconds
    // to stop, 0.75 seconds to settle, the next 15-second request window, and publication margin.
    private const int XagmanCapacitySenderHoldSeconds = 110;

    private sealed class XagmanCapacityRequest
    {
        public required string Id;
        public required XagmanTradeCapacityIdentity Local;
        public required XagmanTradeCapacityIdentity Peer;
        public required DateTime StartedUtc;
        public bool Onh;
        public int Sequence;
        public uint ReceiverEntityId;
        public long TaskRunId;
        public DateTime LastTellQueuedUtc;
        public DateTime ReceivedUtc;
        public DateTime RemoteObservedUtc;
        public bool Known;
        public int FreeSlots;
    }

    private sealed record XagmanCapacityResponse(string Id, XagmanTradeCapacityIdentity Local,
        XagmanTradeCapacityIdentity Peer, bool Known, int FreeSlots, DateTime ObservedUtc);

    private XagmanCapacityRequest? xagmanCapacityRequest;
    private XagmanCapacityResponse? xagmanCapacityResponse;
    private string xagmanCapacitySequenceSession = string.Empty;
    private int xagmanCapacitySequence;
    private int xagmanCapacityReceivedSequence;
    private string xagmanCapacityReceivedRequest = string.Empty;
    private bool xagmanCapacityReceivedEnded;

    private void ResetXagmanTradeCapacityExchange(bool notifyRemote = true)
    {
        var outgoing = xagmanCapacityRequest;
        xagmanCapacityRequest = null;
        xagmanCapacityResponse = null;
        // Retain sequence high-water marks within the same ONH session, including between batches.
        var retained = onhTellOutbox.Where(text => !text.Contains(" capreq ", StringComparison.Ordinal)
            && !text.Contains(" cap ", StringComparison.Ordinal)).ToArray();
        onhTellOutbox.Clear();
        foreach (var text in retained) onhTellOutbox.Enqueue(text);
        if (notifyRemote && outgoing is { Onh: true } && outgoing.Local.Scope == "onh/" + onhSession
            && onhTellOutbox.Count < 8)
            onhTellOutbox.Enqueue($"/xa onh {onhSession} capend {XagmanTradeCapacityProtocol.Revision} {outgoing.Sequence} {outgoing.Id}");
    }

    private string GetXagmanTradeCapacityScope()
        => plugin.Configuration.XagmanOutsideNetworkHelper ? "onh/" + onhSession
            : string.Join("/", xagmanNetworkSession, xagmanNetworkEpoch.ToString(CultureInfo.InvariantCulture),
                IsXagmanCollectionFirstRunActive() ? xagmanRunId : string.Empty,
                IsXagmanCollectionFirstRunActive() ? ((int)xagmanRunPhase).ToString(CultureInfo.InvariantCulture) : string.Empty,
                IsXagmanSupplyCycleActive() ? (xagmanActiveRole == XagmanRole.Tony ? xagmanSupplyPassId : xagmanOwnerSupplyPassId) : string.Empty);

    private static string GetXagmanTradeCapacityPeerScope(XagmanPeerPresence peer)
        => string.Join("/", peer.RecoverySessionId, peer.RecoveryEpoch.ToString(CultureInfo.InvariantCulture),
            peer.CollectionFirstEnabled ? peer.RunId : string.Empty,
            peer.CollectionFirstEnabled ? ((int)peer.RunPhase).ToString(CultureInfo.InvariantCulture) : string.Empty,
            peer.SupplyPassId);

    private bool TryGetXagmanTradeCapacityLocal(out XagmanTradeCapacityIdentity identity)
    {
        identity = null!;
        var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        if (!xagmanRunning || !Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null
            || xagmanStatus is XagmanStatus.Error or XagmanStatus.Idle or XagmanStatus.Relogging or XagmanStatus.ReturningHome
            || Plugin.PlayerState.ContentId == 0 || string.IsNullOrWhiteSpace(current)
            || !current.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(xagmanActiveTradePartner)) return false;
        if (plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            if (!XagmanTradeCapacityProtocol.IsRequestIdValid(onhSession)) return false;
            var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
            if (actor == null || actor.EntityId == 0) return false;
            identity = new(plugin.InstanceId, current, Plugin.PlayerState.ContentId, string.Empty,
                $"{actor.Name.TextValue}@{actor.HomeWorld.Value.Name}", GetXagmanTradeCapacityScope());
            return true;
        }
        if (IsXagmanNetworkRecoveryHolding || string.IsNullOrWhiteSpace(xagmanNetworkSession)
            || string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId)) return false;
        identity = new(plugin.InstanceId, current, Plugin.PlayerState.ContentId, xagmanActiveTradePartnerInstanceId,
            xagmanActiveTradePartner, GetXagmanTradeCapacityScope());
        return true;
    }

    private bool TryGetXagmanTradeCapacityConnectedPeer(XagmanTradeCapacityIdentity local,
        out XagmanPeerPresence peer, out XagmanTradeCapacityIdentity identity)
    {
        peer = null!;
        identity = null!;
        var matches = plugin.XagmanPeers.Peers.Where(candidate => candidate.InstanceId == local.PartnerInstanceId).ToList();
        if (matches.Count != 1) return false;
        var candidate = matches[0];
        if (!candidate.XagmanEnabled || !candidate.IsLoggedIn || candidate.ContentId == 0
            || candidate.TradeCapacityEntityId == 0
            || candidate.Role == xagmanActiveRole || candidate.Role is not (XagmanRole.Tony or XagmanRole.FranchiseOwner)
            || !XagmanTradeCapacityProtocol.IsFresh(DateTime.UtcNow, candidate.LastSeenUtc, 5)
            || !string.Equals(candidate.ActiveCharacter, local.PartnerCharacter, StringComparison.OrdinalIgnoreCase)
            || !string.Equals($"{candidate.CharacterName}@{candidate.HomeWorld}", local.PartnerCharacter, StringComparison.OrdinalIgnoreCase)
            || candidate.ActiveTradePartnerInstanceId != plugin.InstanceId
            || !string.Equals(candidate.ActiveTradePartner, local.Character, StringComparison.OrdinalIgnoreCase)
            || !IsXagmanPeerInCurrentRunPhase(candidate) || !IsXagmanPeerInSupplyCoordinatorScope(candidate)
            || candidate.CollectionFirstEnabled != IsXagmanCollectionFirstRunActive()
            || (IsXagmanSupplyCycleActive() && candidate.SupplyPassId != (xagmanActiveRole == XagmanRole.Tony ? xagmanSupplyPassId : xagmanOwnerSupplyPassId))
            || candidate.RecoverySuspended || candidate.RecoverySessionId != xagmanNetworkSession
            || candidate.RecoveryEpoch != xagmanNetworkEpoch
            || candidate.Status is XagmanStatus.Error or XagmanStatus.Idle or XagmanStatus.Relogging or XagmanStatus.ReturningHome)
            return false;
        peer = candidate;
        identity = new(candidate.InstanceId, candidate.ActiveCharacter, candidate.ContentId,
            candidate.ActiveTradePartnerInstanceId, candidate.ActiveTradePartner, GetXagmanTradeCapacityPeerScope(candidate));
        return true;
    }

    private bool TryGetXagmanTradeCapacityOnhPeer(XagmanTradeCapacityIdentity local,
        out XagmanTradeCapacityIdentity identity, PlayerPayload? sender = null)
    {
        identity = null!;
        if (xagmanOnhPhase is not (XagmanOnhPhase.FoGiving or XagmanOnhPhase.TonyReceiving
            or XagmanOnhPhase.TonySupplying or XagmanOnhPhase.FoReceiving)) return false;
        var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        if (actor == null || actor.EntityId == 0
            || (sender != null && (!sender.PlayerName.Equals(actor.Name.TextValue, StringComparison.OrdinalIgnoreCase)
                || sender.World.RowId != actor.HomeWorld.RowId))) return false;
        identity = new(string.Empty, $"{actor.Name.TextValue}@{actor.HomeWorld.Value.Name}", actor.EntityId,
            string.Empty, local.Character, GetXagmanTradeCapacityScope());
        return true;
    }

    private bool RequestXagmanTradeCapacity(string requestId, out string failure)
    {
        failure = string.Empty;
        if (!TryRequireXagmanTradeVersions(out failure)) return false;
        if (!XagmanTradeCapacityProtocol.IsRequestIdValid(requestId))
        { failure = "Invalid capacity request identifier."; return false; }
        if (xagmanCapacityRequest?.Id == requestId)
            return PollXagmanTradeCapacity(requestId, out _, out failure) != XagmanTradeCapacityState.Invalid;
        if (!TryGetXagmanTradeCapacityLocal(out var local))
        { failure = "The live sender character or trade lock is unavailable."; return false; }
        var onh = plugin.Configuration.XagmanOutsideNetworkHelper;
        XagmanTradeCapacityIdentity peerIdentity;
        uint receiverEntityId;
        if (onh)
        {
            if (!TryGetXagmanTradeCapacityOnhPeer(local, out peerIdentity))
            { failure = "The ONH receiver is not in the active exchange."; return false; }
            EnsureXagmanCapacityOnhSequenceSession();
            if (xagmanCapacitySequence >= XagmanTradeCapacityProtocol.MaxSequence)
            { failure = "The ONH capacity request sequence is exhausted."; return false; }
            receiverEntityId = checked((uint)peerIdentity.CharacterId);
        }
        else
        {
            if (!TryGetXagmanTradeCapacityConnectedPeer(local, out var peer, out peerIdentity))
            { failure = "Fresh receiver identity and mutual trade lock could not be verified."; return false; }
            if (peer.TradeCapacityProtocolRevision != XagmanTradeCapacityProtocol.Revision)
            { failure = "The receiver does not support the required trade-capacity protocol."; return false; }
            receiverEntityId = peer.TradeCapacityEntityId;
        }
        xagmanCapacityRequest = new XagmanCapacityRequest
        {
            Id = requestId, Local = local, Peer = peerIdentity, StartedUtc = DateTime.UtcNow,
            Onh = onh, Sequence = onh ? ++xagmanCapacitySequence : 0,
            ReceiverEntityId = receiverEntityId,
            TaskRunId = plugin.TaskRunner.CurrentRunId,
        };
        if (onh) QueueXagmanCapacityOnhRequest(xagmanCapacityRequest);
        else PublishXagmanPresence();
        return true;
    }

    private XagmanTradeCapacityState PollXagmanTradeCapacity(string requestId, out int freeSlots, out string failure)
    {
        freeSlots = 0;
        failure = string.Empty;
        if (!TryRequireXagmanTradeVersions(out failure)) return XagmanTradeCapacityState.Invalid;
        var request = xagmanCapacityRequest;
        if (request == null || request.Id != requestId)
        { failure = "The capacity request was canceled or replaced."; return XagmanTradeCapacityState.Invalid; }
        if (!plugin.TaskRunner.IsRunning || plugin.TaskRunner.CurrentRunId != request.TaskRunId)
        { failure = "The capacity request's owning task ended or changed."; return XagmanTradeCapacityState.Invalid; }
        if (!XagmanTradeCapacityProtocol.IsFresh(DateTime.UtcNow, request.StartedUtc, 15))
        { failure = "Fresh receiver capacity was not confirmed within 15 seconds."; return XagmanTradeCapacityState.Invalid; }
        if (request.Onh != plugin.Configuration.XagmanOutsideNetworkHelper
            || !TryGetXagmanTradeCapacityLocal(out var local)
            || !XagmanTradeCapacityProtocol.MatchesIdentity(request.Local, local))
        { failure = "The sender character, partner or exchange changed."; return XagmanTradeCapacityState.Invalid; }
        if (request.Onh)
        {
            if (!TryGetXagmanTradeCapacityOnhPeer(local, out var peerIdentity)
                || !XagmanTradeCapacityProtocol.MatchesIdentity(request.Peer, peerIdentity))
            { failure = "The ONH receiver identity changed or left the active exchange."; return XagmanTradeCapacityState.Invalid; }
            if (request.ReceivedUtc == DateTime.MinValue) QueueXagmanCapacityOnhRequest(request);
        }
        else
        {
            if (!TryGetXagmanTradeCapacityConnectedPeer(local, out var peer, out var peerIdentity)
                || !XagmanTradeCapacityProtocol.MatchesIdentity(request.Peer, peerIdentity)
                || peer.TradeCapacityEntityId != request.ReceiverEntityId
                || peer.TradeCapacityProtocolRevision != XagmanTradeCapacityProtocol.Revision)
            { failure = "Receiver identity, protocol, freshness or trade lock changed."; return XagmanTradeCapacityState.Invalid; }
            if (request.ReceivedUtc == DateTime.MinValue && peer.TradeCapacityResponseRequestId == request.Id
                && peer.TradeCapacityResponseRequesterInstanceId == local.InstanceId
                && string.Equals(peer.TradeCapacityResponseRequesterCharacter, local.Character, StringComparison.OrdinalIgnoreCase)
                && peer.TradeCapacityResponseRequesterContentId == local.CharacterId
                && peer.TradeCapacityResponseObservedAtUtc > DateTime.MinValue)
            {
                request.ReceivedUtc = DateTime.UtcNow;
                request.RemoteObservedUtc = peer.TradeCapacityResponseObservedAtUtc;
                request.Known = peer.TradeCapacityResponseKnown;
                request.FreeSlots = peer.TradeCapacityResponseFreeSlots;
            }
        }
        if (request.ReceivedUtc == DateTime.MinValue) return XagmanTradeCapacityState.Pending;
        if (!XagmanTradeCapacityProtocol.IsFresh(DateTime.UtcNow, request.ReceivedUtc, 3)
            || !XagmanTradeCapacityProtocol.IsCapacityValid(request.Known, request.FreeSlots))
        { failure = request.Known ? "Receiver capacity is invalid or expired." : "Receiver inventory is not loaded; capacity is unknown."; return XagmanTradeCapacityState.Invalid; }
        freeSlots = request.FreeSlots;
        return XagmanTradeCapacityState.Ready;
    }

    // The offer may take longer than a capacity sample remains usable for a new Begin.
    // Its immutable identity still has to hold until that physical offer settles.
    private bool IsXagmanTradeCapacityBindingCurrent(string requestId, out string failure)
    {
        failure = string.Empty;
        if (!TryRequireXagmanTradeVersions(out failure)) return false;
        var request = xagmanCapacityRequest;
        if (request == null || request.Id != requestId
            || !plugin.TaskRunner.IsRunning || plugin.TaskRunner.CurrentRunId != request.TaskRunId
            || request.Onh != plugin.Configuration.XagmanOutsideNetworkHelper
            || !TryGetXagmanTradeCapacityLocal(out var local)
            || !XagmanTradeCapacityProtocol.MatchesIdentity(request.Local, local))
        { failure = "The capacity-limited sender or exchange identity changed."; return false; }
        if (request.Onh)
        {
            if (TryGetXagmanTradeCapacityOnhPeer(local, out var onhPeer)
                && XagmanTradeCapacityProtocol.MatchesIdentity(request.Peer, onhPeer)) return true;
        }
        else if (TryGetXagmanTradeCapacityConnectedPeer(local, out var peer, out var identity)
            && peer.TradeCapacityProtocolRevision == XagmanTradeCapacityProtocol.Revision
            && peer.TradeCapacityEntityId == request.ReceiverEntityId
            && XagmanTradeCapacityProtocol.MatchesIdentity(request.Peer, identity)) return true;
        failure = "The capacity-limited receiver, session or mutual trade lock changed.";
        return false;
    }

    private bool TryFocusXagmanTradeCapacityPartner(string requestId, out string failure)
    {
        if (!IsXagmanTradeCapacityBindingCurrent(requestId, out failure)) return false;
        var request = xagmanCapacityRequest!;
        var matches = Plugin.ObjectTable.OfType<IPlayerCharacter>()
            .Where(actor => actor.IsTargetable && actor.EntityId == request.ReceiverEntityId
                && string.Equals($"{actor.Name.TextValue}@{actor.HomeWorld.Value.Name}", request.Peer.Character,
                    StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        { failure = "The exact receiver character, home world and entity could not be found."; return false; }
        Plugin.TargetManager.Target = matches[0];
        Plugin.TargetManager.FocusTarget = matches[0];
        if (Plugin.TargetManager.Target?.EntityId != request.ReceiverEntityId
            || Plugin.TargetManager.FocusTarget?.EntityId != request.ReceiverEntityId)
        { failure = "The exact receiver could not be confirmed as both target and focus target."; return false; }
        return true;
    }

    private XagmanTradeFailureKind GetXagmanBoundedTradeFailureKind(out string matchedText)
    {
        matchedText = string.Empty;
        if (!AddonHelper.IsAddonVisible("_TextError")) return XagmanTradeFailureKind.None;
        matchedText = AddonHelper.GetAddonTextEntries("_TextError")
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text) && IsXagmanInventoryCapacityFailure(text)) ?? string.Empty;
        if (matchedText.Length > 0) return XagmanTradeFailureKind.TradeNotComplete;
        return GetXagmanTradeFailureKind(out matchedText);
    }

    private void ApplyXagmanTradeCapacityPresence(XagmanPeerPresence presence)
    {
        presence.TradeCapacityProtocolRevision = XagmanTradeCapacityProtocol.Revision;
        presence.TradeCapacityEntityId = Plugin.PlayerState.IsLoaded ? Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0 : 0;
        if (plugin.Configuration.XagmanOutsideNetworkHelper || !TryRequireXagmanTradeVersions(out _)
            || !TryGetXagmanTradeCapacityLocal(out var local)
            || !TryGetXagmanTradeCapacityConnectedPeer(local, out var peer, out var peerIdentity)) return;
        var request = xagmanCapacityRequest;
        if (request is { Onh: false } && plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentRunId == request.TaskRunId
            && XagmanTradeCapacityProtocol.MatchesIdentity(request.Local, local)
            && XagmanTradeCapacityProtocol.MatchesIdentity(request.Peer, peerIdentity))
        {
            presence.TradeCapacityRequestId = request.Id;
            presence.TradeCapacityRequestReceiverInstanceId = request.Peer.InstanceId;
            presence.TradeCapacityRequestReceiverCharacter = request.Peer.Character;
            presence.TradeCapacityRequestReceiverContentId = request.Peer.CharacterId;
        }
        if (peer.TradeCapacityProtocolRevision != XagmanTradeCapacityProtocol.Revision
            || !XagmanTradeCapacityProtocol.IsRequestIdValid(peer.TradeCapacityRequestId)
            || peer.TradeCapacityRequestReceiverInstanceId != local.InstanceId
            || !string.Equals(peer.TradeCapacityRequestReceiverCharacter, local.Character, StringComparison.OrdinalIgnoreCase)
            || peer.TradeCapacityRequestReceiverContentId != local.CharacterId) return;
        var response = GetOrSampleXagmanTradeCapacityResponse(peer.TradeCapacityRequestId, local, peerIdentity);
        presence.TradeCapacityResponseRequestId = response.Id;
        presence.TradeCapacityResponseRequesterInstanceId = response.Peer.InstanceId;
        presence.TradeCapacityResponseRequesterCharacter = response.Peer.Character;
        presence.TradeCapacityResponseRequesterContentId = response.Peer.CharacterId;
        presence.TradeCapacityResponseKnown = response.Known;
        presence.TradeCapacityResponseFreeSlots = response.FreeSlots;
        presence.TradeCapacityResponseObservedAtUtc = response.ObservedUtc;
    }

    private bool IsXagmanTradeCapacitySenderActive()
    {
        if (!TryRequireXagmanTradeVersions(out _) || !TryGetXagmanTradeCapacityLocal(out var local)) return false;
        if (plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            var response = xagmanCapacityResponse;
            return response != null && XagmanTradeCapacityProtocol.MatchesIdentity(response.Local, local)
                && XagmanTradeCapacityProtocol.IsFresh(DateTime.UtcNow, response.ObservedUtc, XagmanCapacitySenderHoldSeconds)
                && TryGetXagmanTradeCapacityOnhPeer(local, out var onhPeer)
                && XagmanTradeCapacityProtocol.MatchesIdentity(response.Peer, onhPeer);
        }
        if (!TryGetXagmanTradeCapacityConnectedPeer(local, out var peer, out var identity)
            || peer.TradeCapacityProtocolRevision != XagmanTradeCapacityProtocol.Revision
            || !XagmanTradeCapacityProtocol.IsRequestIdValid(peer.TradeCapacityRequestId)
            || peer.TradeCapacityRequestReceiverInstanceId != local.InstanceId
            || !string.Equals(peer.TradeCapacityRequestReceiverCharacter, local.Character, StringComparison.OrdinalIgnoreCase)
            || peer.TradeCapacityRequestReceiverContentId != local.CharacterId) return false;
        // Observe a new nonce before ordinary receiver cleanup can release its lock between batches.
        var observed = GetOrSampleXagmanTradeCapacityResponse(peer.TradeCapacityRequestId, local, identity);
        return XagmanTradeCapacityProtocol.IsFresh(DateTime.UtcNow, observed.ObservedUtc, XagmanCapacitySenderHoldSeconds);
    }

    private XagmanCapacityResponse GetOrSampleXagmanTradeCapacityResponse(string requestId,
        XagmanTradeCapacityIdentity local, XagmanTradeCapacityIdentity peer)
    {
        if (xagmanCapacityResponse is { } cached && cached.Id == requestId
            && XagmanTradeCapacityProtocol.MatchesIdentity(cached.Local, local)
            && XagmanTradeCapacityProtocol.MatchesIdentity(cached.Peer, peer)) return cached;
        var known = TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots)
            && XagmanTradeCapacityProtocol.IsCapacityValid(true, freeSlots);
        return xagmanCapacityResponse = new(requestId, local, peer, known, known ? freeSlots : 0, DateTime.UtcNow);
    }

    private void EnsureXagmanCapacityOnhSequenceSession()
    {
        if (xagmanCapacitySequenceSession == onhSession) return;
        xagmanCapacitySequenceSession = onhSession;
        xagmanCapacitySequence = xagmanCapacityReceivedSequence = 0;
        xagmanCapacityReceivedRequest = string.Empty;
        xagmanCapacityReceivedEnded = false;
    }

    private void QueueXagmanCapacityOnhRequest(XagmanCapacityRequest request)
    {
        if ((DateTime.UtcNow - request.LastTellQueuedUtc).TotalSeconds < 2 || onhTellOutbox.Count >= 8) return;
        var text = $"/xa onh {onhSession} capreq {XagmanTradeCapacityProtocol.Revision} {request.Sequence} {request.Id}";
        if (!onhTellOutbox.Contains(text)) onhTellOutbox.Enqueue(text);
        request.LastTellQueuedUtc = DateTime.UtcNow;
    }

    // Called only after the existing tell-channel, roster and engaged-partner checks.
    private bool TryReceiveXagmanTradeCapacityTell(string[] parts, PlayerPayload? sender)
    {
        if (parts.Length < 4 || parts[0] != "/xa" || parts[1] != "onh"
            || parts[3] is not ("capreq" or "cap" or "capend")) return false;
        try
        {
            if (!plugin.Configuration.XagmanOutsideNetworkHelper || sender == null || parts[2] != onhSession
                || !XagmanTradeCapacityProtocol.IsRequestIdValid(onhSession)
                || parts.Length != (parts[3] == "cap" ? 9 : 7)
                || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                || revision != XagmanTradeCapacityProtocol.Revision
                || !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                || !XagmanTradeCapacityProtocol.IsSequenceValid(sequence)
                || !XagmanTradeCapacityProtocol.IsRequestIdValid(parts[6])
                || !TryGetXagmanTradeCapacityLocal(out var local)
                || !TryGetXagmanTradeCapacityOnhPeer(local, out var peer, sender)) return true;
            EnsureXagmanCapacityOnhSequenceSession();
            if (parts[3] == "capend")
            {
                if (xagmanCapacityResponse is { } ended && ended.Id == parts[6]
                    && sequence == xagmanCapacityReceivedSequence
                    && XagmanTradeCapacityProtocol.MatchesIdentity(ended.Local, local)
                    && XagmanTradeCapacityProtocol.MatchesIdentity(ended.Peer, peer))
                {
                    xagmanCapacityResponse = null;
                    xagmanCapacityReceivedEnded = true;
                }
                return true;
            }
            // capend only releases an exact prior request. New requests and accepted
            // capacity replies require this session's negotiated Tony version pair.
            if (!TryRequireXagmanOnhTradeVersions(out _)) return true;
            if (parts[3] == "capreq")
            {
                if (xagmanOnhPhase is not (XagmanOnhPhase.TonyReceiving or XagmanOnhPhase.FoReceiving)
                    || sequence < xagmanCapacityReceivedSequence
                    || (sequence == xagmanCapacityReceivedSequence
                        && (parts[6] != xagmanCapacityReceivedRequest || xagmanCapacityReceivedEnded))) return true;
                if (sequence > xagmanCapacityReceivedSequence) xagmanCapacityReceivedEnded = false;
                xagmanCapacityReceivedSequence = sequence;
                xagmanCapacityReceivedRequest = parts[6];
                var response = GetOrSampleXagmanTradeCapacityResponse(parts[6], local, peer);
                var reply = $"/xa onh {onhSession} cap {XagmanTradeCapacityProtocol.Revision} {sequence} {parts[6]} {(response.Known ? 1 : 0)} {response.FreeSlots}";
                if (onhTellOutbox.Count < 8 && !onhTellOutbox.Contains(reply)) onhTellOutbox.Enqueue(reply);
                return true;
            }
            var request = xagmanCapacityRequest;
            if (request is not { Onh: true } || request.Id != parts[6] || request.Sequence != sequence
                || request.ReceivedUtc != DateTime.MinValue
                || !XagmanTradeCapacityProtocol.MatchesIdentity(request.Local, local)
                || !XagmanTradeCapacityProtocol.MatchesIdentity(request.Peer, peer)
                || !XagmanTradeCapacityProtocol.IsFresh(DateTime.UtcNow, request.StartedUtc, 15)
                || parts[7] is not ("0" or "1")
                || !int.TryParse(parts[8], NumberStyles.None, CultureInfo.InvariantCulture, out var slots)
                || slots is < 0 or > XagmanTradeCapacityProtocol.MaxFreeSlots) return true;
            request.Known = parts[7] == "1";
            request.FreeSlots = slots;
            request.ReceivedUtc = DateTime.UtcNow;
            onhLastActivity = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Xagman] Ignored invalid ONH trade-capacity message.");
            return true;
        }
    }
}
