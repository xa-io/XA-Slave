using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// IPC provider for XA Slave — exposes channels for external plugins to query/control XA Slave.
///
/// Channels:
///   XASlave.IsBusy  (Func→bool) — returns true when any task is running
///   XASlave.ExecuteCommand (Func, string→string) — run the same subcommands accepted by /xa and return an OK/ERROR status string
///   XASlave.RunTask (Action, string) — start a named task from external plugins
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
    private readonly ICallGateProvider<string, string> executeCommandProvider;
    private readonly ICallGateProvider<string, object> runTaskProvider;

    public IpcProvider(IDalamudPluginInterface pluginInterface, Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;

        // XASlave.IsBusy — returns true when TaskRunner or AutoCollector is running
        isBusyProvider = pluginInterface.GetIpcProvider<bool>("XASlave.IsBusy");
        isBusyProvider.RegisterFunc(IsBusy);

        // XASlave.ExecuteCommand — mirrors the /xa command surface over IPC
        executeCommandProvider = pluginInterface.GetIpcProvider<string, string>("XASlave.ExecuteCommand");
        executeCommandProvider.RegisterFunc(ExecuteCommand);

        // XASlave.RunTask — start a named task (currently supports: "SaveToXaDatabase")
        runTaskProvider = pluginInterface.GetIpcProvider<string, object>("XASlave.RunTask");
        runTaskProvider.RegisterAction(RunTask);

        log.Information("[XASlave] IPC provider initialized (3 channels).");
    }

    private bool IsBusy()
    {
        return plugin.TaskRunner.IsRunning || plugin.AutoCollector.IsRunning;
    }

    private void RunTask(string taskName)
    {
        log.Information($"[XASlave] IPC: RunTask('{taskName}') called.");

        if (plugin.TaskRunner.IsRunning || plugin.AutoCollector.IsRunning)
        {
            log.Warning($"[XASlave] IPC: RunTask('{taskName}') rejected — already busy.");
            return;
        }

        switch (taskName.ToLowerInvariant())
        {
            case "save":
            case "savetoxadatabase":
                plugin.SaveToXaDatabaseAndRecordSync();
                log.Information("[XASlave] IPC: RunTask — triggered Save to XA Database.");
                break;

            default:
                log.Warning($"[XASlave] IPC: RunTask — unknown task '{taskName}'.");
                break;
        }
    }

    private string ExecuteCommand(string commandText)
    {
        log.Information($"[XASlave] IPC: ExecuteCommand('{commandText}') called.");

        var success = plugin.TryExecuteXaCommandFromIpc(commandText, out var message);
        var response = string.IsNullOrWhiteSpace(message)
            ? (success ? "OK" : "ERROR")
            : $"{(success ? "OK" : "ERROR")}: {message}";

        if (success)
            log.Information($"[XASlave] IPC: ExecuteCommand('{commandText}') succeeded. {message}");
        else
            log.Warning($"[XASlave] IPC: ExecuteCommand('{commandText}') failed. {message}");

        return response;
    }

    public void Dispose()
    {
        try
        {
            isBusyProvider.UnregisterFunc();
            executeCommandProvider.UnregisterFunc();
            runTaskProvider.UnregisterAction();
        }
        catch { }
    }
}
