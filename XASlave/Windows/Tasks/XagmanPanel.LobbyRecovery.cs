using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class XagmanLobbyOperation
    {
        public long RunId;
        public bool RunnerOwned;
        public string Command = string.Empty;
        public string Character = string.Empty;
        public string World = string.Empty;
        public global::XASlave.Localization.UiMessage LabelMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        public string Label => LabelMessage.Original;
        public bool DirectLogin;
        public bool ExpectedTravelLogout;
        public Action? BeforeReplay;
        public Func<bool>? Completed;
        public long ErrorSequence;
        public DateTime QuietSince;
        public DateTime NextPoll;
        public DateTime RecoveryStarted;
        public string Activity = string.Empty;
        public bool Pending;
        public int Attempts;
        public StepMachine? Recovery;
    }

    private XagmanLobbyOperation? xagmanLobbyOperation;
    private static readonly string[] XagmanLobbyActivityAddons =
    [
        "Dialogue", "SelectOk", "SelectYesno", "_TitleMenu", "_TitleLogo", "TitleDCWorldMap",
        "CharaSelect", "_CharaSelectListMenu", "_CharaSelectReturn", "CharaSelectWorldServer",
        "CharaSelectDataCenter", "CharaSelectCharacterList", "NowLoading", "MovieStaffList",
    ];

    // Shared by the debug button and automatic recovery. Success means dispatch, not idle.
    private bool CancelLifestreamTask(out string message)
    {
        var result = CancelLifestreamTaskMessage(out var presentation);
        message = presentation.Original;
        return result;
    }

    private bool CancelLifestreamTaskMessage(out global::XASlave.Localization.UiMessage message)
    {
        var sent = plugin.IpcClient.LifestreamAbort();
        message = (sent ? global::XASlave.Localization.UiMessage.Key("S40EE21D3FC50EAE8") : global::XASlave.Localization.UiMessage.Key("S61BA55A63CF4B368"));
        return sent;
    }

    private void BeginXagmanLobbyOperation(string command, string character, global::XASlave.Localization.UiMessage label,
        bool directLogin, bool expectedTravelLogout, Action? beforeReplay = null, Func<bool>? completed = null)
    {
        var runner = plugin.TaskRunner;
        if (xagmanLobbyOperation is { } existing && existing.RunId == runner.CurrentRunId
            && existing.RunnerOwned == runner.IsRunning && existing.Command == command
            && existing.Character == character && existing.Label == label.Original)
            return; // A dropped-command reissue must not replenish the recovery budget.
        EndXagmanLobbyOperation();
        xagmanLobbyOperation = new XagmanLobbyOperation
        {
            RunId = runner.CurrentRunId, RunnerOwned = runner.IsRunning,
            Command = command, Character = character, World = GetCurrentWorldName(), LabelMessage = label,
            DirectLogin = directLogin, ExpectedTravelLogout = expectedTravelLogout,
            BeforeReplay = beforeReplay, Completed = completed,
            ErrorSequence = plugin.LobbyErrorAutoClose.ErrorSequence,
        };
        plugin.LobbyErrorAutoClose.SetRequiredByXagman(true);
        if (runner.IsRunning) runner.SuspendTick = PollXagmanLobbyRecovery;
        runner.AddLog($"Xagman: lobby monitor armed for {label}; character '{character}', command '/li {command}'.");
    }

    private void EndXagmanLobbyOperation()
    {
        xagmanLobbyOperation?.Recovery?.Stop();
        xagmanLobbyOperation = null;
        plugin.TaskRunner.SuspendTick = null;
        plugin.LobbyErrorAutoClose.SetRequiredByXagman(false);
    }

    private string GetXagmanLobbyActivity()
    {
        var readable = plugin.IpcClient.TryGetLifestreamBusy(out var busy);
        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        var movement = position.HasValue ? $"{MathF.Round(position.Value.X)}:{MathF.Round(position.Value.Y)}:{MathF.Round(position.Value.Z)}" : string.Empty;
        return $"{movement}|{MonthlyReloggerTask.GetCurrentCharacterNameWorld()}|{GetCurrentWorldName()}|{Plugin.ClientState.TerritoryType}|"
            + $"{readable}:{busy}|{Plugin.Condition[ConditionFlag.BetweenAreas]}:{Plugin.Condition[ConditionFlag.BetweenAreas51]}|"
            + string.Join("|", XagmanLobbyActivityAddons.Select(name => AddonHelper.IsAddonVisible(name)
                ? name + ":" + string.Join("/", AddonHelper.GetAddonTextEntries(name)) : string.Empty));
    }

    private void FailXagmanLobbyRecovery(global::XASlave.Localization.UiMessage reason)
    {
        if (IsXagmanNetworkRecoveryHolding)
        {
            HoldXagmanNetworkRecovery(reason.Original);
            return;
        }
        plugin.TaskRunner.AddLog($"Xagman: lobby recovery stopped: {reason}");
        CancelLifestreamTask(out _);
        EndXagmanLobbyOperation();
        StopXagmanTask();
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S88126A18D9F37BA4", reason));
    }

    private bool PollXagmanLobbyRecovery()
    {
        try { return PollXagmanLobbyRecoveryCore(); }
        catch (Exception ex)
        {
            FailXagmanLobbyRecovery(global::XASlave.Localization.UiMessage.Format("SEB65187AD81298A8", ex.Message));
            return true;
        }
    }

    private bool PollXagmanLobbyRecoveryCore()
    {
        var op = xagmanLobbyOperation;
        if (op == null) return false;
        var runner = plugin.TaskRunner;
        if (!xagmanRunning || runner.CurrentRunId != op.RunId || (op.RunnerOwned && !runner.IsRunning))
        {
            EndXagmanLobbyOperation();
            return false;
        }
        var now = DateTime.UtcNow;
        var errors = plugin.LobbyErrorAutoClose;
        errors.ObserveDialogue();
        if (op.ErrorSequence != errors.ErrorSequence)
        {
            op.ErrorSequence = errors.ErrorSequence;
            if (!op.Pending) op.RecoveryStarted = now;
            op.Pending = true;
            op.Recovery?.Stop();
            op.Recovery = null;
            op.QuietSince = now;
            op.Activity = GetXagmanLobbyActivity();
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S3EE49510147783F5", op.LabelMessage));
            runner.AddLog($"Xagman: lobby error during {op.Label}, '/li {op.Command}': {errors.LastErrorText}. Waiting for closure and 30 seconds without activity.");
        }
        if (!op.Pending)
        {
            if (!op.RunnerOwned && op.Completed?.Invoke() == true && IsXagmanLobbyCharacterReady(op.Character))
                EndXagmanLobbyOperation();
            return false;
        }
        if (now < op.NextPoll) return true;
        op.NextPoll = now.AddMilliseconds(250);
        if ((now - op.RecoveryStarted).TotalMinutes >= 20)
        {
            FailXagmanLobbyRecovery(global::XASlave.Localization.UiMessage.Key("S6D4A4F1BC6DDC674"));
            return true;
        }
        if (op.Recovery != null)
        {
            if (errors.HasSupportedError) return true;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD4CA6A0A5CA51DEA", op.Attempts, op.Recovery.CurrentStepNameMessage));
            op.Recovery.Tick();
            if (ReferenceEquals(op, xagmanLobbyOperation) && op.Recovery?.LastErrorMessage is { } error)
                FailXagmanLobbyRecovery(error);
            return true;
        }
        var activity = GetXagmanLobbyActivity();
        if (errors.HasSupportedError || AddonHelper.IsAddonVisible("Dialogue")
            || AddonHelper.IsAddonVisible("SelectOk") || activity != op.Activity)
        {
            op.Activity = activity;
            op.QuietSince = now;
            return true;
        }
        // A transient popup may clear and the original operation may reach its verified destination.
        if (op.Completed?.Invoke() == true && IsXagmanLobbyCharacterReady(op.Character))
        {
            op.Pending = false;
            runner.AddLog("Xagman: original Lifestream operation recovered naturally; resuming normal verification.");
            return false;
        }
        if ((now - op.QuietSince).TotalSeconds < 30) return true;
        if (++op.Attempts > 2)
        {
            FailXagmanLobbyRecovery(global::XASlave.Localization.UiMessage.Format("S88C39650B79047D4", op.Command));
            return true;
        }
        BuildXagmanLobbyRecovery(op);
        return true;
    }

    private bool IsXagmanLobbyCharacterReady(string character)
        => !string.IsNullOrWhiteSpace(character)
            && MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(character, StringComparison.OrdinalIgnoreCase)
            && CharacterSafetyHelper.IsCharacterSafeWaitReady()
            && plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy;

    private void BuildXagmanLobbyRecovery(XagmanLobbyOperation op)
    {
        var runner = plugin.TaskRunner;
        runner.AddLog($"Xagman: lobby recovery {op.Attempts}/2 for {op.Label}; saved character '{op.Character}', saved command '/li {op.Command}'.");
        var steps = new List<TaskStep>();
        void Fail(global::XASlave.Localization.UiMessage reason) => FailXagmanLobbyRecovery(reason);
        bool Live() => ReferenceEquals(op, xagmanLobbyOperation) && xagmanRunning;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SDBBAD43E3296FB4F"),
            OnEnter = () => { if (!CancelLifestreamTaskMessage(out var message)) Fail(message); else runner.AddLog(message.Original); },
            IsComplete = () => true, TimeoutSec = 1,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S6A35927E4B173122"),
            IsComplete = () => plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy,
            TimeoutSec = 30, OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("SEA7B11495698DDD2")),
        });
        // Relative commands belong to the outgoing character, never the next roster entry.
        var reuseLoggedCharacter = false;
        var needsLogout = false;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S6DAB349A84ED854F"),
            OnEnter = () =>
            {
                reuseLoggedCharacter = !op.DirectLogin && IsXagmanLobbyCharacterReady(op.Character);
                needsLogout = !reuseLoggedCharacter && !string.IsNullOrWhiteSpace(MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                if (needsLogout && (!CharacterSafetyHelper.IsCharacterSafeWaitReady() || AddonHelper.IsAddonVisible("SelectYesno")))
                    Fail(global::XASlave.Localization.UiMessage.Key("SC067D4966B1EC44B"));
                if (needsLogout) xagmanExpectedLogout = true;
            },
            IsComplete = () => true, TimeoutSec = 1,
        });
        var logout = new List<TaskStep>();
        MonthlyReloggerTask.AddLogoutOnCompleteSteps(logout, runner);
        foreach (var step in logout)
        {
            if (step.Name != "Logout Confirm")
            {
                steps.Add(MonthlyReloggerTask.WithSkip(step, () => !needsLogout));
                continue;
            }
            steps.Add(new TaskStep
            {
                NameMessage = step.NameMessage, OnEnter = step.OnEnter, TimeoutSec = step.TimeoutSec,
                ShouldSkip = () => !needsLogout,
                OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("S43F43F564D05FB01")),
                IsComplete = () =>
                {
                    if (AddonHelper.IsAddonReady("SelectYesno")
                        && (!AddonHelper.TryGetSelectYesnoText(out var prompt)
                            || (!prompt.Contains("log out", StringComparison.OrdinalIgnoreCase)
                                && !prompt.Contains("logout", StringComparison.OrdinalIgnoreCase))))
                    { Fail(global::XASlave.Localization.UiMessage.Key("SC3336E90A3F84884")); return false; }
                    return step.IsComplete();
                },
            });
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SA2141EA78E666D86"),
            ShouldSkip = () => !needsLogout,
            IsComplete = () => !Plugin.ClientState.IsLoggedIn
                && (AddonHelper.IsAddonVisible("_TitleMenu") || AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu")),
            TimeoutSec = 60, OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("S00B9628E44E452F1")),
        });
        var helper = new MonthlyReloggerTask(plugin);
        foreach (var step in helper.BuildPreFlightOnlySteps(new List<string> { op.Character }, runner, RunXagmanPreflightArMultiGuard))
            steps.Add(MonthlyReloggerTask.WithSkip(step, () => reuseLoggedCharacter));
        var nextNavigation = DateTime.MinValue;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S680F543E099730F6"),
            ShouldSkip = () => reuseLoggedCharacter,
            IsComplete = () =>
            {
                if (Plugin.ClientState.IsLoggedIn || errorsVisible()) return false;
                var characterSelect = AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu");
                var dataCenterSelect = AddonHelper.IsAddonVisible("TitleDCWorldMap");
                if (!characterSelect && !dataCenterSelect && AddonHelper.IsAddonReady("_TitleMenu")
                    && AddonHelper.IsAddonVisible("_TitleMenu")) return true;
                if (DateTime.UtcNow >= nextNavigation)
                {
                    nextNavigation = DateTime.UtcNow.AddSeconds(2);
                    if (characterSelect) AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
                    else if (dataCenterSelect) KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                }
                return false;
            },
            TimeoutSec = 30, OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("S79E19584A111B368")),
        });
        bool errorsVisible() => AddonHelper.IsAddonVisible("Dialogue") || AddonHelper.IsAddonVisible("SelectOk") || AddonHelper.IsAddonVisible("SelectYesno");
        var safePasses = 0;
        var nextPass = DateTime.MinValue;
        var relativeLogin = op.Command.Equals("fc", StringComparison.OrdinalIgnoreCase) || op.Command.Equals("home", StringComparison.OrdinalIgnoreCase)
            ? op.Character : $"{op.Character} {op.World}";
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SD284B9384D29A4FF"),
            ShouldSkip = () => op.DirectLogin || reuseLoggedCharacter,
            OnEnter = () =>
            {
                if (!op.Character.Contains('@') || op.Character.EndsWith("@Unknown", StringComparison.OrdinalIgnoreCase))
                { Fail(global::XASlave.Localization.UiMessage.Key("S59D5B5157A3F3D36")); return; }
                if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy || !plugin.IpcClient.LifestreamExecuteCommand(relativeLogin))
                    Fail(global::XASlave.Localization.UiMessage.Key("S720B75C6377FBBE6"));
                else runner.AddLog($"Xagman: restoring outgoing character with /li {relativeLogin} before /li {op.Command}.");
            },
            IsComplete = () =>
            {
                if (!IsXagmanLobbyCharacterReady(op.Character)
                    || (relativeLogin != op.Character && !GetCurrentWorldName().Equals(op.World, StringComparison.OrdinalIgnoreCase)))
                { safePasses = 0; nextPass = DateTime.UtcNow.AddSeconds(1); return false; }
                if (DateTime.UtcNow < nextPass) return false;
                nextPass = DateTime.UtcNow.AddSeconds(1);
                return ++safePasses >= 3;
            },
            TimeoutSec = 600, OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("S5CD8F0B1AD6EB4B1")),
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S9BD0D35FEED1D471"),
            OnEnter = () =>
            {
                if (!Live()) return;
                if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy || errorsVisible()
                    || (!op.DirectLogin && !IsXagmanLobbyCharacterReady(op.Character)))
                { Fail(global::XASlave.Localization.UiMessage.Key("S259453F4D193F053")); return; }
                op.BeforeReplay?.Invoke();
                if (!Live()) return;
                xagmanExpectedLogout = op.DirectLogin && Plugin.ClientState.IsLoggedIn;
                var sent = op.ExpectedTravelLogout
                    ? ExecuteXagmanTravelCommandWithExpectedLogout(op.Command, op.Label)
                    : plugin.IpcClient.LifestreamExecuteCommand(op.Command);
                if (!sent) { Fail(global::XASlave.Localization.UiMessage.Key("S64769DB1A90B01C0")); return; }
                op.Pending = false;
                op.Recovery = null;
                runner.AddLog($"Xagman: replayed /li {op.Command}; resuming original completion checks.");
            },
            IsComplete = () => true, TimeoutSec = 1,
        });
        op.Recovery = new StepMachine(Plugin.Log, "Xagman lobby recovery", runner.AddLog) { HaltOnError = true };
        op.Recovery.Start(steps);
    }
}
