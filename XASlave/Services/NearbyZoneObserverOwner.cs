using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using XASlave.Data;

namespace XASlave.Services;

internal sealed unsafe class NearbyZoneObserverOwner : IDisposable
{
    // All four entries consume one pointer. Preserve the complete opaque RAX result even for the void/AL callers.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NativeCall(nint proxy);
    private readonly NearbyZoneNativeBinding binding;
    private readonly NearbyZoneCache cache;
    private readonly nint[] targets;
    private readonly IHook<NativeCall>?[] hooks = new IHook<NativeCall>?[4];
    private readonly NearbyZoneNativeGate?[] gates = new NearbyZoneNativeGate?[4];
    private readonly NativeCall?[] originals = new NativeCall?[4];
    private readonly byte[][] prefixes = new byte[4][];
    private Lock? hookLock;
    private volatile bool enabled;
    private bool initialized, failed, disposed;
    private string? callbackFailure;
    private Exception? setupFailure;
    private string setupStage = "starting observer setup";
    private static readonly string[] TargetNames = ["search completion", "search request", "content-member completion", "content-member request"];

    internal NearbyZoneObserverOwner(NearbyZoneNativeBinding binding, NearbyZoneCache cache)
    {
        this.binding = binding; this.cache = cache;
        targets = [binding.SearchEnd, binding.SearchRequest, binding.ContentEnd, binding.ContentStart];
    }

    internal void Enable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NearbyZoneNativeBinding.RequireFramework();
        if (failed) throw new InvalidOperationException(setupFailure?.Message ?? "Zone observer setup failed.", setupFailure);
        if (callbackFailure is { } failure) { Disable(); throw new InvalidOperationException(failure); }
        if (enabled) { Validate(); return; }
        try
        {
            setupStage = "resolving Dalamud hook synchronization";
            hookLock ??= ResolveHookLock();
            using (hookLock.EnterScope())
            {
                if (!initialized)
                {
                    setupStage = "validating search proxy";
                    binding.CurrentProxy(NearbyZoneMode.Search);
                    setupStage = "validating content-member proxy";
                    binding.CurrentProxy(NearbyZoneMode.ContentMember);
                    // Hook installation depends on proxy/code identity, not either refresh agent.
                    // The cache and request adapter validate the selected agent immediately before use.
                    // A content-member agent mismatch must not disable ordinary player searches.
                    var proofs = new byte[4][];
                    for (var i = 0; i < 4; i++)
                    {
                        setupStage = "validating " + TargetNames[i] + " code";
                        proofs[i] = binding.ValidateUnownedTarget(targets[i]);
                    }
                    for (var i = 0; i < 4; i++)
                    {
                        setupStage = "creating " + TargetNames[i] + " observer";
                        var index = i;
                        gates[i] = new((NativeCall)(proxy => Dispatch(index, proxy)));
                        hooks[i] = ReloadedHooks.Instance.CreateHook<NativeCall>((void*)gates[i]!.Entry, (long)targets[i]);
                        var original = (nint)hooks[i]!.OriginalFunctionAddress;
                        originals[i] = Marshal.GetDelegateForFunctionPointer<NativeCall>(original);
                        gates[i]!.SetOriginal(original);
                    }
                    // Native guards stay inactive until every hook and Original is ready.
                    for (var i = 0; i < 4; i++)
                    {
                        setupStage = "activating " + TargetNames[i] + " observer";
                        hooks[i]!.Activate();
                        prefixes[i] = NearbyZoneNativeBinding.Read(targets[i], Math.Min(32, proofs[i].Length));
                    }
                    initialized = true;
                }
                else
                {
                    setupStage = "validating existing observers";
                    Validate();
                    setupStage = "re-enabling existing observers";
                    foreach (var hook in hooks) hook!.Enable();
                }
                setupStage = "activating callback gates";
                foreach (var gate in gates) gate!.Activate();
                enabled = true;
                cache.SetObserversReady(true);
            }
        }
        catch (Exception error)
        {
            failed = true;
            setupFailure = new InvalidOperationException($"Zone observer setup failed while {setupStage}: {error.Message}", error);
            // Preserve the first failure across future polls and cleanup; do not flood the log at 1 Hz.
            Plugin.Log.Error(setupFailure, "[XASlave] XA Nearby zone observer setup failed.");
            try { Disable(); }
            catch (Exception cleanupError) { Plugin.Log.Error(cleanupError, "[XASlave] XA Nearby zone observer cleanup also failed."); }
            throw setupFailure;
        }
    }

    private nint Dispatch(int index, nint proxy)
    {
        var observe = enabled && Plugin.Framework.IsInFrameworkUpdateThread && Plugin.ClientState.IsLoggedIn;
        var generation = cache.Generation;
        var mode = index < 2 ? NearbyZoneMode.Search : NearbyZoneMode.ContentMember;
        var isRequest = (index & 1) != 0;
        NearbyZoneCache.RequestStart start = default;
        var entered = false;
        if (observe && isRequest)
        {
            try { start = cache.EnterRequest(mode, proxy); entered = true; }
            catch (Exception error) { RecordFailure(error); }
        }
        // Never catch an uncertain Original failure and replay it. EndRequest is Original-first.
        var result = originals[index]!(proxy);
        if (observe && enabled && cache.Generation == generation)
        {
            try
            {
                if (!isRequest) cache.ObserveCompleted(mode, proxy, generation);
                else if (entered) cache.RequestReturned(start);
            }
            catch (Exception error) { RecordFailure(error); }
        }
        return result;
    }

    private void RecordFailure(Exception error)
    {
        callbackFailure ??= error.Message;
        foreach (var gate in gates) gate?.Deactivate();
        enabled = false;
        cache.SetObserversReady(false);
        cache.ReportUnavailable(error.Message);
    }

    private void Validate()
    {
        for (var i = 0; i < 4; i++) binding.ValidateInstalledTarget(targets[i], prefixes[i]);
    }

    internal void Disable()
    {
        foreach (var gate in gates) gate?.Deactivate();
        if (enabled) { enabled = false; cache.SetObserversReady(false); }
        Exception? failure = null;
        if (hookLock != null)
        {
            using (hookLock.EnterScope())
                foreach (var hook in hooks)
                    try { if (hook?.IsHookActivated == true) hook.Disable(); }
                    catch (Exception error) { failure ??= error; }
        }
        if (failure != null) cache.ReportUnavailable("Zone observer cleanup: " + failure.Message);
    }

    private static Lock ResolveHookLock()
    {
        var manager = typeof(IFramework).Assembly.GetType("Dalamud.Hooking.Internal.HookManager", throwOnError: true)!;
        return manager.GetProperty("HookEnableSyncRoot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Lock
            ?? throw new NotSupportedException("The installed hook synchronization contract is unavailable.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Disable(); }
        finally { foreach (var gate in gates) gate?.Dispose(); }
        // Reloaded retains its reverse wrappers and Originals; it provides no trampoline-destroy operation.
        // Retired native relays need those Originals even after the managed observer root has drained.
    }
}
