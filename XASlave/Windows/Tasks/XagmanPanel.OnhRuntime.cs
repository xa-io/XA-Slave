using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

// Owner-requested offline collection and resupply. Protocol effects live in the ONH partials.
public partial class SlaveWindow
{
    private enum XagmanOnhPhase
    {
        Idle,
        StartCharacter,      // launch the relog+travel sub-task for the current character
        AwaitStartup,        // relog+travel sub-task launched; resume here when it finishes

        // Franchise Owner
        FoFindTony,          // find a present roster Tony and approach
        FoAwaitReservation,  // owner request, Tony reservation, confirmation and effective readiness
        FoGiving,            // a give sub-task is running; resume here when done
        FoSettle,            // collection reconciliation and resupply request
        FoSendDoneGil,       // a send-1-gil sub-task is running; resume here when done
        FoConfirmCompletion, // regional completion requires the owner's verified-goal acknowledgement

        // Tony queue owner
        TonySearch,          // available; wait for an eligible owner request
        TonyAwaitConfirmation, // bind the requester before accepting any collection trade
        TonyReceiving,       // collect until the owner confirms its complete supply request
        TonyAwaitOwnerCompletion,

        FoReceiving,
        FoSendFailed,
        TonySupplying,
        TonySendFinish,
        TonySendRotate,
        TonyPauseForSale,
        TonySelling,
        TonyReturnFromSale,
        TonyAwaitSaleResolution,
        FoAwaitSale,
        ReturningHome,
        Completed,
        Error,
    }

    private static global::XASlave.Localization.UiMessage GetXagmanOnhPhaseMessage(XagmanOnhPhase value)
        => value switch
        {
            XagmanOnhPhase.Idle => global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441"),
            XagmanOnhPhase.StartCharacter => global::XASlave.Localization.UiMessage.Key("S18A256D257EBC835"),
            XagmanOnhPhase.AwaitStartup => global::XASlave.Localization.UiMessage.Key("S18951979DFBF87A2"),
            XagmanOnhPhase.FoFindTony => global::XASlave.Localization.UiMessage.Key("SF1CDA68741501B0D"),
            XagmanOnhPhase.FoAwaitReservation => global::XASlave.Localization.UiMessage.Authored("Requesting an ONH collection exchange."),
            XagmanOnhPhase.FoGiving => global::XASlave.Localization.UiMessage.Key("SEBDA7F07AABD3CD5"),
            XagmanOnhPhase.FoSettle => global::XASlave.Localization.UiMessage.Key("S036D9B29686E3551"),
            XagmanOnhPhase.FoSendDoneGil => global::XASlave.Localization.UiMessage.Key("S25B6F0ED1C76AB74"),
            XagmanOnhPhase.FoConfirmCompletion => global::XASlave.Localization.UiMessage.Authored("Confirming ONH completion."),
            XagmanOnhPhase.TonySearch => global::XASlave.Localization.UiMessage.Key("SE46287AED3FC46F3"),
            XagmanOnhPhase.TonyAwaitConfirmation => global::XASlave.Localization.UiMessage.Authored("Waiting for ONH collection confirmation."),
            XagmanOnhPhase.TonyReceiving => global::XASlave.Localization.UiMessage.Key("S05F4BE0A4FF9EF83"),
            XagmanOnhPhase.TonyAwaitOwnerCompletion => global::XASlave.Localization.UiMessage.Authored("Waiting for ONH owner completion confirmation."),
            XagmanOnhPhase.FoReceiving => global::XASlave.Localization.UiMessage.Key("S4B037E60B93FC1AF"),
            XagmanOnhPhase.FoSendFailed => global::XASlave.Localization.UiMessage.Key("SB6E103E230B3A249"),
            XagmanOnhPhase.TonySupplying => global::XASlave.Localization.UiMessage.Key("SC35B5A0F022884CA"),
            XagmanOnhPhase.TonySendFinish => global::XASlave.Localization.UiMessage.Key("SE17C38490B0F0283"),
            XagmanOnhPhase.TonySendRotate => global::XASlave.Localization.UiMessage.Key("S7A5A9DE71AF333C1"),
            XagmanOnhPhase.TonyPauseForSale => global::XASlave.Localization.UiMessage.Key("SE4EE450C33D21D14"),
            XagmanOnhPhase.TonySelling => global::XASlave.Localization.UiMessage.Key("S8B183230429E73A4"),
            XagmanOnhPhase.TonyReturnFromSale => global::XASlave.Localization.UiMessage.Key("S32E60C761C6BBBF7"),
            XagmanOnhPhase.TonyAwaitSaleResolution => global::XASlave.Localization.UiMessage.Authored("Waiting for ONH sale-return confirmation."),
            XagmanOnhPhase.FoAwaitSale => global::XASlave.Localization.UiMessage.Key("S30EEAC21EF5E6FE7"),
            XagmanOnhPhase.ReturningHome => global::XASlave.Localization.UiMessage.Key("SF92BBC0FDA7E1F62"),
            XagmanOnhPhase.Completed => global::XASlave.Localization.UiMessage.Key("S22A970D2E5B1CC23"),
            XagmanOnhPhase.Error => global::XASlave.Localization.UiMessage.Key("S54A0E8C17EBB21A1"),
            _ => global::XASlave.Localization.UiMessage.Raw(value.ToString()),
        };

    // ONH run state.
    private XagmanOnhPhase xagmanOnhPhase = XagmanOnhPhase.Idle;
    private readonly List<string> xagmanOnhRunList = new();
    private int xagmanOnhIndex = -1;
    private bool xagmanOnhRegionalMatching;
    private readonly List<string> xagmanOnhPartnerRoster = new();
    private bool xagmanOnhSubTaskFailed;
    private bool xagmanOnhSubTaskCompleted;

    // The partner this character is currently engaged with (FO -> its Tony, or Tony -> its FO).
    private string xagmanOnhEngagedPartner = string.Empty;

    // Tony receiving bookkeeping.
    private readonly HashSet<string> xagmanOnhCompletedPartners = new(StringComparer.OrdinalIgnoreCase);

    // FO bookkeeping.
    private DateTime xagmanOnhFoSettleSinceUtc = DateTime.MinValue;

    // Shared sub-task abort flag for the send-1-gil builder.
    private bool xagmanOnhGilSendAborted;

    // Throttled "waiting" logging.
    private bool xagmanOnhWaitingLogged;
    private DateTime xagmanOnhLastWaitLogUtc = DateTime.MinValue;

    // Both roles receive bounded protocol tells; actual trade deltas verify signals.
    private bool xagmanOnhChatHooked;

    // Tuning (first-pass values; tune from live testing).
    private const float XagmanOnhTradeStopDistance = 1.5f;
    private const float XagmanOnhScanRadius = 60f;                    // how far Tony looks for an arriving FO
    private const int XagmanOnhTonyMinFreeSlots = 0;                  // positive capacity can receive a bounded batch
    private const double XagmanOnhWaitLogSeconds = 15.0;

    private static bool HasXagmanOnhMeetDestination(Configuration cfg)
        => !string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte);

    private IReadOnlyList<string> GetXagmanOnhPartnerRoster()
        => xagmanOnhRegionalMatching ? xagmanOnhPartnerRoster
            : xagmanActiveRole == XagmanRole.Tony
                ? plugin.Configuration.XagmanOnhFriendFoCharacters : plugin.Configuration.XagmanOnhFriendTonyCharacters;

    private bool IsXagmanOnhPartnerInActiveRegion(string key)
        => !xagmanOnhRegionalMatching
            || (!string.IsNullOrWhiteSpace(GetXagmanRegionOfChar(xagmanActiveCharacter))
                && GetXagmanRegionOfChar(key).Equals(GetXagmanRegionOfChar(xagmanActiveCharacter), StringComparison.OrdinalIgnoreCase));

    private bool HasXagmanOnhNextTonyInRegion()
        => xagmanOnhIndex + 1 < xagmanOnhRunList.Count
            && (!xagmanOnhRegionalMatching || IsXagmanOnhPartnerInActiveRegion(xagmanOnhRunList[xagmanOnhIndex + 1]));

    private bool IsXagmanOnhRegionComplete()
        => xagmanOnhRegionalMatching
            && GetXagmanOnhPartnerRoster().Where(IsXagmanOnhPartnerInActiveRegion)
                .All(xagmanOnhCompletedPartners.Contains);

    private void HoldXagmanOnhRegionalWork(global::XASlave.Localization.UiMessage reason)
    {
        // Preserve the pinned roster, goals and receipts until the operator explicitly stops.
        HoldXagmanOnhCollection(reason.Original);
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, reason);
    }

    // Entry point wired to the Start button when Outside Network Helper is enabled.
    private void StartXagmanOnhRun()
    {
        var cfg = plugin.Configuration;
        if (xagmanRunning)
            return;

        if (!TryCaptureXagmanCustomMeetingSettings())
            return;

        if (!cfg.XagmanServerMatchingEnabled && !HasXagmanOnhMeetDestination(cfg))
        {
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Key("S6FDD70B9E13253C7"));
            return;
        }

        var runList = cfg.XagmanRole == XagmanRole.Tony
            ? GetSelectedXagmanTonyCharacters().Select(entry => entry.CharacterNameWorld)
                .Where(key => !string.IsNullOrWhiteSpace(key)).ToList()
            : GetSelectedXagmanFranchiseCharacters()
                .Where(key => !string.IsNullOrWhiteSpace(key)).ToList();

        if (runList.Count == 0)
        {
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Key("S057075F486F04288"));
            return;
        }
        var partnerRoster = NormalizeXagmanOnhCharacterNames(
            cfg.XagmanRole == XagmanRole.Tony ? cfg.XagmanOnhFriendFoCharacters : cfg.XagmanOnhFriendTonyCharacters);
        if (cfg.XagmanServerMatchingEnabled
            && (partnerRoster.Count == 0 || partnerRoster.Any(key => !IsXagmanOnhFullKey(key) || string.IsNullOrWhiteSpace(GetXagmanRegionOfChar(key)))))
        {
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Authored("ONH regional matching requires valid imported character names and home worlds."));
            return;
        }
        ResetXagmanServerMatchingRunState();
        PinXagmanMeetRouteSnapshot(cfg.XagmanServerMatchingEnabled);
        foreach (var character in runList)
        {
            var meetWorld = GetXagmanFixedMeetWorld();
            global::XASlave.Localization.UiMessage routeFailure;
            var valid = cfg.XagmanServerMatchingEnabled
                ? TryValidateXagmanRegionalMeetForCharacterMessage(character, out meetWorld, out routeFailure)
                : TryValidateXagmanMeetTravelMessage(character, meetWorld, out routeFailure);
            if (valid)
                continue;

            ReportXagmanTravelRouteError(
                global::XASlave.Localization.UiMessage.Format("S9E9AAA3CCF5AEB1A", character, meetWorld, routeFailure));
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Capture(ref xagmanStatusTextUiMessage, xagmanStatusText));
            ResetXagmanMeetRouteSnapshot();
            return;
        }

        if (cfg.XagmanRole == XagmanRole.FranchiseOwner)
        {
            if (!TryPrepareXagmanOwnerPolicyRunCapabilities(
                    runList,
                    "Outside Network Helper Franchise Owner start"))
            {
                ResetXagmanMeetRouteSnapshot();
                return;
            }
        }
        else
        {
            ClearXagmanOwnerPolicyRunCapabilities();
        }

        // Both sides need the partner roster: a Tony looks for FOs by name, an FO looks for Tonys.
        if (cfg.XagmanRole == XagmanRole.FranchiseOwner && cfg.XagmanOnhFriendTonyCharacters.Count == 0)
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Key("S2143DED8F5414829"));
        if (cfg.XagmanRole == XagmanRole.Tony && cfg.XagmanOnhFriendFoCharacters.Count == 0)
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Key("S44BCBBEC41C64412"));

        // ONH never uses the peer network. Make sure it is disconnected.
        if (plugin.XagmanPeers != null && !plugin.XagmanPeers.IsDisposed && plugin.XagmanPeers.IsStarted)
            plugin.SetXagmanPeerConnectionsEnabled(false);

        ClearXagmanRunSnapshot();
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearRunHistory();
        xagmanCharacterFailureReasons.Clear();
        xagmanSkipFcReturnAfterFailureCharacter = string.Empty;
        ResetXagmanTravelFailureMonitor();
        plugin.TaskRunner.AddLog($"Xagman ONH: starting {cfg.XagmanRole} run with {runList.Count} selected character(s).");
        foreach (var regionGroup in runList.GroupBy(GetXagmanRegionOfChar))
        {
            var regionLabel = string.IsNullOrWhiteSpace(regionGroup.Key) ? "unknown" : regionGroup.Key;
            plugin.TaskRunner.AddLog(
                $"Xagman ONH: roster region {regionLabel}: {regionGroup.Count()} character(s): {string.Join(", ", regionGroup)}.");
        }
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);

        ResetXagmanOnhState();
        xagmanOnhRegionalMatching = cfg.XagmanServerMatchingEnabled;
        if (xagmanOnhRegionalMatching)
        {
            runList = NormalizeXagmanOnhCharacterNames(runList)
                .OrderBy(key => Array.IndexOf(WorldData.RegionOrder, GetXagmanRegionOfChar(key)))
                .ToList();
            xagmanOnhPartnerRoster.AddRange(partnerRoster);
        }
        xagmanOnhRunList.AddRange(runList);
        xagmanOnhIndex = 0;

        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession(global::XASlave.Localization.UiMessage.Key("S007648E8D7B842AA")))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S0C5CB5A72A548814"));
            }
            plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanStatusText}");
            ResetXagmanMeetRouteSnapshot();
            return;
        }

        SetXagmanRunning(true);
        xagmanActiveRole = cfg.XagmanRole;
        xagmanActiveCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanObservedDropboxBusy = false;
        SetXagmanActiveMeetDestination(
            xagmanOnhRegionalMatching ? GetXagmanRegionMeetWorld(GetXagmanRegionOfChar(runList[0])) : GetXagmanFixedMeetWorld(),
            GetXagmanSharedMeetLocation());
        xagmanStatus = XagmanStatus.Preflight;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SF5E0EB0E23ADFAD1", runList.Count));

        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            xagmanTonyTotalCharacters = runList.Count;
            xagmanTonyCompletedCharacters = 0;

        }
        else
        {
            xagmanOwnerTotalCharacters = runList.Count;
            xagmanOwnerCompletedCharacters = 0;
        }
        if (!TryStartXagmanOnhCollectionObservation())
        {
            FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Authored("ONH trade messages are unavailable for the current game language."));
            return;
        }
        HookXagmanOnhChat();
        if (!plugin.AutoRefuseTrade.SetOnhTradeAdmission(AdmitXagmanOnhTrade, ObserveXagmanOnhTradeStart, SuppressXagmanOnhIncomingTrade))
        {
            FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S47D1B5A2992A717E"));
            return;
        }
        plugin.TaskRunner.TotalItems = runList.Count;
        plugin.TaskRunner.CompletedItems = 0;

        plugin.TaskRunner.AddLog(
            $"Xagman ONH: started as {cfg.XagmanRole} for {runList.Count} character(s); meet {GetXagmanActiveMeetDestinationLabel()}.");
        SetXagmanOnhPhase(XagmanOnhPhase.StartCharacter);
    }

    private void SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage messagePresentation)
    {
        var message = messagePresentation.Original;
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, messagePresentation);
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    // Cleared on stop (called from StopXagmanTask) and at the start of a run.
    private void ResetXagmanOnhState()
    {
        UnhookXagmanOnhChat();
        plugin.AutoRefuseTrade.SetOnhTradeAdmission(null);
        ResetXagmanOnhProtocol();
        xagmanOnhPhase = XagmanOnhPhase.Idle;
        xagmanOnhRunList.Clear();
        xagmanOnhIndex = -1;
        xagmanOnhRegionalMatching = false;
        xagmanOnhPartnerRoster.Clear();
        xagmanOnhSubTaskFailed = false;
        xagmanOnhSubTaskCompleted = false;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanOnhCompletedPartners.Clear();
        xagmanOnhFoSettleSinceUtc = DateTime.MinValue;
        xagmanOnhGilSendAborted = false;
        xagmanOnhWaitingLogged = false;
        xagmanOnhLastWaitLogUtc = DateTime.MinValue;
    }

    private void SetXagmanOnhPhase(XagmanOnhPhase phase)
    {
        xagmanOnhPhase = phase;
    }

    private int GetXagmanOwnGil() => GetXagmanLiveLocalItemQuantity(1, false);

    private void MaybeLogOnhWait(string message)
    {
        if (xagmanOnhWaitingLogged
            && (DateTime.UtcNow - xagmanOnhLastWaitLogUtc).TotalSeconds < XagmanOnhWaitLogSeconds)
            return;
        plugin.TaskRunner.AddLog($"Xagman ONH: {message}");
        xagmanOnhWaitingLogged = true;
        xagmanOnhLastWaitLogUtc = DateTime.UtcNow;
    }

    // ---- Protocol tell subscription ----

    private void HookXagmanOnhChat()
    {
        if (xagmanOnhChatHooked)
            return;
        try
        {
            Plugin.ChatGui.CheckMessageHandled += OnXagmanOnhChatMessage;
            xagmanOnhChatHooked = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Xagman] Failed to hook chat for ONH protocol messages.");
        }
    }

    private void UnhookXagmanOnhChat()
    {
        if (!xagmanOnhChatHooked)
            return;
        try { Plugin.ChatGui.CheckMessageHandled -= OnXagmanOnhChatMessage; }
        catch { /* best effort */ }
        xagmanOnhChatHooked = false;
    }

    private void OnXagmanOnhChatMessage(IHandleableChatMessage message)
    {
        try { ReceiveXagmanOnhTell(message); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Xagman ONH] Could not parse tell."); }
    }

    // Per-frame driver. Called from UpdateXagmanFrameworkTick when ONH is running and no TaskRunner
    // sub-task is currently executing.
    private void UpdateXagmanOnhRuntime()
    {
        if (!xagmanRunning || !plugin.Configuration.XagmanOutsideNetworkHelper)
            return;
        if (onhCollectionHeld) return;
        if (!UpdateXagmanOnhIncomingRefusal()) return;
        if (!TryValidateXagmanOnhLocalVersions(out var versionFailure))
            RejectXagmanOnhTradeVersions(versionFailure);
        if (onhCollectionHeld) return;
        if (!string.IsNullOrEmpty(onhVersionFailure))
        { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Capture(onhVersionFailureUiMessage, onhVersionFailure)); return; }
        ObserveXagmanOnhTrade();
        if (onhCollectionHeld) return;
        if (xagmanActiveRole == XagmanRole.Tony) RefreshXagmanOnhQueue();
        if (plugin.TaskRunner.IsRunning)
            return;
        if (xagmanActiveRole == XagmanRole.FranchiseOwner && TryRetireXagmanOnhCollectionPartner())
            return;
        if (xagmanOnhPhase == XagmanOnhPhase.TonySelling && !onhSaleCompleted
            && plugin.TaskRunner.StatusText is "Cancelled" or "Halted")
        { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S85CFDE754AC94C85")); return; }
        if (xagmanActiveRole == XagmanRole.FranchiseOwner && onhPartnerSelling)
        {
            if (plugin.TaskRunner.StatusText is "Cancelled" or "Halted")
            { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S04DD4E6438412C4E")); return; }
            SetXagmanOnhPhase(XagmanOnhPhase.FoAwaitSale);
        }
        if (xagmanStatus == XagmanStatus.Error || xagmanOnhPhase is XagmanOnhPhase.Error or XagmanOnhPhase.Completed or XagmanOnhPhase.Idle)
            return;

        if (DriveXagmanOnhExtendedPhase()) return;
        switch (xagmanOnhPhase)
        {
            case XagmanOnhPhase.StartCharacter:
                DriveXagmanOnhStartCharacter();
                break;
            case XagmanOnhPhase.AwaitStartup:
                if (!TryConsumeXagmanOnhSubTaskCompletion())
                    return;
                DriveXagmanOnhAfterStartup();
                break;

            case XagmanOnhPhase.FoFindTony:
                DriveXagmanOnhFoFindTony();
                break;
            case XagmanOnhPhase.FoAwaitReservation:
                DriveXagmanOnhOwnerPairing();
                break;
            case XagmanOnhPhase.FoGiving:
                if (!TryConsumeXagmanOnhSubTaskCompletion())
                    return;
                DriveXagmanOnhFoAfterGive();
                break;
            case XagmanOnhPhase.FoSettle:
                DriveXagmanOnhFoSettle();
                break;
            case XagmanOnhPhase.FoSendDoneGil:
                if (!TryConsumeXagmanOnhSubTaskCompletion())
                    return;
                DriveXagmanOnhFoAfterDoneGil();
                break;

            case XagmanOnhPhase.TonySearch:
                DriveXagmanOnhTonySearch();
                break;
            case XagmanOnhPhase.TonyAwaitConfirmation:
                DriveXagmanOnhTonyPairing();
                break;
            case XagmanOnhPhase.TonyReceiving:
                DriveXagmanOnhTonyReceiving();
                break;
        }
    }

    private bool TryStartXagmanOnhSubTask(List<TaskStep> steps)
    {
        xagmanOnhSubTaskCompleted = false;
        return plugin.TaskRunner.Start(
            global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"),
            steps,
            onFinished: () => xagmanOnhSubTaskCompleted = true,
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"),
            suppressLogoutCancel: true,
            preserveRunHistory: true);
    }

    private bool TryConsumeXagmanOnhSubTaskCompletion()
    {
        if (xagmanOnhSubTaskCompleted)
        {
            xagmanOnhSubTaskCompleted = false;
            return true;
        }

        if (plugin.TaskRunner.StatusText is "Cancelled" or "Halted")
            FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Format("SE581E9D9211D7AB3", GetXagmanOnhPhaseMessage(xagmanOnhPhase), (plugin.TaskRunner.StatusText == "Cancelled" ? global::XASlave.Localization.UiMessage.Key("S8B47045EB7B8CA42") : global::XASlave.Localization.UiMessage.Key("S9399371668888E71"))));

        return false;
    }

    // ---- Shared per-character startup ----

    private void DriveXagmanOnhStartCharacter()
    {
        if (xagmanOnhIndex < 0 || xagmanOnhIndex >= xagmanOnhRunList.Count)
        {
            CompleteXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SEE16A13BC9967CA3"));
            return;
        }

        var charKey = xagmanOnhRunList[xagmanOnhIndex];
        xagmanActiveCharacter = charKey;
        if (xagmanOnhRegionalMatching)
        {
            if (!TryValidateXagmanRegionalMeetForCharacterMessage(charKey, out var meetWorld, out var routeFailure))
            {
                FailXagmanOnhRun(routeFailure);
                return;
            }
            SetXagmanActiveMeetDestination(meetWorld, GetXagmanSharedMeetLocation());
        }
        ResetXagmanOnhCharacterProtocol();
        ClearXagmanExpectedTravelLogoutWindow();
        xagmanOnhSubTaskFailed = false;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanOnhWaitingLogged = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanObservedDropboxBusy = false;

        plugin.TaskRunner.AddLog($"Xagman ONH: preparing {charKey} ({xagmanOnhIndex + 1}/{xagmanOnhRunList.Count}).");

        var steps = BuildXagmanOnhStartupSteps(
            charKey,
            plugin.Configuration.XagmanRole == XagmanRole.Tony);
        if (!TryStartXagmanOnhSubTask(steps))
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: could not start the startup sub-task for {charKey}; retrying next tick.");
            return;
        }

        SetXagmanOnhPhase(XagmanOnhPhase.AwaitStartup);
    }

    private List<TaskStep> BuildXagmanOnhStartupSteps(string charKey, bool isTony)
    {
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();

        bool ShouldSkipStartup() => xagmanOnhSubTaskFailed;

        if (plugin.Configuration.XagmanUsePreflightOnFirstCharacter && xagmanOnhIndex == 0)
            steps.AddRange(helper.BuildPreFlightOnlySteps(new List<string> { charKey }, runner, RunXagmanPreflightArMultiGuard));

        AddXagmanRelogSteps(
            steps,
            charKey,
            runner,
            () =>
            {
                xagmanStatus = XagmanStatus.Relogging;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S7B20348EE6C12574", charKey));
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC6206531396E6801", charKey, GetXagmanActiveMeetDestinationLabel()));
            },
            () =>
            {
                xagmanOnhSubTaskFailed = true;
                runner.AddLog($"Xagman ONH: failed to relog to {charKey}.");
            },
            ShouldSkipStartup);

        AddXagmanTeleportSteps(
            steps,
            global::XASlave.Localization.UiMessage.Key("SF19BAEEC30EC8680"),
            GetXagmanActiveMeetDestinationCommand,
            runner,
            () => IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()),
            false,
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC6206531396E6801", charKey, GetXagmanActiveMeetDestinationLabel()));
            },
            () =>
            {
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC53F997DEC16257A", charKey, GetXagmanActiveMeetDestinationLabel()));
            },
            () =>
            {
                xagmanOnhSubTaskFailed = true;
                runner.AddLog($"Xagman ONH: failed to travel {charKey} to {GetXagmanActiveMeetDestinationLabel()}.");
            },
            ShouldSkipStartup,
            waitStartTimeoutSec: 600f,
            reissueWhileWaiting: true,
            expectCrossDataCenterLogout: true,
            travelSourceCharacterProvider: () => charKey,
            travelDestinationWorldProvider: GetXagmanActiveMeetWorld);

        AddXagmanCustomMeetingStep(steps, charKey, ShouldSkipStartup);

        // Both roles must have Dropbox ready before negotiating collection.
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB0BBE6ACE1042AD3", charKey),
                ShouldSkip = ShouldSkipStartup,
                OnEnter = OpenXagmanDropboxWindow,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S5CD3315AC4F5BEC3", charKey),
                ShouldSkip = ShouldSkipStartup,
                OnEnter = OpenXagmanDropboxTradeTab,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S63F05866EC61C9B8", charKey), 1.0f, ShouldSkipStartup));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S15140D2161A17A49", charKey),
                ShouldSkip = ShouldSkipStartup,
                OnEnter = ClearXagmanDropbox,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
        }

        return steps;
    }

    private void DriveXagmanOnhAfterStartup()
    {
        if (xagmanOnhSubTaskFailed)
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: skipping {xagmanActiveCharacter} after a startup failure.");
            AdvanceXagmanOnhCharacter(failed: true);
            return;
        }

        if (!IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()))
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanActiveCharacter} is not at the meet spot after startup; skipping.");
            AdvanceXagmanOnhCharacter(failed: true);
            return;
        }

        xagmanOnhWaitingLogged = false;
        if (xagmanActiveRole == XagmanRole.Tony)
        {
            // Stay closed until a requesting owner is bound and compatibility is confirmed.
            if (!TrySetXagmanDropboxAutoAccept(false))
            {
                FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Format("SC96E9981A964BE11", xagmanActiveCharacter));
                return;
            }
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
                global::XASlave.Localization.UiMessage.Authored("Waiting for a valid owner collection request."));
            SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
            return;
        }

        if (GetXagmanOwnGil() < 2)
        {
            plugin.TaskRunner.AddLog("Xagman ONH: owner needs at least 2 gil for the full-inventory failure signal; recording failure and returning home.");
            AdvanceXagmanOnhCharacter(true);
            return;
        }
        InitializeXagmanOnhOwnerGoals();
        xagmanOnhEngagedPartner = string.Empty;
        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SDB748F2E2B41D4BD", xagmanActiveCharacter));
        SetXagmanOnhPhase(XagmanOnhPhase.FoFindTony);
    }

    // ---- Presence / proximity helpers ----

    // Roster order is informational. Discover the closest exact eligible actor;
    // temporarily deferred busy Tonys do not block other available candidates.
    private string FindNearbyXagmanOnhRosterMember(IReadOnlyList<string> roster, ISet<string> exclude, out Vector3 position)
    {
        position = default;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null)
            return string.Empty;

        var best = string.Empty;
        var bestDistance = float.MaxValue;
        foreach (var key in roster)
        {
            var name = key.Trim();
            if (!IsXagmanOnhFullKey(name) || exclude.Contains(name)
                || !IsXagmanOnhPartnerInActiveRegion(name)
                || (xagmanActiveRole == XagmanRole.FranchiseOwner && IsXagmanOnhTonyDeferred(name)))
                continue;
            var actor = FindXagmanOnhPlayer(name);
            if (actor == null) continue;
            var distance = Vector3.Distance(actor.Position, local.Position);
            if (distance <= XagmanOnhScanRadius && distance < bestDistance)
            {
                bestDistance = distance;
                best = GetXagmanOnhActorKey(actor);
                position = actor.Position;
            }
        }
        return best;
    }

    private bool TryGetXagmanObjectPosition(string nameOrKey, out Vector3 position)
    {
        position = default;
        var actor = FindXagmanOnhPlayer(nameOrKey);
        if (actor == null) return false;
        position = actor.Position;
        return true;
    }

    private bool IsXagmanOnhPartnerPresent(string nameOrKey)
        => TryGetXagmanObjectPosition(nameOrKey, out _);

    // ---- Advancement / completion ----

    private void AdvanceXagmanOnhCharacter(bool failed, bool incomplete = false, bool rotateForFull = false)
    {
        if (xagmanOnhRegionalMatching && xagmanActiveRole == XagmanRole.Tony
            && !IsXagmanOnhRegionComplete() && !HasXagmanOnhNextTonyInRegion())
        {
            if (!plugin.TaskRunner.IncompleteCharacters.Contains(xagmanActiveCharacter))
                plugin.TaskRunner.RecordIncompleteCharacter(xagmanActiveCharacter);
            HoldXagmanOnhRegionalWork(global::XASlave.Localization.UiMessage.Authored("ONH regional work remains, but no Tony is available in this region."));
            return;
        }
        BeginXagmanOnhReturnHome(failed, incomplete, rotateForFull);
    }

    private void FinishXagmanOnhCharacter(bool failed, bool incomplete, bool rotateForFull)
    {
        var charKey = xagmanActiveCharacter;
        if (!string.IsNullOrWhiteSpace(charKey))
        {
            if (failed && !plugin.TaskRunner.FailedCharacters.Contains(charKey))
                plugin.TaskRunner.RecordFailedCharacter(charKey);
            else if (incomplete && !plugin.TaskRunner.IncompleteCharacters.Contains(charKey))
                plugin.TaskRunner.RecordIncompleteCharacter(charKey);
        }

        xagmanActiveTradePartner = string.Empty;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanObservedDropboxBusy = false;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S8AE27137F1B12B9A", charKey)))
            return;
        ClearXagmanFocusTarget();

        // A "full" rotation is not a completed unit of work for this Tony; keep the completed count
        // tied to the index but note in the log that this was a fill-rotation, not a finished Tony.
        xagmanOnhIndex++;
        if (xagmanActiveRole == XagmanRole.Tony && IsXagmanOnhRegionComplete())
        {
            // Spare Tonys are capacity reserves, not additional passes through completed owners.
            while (xagmanOnhIndex < xagmanOnhRunList.Count
                && IsXagmanOnhPartnerInActiveRegion(xagmanOnhRunList[xagmanOnhIndex]))
                xagmanOnhIndex++;
        }
        if (xagmanActiveRole == XagmanRole.Tony)
            xagmanTonyCompletedCharacters = Math.Min(xagmanOnhIndex, xagmanTonyTotalCharacters);
        else
            xagmanOwnerCompletedCharacters = Math.Min(xagmanOnhIndex, xagmanOwnerTotalCharacters);
        plugin.TaskRunner.CompletedItems = xagmanOnhIndex;

        if (xagmanOnhIndex >= xagmanOnhRunList.Count)
        {
            if (rotateForFull)
                plugin.TaskRunner.AddLog("Xagman ONH: no more Tony characters to rotate to after filling up.");
            CompleteXagmanOnhRun((rotateForFull ? global::XASlave.Localization.UiMessage.Key("SF2E4827831242F27") : global::XASlave.Localization.UiMessage.Key("SEE16A13BC9967CA3")));
            return;
        }
        SetXagmanOnhPhase(XagmanOnhPhase.StartCharacter);
    }

    private void CompleteXagmanOnhRun(global::XASlave.Localization.UiMessage reason)
    {
        plugin.TaskRunner.AddLog($"Xagman ONH: run complete ({reason}).");
        xagmanStatus = XagmanStatus.Completed;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S095D250E9CFA75A3", reason));
        SetXagmanOnhPhase(XagmanOnhPhase.Completed);
        AutoCleanXagmanQueueResults();
        StopXagmanTask();
    }

    private void FailXagmanOnhRun(global::XASlave.Localization.UiMessage reason)
    {
        if (onhCollectionHeld) return;
        plugin.TaskRunner.AddLog($"Xagman ONH: stopping due to error - {reason}.");
        SetXagmanOnhPhase(XagmanOnhPhase.Error);
        if (plugin.TaskRunner.IsRunning) plugin.TaskRunner.RequestHalt(reason.Original);
        TryStopXagmanDropboxTradeQueue();
        if (AddonHelper.IsAddonVisible("Trade")) TryAbortXagmanTradeWindow();
        ClearXagmanDropbox();
        var tradeSafetyClosed = TryEndXagmanTradeSafetySession("Outside Network Helper failure");
        ClearXagmanFocusTarget();
        SetXagmanRunning(false);
        ResetXagmanMeetRouteSnapshot();
        ResetXagmanOnhState();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (tradeSafetyClosed ? global::XASlave.Localization.UiMessage.Format("SF2F5692FC26C5994", reason) : (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Format("S0B28A1FA80092370", reason) : global::XASlave.Localization.UiMessage.Format("S4F7FFB836308E5B6", reason))));
    }
}
