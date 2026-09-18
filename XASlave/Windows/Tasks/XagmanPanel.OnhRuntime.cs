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

        }
        else
        {
            xagmanOwnerTotalCharacters = runList.Count;
            xagmanOwnerCompletedCharacters = 0;
        }
        HookXagmanOnhChat();
        if (!plugin.AutoRefuseTrade.SetOnhTradeAdmission(AdmitXagmanOnhTrade, ObserveXagmanOnhTradeStart))
        {
            FailXagmanOnhRun("required partner admission hooks are unavailable");
            return;
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
        { FailXagmanOnhRun("selling was interrupted"); return; }
        if (xagmanActiveRole == XagmanRole.FranchiseOwner && onhPartnerSelling)
        {
            if (plugin.TaskRunner.StatusText is "Cancelled" or "Halted")
            { FailXagmanOnhRun("owner collection was interrupted during selling pause"); return; }
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
            "Xagman",
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
            FailXagmanOnhRun($"the {xagmanOnhPhase} sub-task ended as {plugin.TaskRunner.StatusText.ToLowerInvariant()} instead of completing");

        return false;
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

        // Both roles must have Dropbox ready before the first invitation.
        {
            steps.Add(new TaskStep
            {
                Name = $"Xagman ONH Open Dropbox: {charKey}",
                ShouldSkip = ShouldSkipStartup,
                OnEnter = OpenXagmanDropboxWindow,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                Name = $"Xagman ONH Open Trade Tab: {charKey}",
                ShouldSkip = ShouldSkipStartup,
                OnEnter = OpenXagmanDropboxTradeTab,
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Xagman ONH Dropbox Wait: {charKey}", 1.0f, ShouldSkipStartup));
            steps.Add(new TaskStep
            {
                Name = $"Xagman ONH Clear Dropbox: {charKey}",
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
                xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = $"Tony {xagmanActiveCharacter} is staged; looking for a Franchise Owner.";
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
        xagmanStatusText = $"{xagmanActiveCharacter} is at the meet spot; looking for a Tony.";
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
        if (plugin.TaskRunner.IsRunning) plugin.TaskRunner.RequestHalt(reason);
        TryStopXagmanDropboxTradeQueue();
        if (AddonHelper.IsAddonVisible("Trade")) TryAbortXagmanTradeWindow();
        ClearXagmanDropbox();
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
