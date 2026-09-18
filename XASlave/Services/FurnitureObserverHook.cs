using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;

namespace XASlave.Services;

internal sealed unsafe class FurnitureObserverHook<T> : IDisposable where T : Delegate
{
    private readonly FurnitureRestoreNativeBinding binding;
    private readonly string name;
    private readonly int stackArguments;
    private readonly T callback;
    private readonly Lock hookLock;
    private readonly MethodInfo registerUnhooker;
    private readonly MethodInfo trimUnhooker;
    private FurnitureRestoreNativeGate? gate;
    private IHook<T>? hook;
    private bool enabled, disposed;
    internal T Original { get; private set; } = null!;

    internal FurnitureObserverHook(FurnitureRestoreNativeBinding binding, string name, int stackArguments, T callback)
    {
        this.binding = binding; this.name = name; this.stackArguments = stackArguments; this.callback = callback;
        var manager = typeof(IFramework).Assembly.GetType("Dalamud.Hooking.Internal.HookManager", throwOnError: true)!;
        hookLock = manager.GetProperty("HookEnableSyncRoot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Lock
            ?? throw new NotSupportedException("Furniture hook synchronization is unavailable.");
        registerUnhooker = manager.GetMethod("RegisterUnhooker", BindingFlags.Static | BindingFlags.Public, null, [typeof(nint)], null)
            ?? throw new NotSupportedException("Furniture hook registration is unavailable.");
        trimUnhooker = registerUnhooker.ReturnType.GetMethod("TrimAfterHook", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new NotSupportedException("Furniture hook restoration tracking is unavailable.");
    }
    internal void Enable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using (hookLock.EnterScope())
        {
            if (enabled) { binding.Validate(); return; }
            binding.Validate();
            try
            {
                if (hook == null)
                {
                    gate = new(callback, stackArguments);
                    // Register the verified entry with Dalamud's hook manager so
                    // later SDK hooks recognise it instead of following its jump.
                    var unhooker = registerUnhooker.Invoke(null, [binding.Function(name)]);
                    // The SDK Reloaded constructor briefly activates its hook;
                    // this relay needs its native Original set before publication.
                    hook = ReloadedHooks.Instance.CreateHook<T>((void*)gate.Entry, (long)binding.Function(name));
                    Original = Marshal.GetDelegateForFunctionPointer<T>((nint)hook.OriginalFunctionAddress);
                    gate.SetOriginal((nint)hook.OriginalFunctionAddress);
                    binding.RecordOriginal(name, (nint)hook.OriginalFunctionAddress);
                    hook.Activate();
                    trimUnhooker.Invoke(unhooker, null);
                }
                else hook.Enable();
                binding.RecordInstalled(name); gate!.Activate(); enabled = true;
            }
            catch
            {
                gate?.Deactivate();
                if (hook?.IsHookActivated == true) hook.Disable();
                binding.RetireInstalled(name);
                throw;
            }
        }
    }
    internal void Disable()
    {
        gate?.Deactivate();
        using (hookLock.EnterScope())
        {
            if (hook?.IsHookActivated == true) hook.Disable();
            binding.RetireInstalled(name); enabled = false;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Disable(); } finally { gate?.Dispose(); }
        // Feature disposal revokes managed callbacks. Registered native forwarding
        // paths and their verified proofs survive plugin assembly reloads together.
    }
}
