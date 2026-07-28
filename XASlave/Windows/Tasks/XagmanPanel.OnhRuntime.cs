using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

// Xagman "Outside Network Helper" (ONH) runtime - 1-gil handshake edition.
//
// ONH coordinates two DIFFERENT players on DIFFERENT machines with NO shared peer hub. Normal
// Xagman discovers partners and signals readiness/completion over the TCP peer network
// (XagmanPeerService). ONH cannot: this plugin instance only controls its own characters and can
// never see the partner's plugin. So coordination is purely in-game, using 1-gil trades as the
// readiness/completion signals (gil is conserved: each side nets +0 per cycle):
//
//   START / RESUME : Tony -> FO (1 gil). Tony scans nearby players; when one is on its imported FO
//                    roster it waits up to ~10s for them to stop moving, then approaches and sends
//                    1 gil. The FO watches its own gil; +1 = "begin giving" (also used to resume
//                    after a Tony rotates because it filled up).
//   ITEM TRANSFER  : the existing Dropbox give flow. FO is the giver; Tony only receives.
//   DONE           : FO -> Tony (1 gil). After the give finishes and both Dropboxes are idle for
//                    ~5s, the FO sends 1 gil. Tony sees its gil go up -> marks that FO complete and
//                    logs it (chat line captured for enrichment). The FO then returns home + relogs.
//   TONY FULL      : Dropbox busy + a trade error (or free slots exhausted) -> Tony disables
//                    auto-accept and rotates to its next Tony character; on arrival it re-finds the
//                    still-present FO and re-sends the start gil to resume.
//
// The runtime is a self-hosted state machine driven from UpdateXagmanFrameworkTick (exactly like
// the normal Tony runtime), and it delegates linear per-character relog+travel and each Dropbox
// trade to short TaskRunner sub-tasks built from the SAME reusable step builders the peer flow
// uses. Sub-tasks are named "Xagman" so the existing priority Stop button and StopXagmanTask handle
// ONH for free. No peer publishing or peer connections are touched here.
public partial class SlaveWindow
{
    private enum XagmanOnhPhase
    {
        Idle,
        StartCharacter,      // launch the relog+travel sub-task for the current character
        AwaitStartup,        // relog+travel sub-task launched; resume here when it finishes

        // Franchise Owner (giver)
        FoFindTony,          // find a present roster Tony and approach
        FoAwaitStartGil,     // at Tony; auto-accept on; wait for Tony's +1 gil (start trigger)
        FoGiving,            // a give sub-task is running; resume here when done
        FoSettle,            // post-give 5s idle settle before signalling done
        FoSendDoneGil,       // a send-1-gil sub-task is running; resume here when done

        // Tony (receiver / driver)
        TonySearch,          // scan roster FO nearby; wait-if-moving; approach
        TonySendStartGil,    // a send-1-gil sub-task is running; resume here when done
        TonyReceiving,       // receiving items; watch full/errors; wait for the FO's done gil

        Completed,
        Error,
    }

    // ONH run state.
    private XagmanOnhPhase xagmanOnhPhase = XagmanOnhPhase.Idle;
    private readonly List<string> xagmanOnhRunList = new();
    private int xagmanOnhIndex = -1;
    private bool xagmanOnhSubTaskFailed;

    // The partner this character is currently engaged with (FO -> its Tony, or Tony -> its FO).
    private string xagmanOnhEngagedPartner = string.Empty;

    // Own-gil snapshot used to detect an incoming 1-gil signal (gil = item id 1).
    private int xagmanOnhGilBaseline = -1;

    // Tony candidate "wait if moving" tracking.
    private string xagmanOnhCandidate = string.Empty;
    private DateTime xagmanOnhCandidateFirstSeenUtc = DateTime.MinValue;
    private DateTime xagmanOnhCandidateLastMovedUtc = DateTime.MinValue;
    private Vector3 xagmanOnhCandidateLastPos;

    // Tony receiving bookkeeping.
    private readonly HashSet<string> xagmanOnhCompletedPartners = new(StringComparer.OrdinalIgnoreCase);
    private int xagmanOnhTonyLastFreeSlots = -1;
    private DateTime xagmanOnhTonyLastActivityUtc = DateTime.MinValue;
    private DateTime xagmanOnhSearchSinceUtc = DateTime.MinValue;

    // FO bookkeeping.
    private DateTime xagmanOnhFoAwaitSinceUtc = DateTime.MinValue;
    private DateTime xagmanOnhFoSettleSinceUtc = DateTime.MinValue;
    private int xagmanOnhGiveRemainingBefore;

    // Shared sub-task abort flag for the send-1-gil builder.
    private bool xagmanOnhGilSendAborted;

    // Throttled "waiting" logging.
    private bool xagmanOnhWaitingLogged;
    private DateTime xagmanOnhLastWaitLogUtc = DateTime.MinValue;

    // Chat capture (Tony) - log enrichment only; the gil delta is the authoritative signal.
    private bool xagmanOnhChatHooked;
    private string xagmanOnhLastTradeChatLine = string.Empty;

    // Tuning (first-pass values; tune from live testing).
    private const float XagmanOnhTradeStopDistance = 1.5f;
    private const float XagmanOnhScanRadius = 60f;                    // how far Tony looks for an arriving FO
    private const float XagmanOnhMoveEpsilon = 0.3f;                  // position delta that counts as "still moving"
    private const int XagmanOnhTonyMinFreeSlots = 2;                  // free slots at/below which Tony is "full"
    private const double XagmanOnhMoveSettleSeconds = 1.5;            // candidate must be still this long...
    private const double XagmanOnhMoveMaxWaitSeconds = 10.0;          // ...or proceed anyway after this
    private const double XagmanOnhFoSettleSeconds = 5.0;              // FO post-give idle before the done gil
    private const double XagmanOnhTonyReceiveSettleSeconds = 2.5;     // quiet window before trusting the done gil
    private const double XagmanOnhStartGilTimeoutSeconds = 90.0;      // FO waiting for the start gil
    private const double XagmanOnhDoneGilTimeoutSeconds = 120.0;      // Tony waiting for the done gil
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

        if (!HasXagmanOnhMeetDestination(cfg))
        {
            SetXagmanOnhUiStatus("Xagman ONH: set your meet world and location first.");
            return;
        }

        var runList = cfg.XagmanRole == XagmanRole.Tony
            ? GetSelectedXagmanTonyCharacters().Select(entry => entry.CharacterNameWorld)
                .Where(key => !string.IsNullOrWhiteSpace(key)).ToList()
            : GetSelectedXagmanFranchiseCharacters()
                .Where(key => !string.IsNullOrWhiteSpace(key)).ToList();

        if (runList.Count == 0)
        {
            SetXagmanOnhUiStatus("Xagman ONH: select at least one character to run.");
            return;
        }
        foreach (var character in runList)
        {
            if (TryValidateXagmanMeetTravel(character, cfg.XagmanTargetWorld, out var routeFailure))
                continue;

            ReportXagmanTravelRouteError(
                $"Outside Network Helper start rejected {character} -> {cfg.XagmanTargetWorld}: {routeFailure}");
            SetXagmanOnhUiStatus(xagmanStatusText);
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
            SetXagmanOnhUiStatus("Xagman ONH: warning - no partner Tony characters imported; nobody to give to.");
        if (cfg.XagmanRole == XagmanRole.Tony && cfg.XagmanOnhFriendFoCharacters.Count == 0)
            SetXagmanOnhUiStatus("Xagman ONH: warning - no partner Franchise Owner characters imported; nobody to call.");

        // ONH never uses the peer network. Make sure it is disconnected.
        if (plugin.XagmanPeers != null && !plugin.XagmanPeers.IsDisposed && plugin.XagmanPeers.IsStarted)
            plugin.SetXagmanPeerConnectionsEnabled(false);

        ClearXagmanRunSnapshot();
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearLog();
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);

        ResetXagmanOnhState();
        xagmanOnhRunList.AddRange(runList);
        xagmanOnhIndex = 0;

        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession("Outside Network Helper start"))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = "Outside Network Helper could not establish safe Dropbox/refusal coordination.";
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
        xagmanStatusText = $"Outside Network Helper starting ({runList.Count} character(s)).";

        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            xagmanTonyTotalCharacters = runList.Count;
            xagmanTonyCompletedCharacters = 0;
            HookXagmanOnhChat();
        }
        else
        {
            xagmanOwnerTotalCharacters = runList.Count;
            xagmanOwnerCompletedCharacters = 0;
        }
        plugin.TaskRunner.TotalItems = runList.Count;
        plugin.TaskRunner.CompletedItems = 0;

        plugin.TaskRunner.AddLog(
            $"Xagman ONH: started as {cfg.XagmanRole} for {runList.Count} character(s); meet {GetXagmanActiveMeetDestinationLabel()}.");
        SetXagmanOnhPhase(XagmanOnhPhase.StartCharacter);
    }

    private void SetXagmanOnhUiStatus(string message)
    {
        arImportStatus = message;
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    // Cleared on stop (called from StopXagmanTask) and at the start of a run.
    private void ResetXagmanOnhState()
    {
        UnhookXagmanOnhChat();
        xagmanOnhPhase = XagmanOnhPhase.Idle;
        xagmanOnhRunList.Clear();
        xagmanOnhIndex = -1;
        xagmanOnhSubTaskFailed = false;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanOnhGilBaseline = -1;
        ClearXagmanOnhCandidate();
        xagmanOnhCompletedPartners.Clear();
        xagmanOnhTonyLastFreeSlots = -1;
        xagmanOnhTonyLastActivityUtc = DateTime.MinValue;
        xagmanOnhSearchSinceUtc = DateTime.MinValue;
        xagmanOnhFoAwaitSinceUtc = DateTime.MinValue;
        xagmanOnhFoSettleSinceUtc = DateTime.MinValue;
        xagmanOnhGiveRemainingBefore = 0;
        xagmanOnhGilSendAborted = false;
        xagmanOnhWaitingLogged = false;
        xagmanOnhLastWaitLogUtc = DateTime.MinValue;
        xagmanOnhLastTradeChatLine = string.Empty;
    }

    private void ClearXagmanOnhCandidate()
    {
        xagmanOnhCandidate = string.Empty;
        xagmanOnhCandidateFirstSeenUtc = DateTime.MinValue;
        xagmanOnhCandidateLastMovedUtc = DateTime.MinValue;
        xagmanOnhCandidateLastPos = default;
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

    // ---- Chat capture (Tony) ----

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
            Plugin.Log.Warning(ex, "[Xagman] Failed to hook chat for ONH completion logging.");
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
        try
        {
            if (message.IsHandled)
                return;
            var text = message.Message.TextValue;
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (text.IndexOf("gil", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("trade", StringComparison.OrdinalIgnoreCase) >= 0)
                xagmanOnhLastTradeChatLine = text.Trim();
        }
        catch { /* never let chat parsing throw into the game */ }
    }

    // Per-frame driver. Called from UpdateXagmanFrameworkTick when ONH is running and no TaskRunner
    // sub-task is currently executing.
    private void UpdateXagmanOnhRuntime()
    {
        if (!xagmanRunning || !plugin.Configuration.XagmanOutsideNetworkHelper)
            return;
        if (plugin.TaskRunner.IsRunning)
            return;
        if (xagmanStatus == XagmanStatus.Error || xagmanOnhPhase is XagmanOnhPhase.Error or XagmanOnhPhase.Completed or XagmanOnhPhase.Idle)
            return;

        switch (xagmanOnhPhase)
        {
            case XagmanOnhPhase.StartCharacter:
                DriveXagmanOnhStartCharacter();
                break;
            case XagmanOnhPhase.AwaitStartup:
                DriveXagmanOnhAfterStartup();
                break;

            case XagmanOnhPhase.FoFindTony:
                DriveXagmanOnhFoFindTony();
                break;
            case XagmanOnhPhase.FoAwaitStartGil:
                DriveXagmanOnhFoAwaitStartGil();
                break;
            case XagmanOnhPhase.FoGiving:
                DriveXagmanOnhFoAfterGive();
                break;
            case XagmanOnhPhase.FoSettle:
                DriveXagmanOnhFoSettle();
                break;
            case XagmanOnhPhase.FoSendDoneGil:
                DriveXagmanOnhFoAfterDoneGil();
                break;

            case XagmanOnhPhase.TonySearch:
                DriveXagmanOnhTonySearch();
                break;
            case XagmanOnhPhase.TonySendStartGil:
                DriveXagmanOnhTonyAfterStartGil();
                break;
            case XagmanOnhPhase.TonyReceiving:
                DriveXagmanOnhTonyReceiving();
                break;
        }
    }

    // ---- Shared per-character startup ----

    private void DriveXagmanOnhStartCharacter()
    {
        if (xagmanOnhIndex < 0 || xagmanOnhIndex >= xagmanOnhRunList.Count)
        {
            CompleteXagmanOnhRun("all characters processed");
            return;
        }

        var charKey = xagmanOnhRunList[xagmanOnhIndex];
        xagmanActiveCharacter = charKey;
        ClearXagmanExpectedTravelLogoutWindow();
        xagmanOnhSubTaskFailed = false;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanOnhWaitingLogged = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanObservedDropboxBusy = false;
        ClearXagmanOnhCandidate();

        plugin.TaskRunner.AddLog($"Xagman ONH: preparing {charKey} ({xagmanOnhIndex + 1}/{xagmanOnhRunList.Count}).");

        var steps = BuildXagmanOnhStartupSteps(
            charKey,
            plugin.Configuration.XagmanRole == XagmanRole.Tony);
        SetXagmanOnhPhase(XagmanOnhPhase.AwaitStartup);
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private List<TaskStep> BuildXagmanOnhStartupSteps(string charKey, bool isTony)
    {
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
        runner.SuppressLogoutCancel = true;

        bool ShouldSkipStartup() => xagmanOnhSubTaskFailed;

        if (plugin.Configuration.XagmanUsePreflightOnFirstCharacter && xagmanOnhIndex == 0)
            steps.AddRange(helper.BuildPreFlightOnlySteps(new List<string> { charKey }, runner));

        AddXagmanRelogSteps(
            steps,
            charKey,
            runner,
            () =>
            {
                xagmanStatus = XagmanStatus.Relogging;
                xagmanStatusText = $"Relogging to {charKey}.";
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = $"Traveling {charKey} to {GetXagmanActiveMeetDestinationLabel()}.";
            },
            () =>
            {
                xagmanOnhSubTaskFailed = true;
                runner.AddLog($"Xagman ONH: failed to relog to {charKey}.");
            },
            ShouldSkipStartup);

        AddXagmanTeleportSteps(
            steps,
            "ONH Meet",
            GetXagmanActiveMeetDestinationCommand,
            runner,
            () => IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()),
            false,
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = $"Traveling {charKey} to {GetXagmanActiveMeetDestinationLabel()}.";
            },
            () =>
            {
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = $"{charKey} is staged at {GetXagmanActiveMeetDestinationLabel()}.";
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

        if (isTony)
        {
            steps.Add(new TaskStep
            {
                Name = $"Xagman ONH Tony Open Dropbox: {charKey}",
                ShouldSkip = ShouldSkipStartup,
                OnEnter = OpenXagmanDropboxWindow,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman ONH Tony Open Trade Tab: {charKey}",
                ShouldSkip = ShouldSkipStartup,
                OnEnter = OpenXagmanDropboxTradeTab,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman ONH Tony Dropbox Wait: {charKey}", 1.0f, ShouldSkipStartup));
            steps.Add(new TaskStep
            {
                Name = $"Xagman ONH Tony Clear Dropbox: {charKey}",
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
                FailXagmanOnhRun($"could not confirm Dropbox auto-accept off for Tony {xagmanActiveCharacter}");
                return;
            }
            xagmanOnhSearchSinceUtc = DateTime.UtcNow;
            ClearXagmanOnhCandidate();
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is staged; looking for a Franchise Owner.";
            SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
            return;
        }

        xagmanOnhEngagedPartner = string.Empty;
        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = $"{xagmanActiveCharacter} is at the meet spot; looking for a Tony.";
        SetXagmanOnhPhase(XagmanOnhPhase.FoFindTony);
    }

    // ---- Franchise Owner (giver) ----

    private void DriveXagmanOnhFoFindTony()
    {
        var cfg = plugin.Configuration;

        var remaining = CountXagmanOnhGiveUnitsAvailable(cfg.XagmanItems);
        if (remaining <= 0)
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanActiveCharacter} has nothing left to give; advancing.");
            AdvanceXagmanOnhCharacter(failed: false);
            return;
        }

        var tony = FindPresentXagmanOnhPartner(cfg.XagmanOnhFriendTonyCharacters);
        if (string.IsNullOrWhiteSpace(tony))
        {
            xagmanStatus = XagmanStatus.Standby;
            xagmanStatusText = $"{xagmanActiveCharacter} is waiting for a Tony at {GetXagmanActiveMeetDestinationLabel()}.";
            MaybeLogOnhWait($"{xagmanActiveCharacter} sees no roster Tony nearby; standing by ({remaining} unit(s) to give).");
            return;
        }

        xagmanOnhEngagedPartner = tony;
        xagmanActiveTradePartner = tony;

        if (!IsCurrentTargetWithinStopDistanceAndStopped(tony, XagmanOnhTradeStopDistance))
        {
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = $"{xagmanActiveCharacter} approaching {tony}.";
            return;
        }

        BeginXagmanOnhFoAwaitStart("waiting for the start signal");
    }

    private void BeginXagmanOnhFoAwaitStart(string reason)
    {
        // Receiver: enable auto-accept so the partner's 1-gil start trade is accepted.
        var activeCharacter = xagmanActiveCharacter;
        if (!TryRequireXagmanReceiverAutoAccept($"ONH owner {activeCharacter} waiting for start gil"))
        {
            FailXagmanOnhRun($"failed to enable Dropbox auto-accept for {activeCharacter}");
            return;
        }
        xagmanOnhGilBaseline = GetXagmanOwnGil();
        xagmanOnhFoAwaitSinceUtc = DateTime.UtcNow;
        xagmanOnhWaitingLogged = false;
        xagmanStatus = XagmanStatus.Standby;
        xagmanStatusText = $"{xagmanActiveCharacter} {reason} from {xagmanOnhEngagedPartner}.";
        SetXagmanOnhPhase(XagmanOnhPhase.FoAwaitStartGil);
    }

    private void DriveXagmanOnhFoAwaitStartGil()
    {
        var cfg = plugin.Configuration;

        if (!IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner))
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanActiveCharacter} lost sight of {xagmanOnhEngagedPartner}; re-finding a Tony.");
            if (!TrySetXagmanDropboxAutoAccept(false))
            {
                FailXagmanOnhRun($"could not disable Dropbox auto-accept after {xagmanActiveCharacter} lost its Tony");
                return;
            }
            SetXagmanOnhPhase(XagmanOnhPhase.FoFindTony);
            return;
        }

        var gilNow = GetXagmanOwnGil();
        if (xagmanOnhGilBaseline >= 0 && gilNow > xagmanOnhGilBaseline)
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanActiveCharacter} received the start gil from {xagmanOnhEngagedPartner}; giving items.");
            var remaining = CountXagmanOnhGiveUnitsAvailable(cfg.XagmanItems);
            xagmanStatus = XagmanStatus.Trading;
            xagmanStatusText = $"{xagmanActiveCharacter} is giving to {xagmanOnhEngagedPartner}.";
            var steps = BuildXagmanOnhGiveSteps(xagmanOnhEngagedPartner, remaining);
            SetXagmanOnhPhase(XagmanOnhPhase.FoGiving);
            plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
            return;
        }

        if ((DateTime.UtcNow - xagmanOnhFoAwaitSinceUtc).TotalSeconds >= XagmanOnhStartGilTimeoutSeconds)
        {
            plugin.TaskRunner.AddLog(
                $"Xagman ONH: {xagmanActiveCharacter} got no start gil from {xagmanOnhEngagedPartner} for {XagmanOnhStartGilTimeoutSeconds / 60.0:0} min; re-finding a Tony.");
            if (!TrySetXagmanDropboxAutoAccept(false))
            {
                FailXagmanOnhRun($"could not disable Dropbox auto-accept after {xagmanActiveCharacter} timed out waiting for start gil");
                return;
            }
            SetXagmanOnhPhase(XagmanOnhPhase.FoFindTony);
            return;
        }

        xagmanStatus = XagmanStatus.Standby;
        xagmanStatusText = $"{xagmanActiveCharacter} waiting for {xagmanOnhEngagedPartner} to start.";
        MaybeLogOnhWait($"{xagmanActiveCharacter} waiting for the start gil from {xagmanOnhEngagedPartner}.");
    }

    private void DriveXagmanOnhFoAfterGive()
    {
        xagmanObservedDropboxBusy = false;
        xagmanActiveTradePartner = xagmanOnhEngagedPartner;
        xagmanOnhFoSettleSinceUtc = DateTime.UtcNow;
        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = $"{xagmanActiveCharacter} finished a give pass; settling before the completion signal.";
        SetXagmanOnhPhase(XagmanOnhPhase.FoSettle);
    }

    private void DriveXagmanOnhFoSettle()
    {
        var cfg = plugin.Configuration;

        if (plugin.IpcClient.DropboxIsBusy())
        {
            xagmanOnhFoSettleSinceUtc = DateTime.UtcNow; // keep resetting while still trading
            xagmanStatus = XagmanStatus.Trading;
            xagmanStatusText = $"{xagmanActiveCharacter} is still trading; waiting for Dropbox to finish.";
            return;
        }

        if ((DateTime.UtcNow - xagmanOnhFoSettleSinceUtc).TotalSeconds < XagmanOnhFoSettleSeconds)
        {
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"{xagmanActiveCharacter} settling before the completion signal.";
            return;
        }

        var remaining = CountXagmanOnhGiveUnitsAvailable(cfg.XagmanItems);
        if (remaining > 0)
        {
            // Items still left: the Tony almost certainly filled up and rotated. Go re-find whichever
            // Tony is present now and wait for a fresh start gil to resume giving.
            plugin.TaskRunner.AddLog(
                $"Xagman ONH: {xagmanActiveCharacter} still has {remaining} unit(s) to give (Tony likely full); waiting for a start gil to resume.");
            SetXagmanOnhPhase(XagmanOnhPhase.FoFindTony);
            return;
        }

        xagmanStatus = XagmanStatus.Trading;
        xagmanStatusText = $"{xagmanActiveCharacter} sending the completion gil to {xagmanOnhEngagedPartner}.";
        var steps = BuildXagmanOnhSendGilSteps(xagmanOnhEngagedPartner, $"done -> {xagmanOnhEngagedPartner}");
        SetXagmanOnhPhase(XagmanOnhPhase.FoSendDoneGil);
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private void DriveXagmanOnhFoAfterDoneGil()
    {
        if (xagmanOnhGilSendAborted)
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanActiveCharacter} failed to send the completion gil to {xagmanOnhEngagedPartner}; retrying.");
            xagmanOnhFoSettleSinceUtc = DateTime.UtcNow;
            SetXagmanOnhPhase(XagmanOnhPhase.FoSettle);
            return;
        }

        plugin.TaskRunner.AddLog($"Xagman ONH: {xagmanActiveCharacter} signalled completion to {xagmanOnhEngagedPartner}; returning home and relogging next.");
        AdvanceXagmanOnhCharacter(failed: false);
    }

    private List<TaskStep> BuildXagmanOnhGiveSteps(string partner, int remainingBefore)
    {
        xagmanOnhGiveRemainingBefore = remainingBefore;
        var runner = plugin.TaskRunner;
        var steps = new List<TaskStep>();
        var partnerName = GetCharacterNameFromKey(partner);
        var aborted = false;
        var queued = 0;
        var sawBusy = false;
        var waitStartUtc = DateTime.MinValue;

        bool Aborted() => aborted;
        bool SkipTrade() => aborted || queued <= 0;

        steps.Add(new TaskStep
        {
            Name = $"Xagman ONH Give Approach: {partnerName}",
            OnEnter = () =>
            {
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = $"{xagmanActiveCharacter} approaching {partnerName}.";
                TryTargetCharacter(partnerName);
            },
            IsComplete = () => IsCurrentTargetWithinStopDistanceAndStopped(partnerName, XagmanOnhTradeStopDistance),
            TimeoutSec = 12f,
            OnTimeout = () =>
            {
                aborted = true;
                runner.AddLog($"Xagman ONH: could not reach {partnerName} for the trade.");
            },
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Give Prime Dropbox",
            ShouldSkip = Aborted,
            OnEnter = PrimeXagmanDropbox,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("Xagman ONH Give Prime Wait", 0.5f, Aborted));
        AppendXagmanDropboxAutoAcceptStep(steps, "Xagman ONH Give", false, Aborted);
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Give Queue Items",
            ShouldSkip = Aborted,
            OnEnter = () => queued = QueueXagmanOwnerCollectionItems(plugin.Configuration.XagmanItems),
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Give Begin Trade",
            ShouldSkip = SkipTrade,
            OnEnter = () =>
            {
                if (!StartXagmanDropboxTrade($"ONH give {xagmanActiveCharacter} -> {partnerName}"))
                {
                    aborted = true;
                    return;
                }
                xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Give Wait",
            ShouldSkip = SkipTrade,
            OnEnter = () =>
            {
                waitStartUtc = DateTime.UtcNow;
                sawBusy = plugin.IpcClient.DropboxIsBusy();
                xagmanStatus = XagmanStatus.Trading;
                xagmanStatusText = $"{xagmanActiveCharacter} trading with {partnerName}.";
            },
            IsComplete = () =>
            {
                if (plugin.IpcClient.DropboxIsBusy())
                {
                    sawBusy = true;
                    xagmanObservedDropboxBusy = true;
                    return false;
                }
                if (sawBusy)
                    return true;
                // Never went busy: nothing was actually traded (e.g. partner full). Give it a short
                // grace window before treating the pass as a no-op.
                return (DateTime.UtcNow - waitStartUtc).TotalSeconds >= 5.0;
            },
            TimeoutSec = 600f,
            OnTimeout = () =>
            {
                aborted = true;
                CleanupXagmanDropboxTradeAttempt($"ONH give {xagmanActiveCharacter} -> {partnerName} timeout");
            },
        });
        AppendXagmanDropboxAutoAcceptStep(steps, "Xagman ONH Give Cleanup", false);
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Give Cleanup",
            OnEnter = () =>
            {
                xagmanObservedDropboxBusy = false;
                ClearXagmanDropbox();
                ClearXagmanFocusTarget();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        return steps;
    }

    // ---- Tony (receiver / driver) ----

    private void DriveXagmanOnhTonySearch()
    {
        var cfg = plugin.Configuration;
        var foRoster = cfg.XagmanOnhFriendFoCharacters;

        if (string.IsNullOrWhiteSpace(xagmanOnhCandidate))
        {
            var found = FindNearbyXagmanOnhRosterMember(foRoster, xagmanOnhCompletedPartners, out var pos);
            if (string.IsNullOrWhiteSpace(found))
            {
                xagmanStatus = XagmanStatus.Standby;
                xagmanStatusText = $"Tony {xagmanActiveCharacter} is waiting for a Franchise Owner at {GetXagmanActiveMeetDestinationLabel()}.";
                MaybeLogOnhWait($"Tony {xagmanActiveCharacter} sees no roster Franchise Owner nearby; standing by.");
                if (xagmanOnhSearchSinceUtc != DateTime.MinValue
                    && (DateTime.UtcNow - xagmanOnhSearchSinceUtc).TotalSeconds >= XagmanOnhTonyIdleAdvanceSeconds)
                {
                    plugin.TaskRunner.AddLog(
                        $"Xagman ONH: Tony {xagmanActiveCharacter} saw no Franchise Owner for {XagmanOnhTonyIdleAdvanceSeconds / 60.0:0} min; advancing.");
                    AdvanceXagmanOnhCharacter(failed: false);
                }
                return;
            }

            xagmanOnhCandidate = found;
            xagmanOnhCandidateFirstSeenUtc = DateTime.UtcNow;
            xagmanOnhCandidateLastMovedUtc = DateTime.UtcNow;
            xagmanOnhCandidateLastPos = pos;
            xagmanOnhWaitingLogged = false;
            plugin.TaskRunner.AddLog($"Xagman ONH: Tony {xagmanActiveCharacter} spotted Franchise Owner {found}; waiting for them to settle.");
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} found {found}; waiting for them to stop moving.";
            return;
        }

        if (!TryGetXagmanObjectPosition(xagmanOnhCandidate, out var currentPos))
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: Tony {xagmanActiveCharacter} lost sight of {xagmanOnhCandidate}; rescanning.");
            ClearXagmanOnhCandidate();
            return;
        }

        if (Vector3.Distance(currentPos, xagmanOnhCandidateLastPos) > XagmanOnhMoveEpsilon)
        {
            xagmanOnhCandidateLastMovedUtc = DateTime.UtcNow;
            xagmanOnhCandidateLastPos = currentPos;
        }

        var stillForSeconds = (DateTime.UtcNow - xagmanOnhCandidateLastMovedUtc).TotalSeconds;
        var waitedForSeconds = (DateTime.UtcNow - xagmanOnhCandidateFirstSeenUtc).TotalSeconds;
        if (stillForSeconds < XagmanOnhMoveSettleSeconds && waitedForSeconds < XagmanOnhMoveMaxWaitSeconds)
        {
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} waiting for {xagmanOnhCandidate} to settle ({waitedForSeconds:0}s).";
            return;
        }

        if (!IsCurrentTargetWithinStopDistanceAndStopped(xagmanOnhCandidate, XagmanOnhTradeStopDistance))
        {
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} approaching {xagmanOnhCandidate}.";
            return;
        }

        // In range and settled: engage and send the start gil.
        xagmanOnhEngagedPartner = xagmanOnhCandidate;
        xagmanActiveTradePartner = xagmanOnhCandidate;
        xagmanStatus = XagmanStatus.Trading;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} sending the start signal to {xagmanOnhCandidate}.";
        var steps = BuildXagmanOnhSendGilSteps(xagmanOnhCandidate, $"start -> {xagmanOnhCandidate}");
        SetXagmanOnhPhase(XagmanOnhPhase.TonySendStartGil);
        plugin.TaskRunner.Start("Xagman", steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"));
    }

    private void DriveXagmanOnhTonyAfterStartGil()
    {
        if (xagmanOnhGilSendAborted)
        {
            plugin.TaskRunner.AddLog($"Xagman ONH: Tony {xagmanActiveCharacter} failed to send the start gil to {xagmanOnhEngagedPartner}; rescanning.");
            xagmanOnhEngagedPartner = string.Empty;
            xagmanActiveTradePartner = string.Empty;
            ClearXagmanOnhCandidate();
            SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
            return;
        }

        // Become the receiver for the item give + the FO's eventual completion gil.
        var activeCharacter = xagmanActiveCharacter;
        if (!TryRequireXagmanReceiverAutoAccept($"ONH Tony {activeCharacter} receiving from {xagmanOnhEngagedPartner}"))
        {
            FailXagmanOnhRun($"failed to enable Dropbox auto-accept for {activeCharacter}");
            return;
        }

        xagmanOnhGilBaseline = GetXagmanOwnGil();
        xagmanOnhTonyLastFreeSlots = GetXagmanLiveLocalMainInventoryFreeSlots();
        xagmanOnhTonyLastActivityUtc = DateTime.UtcNow;
        xagmanObservedDropboxBusy = false;
        plugin.TaskRunner.AddLog(
            $"Xagman ONH: Tony {xagmanActiveCharacter} sent the start gil to {xagmanOnhEngagedPartner}; receiving " +
            $"(gil baseline {xagmanOnhGilBaseline}, {xagmanOnhTonyLastFreeSlots} free slot(s)).");
        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} receiving from {xagmanOnhEngagedPartner}.";
        SetXagmanOnhPhase(XagmanOnhPhase.TonyReceiving);
    }

    private void DriveXagmanOnhTonyReceiving()
    {
        var busy = plugin.IpcClient.DropboxIsBusy();
        var freeSlots = GetXagmanLiveLocalMainInventoryFreeSlots();
        var gilNow = GetXagmanOwnGil();

        if (busy)
        {
            xagmanObservedDropboxBusy = true;
            xagmanOnhTonyLastActivityUtc = DateTime.UtcNow;
            xagmanStatus = XagmanStatus.Trading;
        }

        // Inventory shrank -> an item arrived; count it as activity.
        if (xagmanOnhTonyLastFreeSlots >= 0 && freeSlots < xagmanOnhTonyLastFreeSlots)
            xagmanOnhTonyLastActivityUtc = DateTime.UtcNow;
        xagmanOnhTonyLastFreeSlots = freeSlots;

        // Full / trade-error -> rotate to the next Tony so the FO can resume on a fresh receiver.
        var failureKind = GetXagmanTradeFailureKind(out var failureText);
        var full = freeSlots <= XagmanOnhTonyMinFreeSlots
                   || (busy && failureKind != XagmanTradeFailureKind.None);
        if (full)
        {
            var errorNote = failureKind != XagmanTradeFailureKind.None ? $", error: {failureText}" : string.Empty;
            plugin.TaskRunner.AddLog(
                $"Xagman ONH: Tony {xagmanActiveCharacter} is full ({freeSlots} free{errorNote}); rotating to the next Tony so {xagmanOnhEngagedPartner} can resume.");
            AdvanceXagmanOnhCharacter(failed: false, rotateForFull: true);
            return;
        }

        if (busy)
        {
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is receiving a trade from {xagmanOnhEngagedPartner}.";
            return;
        }

        // Completion gil: only trust it once the transfer has been quiet for a moment (so giving gil
        // as a normal item can't be mistaken for the done signal).
        var quietForSeconds = (DateTime.UtcNow - xagmanOnhTonyLastActivityUtc).TotalSeconds;
        if (quietForSeconds >= XagmanOnhTonyReceiveSettleSeconds
            && xagmanOnhGilBaseline >= 0 && gilNow > xagmanOnhGilBaseline)
        {
            var chatNote = string.IsNullOrWhiteSpace(xagmanOnhLastTradeChatLine)
                ? string.Empty
                : $" [chat: {xagmanOnhLastTradeChatLine}]";
            plugin.TaskRunner.AddLog(
                $"Xagman ONH: Tony {xagmanActiveCharacter} received the completion gil from {xagmanOnhEngagedPartner}; marking complete.{chatNote}");
            if (!string.IsNullOrWhiteSpace(xagmanOnhEngagedPartner))
                xagmanOnhCompletedPartners.Add(GetCharacterNameFromKey(xagmanOnhEngagedPartner));
            xagmanOnhGilBaseline = gilNow;
            FinishXagmanOnhTonyEngagement();
            return;
        }

        // No completion gil for a long time -> release this FO and look for another.
        if (quietForSeconds >= XagmanOnhDoneGilTimeoutSeconds)
        {
            plugin.TaskRunner.AddLog(
                $"Xagman ONH: Tony {xagmanActiveCharacter} saw no completion gil from {xagmanOnhEngagedPartner} for {XagmanOnhDoneGilTimeoutSeconds / 60.0:0} min; releasing.");
            FinishXagmanOnhTonyEngagement();
            return;
        }

        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} receiving from {xagmanOnhEngagedPartner} ({freeSlots} free).";
    }

    private void FinishXagmanOnhTonyEngagement()
    {
        xagmanOnhEngagedPartner = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanObservedDropboxBusy = false;
        ClearXagmanOnhCandidate();
        xagmanOnhSearchSinceUtc = DateTime.UtcNow;
        xagmanOnhWaitingLogged = false;
        // Stay ready as a giver for the next FO's start gil.
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "ONH Tony engagement completion"))
            return;
        SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
    }

    // ---- Shared 1-gil send sub-task ----

    private List<TaskStep> BuildXagmanOnhSendGilSteps(string targetKey, string contextLabel)
    {
        xagmanOnhGilSendAborted = false;
        var runner = plugin.TaskRunner;
        var steps = new List<TaskStep>();
        var targetName = GetCharacterNameFromKey(targetKey);
        var sawBusy = false;
        var waitStartUtc = DateTime.MinValue;

        bool Aborted() => xagmanOnhGilSendAborted;

        steps.Add(new TaskStep
        {
            Name = $"Xagman ONH Gil Approach: {targetName}",
            OnEnter = () =>
            {
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = $"{xagmanActiveCharacter} approaching {targetName} for the 1-gil signal.";
                TryTargetCharacter(targetName);
            },
            IsComplete = () => IsCurrentTargetWithinStopDistanceAndStopped(targetName, XagmanOnhTradeStopDistance),
            TimeoutSec = 15f,
            OnTimeout = () =>
            {
                xagmanOnhGilSendAborted = true;
                runner.AddLog($"Xagman ONH: could not reach {targetName} to send 1 gil ({contextLabel}).");
            },
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Gil Prime Dropbox",
            ShouldSkip = Aborted,
            OnEnter = PrimeXagmanDropbox,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("Xagman ONH Gil Prime Wait", 0.5f, Aborted));
        // Sender is the giver for this trade: auto-accept off.
        AppendXagmanDropboxAutoAcceptStep(steps, "Xagman ONH Gil", false, Aborted);
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Gil Queue (1 gil)",
            ShouldSkip = Aborted,
            OnEnter = () =>
            {
                if (!plugin.IpcClient.DropboxSetItemQuantity(1, false, 1))
                {
                    xagmanOnhGilSendAborted = true;
                    runner.AddLog($"Xagman ONH: failed to queue 1 gil ({contextLabel}).");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Gil Begin Trade",
            ShouldSkip = Aborted,
            OnEnter = () =>
            {
                if (!StartXagmanDropboxTrade($"ONH 1-gil {contextLabel}"))
                {
                    xagmanOnhGilSendAborted = true;
                    return;
                }
                xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Gil Wait",
            ShouldSkip = Aborted,
            OnEnter = () =>
            {
                waitStartUtc = DateTime.UtcNow;
                sawBusy = plugin.IpcClient.DropboxIsBusy();
                xagmanStatus = XagmanStatus.Trading;
                xagmanStatusText = $"{xagmanActiveCharacter} sending 1 gil to {targetName}.";
            },
            IsComplete = () =>
            {
                if (plugin.IpcClient.DropboxIsBusy())
                {
                    sawBusy = true;
                    xagmanObservedDropboxBusy = true;
                    return false;
                }
                if (sawBusy)
                    return true;
                // A 1-gil trade can complete before a busy frame is ever observed; allow a short grace.
                return (DateTime.UtcNow - waitStartUtc).TotalSeconds >= 6.0;
            },
            TimeoutSec = 60f,
            OnTimeout = () =>
            {
                xagmanOnhGilSendAborted = true;
                CleanupXagmanDropboxTradeAttempt($"ONH 1-gil {contextLabel} timeout");
            },
        });
        steps.Add(new TaskStep
        {
            Name = "Xagman ONH Gil Cleanup",
            OnEnter = () =>
            {
                xagmanObservedDropboxBusy = false;
                ClearXagmanDropbox();
                ClearXagmanFocusTarget();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        return steps;
    }

    // ---- Presence / proximity helpers ----

    // Sum of givable units still on the active character, mirroring QueueXagmanOwnerCollectionItems'
    // quantity rules but WITHOUT touching the Dropbox queue.
    private int CountXagmanOnhGiveUnitsAvailable(IReadOnlyList<XagmanItemEntry> items)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var effectiveItems = ResolveXagmanItemsForOwner(items, localCharacter);
        var total = 0;
        foreach (var item in effectiveItems)
        {
            if (item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
                continue;
            var localAvailable = GetXagmanCharacterItemQuantity(localCharacter, item.ItemId, item.IsHq, item.ItemName);
            var quantity = item.Mode switch
            {
                XagmanItemMode.Give => item.Quantity <= 0 ? localAvailable : Math.Min(localAvailable, item.Quantity),
                XagmanItemMode.Balance => Math.Max(0, localAvailable - Math.Max(0, item.Quantity)),
                _ => 0,
            };
            total += Math.Max(0, quantity);
        }
        return total;
    }

    // Returns the roster name of the first partner currently present/targetable, or empty.
    private string FindPresentXagmanOnhPartner(IReadOnlyList<string> roster)
    {
        foreach (var key in roster)
        {
            var name = GetCharacterNameFromKey(key);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (AddonHelper.TryTargetByName(name, out _))
                return name;
        }
        return string.Empty;
    }

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
        var charKey = xagmanActiveCharacter;
        if (!string.IsNullOrWhiteSpace(charKey))
        {
            if (failed && !plugin.TaskRunner.FailedCharacters.Contains(charKey))
                plugin.TaskRunner.FailedCharacters.Add(charKey);
            else if (incomplete && !plugin.TaskRunner.IncompleteCharacters.Contains(charKey))
                plugin.TaskRunner.IncompleteCharacters.Add(charKey);
        }

        xagmanActiveTradePartner = string.Empty;
        xagmanOnhEngagedPartner = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanOnhGilBaseline = -1;
        xagmanOnhTonyLastFreeSlots = -1;
        ClearXagmanOnhCandidate();
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"ONH character advance from {charKey}"))
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
            CompleteXagmanOnhRun(rotateForFull ? "out of Tony characters" : "all characters processed");
            return;
        }
        SetXagmanOnhPhase(XagmanOnhPhase.StartCharacter);
    }

    private void CompleteXagmanOnhRun(string reason)
    {
        plugin.TaskRunner.AddLog($"Xagman ONH: run complete ({reason}).");
        xagmanStatus = XagmanStatus.Completed;
        xagmanStatusText = $"Outside Network Helper finished ({reason}).";
        SetXagmanOnhPhase(XagmanOnhPhase.Completed);
        StopXagmanTask();
    }

    private void FailXagmanOnhRun(string reason)
    {
        plugin.TaskRunner.AddLog($"Xagman ONH: stopping due to error - {reason}.");
        SetXagmanOnhPhase(XagmanOnhPhase.Error);
        var tradeSafetyClosed = TryEndXagmanTradeSafetySession("Outside Network Helper failure");
        ClearXagmanFocusTarget();
        SetXagmanRunning(false);
        ResetXagmanOnhState();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = tradeSafetyClosed
            ? $"Outside Network Helper error: {reason}"
            : xagmanTradeSafetySessionActive
                ? $"Outside Network Helper error: {reason}; Dropbox auto-accept state remains unknown and trade refusal is suppressed."
                : $"Outside Network Helper error: {reason}; Dropbox auto-accept is off, but the saved manual Refuse Trade Request preference could not be restored.";
    }
}
