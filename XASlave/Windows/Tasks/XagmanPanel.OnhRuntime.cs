using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

// Offline queue, invitation, collection and resupply. Protocol effects live in OnhProtocol.cs.
public partial class SlaveWindow
{
    private enum XagmanOnhPhase
    {
        Idle,
        StartCharacter,      // launch the relog+travel sub-task for the current character
        AwaitStartup,        // relog+travel sub-task launched; resume here when it finishes

        // Franchise Owner
        FoFindTony,          // find a present roster Tony and approach
        FoAwaitStartGil,     // at Tony; auto-accept on; wait for Tony's +1 gil (start trigger)
        FoGiving,            // a give sub-task is running; resume here when done
        FoSettle,            // collection reconciliation and resupply request
        FoSendDoneGil,       // a send-1-gil sub-task is running; resume here when done

        // Tony queue owner
        TonySearch,          // scan roster FO nearby; wait-if-moving; approach
        TonySendStartGil,    // a send-1-gil sub-task is running; resume here when done
        TonyReceiving,       // collect until the owner confirms its complete supply request

        FoReceiving,
        FoSendFailed,
        TonySupplying,
        TonySendFinish,
        TonySendRotate,
        TonyPauseForSale,
        TonySelling,
        TonyReturnFromSale,
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
            XagmanOnhPhase.FoAwaitStartGil => global::XASlave.Localization.UiMessage.Key("S93704451549487B3"),
            XagmanOnhPhase.FoGiving => global::XASlave.Localization.UiMessage.Key("SEBDA7F07AABD3CD5"),
            XagmanOnhPhase.FoSettle => global::XASlave.Localization.UiMessage.Key("S036D9B29686E3551"),
            XagmanOnhPhase.FoSendDoneGil => global::XASlave.Localization.UiMessage.Key("S25B6F0ED1C76AB74"),
            XagmanOnhPhase.TonySearch => global::XASlave.Localization.UiMessage.Key("SE46287AED3FC46F3"),
            XagmanOnhPhase.TonySendStartGil => global::XASlave.Localization.UiMessage.Key("S3BA44B04941942FF"),
            XagmanOnhPhase.TonyReceiving => global::XASlave.Localization.UiMessage.Key("S05F4BE0A4FF9EF83"),
            XagmanOnhPhase.FoReceiving => global::XASlave.Localization.UiMessage.Key("S4B037E60B93FC1AF"),
            XagmanOnhPhase.FoSendFailed => global::XASlave.Localization.UiMessage.Key("SB6E103E230B3A249"),
            XagmanOnhPhase.TonySupplying => global::XASlave.Localization.UiMessage.Key("SC35B5A0F022884CA"),
            XagmanOnhPhase.TonySendFinish => global::XASlave.Localization.UiMessage.Key("SE17C38490B0F0283"),
            XagmanOnhPhase.TonySendRotate => global::XASlave.Localization.UiMessage.Key("S7A5A9DE71AF333C1"),
            XagmanOnhPhase.TonyPauseForSale => global::XASlave.Localization.UiMessage.Key("SE4EE450C33D21D14"),
            XagmanOnhPhase.TonySelling => global::XASlave.Localization.UiMessage.Key("S8B183230429E73A4"),
            XagmanOnhPhase.TonyReturnFromSale => global::XASlave.Localization.UiMessage.Key("S32E60C761C6BBBF7"),
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
    private bool xagmanOnhSubTaskFailed;
    private bool xagmanOnhSubTaskCompleted;

    // The partner this character is currently engaged with (FO -> its Tony, or Tony -> its FO).
    private string xagmanOnhEngagedPartner = string.Empty;

    // Tony receiving bookkeeping.
    private readonly HashSet<string> xagmanOnhCompletedPartners = new(StringComparer.OrdinalIgnoreCase);
    private DateTime xagmanOnhSearchSinceUtc = DateTime.MinValue;

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
    private const int XagmanOnhTonyMinFreeSlots = 2;                  // free slots at/below which Tony is "full"
    private const double XagmanOnhTonyIdleAdvanceSeconds = 300.0;     // no FO at all -> advance this Tony
    private const double XagmanOnhWaitLogSeconds = 15.0;

    private static bool HasXagmanOnhMeetDestination(Configuration cfg)
        => !string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte);

    // Entry point wired to the Start button when Outside Network Helper is enabled.
    private void StartXagmanOnhRun()
    {
        var cfg = plugin.Configuration;
        if (xagmanRunning)
            return;

        if (!TryCaptureXagmanCustomMeetingSettings())
            return;

        if (!HasXagmanOnhMeetDestination(cfg))
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
        foreach (var character in runList)
        {
            if (TryValidateXagmanMeetTravelMessage(character, cfg.XagmanTargetWorld, out var routeFailure))
                continue;

            ReportXagmanTravelRouteError(
                global::XASlave.Localization.UiMessage.Format("S9E9AAA3CCF5AEB1A", character, cfg.XagmanTargetWorld, routeFailure));
            SetXagmanOnhUiStatus(global::XASlave.Localization.UiMessage.Capture(ref xagmanStatusTextUiMessage, xagmanStatusText));
            return;
        }

        if (cfg.XagmanRole == XagmanRole.FranchiseOwner)
        {
            if (!TryPrepareXagmanOwnerPolicyRunCapabilities(
                    runList,
                    "Outside Network Helper Franchise Owner start"))
            {
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
            return;
        }

        SetXagmanRunning(true);
        xagmanActiveRole = cfg.XagmanRole;
        xagmanActiveCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanObservedDropboxBusy = false;
        SetXagmanActiveMeetDestination(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte);
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
        HookXagmanOnhChat();
        if (!plugin.AutoRefuseTrade.SetOnhTradeAdmission(AdmitXagmanOnhTrade, ObserveXagmanOnhTradeStart))
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
        xagmanOnhSubTaskFailed = false;
        xagmanOnhSubTaskCompleted = false;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanOnhCompletedPartners.Clear();
        xagmanOnhSearchSinceUtc = DateTime.MinValue;
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
        ObserveXagmanOnhTrade();
        if (xagmanActiveRole == XagmanRole.Tony) RefreshXagmanOnhQueue();
        if (plugin.TaskRunner.IsRunning)
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
            case XagmanOnhPhase.FoAwaitStartGil:
                DriveXagmanOnhFoAwaitStartGil();
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
            case XagmanOnhPhase.TonySendStartGil:
                if (!TryConsumeXagmanOnhSubTaskCompletion())
                    return;
                DriveXagmanOnhTonyAfterStartGil();
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

        // Both roles must have Dropbox ready before the first invitation.
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
            // Tony sends the start gil first, so begin as a giver (auto-accept off) until then.
            if (!TrySetXagmanDropboxAutoAccept(false))
            {
                FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Format("SC96E9981A964BE11", xagmanActiveCharacter));
                return;
            }
            xagmanOnhSearchSinceUtc = DateTime.UtcNow;
                xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA6582F5D40CC95E2", xagmanActiveCharacter));
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

    // Closest present roster member within the scan radius that has not already completed, plus its
    // world position (used for the Tony "wait if moving" check).
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
            var name = GetCharacterNameFromKey(key);
            if (string.IsNullOrWhiteSpace(name) || exclude.Contains(name))
                continue;
            foreach (var gameObject in Plugin.ObjectTable)
            {
                if (gameObject == null)
                    continue;
                if (!gameObject.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var distance = Vector3.Distance(gameObject.Position, local.Position);
                if (distance <= XagmanOnhScanRadius && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = name;
                    position = gameObject.Position;
                }
                break;
            }
        }
        return best;
    }

    private bool TryGetXagmanObjectPosition(string nameOrKey, out Vector3 position)
    {
        position = default;
        var visibleName = GetCharacterNameFromKey(nameOrKey);
        if (string.IsNullOrWhiteSpace(visibleName))
            return false;
        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject == null)
                continue;
            if (!gameObject.Name.ToString().Equals(visibleName, StringComparison.OrdinalIgnoreCase))
                continue;
            position = gameObject.Position;
            return true;
        }
        return false;
    }

    private bool IsXagmanOnhPartnerPresent(string nameOrKey)
        => TryGetXagmanObjectPosition(nameOrKey, out _);

    // ---- Advancement / completion ----

    private void AdvanceXagmanOnhCharacter(bool failed, bool incomplete = false, bool rotateForFull = false)
    {
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
        plugin.TaskRunner.AddLog($"Xagman ONH: stopping due to error - {reason}.");
        SetXagmanOnhPhase(XagmanOnhPhase.Error);
        if (plugin.TaskRunner.IsRunning) plugin.TaskRunner.RequestHalt(reason.Original);
        TryStopXagmanDropboxTradeQueue();
        if (AddonHelper.IsAddonVisible("Trade")) TryAbortXagmanTradeWindow();
        ClearXagmanDropbox();
        var tradeSafetyClosed = TryEndXagmanTradeSafetySession("Outside Network Helper failure");
        ClearXagmanFocusTarget();
        SetXagmanRunning(false);
        ResetXagmanOnhState();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (tradeSafetyClosed ? global::XASlave.Localization.UiMessage.Format("SF2F5692FC26C5994", reason) : (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Format("S0B28A1FA80092370", reason) : global::XASlave.Localization.UiMessage.Format("S4F7FFB836308E5B6", reason))));
    }
}
