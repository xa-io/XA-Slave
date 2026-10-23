using System;
using System.Collections.Generic;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private NpcSellSession? manualNpcSell;
    private NpcSellSession? xagmanNpcSell;

    public bool TryExecuteNpcSellCommand(string arguments, out string message) => TryExecuteNpcSellCommandLocalizedCore(out _, arguments, out message);
    internal bool TryExecuteNpcSellCommandMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryExecuteNpcSellCommandLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryExecuteNpcSellCommandLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        var runner = plugin.TaskRunner;
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (runner.CurrentTaskName != "NPC Sell" || !runner.IsRunning)
            { message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S2E74238869FEE548")); return false; }
            manualNpcSell?.Abort("stopped by operator");
            runner.Cancel();
            manualNpcSell = null;
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S7F2789ECAA75D09D"));
            return true;
        }
        if (xagmanRunning || xagmanOwnerStandbyPending || xagmanOwnerPauseForTonyRotationRequested || runner.IsRunning)
        { message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S43558231E10B689C")); return false; }
        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady() && !AddonHelper.IsAddonReady("Shop"))
        { message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SBB268A5D78E039DD")); return false; }
        if (!NpcSellSession.TryParseIds(arguments, out var ids, out message)) return false;
        manualNpcSell = new NpcSellSession(ids, runner.AddLog);
        var session = manualNpcSell;
        var started = runner.Start(global::XASlave.Localization.UiMessage.Key("S2D7503060C7832A0"), new List<TaskStep>
        {
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S4BEE86CF7E5373F5"),
                IsComplete = session.Tick,
                TimeoutSec = 905f,
                OnTimeout = () => session.Abort("task timeout"),
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S81812715D67DC319"),
                OnEnter = () =>
                {
                    if (!session.Succeeded) runner.RequestHalt(session.Result);
                },
            },
        }, onTerminal: () =>
        {
            session.Dispose();
            if (ReferenceEquals(manualNpcSell, session)) manualNpcSell = null;
        });
        if (!started)
        {
            session.Dispose();
            if (ReferenceEquals(manualNpcSell, session)) manualNpcSell = null;
        }
        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, (started ? global::XASlave.Localization.UiMessage.Key("SB9FF6869B367604E") : global::XASlave.Localization.UiMessage.Key("S6846361409A80611")));
        return started;
    }
}
