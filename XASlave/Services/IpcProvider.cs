using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// IPC provider for XA Slave - exposes channels for external plugins to query/control XA Slave.
///
/// Channels:
///   XASlave.IsBusy  (Func→bool) - returns true when any task is running
///   XASlave.ExecuteCommand (Func, string→string) - run the same subcommands accepted by /xa and return an OK/ERROR status string
///   XASlave.RunTask (Action, string) - start a named task from external plugins
///
/// ExecuteCommand examples:
///   "xamods"
///   "sprint on"
///   "killgame"
///   "res 500x345"
///   "preset load Favorites"
///   "/xa logout"
/// </summary>
public sealed class IpcProvider : IDisposable
{
    private readonly Plugin plugin;
    private readonly IPluginLog log;

    private readonly ICallGateProvider<bool> isBusyProvider;
    private readonly ICallGateProvider<string> getActivityProvider;
    private readonly ICallGateProvider<string, string> executeCommandProvider;
    private readonly ICallGateProvider<string, object> runTaskProvider;

    public IpcProvider(IDalamudPluginInterface pluginInterface, Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;

        // XASlave.IsBusy - returns true when TaskRunner, AutoCollector, or Auto Open Moogle Mail is running
        isBusyProvider = pluginInterface.GetIpcProvider<bool>("XASlave.IsBusy");
        isBusyProvider.RegisterFunc(IsBusy);

        // Detailed activity for XA Sub Terminal and other observers.
        getActivityProvider = pluginInterface.GetIpcProvider<string>("XASlave.GetActivityJson");
        getActivityProvider.RegisterFunc(GetActivityJson);

        // XASlave.ExecuteCommand - mirrors the /xa command surface over IPC
        executeCommandProvider = pluginInterface.GetIpcProvider<string, string>("XASlave.ExecuteCommand");
        executeCommandProvider.RegisterFunc(ExecuteCommand);

        // XASlave.RunTask - start a named task (currently supports: "SaveToXaDatabase")
        runTaskProvider = pluginInterface.GetIpcProvider<string, object>("XASlave.RunTask");
        runTaskProvider.RegisterAction(RunTask);

    }

    private bool IsBusy()
    {
        return plugin.TaskRunner.IsRunning
            || plugin.ArPostProcessor.IsRunning
            || plugin.AutoCollector.IsRunning
            || plugin.AutoOpenMoogleMail.IsProcessing;
    }

    private string GetActivityJson()
    {
        var source = string.Empty;
        var detail = string.Empty;
        if (plugin.TaskRunner.IsRunning)
        {
            source = "TaskRunner";
            detail = string.IsNullOrWhiteSpace(plugin.TaskRunner.CurrentTaskName)
                ? plugin.TaskRunner.StatusText
                : plugin.TaskRunner.CurrentTaskName;
        }
        else if (plugin.ArPostProcessor.IsRunning)
        {
            source = "AR post-process";
            detail = plugin.ArPostProcessor.StatusText;
        }
        else if (plugin.AutoCollector.IsRunning)
        {
            source = "Auto Collector";
            detail = plugin.AutoCollector.StatusText;
        }
        else if (plugin.AutoOpenMoogleMail.IsProcessing)
        {
            source = "Moogle Mail";
            detail = plugin.AutoOpenMoogleMail.StatusText;
        }

        return JsonSerializer.Serialize(new
        {
            available = true,
            busy = IsBusy(),
            source,
            detail,
            task = new
            {
                running = plugin.TaskRunner.IsRunning,
                name = plugin.TaskRunner.CurrentTaskName,
                status = plugin.TaskRunner.StatusText,
                currentStep = plugin.TaskRunner.CurrentStep,
                totalSteps = plugin.TaskRunner.TotalSteps,
                completedItems = plugin.TaskRunner.CompletedItems,
                totalItems = plugin.TaskRunner.TotalItems,
                currentItem = plugin.TaskRunner.CurrentItemLabel,
            },
            arPostProcess = new
            {
                running = plugin.ArPostProcessor.IsRunning,
                status = plugin.ArPostProcessor.StatusText,
                lastAction = string.Empty,
            },
            autoCollector = new
            {
                running = plugin.AutoCollector.IsRunning,
                status = plugin.AutoCollector.StatusText,
                lastAction = string.Empty,
            },
            moogleMail = new
            {
                running = plugin.AutoOpenMoogleMail.IsProcessing,
                status = plugin.AutoOpenMoogleMail.StatusText,
                lastAction = plugin.AutoOpenMoogleMail.LastActionText,
            },
            error = string.Empty,
        });
    }

    private void RunTask(string taskName)
    {
        log.Information($"[XASlave] IPC: RunTask('{taskName}') called.");

        if (plugin.TaskRunner.IsRunning || plugin.AutoCollector.IsRunning)
        {
            log.Warning($"[XASlave] IPC: RunTask('{taskName}') rejected - already busy.");
            return;
        }

        switch (taskName.ToLowerInvariant())
        {
            case "save":
            case "savetoxadatabase":
                plugin.SaveToXaDatabaseAndRecordSync();
                log.Information("[XASlave] IPC: RunTask - triggered Save to XA Database.");
                break;

            default:
                log.Warning($"[XASlave] IPC: RunTask - unknown task '{taskName}'.");
                break;
        }
    }

    private string ExecuteCommand(string commandText)
    {
        log.Information($"[XASlave] IPC: ExecuteCommand('{commandText}') called.");

        var normalized = NormalizeCommand(commandText);
        bool success;
        string message;
        if (normalized.Equals("logout", StringComparison.OrdinalIgnoreCase))
        {
            success = TryRequestForcedLogout(killGame: false, out message);
        }
        else if (normalized.Equals("killgame", StringComparison.OrdinalIgnoreCase))
        {
            success = TryRequestForcedLogout(killGame: true, out message);
        }
        else
        {
            success = plugin.TryExecuteXaCommandFromIpc(commandText, out message);
        }
        var response = string.IsNullOrWhiteSpace(message)
            ? (success ? "OK" : "ERROR")
            : $"{(success ? "OK" : "ERROR")}: {message}";

        if (success)
            log.Information($"[XASlave] IPC: ExecuteCommand('{commandText}') succeeded. {message}");
        else
            log.Warning($"[XASlave] IPC: ExecuteCommand('{commandText}') failed. {message}");

        return response;
    }

    private bool TryRequestForcedLogout(bool killGame, out string message)
    {
        if (!plugin.CanTriggerLogoutActions(out message))
            return false;

        var success = killGame
            ? plugin.InstantLogout.RequestKillGame(force: true)
            : plugin.InstantLogout.RequestLogout(force: true);
        message = success
            ? $"Queued one-shot {(killGame ? "killgame" : "logout")} action."
            : plugin.InstantLogout.StatusText;
        return success;
    }

    private static string NormalizeCommand(string commandText)
    {
        var normalized = (commandText ?? string.Empty).Trim();
        if (normalized.StartsWith("/xa", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..].TrimStart();
        return normalized;
    }

    public void Dispose()
    {
        try
        {
            isBusyProvider.UnregisterFunc();
            getActivityProvider.UnregisterFunc();
            executeCommandProvider.UnregisterFunc();
            runTaskProvider.UnregisterAction();
        }
        catch { }
    }
}
