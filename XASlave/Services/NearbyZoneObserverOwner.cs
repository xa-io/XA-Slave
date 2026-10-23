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
    private global::XASlave.Localization.UiMessage? callbackFailureMessage;
    private Exception? setupFailure;
    private global::XASlave.Localization.UiMessage setupStage = global::XASlave.Localization.UiMessage.Key("S68269058F0889D83");
    private static readonly global::XASlave.Localization.UiMessage[] TargetNames = [global::XASlave.Localization.UiMessage.Key("S7C2C28FFBD9DDCCC"), global::XASlave.Localization.UiMessage.Key("S08B79BBB8EFD3A64"), global::XASlave.Localization.UiMessage.Key("SDBF8024C3A3098DD"), global::XASlave.Localization.UiMessage.Key("S20FE3F55EF01986D")];

    internal NearbyZoneObserverOwner(NearbyZoneNativeBinding binding, NearbyZoneCache cache)
    {
        this.binding = binding; this.cache = cache;
        targets = [binding.SearchEnd, binding.SearchRequest, binding.ContentEnd, binding.ContentStart];
    }

    internal void Enable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NearbyZoneNativeBinding.RequireFramework();
        if (failed) throw global::XASlave.Localization.UiMessage.CreateException((setupFailure is { } priorSetup ? global::XASlave.Localization.UiMessage.FromException(priorSetup) : global::XASlave.Localization.UiMessage.Key("S9BAD5A4961CF1397")), originalMessageText => new InvalidOperationException(originalMessageText, setupFailure));
        if (callbackFailure is { } failure) { Disable(); throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Capture(callbackFailureMessage, failure), originalMessageText => new InvalidOperationException(originalMessageText)); }
        if (enabled) { Validate(); return; }
        try
        {
            setupStage = global::XASlave.Localization.UiMessage.Key("S16A463EDD8C2EDFC");
            hookLock ??= ResolveHookLock();
            using (hookLock.EnterScope())
            {
                if (!initialized)
                {
                    setupStage = global::XASlave.Localization.UiMessage.Key("S6DA6F178954C8A7A");
                    binding.CurrentProxy(NearbyZoneMode.Search);
                    setupStage = global::XASlave.Localization.UiMessage.Key("S15F0BA01BF51C993");
                    binding.CurrentProxy(NearbyZoneMode.ContentMember);
                    // Hook installation depends on proxy/code identity, not either refresh agent.
                    // The cache and request adapter validate the selected agent immediately before use.
                    // A content-member agent mismatch must not disable ordinary player searches.
                    var proofs = new byte[4][];
                    for (var i = 0; i < 4; i++)
                    {
                        setupStage = global::XASlave.Localization.UiMessage.Format("S4E91DCB371368163" , TargetNames[i]);
                        proofs[i] = binding.ValidateUnownedTarget(targets[i]);
                    }
                    for (var i = 0; i < 4; i++)
                    {
                        setupStage = global::XASlave.Localization.UiMessage.Format("SA71A12E47F45E030" , TargetNames[i]);
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
                        setupStage = global::XASlave.Localization.UiMessage.Format("S52E3E751AD9E7835" , TargetNames[i]);
                        hooks[i]!.Activate();
                        prefixes[i] = NearbyZoneNativeBinding.Read(targets[i], Math.Min(32, proofs[i].Length));
                    }
                    initialized = true;
                }
                else
                {
                    setupStage = global::XASlave.Localization.UiMessage.Key("S72868346359615F2");
                    Validate();
                    setupStage = global::XASlave.Localization.UiMessage.Key("S757042786E250865");
                    foreach (var hook in hooks) hook!.Enable();
                }
                setupStage = global::XASlave.Localization.UiMessage.Key("S417783DEEE7BE53C");
                foreach (var gate in gates) gate!.Activate();
                enabled = true;
                cache.SetObserversReady(true);
            }
        }
        catch (Exception error)
        {
            failed = true;
            setupFailure = global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("SE240C28A3A2018AB" , setupStage, global::XASlave.Localization.UiMessage.FromException(error)), originalMessageText => new InvalidOperationException(originalMessageText, error));
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
        callbackFailure ??= global::XASlave.Localization.UiMessage.Store(ref callbackFailureMessage, global::XASlave.Localization.UiMessage.FromException(error));
        foreach (var gate in gates) gate?.Deactivate();
        enabled = false;
        cache.SetObserversReady(false);
        cache.ReportUnavailable(global::XASlave.Localization.UiMessage.FromException(error));
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
        if (failure != null) cache.ReportUnavailable(global::XASlave.Localization.UiMessage.Format("SD0FB2B099C83FA19" , global::XASlave.Localization.UiMessage.FromException(failure)));
    }

    private static Lock ResolveHookLock()
    {
        var manager = typeof(IFramework).Assembly.GetType("Dalamud.Hooking.Internal.HookManager", throwOnError: true)!;
        return manager.GetProperty("HookEnableSyncRoot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Lock
            ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S865C2E7363396B55"), originalMessageText => new NotSupportedException(originalMessageText));
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
