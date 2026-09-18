using System;
using System.Text;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XA.FeatureConflicts.Host;

// Compiled into the public loader, never into its collectible private module.
// Its IPC function is String.ToString bound to immutable data, so a retained
// recovery advertisement does not retain either plugin's implementation.
public sealed unsafe class ConflictAdvertisement
{
    private readonly object gate = new();
    private readonly object endpointGate;
    private readonly ICallGateProvider<string> provider;
    private readonly ICallGateSubscriber<string> subscriber;
    private string published;
    private bool retired;
    private nint retainedCounter;
    private Timer? timer;

    public ConflictAdvertisement(IDalamudPluginInterface pluginInterface, string endpoint, string initialJson)
    {
        Validate(initialJson);
        endpointGate = string.Intern("XA.FeatureConflicts.Endpoint." + endpoint);
        provider = pluginInterface.GetIpcProvider<string>(endpoint);
        subscriber = pluginInterface.GetIpcSubscriber<string>(endpoint);
        published = initialJson;
        lock (endpointGate)
        {
            if (subscriber.HasFunction) throw new InvalidOperationException("A conflict advertisement already owns this endpoint.");
            provider.RegisterFunc(initialJson.ToString);
        }
    }

    public void Publish(string json)
    {
        Validate(json);
        lock (gate)
        {
            if (retired) throw new ObjectDisposedException(nameof(ConflictAdvertisement));
            if (json == published) return;
            lock (endpointGate)
            {
                if (!OwnsEndpoint()) throw new InvalidOperationException("Conflict advertisement ownership changed.");
                // No delegate/closure points into private implementation code.
                provider.RegisterFunc(json.ToString);
                published = json;
            }
        }
    }

    // The caller must first retire all native mutation and publish not-ready.
    // Counter storage must remain allocated for the process lifetime. No managed
    // callback is accepted; a timer can outlive the private AssemblyLoadContext.
    public void Retire(string finalJson, bool recoveryRequired, nint retainedInFlightCounter = 0)
    {
        lock (gate)
        {
            if (retired) return;
            Publish(finalJson);
            retired = true;
            if (recoveryRequired) return; // Deliberate immutable tombstone.
            if (retainedInFlightCounter != 0 && ((long)retainedInFlightCounter & 3) != 0)
                return; // Invalid contract remains visibly blocking.
            retainedCounter = retainedInFlightCounter;
            if (Settled()) { Withdraw(); return; }
            // Construct stopped to avoid a callback racing field assignment.
            timer = new Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(25, 25);
        }
    }

    private void Poll(object? _)
    {
        lock (gate)
        {
            try
            {
                if (!Settled()) return;
                Withdraw();
            }
            catch
            {
                // A host failure leaves the immutable blocking advertisement.
                timer?.Dispose();
                timer = null;
            }
        }
    }

    private bool Settled() => retainedCounter == 0 || Volatile.Read(ref *(int*)retainedCounter) == 0;
    private bool OwnsEndpoint() => subscriber.HasFunction && string.Equals(subscriber.InvokeFunc(), published, StringComparison.Ordinal);
    private void Withdraw()
    {
        // Never remove a provider that replaced this instance during shutdown.
        // String interning gives reloads in different AssemblyLoadContexts the
        // same small endpoint gate; creation and withdrawal cannot interleave.
        lock (endpointGate) { if (OwnsEndpoint()) provider.UnregisterFunc(); }
        timer?.Dispose();
        timer = null;
    }

    private static void Validate(string json)
    {
        if (json == null || json.Length > 1024 || new UTF8Encoding(false, true).GetByteCount(json) > 1024)
            throw new ArgumentException("Conflict snapshot exceeds its UTF-8 bound.", nameof(json));
    }
}
