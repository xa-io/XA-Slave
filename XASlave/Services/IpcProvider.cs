using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// IPC provider for XA Slave - exposes channels for external plugins to query/control XA Slave.
///
/// Channels:
///   XASlave.IsBusy  (Func→bool) - returns true when any task is running
///   XASlave.ExecuteCommand (Func, string→string) - run the same subcommands accepted by /xa and return an OK/ERROR status string
///   XASlave.RunTask (Action, string) - start a named task from external plugins
///   XASlave.Development.ReadDependency.v1 (Func, string to string) - fixed, read-only dependency wrapper observations
///
/// ExecuteCommand examples:
///   "" or "/xa"
///   "xamods"
///   "updates"
///   "db subgil 5000000"
///   "sprint on"
///   "killgame"
///   "res 500x345"
///   "res 1280 720"
///   "res reset"
///   "preset load Favorites"
///   "/xa logout"
/// </summary>
public sealed class IpcProvider : IDisposable
{
    private readonly record struct CommandExecutionResult(bool Success, string Message);

    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly string developmentLoadId = Guid.NewGuid().ToString("D");
    private readonly string developmentStartedAtUtc = DateTimeOffset.UtcNow.ToString("O");

    private readonly List<(string Channel, IDisposable Registration)> registrations = new();
    private bool isDisposed;

    public IpcProvider(IDalamudPluginInterface pluginInterface, Plugin plugin, IPluginLog log)
        : this(new DalamudIpcProviderRegistrationHost(pluginInterface), plugin, log)
    {
    }

    internal IpcProvider(IIpcProviderRegistrationHost registrationHost, Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;

        try
        {
            Register("XASlave.IsBusy", registrationHost.RegisterIsBusy(IsBusy));
            Register("XASlave.GetActivityJson", registrationHost.RegisterGetActivityJson(GetActivityJson));
            Register("XASlave.ExecuteCommand", registrationHost.RegisterExecuteCommand(ExecuteCommand));
            Register("XASlave.RunTask", registrationHost.RegisterRunTask(RunTask));
            Register("XASlave.Development.ReadDependency.v1", registrationHost.RegisterReadDevelopmentDependency(ReadDevelopmentDependency));
            log.Information("[XASlave] Development identity: MVID={Mvid}; LoadId={LoadId}.",
                typeof(IpcProvider).Assembly.ManifestModule.ModuleVersionId, developmentLoadId);
        }
        catch
        {
            UnregisterAll();
            throw;
        }
    }

    private bool IsBusy()
        => Plugin.RunOnGameThread(IsBusyOnFrameworkThread).GetAwaiter().GetResult();

    private string ReadDevelopmentDependency(string request)
        => DevelopmentDependencyReadEndpoint.Read(request, plugin.IpcClient,
            !isDisposed && Plugin.Framework.IsInFrameworkUpdateThread && !Plugin.Framework.IsFrameworkUnloading);

    private bool IsBusyOnFrameworkThread()
    {
        return plugin.TaskRunner.IsRunning
            || plugin.ArPostProcessor.IsRunning
            || plugin.AutoCollector.IsRunning
            || plugin.AutoOpenMoogleMail.IsProcessing
            || plugin.AutoUnlockExpertDelivery?.IsProcessing == true;
    }

    private string GetActivityJson()
        => Plugin.RunOnGameThread(GetActivityJsonOnFrameworkThread).GetAwaiter().GetResult();

    private string GetActivityJsonOnFrameworkThread()
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

        if (source.Length == 0 && plugin.AutoUnlockExpertDelivery?.IsProcessing == true)
        {
            source = "Expert Delivery";
            detail = plugin.AutoUnlockExpertDelivery.StatusText;
        }
        var delivery = plugin.AutoUnlockExpertDelivery;
        var lastResult = delivery?.LastResult;
        return new IpcActivitySnapshot(
            new(typeof(IpcProvider).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                developmentLoadId, developmentStartedAtUtc, !isDisposed),
            IsBusyOnFrameworkThread(), source, detail,
            new(plugin.TaskRunner.IsRunning, plugin.TaskRunner.CurrentTaskName, plugin.TaskRunner.StatusText,
                plugin.TaskRunner.CurrentStep, plugin.TaskRunner.TotalSteps, plugin.TaskRunner.CompletedItems,
                plugin.TaskRunner.TotalItems, plugin.TaskRunner.CurrentItemLabel),
            new(plugin.ArPostProcessor.IsRunning, plugin.ArPostProcessor.StatusText, string.Empty),
            new(plugin.AutoCollector.IsRunning, plugin.AutoCollector.StatusText, string.Empty),
            new(plugin.AutoOpenMoogleMail.IsProcessing, plugin.AutoOpenMoogleMail.StatusText,
                plugin.AutoOpenMoogleMail.LastActionText),
            new(delivery?.IsProcessing == true, delivery?.RunId ?? 0, delivery?.StatusText ?? "Unavailable",
                delivery?.DeliveredCount ?? 0, delivery?.ListSummary ?? string.Empty,
                lastResult is not null && lastResult.RunId == delivery!.RunId && !delivery.IsProcessing
                    ? lastResult.Outcome.ToString() : null,
                lastResult is null ? null : new(lastResult.RunId, (int)lastResult.Outcome, lastResult.Delivered,
                    lastResult.Protected, lastResult.CapBlocked, lastResult.ElapsedMilliseconds, lastResult.Message)),
            plugin.ReadDevelopmentUiState())
            .ToJson();
    }

    private void RunTask(string taskName)
    {
        var normalized = IpcCommandPolicy.NormalizeTaskName(taskName);
        if (normalized.Length == 0)
        {
            log.Warning("[XASlave] IPC: RunTask called with a null/empty task name.");
            return;
        }

        log.Information($"[XASlave] IPC: RunTask('{normalized}') called.");

        Plugin.RunOnGameThread(() => RunTaskOnFrameworkThread(normalized))
            .GetAwaiter()
            .GetResult();
    }

    private void RunTaskOnFrameworkThread(string taskName)
    {
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

        var normalized = IpcCommandPolicy.NormalizeCommand(commandText);
        var execution = Plugin
            .RunOnGameThread(() => ExecuteCommandOnFrameworkThread(normalized))
            .GetAwaiter()
            .GetResult();
        var success = execution.Success;
        var message = execution.Message;
        var response = string.IsNullOrWhiteSpace(message)
            ? (success ? "OK" : "ERROR")
            : $"{(success ? "OK" : "ERROR")}: {message}";

        if (success)
            log.Information($"[XASlave] IPC: ExecuteCommand('{commandText}') succeeded. {message}");
        else
            log.Warning($"[XASlave] IPC: ExecuteCommand('{commandText}') failed. {message}");

        return response;
    }

    private CommandExecutionResult ExecuteCommandOnFrameworkThread(string normalized)
    {
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
            success = plugin.TryExecuteXaCommandFromIpc(normalized, out message);
        }

        return new CommandExecutionResult(success, message);
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

    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;

        UnregisterAll();
    }

    private void Register(string channel, IDisposable registration)
        => registrations.Add((channel, registration));

    private void UnregisterAll()
    {
        for (var index = registrations.Count - 1; index >= 0; index--)
            TryUnregister(registrations[index].Channel, registrations[index].Registration);
        registrations.Clear();
    }

    private void TryUnregister(string channel, IDisposable registration)
    {
        try
        {
            registration.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] IPC: failed to unregister {channel}: {ex.Message}");
        }
    }
}
