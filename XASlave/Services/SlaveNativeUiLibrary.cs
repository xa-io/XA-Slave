using System;
using System.Threading;
using System.Threading.Tasks;
using KamiToolKit;

namespace XASlave.Services;

// Reuses the native overlay library lifetime and process claim. Only Slave's captured
// toolkit instance is initialized or retired; other plugins' nodes stay owned.
internal sealed class SlaveNativeUiLibrary(Action retireControls) : IDisposable
{
    private const string OwnerKey = "XASlave.NativeUi.Owner.v1";
    private readonly CancellationTokenSource lifetime = new();
    private Task? initialization, retirement;
    private bool claimed, started;
    private volatile bool disposed;

    internal bool EnsureReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Plugin.Framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Native controls require the framework thread.");
        if (initialization == null)
        {
            lock (AppDomain.CurrentDomain)
            {
                if (AppDomain.CurrentDomain.GetData(OwnerKey) != null)
                    throw new InvalidOperationException("A previous native control generation is still retiring.");
                AppDomain.CurrentDomain.SetData(OwnerKey, this); claimed = true;
            }
            started = true;
            // Feature owners attach controls or create input overlays. The general
            // custom-addon close hook would outlive Dispose during async retirement.
            initialization = KamiToolKitLibrary.InitializeAsync(Plugin.PluginInterface, cancellationToken: lifetime.Token,
                enableNativeAddonCloseCallback: false);
        }
        if (!initialization.IsCompleted) return false;
        initialization.GetAwaiter().GetResult();
        ObjectDisposedException.ThrowIf(disposed, this);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        if (!started) { ReleaseClaim(); lifetime.Dispose(); }
        else retirement = RetireAsync();
    }

    private async Task RetireAsync()
    {
        try
        {
            if (initialization != null)
            {
                try { await initialization.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { throw; }
                catch { }
            }
            KamiToolKitLibrary.StopManagedCallbacks();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Plugin.Framework.Run(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (Plugin.Framework.IsFrameworkUnloading) throw new InvalidOperationException("Native control teardown unavailable during framework shutdown.");
                retireControls();
            }).WaitAsync(deadline.Token).ConfigureAwait(false);
            await KamiToolKitLibrary.ShutdownOwnedAsync().ConfigureAwait(false);
            started = false; ReleaseClaim(); lifetime.Dispose();
        }
        catch (Exception error)
        {
            Plugin.Log.Error(error, "Native control teardown incomplete; ownership retained until game restart.");
        }
    }

    private void ReleaseClaim()
    {
        lock (AppDomain.CurrentDomain)
        {
            if (claimed && ReferenceEquals(AppDomain.CurrentDomain.GetData(OwnerKey), this)) AppDomain.CurrentDomain.SetData(OwnerKey, null);
            claimed = false;
        }
    }
}
