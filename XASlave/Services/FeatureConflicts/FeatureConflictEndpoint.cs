using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XA.FeatureConflicts.Host;

namespace XA.FeatureConflicts;

internal sealed class FeatureConflictEndpoint
{
    private readonly ConflictAdvertisement? advertisement;
    internal FeatureConflictPublication Publication { get; }

    internal FeatureConflictEndpoint(IDalamudPluginInterface pluginInterface, IFramework framework, Guid instance, string endpoint)
    {
        ConflictAdvertisement? creating = null;
        try
        {
            Publication = new(instance, () => framework.IsInFrameworkUpdateThread, json =>
            {
                if (creating == null) creating = new ConflictAdvertisement(pluginInterface, endpoint, json);
                else creating.Publish(json);
            });
            advertisement = creating;
        }
        catch
        {
            // Preserve the previous owner's recovery/busy advertisement. The
            // replacement plugin shell can load, but this feature cannot start.
            Publication = new(instance, () => framework.IsInFrameworkUpdateThread);
            Publication.MarkUnavailable("Conflict endpoint unavailable; an earlier instance may still require recovery. Finish recovery before reloading, or restart the game client.");
        }
    }

    internal void Retire(FeatureConflictActivity actual, nint retainedCounter = 0)
    {
        Publication.BeginShutdown();
        var settled = Publication.CompleteShutdown(actual);
        // Without a retained counter, reported busy cannot be safely polled by
        // the public host. It remains an immutable recovery advertisement.
        advertisement?.Retire(Publication.PublishedJson,
            actual.RecoveryRequired || actual.Enabled || (!settled && retainedCounter == 0), retainedCounter);
    }
}

internal sealed class FeatureConflictPeerReader : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly DalamudPeerLifecycle? lifecycle;
    private readonly string lifecycleFailureReason;
    private readonly Func<string?> peer;
    private readonly string peerLabel;
    private readonly Func<string?>[] providers;
    private int invalidated = 1;
    private bool disposed;
    private bool permitted;
    private bool waiting;
    internal string Reason { get; private set; } = "Peer state has not been evaluated";
    // Presentation metadata only. Reason remains the original protocol/log value.
    internal string? ReasonPeer { get; private set; }
    internal string ReasonDetail { get; private set; } = "Peer state has not been evaluated";
    internal bool Permitted => !disposed && System.Threading.Volatile.Read(ref invalidated) == 0 && permitted;
    internal bool IsWaiting => !disposed && System.Threading.Volatile.Read(ref invalidated) == 0 && !permitted && waiting;

    internal FeatureConflictPeerReader(IDalamudPluginInterface pluginInterface, IFramework framework, string peer, params string[] endpoints)
        : this(pluginInterface, framework, () => peer, peer, endpoints) { }

    internal static FeatureConflictPeerReader FromCapability(IDalamudPluginInterface pluginInterface, IFramework framework, string ownerEndpoint, params string[] endpoints)
    {
        var owner = pluginInterface.GetIpcSubscriber<string>(ownerEndpoint);
        return new(pluginInterface, framework, () =>
        {
            if (!owner.HasFunction) return null;
            var name = owner.InvokeFunc();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
                throw new InvalidOperationException("Invalid conflict capability owner.");
            return name;
        }, "Movement feature peer", endpoints);
    }

    private FeatureConflictPeerReader(IDalamudPluginInterface pluginInterface, IFramework framework, Func<string?> peer, string peerLabel, params string[] endpoints)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.peer = peer;
        this.peerLabel = peerLabel;
        lifecycle = DalamudPeerLifecycle.TryResolve(typeof(IDalamudPluginInterface).Assembly, pluginInterface, out lifecycleFailureReason);
        providers = new Func<string?>[endpoints.Length];
        for (var i = 0; i < endpoints.Length; ++i)
        {
            try
            {
                var subscriber = pluginInterface.GetIpcSubscriber<string>(endpoints[i]);
                // Only HasFunction=false is confirmed missing. Invoke failures
                // flow into unknown state, including unload races.
                providers[i] = () => subscriber.HasFunction ? subscriber.InvokeFunc() : null;
            }
            catch
            {
                // A missing host transport must not prevent the shell loading.
                // This read remains unknown, including for an absent peer.
                providers[i] = () => throw new InvalidOperationException("Peer conflict transport unavailable");
            }
        }
        pluginInterface.ActivePluginsChanged += OnPluginsChanged;
    }

    internal FeatureConflictDecision Read()
    {
        if (disposed || !framework.IsInFrameworkUpdateThread)
            return FeatureConflictDecision.Deny("Peer evaluation requires the framework thread");
        System.Threading.Interlocked.Exchange(ref invalidated, 0);
        var peerName = peerLabel;
        var decision = lifecycle == null
            ? FeatureConflictDecision.Deny($"Lifecycle reader unavailable: {lifecycleFailureReason}")
            : FeatureConflictPeer.Read(() =>
        {
            var name = peer();
            peerName = name ?? peerLabel;
            return name == null ? new PeerPluginObservation(PeerPluginPhase.Absent, Guid.Empty) : lifecycle.Observe(name);
        }, providers);
        if (System.Threading.Volatile.Read(ref invalidated) != 0
            && (decision.Permitted || decision.IsWaiting || decision.RequiresRecheck))
            decision = FeatureConflictDecision.Recheck("Plugin lifecycle changed during evaluation");
        if (!decision.Permitted)
        {
            ReasonPeer = peerName;
            ReasonDetail = decision.Reason;
            decision = decision with { Reason = $"{peerName}: {decision.Reason}" };
        }
        else
        {
            ReasonPeer = null;
            ReasonDetail = decision.Reason;
        }
        permitted = decision.Permitted;
        waiting = decision.IsWaiting;
        Reason = decision.Reason;
        return decision;
    }

    private void OnPluginsChanged(IActivePluginsChangedEventArgs _)
    {
        // No IPC/native work from host event callbacks. Revoke the cached permit
        // immediately; the next framework update resolves fresh lifecycle state.
        System.Threading.Interlocked.Exchange(ref invalidated, 1);
    }

    public void Dispose()
    {
        disposed = true;
        System.Threading.Interlocked.Exchange(ref invalidated, 1);
        pluginInterface.ActivePluginsChanged -= OnPluginsChanged;
    }
}
