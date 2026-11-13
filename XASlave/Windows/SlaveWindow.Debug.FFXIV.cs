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
    private bool debugLoadingEjectionRequested;
    private LoadingScreenEjectionAttempt? debugLoadingEjectionAttempt;
    private UiMessage debugLoadingEjectionMessage = UiMessage.Raw(string.Empty);
    private string debugLoadingEjectionNativeActivity = string.Empty;
    private long debugLoadingEjectionStarted;
    private long debugLoadingEjectionCommitted;
    private long debugLoadingEjectionNextMonitor;
    private XagmanLobbyOperation? debugLoadingEjectionOperation;
    private bool debugLoadingEjectionWasHeld;
    private UiMessage debugLoadingEjectionPreviousHold = UiMessage.Raw(string.Empty);

    private void DrawXaAbuseFfxiv()
    {
        ImGui.TextWrapped(UiLocale.Get(UiLocale.Key("Disconnect this client's game-server connections to escape a stuck loading screen. Windows may ask for administrator approval.")));
        using (ImRaii.Disabled(debugLoadingEjectionRequested || debugLoadingEjectionAttempt != null))
        {
            if (UiNative.Button(UiLocale.Native(UiLocale.Key("Loading Screen Ejection")) + "##xaAbuseLoadingEjection"))
                debugLoadingEjectionRequested = true;
        }
        if (!string.IsNullOrEmpty(debugLoadingEjectionMessage.Original))
            ImGui.TextWrapped(debugLoadingEjectionMessage.Display);
    }

    private static unsafe bool TryGetLoadingEjectionContext(out string nativeActivity)
    {
        var game = GameMain.Instance();
        nativeActivity = game == null ? string.Empty :
            $"{game->ConnectedToZone}|{game->TerritoryLoadState}|{game->CurrentTerritoryTypeId}|{game->NextTerritoryTypeId}";
        return XagmanLoginStallGuard.IsCandidate(true, Plugin.ClientState.IsLoggedIn,
            Plugin.ObjectTable.LocalPlayer != null, game != null && game->ConnectedToZone,
            game == null || game->CurrentTerritoryTypeId != 0,
            XagmanLoginMenusAndDialogs.Any(AddonHelper.IsAddonVisible));
    }

    private bool CanStartLoadingEjection()
    {
        if (!plugin.TaskRunner.IsRunning && !xagmanRunning) return true;
        var op = xagmanLobbyOperation;
        return op != null && IsXagmanLoginRecoveryOwned(op) && op.Recovery == null
            && op.LoginRecovery != null
            && (op.LoginRecovery.Phase == LoginRecoveryPhase.Held
                || (op.LoginRecovery.Phase == LoginRecoveryPhase.Monitoring && !op.Pending));
    }

    private bool IsLoadingEjectionOwnershipCurrent()
        => debugLoadingEjectionOperation is { } op
            ? IsXagmanLoginRecoveryOwned(op)
            : !plugin.TaskRunner.IsRunning && !xagmanRunning;

    private void SetLoadingEjectionMessage(UiMessage message)
    {
        debugLoadingEjectionMessage = message;
        SetDebugResult(message);
    }

    private void StartDebugLoadingEjection()
    {
        debugLoadingEjectionRequested = false;
        if (debugLoadingEjectionAttempt != null || isDisposed) return;
        if (!TryGetLoadingEjectionContext(out var activity))
        {
            SetLoadingEjectionMessage(UiMessage.Key(UiLocale.Key("No stuck loading screen was detected. No disconnect was attempted.")));
            return;
        }
        if (!CanStartLoadingEjection())
        {
            SetLoadingEjectionMessage(UiMessage.Key(UiLocale.Key("Stop other active tasks before using Loading Screen Ejection.")));
            return;
        }
        if (!LoadingScreenEjectionAttempt.TryStart(
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "loading-screen-ejection"),
            out var attempt, out var diagnostic) || attempt == null)
        {
            Plugin.Log.Information($"[XASlave] Loading Screen Ejection preparation: {diagnostic}");
            SetLoadingEjectionMessage(UiMessage.Key(UiLocale.Key("Loading Screen Ejection did not disconnect the client. See the debug log for details.")));
            return;
        }
        debugLoadingEjectionAttempt = attempt;
        debugLoadingEjectionNativeActivity = activity;
        debugLoadingEjectionStarted = Environment.TickCount64;
        debugLoadingEjectionCommitted = 0;
        debugLoadingEjectionNextMonitor = 0;
        debugLoadingEjectionOperation = xagmanLobbyOperation is { } op && IsXagmanLoginRecoveryOwned(op) ? op : null;
        if (debugLoadingEjectionOperation?.LoginRecovery is { } state)
        {
            debugLoadingEjectionWasHeld = state.Phase == LoginRecoveryPhase.Held;
            debugLoadingEjectionPreviousHold = state.HoldMessage;
            state.Phase = LoginRecoveryPhase.Held;
            state.HoldMessage = UiMessage.Key(UiLocale.Key("Waiting for Loading Screen Ejection. Xagman progress is retained."));
            debugLoadingEjectionOperation.Pending = true;
            xagmanStatus = XagmanStatus.Relogging;
        }
        SetLoadingEjectionMessage(UiMessage.Key(UiLocale.Key("Loading Screen Ejection is waiting for Windows approval.")));
    }

    private void BeforeLoadingEjectionReset()
    {
        // Called on Framework.Update immediately before granting the first one-shot
        // reset. Never let UAC/pipe continuation threads read or mutate game state.
        if (isDisposed || !IsLoadingEjectionOwnershipCurrent()
            || !TryGetLoadingEjectionContext(out var activity) || activity != debugLoadingEjectionNativeActivity)
            throw new InvalidOperationException("Loading Screen Ejection admission changed before reset.");
        if (debugLoadingEjectionCommitted != 0) return;
        debugLoadingEjectionCommitted = Environment.TickCount64;
        plugin.LobbyErrorAutoClose.ArmTemporaryMonitor();
        if (debugLoadingEjectionOperation?.LoginRecovery is { } state)
        {
            state.IntentConsumed = false;
            state.IntentExpires = debugLoadingEjectionCommitted + 30_000;
        }
        SetLoadingEjectionMessage(UiMessage.Key(UiLocale.Key("Loading Screen Ejection is waiting for the title screen.")));
    }

    private void FinishDebugLoadingEjection(bool titleVerified, bool mayHaveReset)
    {
        var attempt = debugLoadingEjectionAttempt;
        debugLoadingEjectionAttempt = null;
        attempt?.Dispose(); // EOF/expiry ends the helper; never terminate a game process.
        Plugin.Log.Information($"[XASlave] Loading Screen Ejection: titleVerified={titleVerified}, mayHaveReset={mayHaveReset}; {attempt?.Diagnostic}");
        var message = titleVerified
            ? UiMessage.Key(UiLocale.Key("Loading Screen Ejection reached the title screen."))
            : mayHaveReset
                ? UiMessage.Key(UiLocale.Key("Loading Screen Ejection finished without a verified title screen. See the debug log for details."))
                : UiMessage.Key(UiLocale.Key("Loading Screen Ejection did not disconnect the client. See the debug log for details."));
        if (debugLoadingEjectionOperation is { LoginRecovery: { } state } op && IsXagmanLoginRecoveryOwned(op))
        {
            if (titleVerified)
            {
                state.IntentExpires = 0;
                // Existing pause-aware task polling performs guarded same-character replay.
                xagmanLoginRecoveryResumeRequested = true;
            }
            else if (!mayHaveReset)
            {
                state.IntentExpires = 0;
                state.Phase = debugLoadingEjectionWasHeld ? LoginRecoveryPhase.Held : LoginRecoveryPhase.Monitoring;
                state.HoldMessage = debugLoadingEjectionPreviousHold;
                state.Guard.ResetObservation();
                op.Pending = debugLoadingEjectionWasHeld;
            }
            else state.HoldMessage = message; // Uncertain effect retains the current task.
        }
        debugLoadingEjectionOperation = null;
        SetLoadingEjectionMessage(message);
    }

    private void UpdateDebugLoadingScreenEjection()
    {
        if (isDisposed) return;
        try
        {
            if (debugLoadingEjectionRequested) StartDebugLoadingEjection();
            var attempt = debugLoadingEjectionAttempt;
            if (attempt == null) return;
            var mayHaveReset = attempt.MayHaveReset || debugLoadingEjectionCommitted != 0;
            if (IsXagmanRecoveryTitleReady())
            {
                FinishDebugLoadingEjection(true, mayHaveReset);
                return;
            }
            var admissionValid = IsLoadingEjectionOwnershipCurrent()
                && TryGetLoadingEjectionContext(out var activity) && activity == debugLoadingEjectionNativeActivity;
            attempt.Poll(admissionValid, BeforeLoadingEjectionReset);
            mayHaveReset = attempt.MayHaveReset || debugLoadingEjectionCommitted != 0;
            if (mayHaveReset && debugLoadingEjectionCommitted == 0)
                debugLoadingEjectionCommitted = Environment.TickCount64;
            if (!mayHaveReset && (attempt.IsFinished || !admissionValid))
            {
                FinishDebugLoadingEjection(false, false);
                return;
            }
            var now = Environment.TickCount64;
            if (mayHaveReset && now >= debugLoadingEjectionNextMonitor)
            {
                debugLoadingEjectionNextMonitor = now + 1_000;
                plugin.LobbyErrorAutoClose.ArmTemporaryMonitor();
            }
            if ((mayHaveReset && now - debugLoadingEjectionCommitted >= 60_000)
                || (!mayHaveReset && now - debugLoadingEjectionStarted >= 180_000))
                FinishDebugLoadingEjection(false, mayHaveReset);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] Loading Screen Ejection stopped.");
            FinishDebugLoadingEjection(false, debugLoadingEjectionAttempt?.MayHaveReset == true || debugLoadingEjectionCommitted != 0);
        }
    }

    private void DisposeDebugLoadingScreenEjection()
    {
        debugLoadingEjectionRequested = false;
        debugLoadingEjectionAttempt?.Dispose();
        debugLoadingEjectionAttempt = null;
        debugLoadingEjectionOperation = null;
    }
}
