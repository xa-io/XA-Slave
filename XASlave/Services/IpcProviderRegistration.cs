using System;
using System.Threading;
using Dalamud.Plugin;

namespace XASlave.Services;

internal interface IIpcProviderRegistrationHost
{
    IDisposable RegisterIsBusy(Func<bool> callback);
    IDisposable RegisterGetActivityJson(Func<string> callback);
    IDisposable RegisterExecuteCommand(Func<string, string> callback);
    IDisposable RegisterRunTask(Action<string> callback);
}

internal sealed class DalamudIpcProviderRegistrationHost(IDalamudPluginInterface pluginInterface)
    : IIpcProviderRegistrationHost
{
    public IDisposable RegisterIsBusy(Func<bool> callback)
    {
        var provider = pluginInterface.GetIpcProvider<bool>("XASlave.IsBusy");
        provider.RegisterFunc(callback);
        return new IpcRegistrationHandle(provider.UnregisterFunc);
    }

    public IDisposable RegisterGetActivityJson(Func<string> callback)
    {
        var provider = pluginInterface.GetIpcProvider<string>("XASlave.GetActivityJson");
        provider.RegisterFunc(callback);
        return new IpcRegistrationHandle(provider.UnregisterFunc);
    }

    public IDisposable RegisterExecuteCommand(Func<string, string> callback)
    {
        var provider = pluginInterface.GetIpcProvider<string, string>("XASlave.ExecuteCommand");
        provider.RegisterFunc(callback);
        return new IpcRegistrationHandle(provider.UnregisterFunc);
    }

    public IDisposable RegisterRunTask(Action<string> callback)
    {
        var provider = pluginInterface.GetIpcProvider<string, object>("XASlave.RunTask");
        provider.RegisterAction(callback);
        return new IpcRegistrationHandle(provider.UnregisterAction);
    }
}

internal sealed class IpcRegistrationHandle(Action unregister) : IDisposable
{
    private Action? unregisterAction = unregister;

    public void Dispose()
    {
        var action = Interlocked.Exchange(ref unregisterAction, null);
        action?.Invoke();
    }
}
