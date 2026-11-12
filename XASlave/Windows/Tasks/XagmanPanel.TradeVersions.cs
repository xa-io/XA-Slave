using System;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed record XagmanReceiverVersionIntent(global::XASlave.Localization.UiMessage Context,
        XagmanRole Role, string Character, string Partner, string PartnerInstanceId, string Scope);

    private XagmanReceiverVersionIntent? xagmanVersionPendingReceiver;
    private XagmanReceiverVersionIntent? xagmanVersionArmedReceiver;
    private global::XASlave.Localization.UiMessage? xagmanTradeVersionFailureUiMessage;

    private global::XASlave.Localization.UiMessage GetXagmanTradeVersionFailureMessage(string failure)
        => xagmanTradeVersionFailureUiMessage is { } message && message.Original == failure
            ? message
            : global::XASlave.Localization.UiMessage.Authored(failure);

    // Keep policy outputs in their original language for logic and logs. Retain
    // formatted version values separately so the UI can resolve its current locale.
    private bool TryMatchXagmanTradeVersions(string? tonySlave, string? tonyDropbox,
        string? otherSlave, string? otherDropbox, out string failure)
    {
        var matched = XagmanTradeVersionPolicy.TryMatch(tonySlave, tonyDropbox, otherSlave, otherDropbox, out failure);
        xagmanTradeVersionFailureUiMessage = null;
        if (matched) return true;
        var message = global::XASlave.Localization.UiMessage.Authored(failure);
        if (XagmanTradeVersionPolicy.TryNormalize(tonySlave, out var expectedSlave)
            && XagmanTradeVersionPolicy.TryNormalize(tonyDropbox, out var expectedDropbox))
        {
            message = XagmanTradeVersionPolicy.TryNormalize(otherSlave, out var actualSlave)
                && XagmanTradeVersionPolicy.TryNormalize(otherDropbox, out var actualDropbox)
                ? global::XASlave.Localization.UiMessage.Format(global::XASlave.Localization.UiLocale.Key("Tony requires XA Slave {0} and Dropbox {1}; the trading partner reports XA Slave {2} and Dropbox {3}."), expectedSlave, expectedDropbox, actualSlave, actualDropbox)
                : global::XASlave.Localization.UiMessage.Format(global::XASlave.Localization.UiLocale.Key("Tony requires XA Slave {0} and Dropbox {1}; the trading partner's loaded versions are unavailable."), expectedSlave, expectedDropbox);
        }
        if (message.Original == failure) xagmanTradeVersionFailureUiMessage = message;
        return false;
    }

    private XagmanReceiverVersionIntent CaptureXagmanReceiverVersionIntent(global::XASlave.Localization.UiMessage context)
        => new(context, xagmanActiveRole, xagmanActiveCharacter, xagmanActiveTradePartner,
            xagmanActiveTradePartnerInstanceId, GetXagmanTradeCapacityScope());

    private bool IsXagmanReceiverVersionIntentCurrent(XagmanReceiverVersionIntent intent)
        => intent.Role == xagmanActiveRole && intent.Character == xagmanActiveCharacter
            && intent.Partner == xagmanActiveTradePartner && intent.PartnerInstanceId == xagmanActiveTradePartnerInstanceId
            && intent.Scope == GetXagmanTradeCapacityScope();

    private bool TryGetXagmanTradeVersions(out string slaveVersion, out string dropboxVersion, out string failure)
    {
        slaveVersion = dropboxVersion = string.Empty;
        failure = string.Empty;
        if (!XagmanTradeVersionPolicy.TryNormalize(BuildInfo.Version, out slaveVersion))
        {
            failure = "The loaded XA Slave version is unavailable or invalid.";
            return false;
        }
        if (!plugin.DropboxQueue.TryGetLoadedDropboxVersion(out var loadedDropboxVersion, out failure))
            return false;
        if (!XagmanTradeVersionPolicy.TryNormalize(loadedDropboxVersion, out dropboxVersion))
        {
            failure = "The loaded Dropbox version is unavailable or invalid.";
            return false;
        }
        return true;
    }

    private bool TryGetXagmanConnectedTradeVersionPeer(out XagmanPeerPresence peer)
    {
        peer = null!;
        return TryGetXagmanTradeCapacityLocal(out var local)
            && TryGetXagmanTradeCapacityConnectedPeer(local, out peer, out _);
    }

    private bool TryRequireXagmanTradeVersions(out string failure)
    {
        if (plugin.Configuration.XagmanOutsideNetworkHelper)
            return TryRequireXagmanOnhTradeVersions(out failure);
        if (!TryGetXagmanTradeVersions(out var localSlave, out var localDropbox, out failure))
            return false;
        if (!TryGetXagmanConnectedTradeVersionPeer(out var peer))
        {
            failure = "The active Tony/owner trade pair is not fresh or mutually bound.";
            return false;
        }
        if (peer.TradeVersionProtocolRevision != XagmanTradeVersionPolicy.Revision)
        {
            failure = "The trading partner does not support Tony's version handshake. Update XA Slave on that client.";
            return false;
        }
        // Only the actual Tony in this reciprocal, scoped exchange supplies the required pair.
        return xagmanActiveRole == XagmanRole.Tony
            ? TryMatchXagmanTradeVersions(localSlave, localDropbox, peer.PluginVersion, peer.DropboxVersion, out failure)
            : TryMatchXagmanTradeVersions(peer.PluginVersion, peer.DropboxVersion, localSlave, localDropbox, out failure);
    }

    private bool FailXagmanTradeVersions(string failure)
    {
        if (plugin.Configuration.XagmanOutsideNetworkHelper && onhCollectionHeld) return false;
        var failureMessage = GetXagmanTradeVersionFailureMessage(failure);
        plugin.TaskRunner.AddLog($"Xagman: trade version compatibility failed: {failure}");
        StopXagmanTask();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
            failureMessage);
        PublishXagmanPresence();
        return false;
    }

    private TaskStep CreateXagmanTradeVersionAdmissionStep(Func<bool>? shouldSkip)
        => new()
        {
            NameMessage = global::XASlave.Localization.UiMessage.Authored("Verify Tony's XA Slave and Dropbox versions"),
            ShouldSkip = shouldSkip,
            OnEnter = PublishXagmanPresence,
            IsComplete = () =>
            {
                if (!xagmanRunning) return true;
                if (!TryGetXagmanTradeVersions(out _, out _, out var localFailure))
                    return FailXagmanTradeVersions(localFailure);
                // The call and reciprocal partner lock travel on separate heartbeats.
                // Wait before preparing a queue; actual writes still recheck strictly.
                if (!plugin.Configuration.XagmanOutsideNetworkHelper
                    && !TryGetXagmanConnectedTradeVersionPeer(out _)) return false;
                return TryRequireXagmanTradeVersions(out var failure) || FailXagmanTradeVersions(failure);
            },
            TimeoutSec = 15f,
            OnTimeout = () => FailXagmanTradeVersions("Timed out waiting for the active Tony/owner version handshake."),
        };

    // Receiver pre-arm precedes partner selection. Keep Dropbox closed while waiting;
    // do not stop dispatch, because Tony/owner dispatch establishes the mutual lock.
    private bool UpdateXagmanTradeVersionAdmission()
    {
        if (!xagmanRunning)
        {
            xagmanVersionPendingReceiver = null;
            xagmanVersionArmedReceiver = null;
            return false;
        }
        if (plugin.Configuration.XagmanOutsideNetworkHelper)
            return false; // ONH arms only after its versioned signal/armed exchange.
        if (xagmanDropboxAutoAcceptState == true)
        {
            var armed = xagmanVersionArmedReceiver;
            if (armed == null || !IsXagmanReceiverVersionIntentCurrent(armed)
                || !TryGetXagmanConnectedTradeVersionPeer(out _))
            {
                var waiting = global::XASlave.Localization.UiMessage.Authored("waiting for a version-compatible trading partner");
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, waiting))
                    return true;
                // A short presence gap may resume this exact receive exchange. A new
                // partner or phase needs its own receiver request, never inherited intent.
                if (armed != null && IsXagmanReceiverVersionIntentCurrent(armed))
                    xagmanVersionPendingReceiver = armed;
            }
            else if (!TryRequireXagmanTradeVersions(out var failure))
            {
                FailXagmanTradeVersions(failure);
                return true;
            }
        }
        if (xagmanVersionPendingReceiver is { } pending)
        {
            if (!IsXagmanReceiverVersionIntentCurrent(pending))
                xagmanVersionPendingReceiver = null;
            else if (TryGetXagmanConnectedTradeVersionPeer(out _))
            {
                xagmanVersionPendingReceiver = null;
                if (!TryRequireXagmanReceiverAutoAccept(pending.Context))
                    return true;
            }
        }
        return false;
    }
}
