using System;
using System.Collections.Generic;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private NpcSellSession? manualNpcSell;
    private NpcSellSession? xagmanNpcSell;

    public bool TryExecuteNpcSellCommand(string arguments, out string message)
    {
        var runner = plugin.TaskRunner;
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (runner.CurrentTaskName != "NPC Sell" || !runner.IsRunning)
            { message = "No manual NPC sale is running. Stop Xagman through its own controls."; return false; }
            manualNpcSell?.Abort("stopped by operator");
            runner.Cancel();
            manualNpcSell = null;
            message = "NPC selling stopped.";
            return true;
        }
        if (xagmanRunning || xagmanOwnerStandbyPending || xagmanOwnerPauseForTonyRotationRequested || runner.IsRunning)
        { message = "Stop the active task before starting a manual NPC sale."; return false; }
        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady() && !AddonHelper.IsAddonReady("Shop"))
        { message = "Character must be ready or have an NPC shop open."; return false; }
        if (!NpcSellSession.TryParseIds(arguments, out var ids, out message)) return false;
        manualNpcSell = new NpcSellSession(ids, runner.AddLog);
        var session = manualNpcSell;
        var started = runner.Start("NPC Sell", new List<TaskStep>
        {
            new()
            {
                Name = "Sell selected salvaged treasures",
                IsComplete = session.Tick,
                TimeoutSec = 905f,
                OnTimeout = () => session.Abort("task timeout"),
            },
            new()
            {
                Name = "NPC sale result",
                OnEnter = () =>
                {
                    if (!session.Succeeded) runner.RequestHalt(session.Result);
                },
            },
        }, onFinished: () => manualNpcSell = null);
        if (!started) manualNpcSell = null;
        message = started ? "NPC selling started for the selected treasure IDs only. Use /xa npcsell stop to stop." : "NPC selling could not acquire the task runner.";
        return started;
    }
}
