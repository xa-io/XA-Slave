using System;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string onhVersionSession = string.Empty;
    private string onhVersionLocalCharacter = string.Empty;
    private string onhVersionPartnerCharacter = string.Empty;
    private uint onhVersionPartnerEntityId;
    private string onhTonySlaveVersion = string.Empty;
    private string onhTonyDropboxVersion = string.Empty;
    private string onhPartnerSlaveVersion = string.Empty;
    private string onhPartnerDropboxVersion = string.Empty;
    private bool onhVersionsAccepted;
    private string onhVersionFailure = string.Empty;
    private global::XASlave.Localization.UiMessage? onhVersionFailureUiMessage;

    private void ResetXagmanOnhTradeVersions()
    {
        onhVersionSession = onhVersionLocalCharacter = onhVersionPartnerCharacter = string.Empty;
        onhVersionPartnerEntityId = 0;
        onhTonySlaveVersion = onhTonyDropboxVersion = string.Empty;
        onhPartnerSlaveVersion = onhPartnerDropboxVersion = string.Empty;
        onhVersionsAccepted = false;
        onhVersionFailure = string.Empty;
        onhVersionFailureUiMessage = null;
    }

    private bool TryGetXagmanOnhVersionPartner(PlayerPayload? sender, out string character, out uint entityId)
    {
        character = string.Empty;
        entityId = 0;
        if (!Plugin.Framework.IsInFrameworkUpdateThread || !xagmanRunning
            || !plugin.Configuration.XagmanOutsideNetworkHelper
            || xagmanActiveRole is not (XagmanRole.Tony or XagmanRole.FranchiseOwner)) return false;
        var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        if (actor == null || actor.EntityId == 0 || actor.HomeWorld.RowId == 0
            || (sender != null && (!sender.PlayerName.Equals(actor.Name.TextValue, StringComparison.OrdinalIgnoreCase)
                || sender.World.RowId != actor.HomeWorld.RowId))) return false;
        var key = $"{actor.Name.TextValue}@{actor.HomeWorld.Value.Name}";
        var roster = xagmanActiveRole == XagmanRole.Tony
            ? plugin.Configuration.XagmanOnhFriendFoCharacters : plugin.Configuration.XagmanOnhFriendTonyCharacters;
        if (!roster.Any(entry => entry.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))) return false;
        character = key;
        entityId = actor.EntityId;
        return true;
    }

    private bool TryPinXagmanOnhTonyVersions(string session, string slave, string dropbox,
        PlayerPayload? sender, out string failure)
    {
        if (!TryRequireXagmanOnhProviderIdentity(out failure)) return false;
        failure = "The active Tony version contract is unavailable.";
        if (!XagmanTradeCapacityProtocol.IsRequestIdValid(session)
            || !TryGetXagmanOnhVersionPartner(sender, out var partner, out var entityId)
            || !TryGetXagmanTradeVersions(out var localSlave, out var localDropbox, out failure)
            || !XagmanTradeVersionPolicy.TryNormalize(slave, out var expectedSlave)
            || !XagmanTradeVersionPolicy.TryNormalize(dropbox, out var expectedDropbox)) return false;
        if (!TryMatchXagmanTradeVersions(expectedSlave, expectedDropbox, localSlave, localDropbox, out failure))
            return false;
        var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        if (string.IsNullOrWhiteSpace(current) || !current.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase))
        { failure = "The ONH character changed before version agreement."; return false; }
        if (!string.IsNullOrEmpty(onhVersionSession))
        {
            if (onhVersionSession != session || onhVersionPartnerEntityId != entityId
                || !onhVersionPartnerCharacter.Equals(partner, StringComparison.OrdinalIgnoreCase)
                || !onhVersionLocalCharacter.Equals(current, StringComparison.OrdinalIgnoreCase)
                || onhTonySlaveVersion != expectedSlave || onhTonyDropboxVersion != expectedDropbox)
            { failure = "The active Tony version contract changed during this ONH exchange."; return false; }
            return string.IsNullOrEmpty(onhVersionFailure);
        }
        onhVersionSession = session;
        onhVersionLocalCharacter = current;
        onhVersionPartnerCharacter = partner;
        onhVersionPartnerEntityId = entityId;
        onhTonySlaveVersion = expectedSlave;
        onhTonyDropboxVersion = expectedDropbox;
        failure = string.Empty;
        return true;
    }

    private bool TryAcceptXagmanOnhPeerVersions(string session, string slave, string dropbox,
        PlayerPayload sender, bool initialTonyOffer, out string failure, bool returningTony = false)
    {
        if (!TryRequireXagmanOnhProviderIdentity(out failure)) return false;
        failure = "The ONH version handshake is not established.";
        if (initialTonyOffer)
        {
            // Only a reservation answering this owner's request may define Tony's expected pair.
            if (xagmanActiveRole != XagmanRole.FranchiseOwner || xagmanOnhPhase != XagmanOnhPhase.FoAwaitReservation
                || !TryPinXagmanOnhTonyVersions(session, slave, dropbox, sender, out failure)) return false;
        }
        if (onhVersionSession != session || !string.IsNullOrEmpty(onhVersionFailure)
            || !TryGetXagmanOnhVersionPartner(sender, out var partner, out var entityId)
            || (onhVersionPartnerEntityId != entityId && !(returningTony && onhVersionsAccepted
                && xagmanActiveRole == XagmanRole.FranchiseOwner
                && xagmanOnhPhase == XagmanOnhPhase.FoAwaitSale && onhPartnerSelling))
            || !onhVersionPartnerCharacter.Equals(partner, StringComparison.OrdinalIgnoreCase)
            || !TryGetXagmanTradeVersions(out var localSlave, out var localDropbox, out failure)
            || !TryMatchXagmanTradeVersions(onhTonySlaveVersion, onhTonyDropboxVersion, localSlave, localDropbox, out failure)
            || !TryMatchXagmanTradeVersions(onhTonySlaveVersion, onhTonyDropboxVersion, slave, dropbox, out failure)) return false;
        // The shared policy validates/canonicalizes four-part versions; store the canonical pair.
        XagmanTradeVersionPolicy.TryNormalize(slave, out onhPartnerSlaveVersion);
        XagmanTradeVersionPolicy.TryNormalize(dropbox, out onhPartnerDropboxVersion);
        // Zoning for a negotiated sale can replace Tony's transient object ID. Rebind
        // only his versioned return signal from the same exact character and session.
        onhVersionPartnerEntityId = entityId;
        onhVersionsAccepted = true;
        failure = string.Empty;
        return true;
    }

    private bool TryRequireXagmanOnhTradeVersions(out string failure)
    {
        if (!TryRequireXagmanOnhProviderIdentity(out failure)) return false;
        failure = string.IsNullOrEmpty(onhVersionFailure) ? "Waiting for the active Tony's XA Slave and Dropbox version agreement." : onhVersionFailure;
        if (!onhVersionsAccepted || !string.IsNullOrEmpty(onhVersionFailure)
            || onhVersionSession != onhSession || !XagmanTradeCapacityProtocol.IsRequestIdValid(onhSession)
            || !TryGetXagmanOnhVersionPartner(null, out var partner, out var entityId)
            || onhVersionPartnerEntityId != entityId
            || !onhVersionPartnerCharacter.Equals(partner, StringComparison.OrdinalIgnoreCase)
            || !onhVersionLocalCharacter.Equals(MonthlyReloggerTask.GetCurrentCharacterNameWorld(), StringComparison.OrdinalIgnoreCase)) return false;
        return TryGetXagmanTradeVersions(out var slave, out var dropbox, out failure)
            && TryMatchXagmanTradeVersions(onhTonySlaveVersion, onhTonyDropboxVersion, slave, dropbox, out failure)
            && TryMatchXagmanTradeVersions(onhTonySlaveVersion, onhTonyDropboxVersion, onhPartnerSlaveVersion, onhPartnerDropboxVersion, out failure);
    }

    private bool TryValidateXagmanOnhLocalVersions(out string failure)
    {
        if (!TryRequireXagmanOnhProviderIdentity(out failure)) return false;
        failure = onhVersionFailure;
        if (!string.IsNullOrEmpty(failure)) return false;
        if (string.IsNullOrEmpty(onhVersionSession)) return true;
        // Selling and return travel can temporarily unload the local player and the
        // partner. Keep the version pin across that travel; require the actor at trade time.
        var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        if (!onhVersionLocalCharacter.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
            || (Plugin.PlayerState.IsLoaded && Plugin.ObjectTable.LocalPlayer != null
                && !onhVersionLocalCharacter.Equals(current, StringComparison.OrdinalIgnoreCase)))
        { failure = "The ONH character changed after Tony's version agreement."; return false; }
        return TryGetXagmanTradeVersions(out var slave, out var dropbox, out failure)
            && TryMatchXagmanTradeVersions(onhTonySlaveVersion, onhTonyDropboxVersion, slave, dropbox, out failure);
    }

    private bool TryRequireXagmanOnhProviderIdentity(out string failure)
    {
        failure = "The ONH Dropbox provider is unavailable or has changed.";
        if (onhCollectionHeld) return false;
        if (onhAdmissionProvider != null
            && plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(onhAdmissionProvider, out failure)) return true;
        // Equal versions do not authorize mutating a replacement provider instance.
        HoldXagmanOnhCollection(failure);
        return false;
    }

    private bool TryGetXagmanOnhVersionSuffix(out string suffix, out string failure)
    {
        suffix = string.Empty;
        if (!TryGetXagmanTradeVersions(out var slave, out var dropbox, out failure)) return false;
        // Collection bootstrap uses the request/reservation transport. Control
        // markers may only reuse a fully negotiated pair, never create one.
        if (!TryRequireXagmanOnhTradeVersions(out failure)) return false;
        suffix = $"{XagmanTradeVersionPolicy.Revision} {slave} {dropbox}";
        return true;
    }

    private void RejectXagmanOnhTradeVersions(string failure)
    {
        if (onhCollectionHeld) return;
        onhVersionFailure = string.IsNullOrWhiteSpace(failure) ? "The ONH peer did not supply matching XA Slave and Dropbox versions." : failure;
        if (onhVersionFailureUiMessage?.Original != onhVersionFailure)
            onhVersionFailureUiMessage = GetXagmanTradeVersionFailureMessage(onhVersionFailure);
        onhVersionsAccepted = false;
        xagmanOnhGilSendAborted = true;
        onhTransferFailed = true;
        onhArmedAmount = 0;
        // Fail the runtime on its next framework tick, including while a sub-task is active.
        // Do not send a marker or accept a trade after a mismatched acknowledgement.
        TrySetXagmanDropboxAutoAccept(false);
        plugin.TaskRunner.AddLog($"Xagman ONH: version agreement failed: {onhVersionFailure}");
    }
}
