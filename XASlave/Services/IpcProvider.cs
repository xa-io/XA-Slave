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
///   XASlave.RunTask (Action, string) — start a named task from external plugins
/// </summary>
public sealed class IpcProvider : IDisposable
{
    private readonly Plugin plugin;
    private readonly IPluginLog log;

    private readonly ICallGateProvider<bool> isBusyProvider;
    private readonly ICallGateProvider<string, object> runTaskProvider;

    public IpcProvider(IDalamudPluginInterface pluginInterface, Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;

        // XASlave.IsBusy — returns true when TaskRunner or AutoCollector is running
        isBusyProvider = pluginInterface.GetIpcProvider<bool>("XASlave.IsBusy");
        isBusyProvider.RegisterFunc(IsBusy);

        // XASlave.RunTask — start a named task (currently supports: "SaveToXaDatabase")
        runTaskProvider = pluginInterface.GetIpcProvider<string, object>("XASlave.RunTask");
        runTaskProvider.RegisterAction(RunTask);

        log.Information("[XASlave] IPC provider initialized (2 channels).");
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
                plugin.IpcClient.Save();
                log.Information("[XASlave] IPC: RunTask — triggered Save to XA Database.");
                break;

            default:
                log.Warning($"[XASlave] IPC: RunTask — unknown task '{taskName}'.");
                break;
        }
    }

    /// <summary>Public entry point for /xa run command to invoke RunTask locally.</summary>
    public void InvokeRunTask(string taskName) => RunTask(taskName);

    public void Dispose()
    {
        try
        {
            isBusyProvider.UnregisterFunc();
            runTaskProvider.UnregisterAction();
        }
        catch { }
    }
}
