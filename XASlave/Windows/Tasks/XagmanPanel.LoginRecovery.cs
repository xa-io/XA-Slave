using System;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using XASlave.Data;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private enum LoginRecoveryPhase { Monitoring, WaitingForIdle, WaitingForTitle, Replaying, Held }

    private sealed class XagmanLoginRecovery
    {
        public XagmanLoginStallGuard Guard = new();
        public XagmanRole Role;
        public string Character = string.Empty;
        public string NativeActivity = string.Empty;
        public LoginRecoveryPhase Phase;
        public long StageStarted;
        public long NextPoll;
        public long IntentExpires;
        public bool IntentConsumed;
        public bool ManualResume;
        public UiMessage HoldMessage = UiMessage.Raw(string.Empty);
    }

    // Queues and confirmations are actionable lobby states, never a blank-loading stall.
    // Loading overlays may be allocated yet invisible during the observed failure.
    private static readonly string[] XagmanLoginMenusAndDialogs =
    [
        "_TitleMenu", "_TitleLogo", "TitleDCWorldMap", "CharaSelect", "_CharaSelectListMenu",
        "_CharaSelectReturn", "CharaSelectWorldServer", "CharaSelectDataCenter",
        "CharaSelectCharacterList", "CharaSelectQueue", "CharaSelectWait",
        "Dialogue", "SelectOk", "SelectYesno", "SelectString", "InputString", "MovieStaffList",
    ];

    private bool IsXagmanLoginRecoveryOwned(XagmanLobbyOperation op)
        => ReferenceEquals(op, xagmanLobbyOperation) && !isDisposed && xagmanRunning
            && op.DirectLogin && op.RunnerOwned && plugin.TaskRunner.IsRunning
            && plugin.TaskRunner.CurrentRunId == op.RunId
            && op.LoginRecovery != null && op.LoginRecovery.Role == xagmanActiveRole
            && op.LoginRecovery.Character == op.Character;

    private unsafe bool ObserveXagmanLoginStall(XagmanLobbyOperation op, out string activity, out string nativeActivity)
    {
        var game = GameMain.Instance();
        var readable = plugin.IpcClient.TryGetLifestreamBusy(out var busy);
        var menu = XagmanLoginMenusAndDialogs.Any(AddonHelper.IsAddonVisible);
        nativeActivity = game == null ? "no-native-context" :
            $"{game->ConnectedToZone}|{game->TerritoryLoadState}|{game->CurrentTerritoryTypeId}|{game->NextTerritoryTypeId}";
        activity = $"{nativeActivity}|{readable}:{busy}";
        return XagmanLoginStallGuard.IsCandidate(IsXagmanLoginRecoveryOwned(op),
            Plugin.ClientState.IsLoggedIn, Plugin.ObjectTable.LocalPlayer != null,
            game != null && game->ConnectedToZone, game == null || game->CurrentTerritoryTypeId != 0,
            menu || plugin.LobbyErrorAutoClose.HasSupportedError);
    }

    private static bool IsXagmanRecoveryTitleReady()
        => !Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer == null
            && AddonHelper.IsAddonVisible("_TitleMenu") && AddonHelper.IsAddonReady("_TitleMenu")
            && !XagmanLoginMenusAndDialogs.Any(name => name != "_TitleMenu" && name != "_TitleLogo"
                && AddonHelper.IsAddonVisible(name));

    private void HoldXagmanLoginRecovery(XagmanLobbyOperation op, UiMessage reason, string diagnostic)
    {
        if (!IsXagmanLoginRecoveryOwned(op) || op.LoginRecovery == null) return;
        op.Recovery?.Stop();
        op.Recovery = null;
        op.Pending = true;
        op.LoginRecovery.Phase = LoginRecoveryPhase.Held;
        op.LoginRecovery.HoldMessage = reason;
        xagmanStatus = XagmanStatus.Relogging;
        xagmanStatusText = UiMessage.Store(ref xagmanStatusTextUiMessage, reason);
        plugin.TaskRunner.AddLog($"Xagman: login recovery holds {op.Character}; run {op.RunId}, current step retained. {diagnostic}");
        // Keep the same task, cursor, quantities and completion receipts. Explicit Stop
        // remains the only action here that abandons the operation.
    }

    private bool PollXagmanLoginRecovery(XagmanLobbyOperation op)
    {
        var state = op.LoginRecovery;
        if (state == null) return false;
        if (!IsXagmanLoginRecoveryOwned(op))
        {
            EndXagmanLobbyOperation();
            return true;
        }
        var now = Environment.TickCount64;
        if (state.Phase == LoginRecoveryPhase.Held)
        {
            xagmanStatusText = UiMessage.Store(ref xagmanStatusTextUiMessage, state.HoldMessage);
            return true;
        }
        if (state.Phase != LoginRecoveryPhase.Replaying)
        {
            if (now < state.NextPoll) return state.Phase != LoginRecoveryPhase.Monitoring;
            state.NextPoll = now + 250;
        }
        if (state.Phase == LoginRecoveryPhase.Monitoring)
        {
            if (op.Pending) { state.Guard.ResetObservation(); return false; }
            var candidate = ObserveXagmanLoginStall(op, out var activity, out var nativeActivity);
            if (!state.Guard.Observe(now, candidate, activity)) return false;
            if (op.Attempts >= 2 || !state.Guard.TryBeginRecovery(now))
            {
                HoldXagmanLoginRecovery(op, UiMessage.Key(UiLocale.Key("Login recovery reached its retry or time limit. Progress is retained.")), "Recovery budget exhausted.");
                return true;
            }
            op.Attempts = Math.Max(op.Attempts + 1, state.Guard.Attempts);
            op.Pending = true;
            op.RecoveryStarted = DateTime.UtcNow;
            state.Phase = LoginRecoveryPhase.WaitingForIdle;
            state.StageStarted = now;
            state.NativeActivity = nativeActivity;
            xagmanStatus = XagmanStatus.Relogging;
            xagmanStatusText = UiMessage.Store(ref xagmanStatusTextUiMessage,
                UiMessage.Format(UiLocale.Key("Recovering stalled login for {0} ({1}/2)."), op.Character, op.Attempts));
            plugin.TaskRunner.AddLog($"Xagman: {op.Character} has no playable character or usable lobby screen after 180 seconds of unchanged connected loading state; recovering the same login, attempt {op.Attempts}/2.");
            if (!CancelLifestreamTaskMessage(out var cancellation))
                HoldXagmanLoginRecovery(op, cancellation, "Owned Lifestream abort was not dispatched.");
            return true;
        }
        if (!state.ManualResume && state.Guard.BudgetExpired(now))
        {
            HoldXagmanLoginRecovery(op, UiMessage.Key(UiLocale.Key("Login recovery reached its retry or time limit. Progress is retained.")), "Monotonic recovery deadline elapsed.");
            return true;
        }
        if (state.Phase == LoginRecoveryPhase.Replaying) return false; // Existing guarded preflight/replay owns this stage.

        // Deliberate reset errors belong to this operation; do not restart ordinary
        // popup recovery and discard its one-shot disconnect/result state.
        op.ErrorSequence = plugin.LobbyErrorAutoClose.ErrorSequence;
        if (IsXagmanRecoveryTitleReady())
        {
            state.IntentExpires = 0;
            state.Phase = LoginRecoveryPhase.Replaying;
            BuildXagmanLobbyRecovery(op);
            return true;
        }
        if (state.Phase == LoginRecoveryPhase.WaitingForTitle)
        {
            if (now - state.StageStarted >= 60_000)
                HoldXagmanLoginRecovery(op, UiMessage.Key(UiLocale.Key("Login recovery did not reach the title screen. Progress is retained.")), "No usable title menu within 60 seconds of reset.");
            return true;
        }
        if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy)
        {
            if (now - state.StageStarted >= 30_000)
                HoldXagmanLoginRecovery(op, UiMessage.Key("SEA7B11495698DDD2"), "Lifestream idle could not be confirmed after abort.");
            return true;
        }
        // A menu, player or native transition that changed while aborting invalidates
        // reset admission. Resume ordinary verification without a network mutation.
        if (!ObserveXagmanLoginStall(op, out _, out var currentNativeActivity)
            || currentNativeActivity != state.NativeActivity)
        {
            state.Phase = LoginRecoveryPhase.Monitoring;
            state.Guard.ResetObservation();
            op.Pending = plugin.LobbyErrorAutoClose.HasSupportedError;
            return false;
        }
        state.Phase = LoginRecoveryPhase.WaitingForTitle;
        state.StageStarted = now;
        state.IntentConsumed = false;
        state.IntentExpires = now + 30_000; // Arm before dispatch: logout can arrive before a result.
        var result = XagmanLoginTcpReset.TryResetCurrentProcess(
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "xagman-login-recovery"), Guid.NewGuid(),
            () => ObserveXagmanLoginStall(op, out _, out var latestNativeActivity)
                && latestNativeActivity == state.NativeActivity);
        if (!result.Attempted) state.IntentExpires = 0;
        plugin.TaskRunner.AddLog($"Xagman: login connection reset: {result.Diagnostic}");
        if (!result.Succeeded && !result.Attempted)
        {
            // The game may have progressed during the final transport admission
            // check. Preserve natural login verification without another reset.
            if (!ObserveXagmanLoginStall(op, out _, out var latestNativeActivity)
                || latestNativeActivity != state.NativeActivity)
            {
                state.Phase = LoginRecoveryPhase.Monitoring;
                state.Guard.ResetObservation();
                op.Pending = plugin.LobbyErrorAutoClose.HasSupportedError;
                return false;
            }
            var reason = result.RequiresElevation
                ? UiMessage.Key(UiLocale.Key("Login recovery requires administrator access. Progress is retained."))
                : UiMessage.Key(UiLocale.Key("Login recovery could not reset this client's game connection. Progress is retained."));
            HoldXagmanLoginRecovery(op, reason, result.Diagnostic);
            return true;
        }
        // A partial/uncertain attempt is never resent. One reset may already have
        // caused the other connection to disappear or native loading to progress.
        // Independent title observation decides whether continuation is safe.
        xagmanStatusText = UiMessage.Store(ref xagmanStatusTextUiMessage,
            UiMessage.Format(UiLocale.Key("Waiting for the title screen before retrying {0}."), op.Character));
        return true;
    }

    private bool TryConsumeXagmanLoginRecoveryLogout()
    {
        var op = xagmanLobbyOperation;
        if (op == null || !IsXagmanLoginRecoveryOwned(op) || op.LoginRecovery == null) return false;
        var state = op.LoginRecovery;
        if (state.IntentConsumed || state.IntentExpires == 0 || Environment.TickCount64 > state.IntentExpires
            || plugin.LastClientStateLogoutCode != 90002) return false;
        state.IntentConsumed = true;
        state.IntentExpires = 0;
        xagmanExpectedLogout = false;
        plugin.TaskRunner.AddLog($"Xagman: consumed intentional login-recovery 90002 for {op.Character}; retaining run {op.RunId} without starting a fleet recovery.");
        return true;
    }

    private void OnXagmanLoginRecoveryReplayed(XagmanLobbyOperation op)
    {
        if (op.LoginRecovery is not { } state) return;
        state.Phase = LoginRecoveryPhase.Monitoring;
        state.Guard.ResetObservation();
        state.IntentExpires = 0;
        state.ManualResume = false;
    }

    private void DrawXagmanLoginRecoveryControls()
    {
        if (xagmanLobbyOperation is not { LoginRecovery.Phase: LoginRecoveryPhase.Held } op) return;
        // This only requests guarded replay after the user has recovered the title
        // screen. It never grants extra automatic resets or sends commands from Draw.
        using (ImRaii.Disabled(!IsXagmanRecoveryTitleReady()))
        {
            if (UiNative.Button(UiLocale.Native(UiLocale.Key("Resume recovered login")) + "##xagmanLoginRecoveryResume"))
                xagmanLoginRecoveryResumeRequested = true;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(UiLocale.Get(UiLocale.Key("Return to the title screen, then select Resume recovered login to continue the retained task.")));
    }

    private bool xagmanLoginRecoveryResumeRequested;

    private void ApplyXagmanLoginRecoveryResume(XagmanLobbyOperation op)
    {
        if (!xagmanLoginRecoveryResumeRequested) return;
        xagmanLoginRecoveryResumeRequested = false;
        if (!IsXagmanLoginRecoveryOwned(op) || op.LoginRecovery is not { Phase: LoginRecoveryPhase.Held } state
            || !IsXagmanRecoveryTitleReady()) return;
        state.Phase = LoginRecoveryPhase.Replaying;
        state.ManualResume = true;
        op.RecoveryStarted = DateTime.UtcNow;
        // The verified title closes the manual ejection's deliberate error. Do not
        // let that earlier popup cancel the fresh replay as a new lobby failure.
        op.ErrorSequence = plugin.LobbyErrorAutoClose.ErrorSequence;
        BuildXagmanLobbyRecovery(op);
    }
}
