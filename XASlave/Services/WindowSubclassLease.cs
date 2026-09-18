using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace XASlave.Services;

// Detached from the plugin service: callbacks/timers use only Win32 and this lease.
// A failed removal intentionally retains inert state until removal or destruction.
internal sealed class WindowSubclassLease : IWindowSubclassLease
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassCall(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    private static long nextId;
    private readonly WindowSubclassNativeGate gate;
    private readonly nuint id, token;
    private readonly uint cleanupMessage;
    private GCHandle root;
    private readonly object lifetimeSync = new();
    private Timer? cleanupTimer;
    private int registered, detached, destroyed, blocks, cleanupRequested, pendingPost, removalAttempts, postAttempts, released;
    private string? diagnostic;
    private int cleanupPulses, drainPulses;
    public WindowLockTarget Target { get; }
    public bool Registered => Volatile.Read(ref registered) != 0 && !Detached;
    public bool Detached => Volatile.Read(ref detached) != 0;
    public bool Destroyed => Volatile.Read(ref destroyed) != 0;
    public bool BlocksMovement => Registered && !Destroyed && Volatile.Read(ref blocks) != 0;

    internal WindowSubclassLease(WindowLockTarget target)
    {
        WindowSubclassApi.RequireOwnerThread(target);
        Target = target;
        id = unchecked((nuint)Interlocked.Increment(ref nextId));
        if (id == 0) throw new InvalidOperationException("Window subclass identity exhausted.");
        cleanupMessage = WindowSubclassApi.CleanupMessage;
        root = GCHandle.Alloc(this);
        token = unchecked((nuint)GCHandle.ToIntPtr(root));
        WindowSubclassNativeGate? createdGate = null;
        try
        {
            gate = createdGate = new((SubclassCall)Dispatch);
            gate.SetOriginal(WindowSubclassApi.ForwardAddress);
            gate.Activate();
        }
        catch { createdGate?.Dispose(); root.Free(); throw; }
    }

    internal void Install()
    {
        try { WindowSubclassApi.RequireOwnerThread(Target); }
        catch (Exception error) { Note(error.Message); MarkDetached(); return; }
        try
        {
            if (!WindowSubclassApi.SetWindowSubclass(Target.Handle, gate.Entry, id, token))
            {
                Note("SetWindowSubclass failed: " + Marshal.GetLastWin32Error());
                MarkDetached(); return;
            }
            Volatile.Write(ref registered, 1);
            if (Destroyed || Volatile.Read(ref cleanupRequested) != 0) RequestRemoval();
        }
        catch (Exception error)
        {
            // If native registration threw after entry, retain the root until an
            // owning-thread removal can prove that Windows no longer owns it.
            Volatile.Write(ref registered, 1); Deactivate();
            Note("Window subclass installation uncertain: " + error.Message);
            RequestRemoval();
        }
    }

    public void Activate()
    {
        WindowSubclassApi.RequireOwnerThread(Target);
        if (!Registered || Destroyed || Volatile.Read(ref cleanupRequested) != 0)
            throw new InvalidOperationException("Window subclass cannot be activated after cleanup.");
        Volatile.Write(ref blocks, 1);
    }
    public void Deactivate() => Interlocked.Exchange(ref blocks, 0);

    public void RequestRemoval()
    {
        Deactivate(); Interlocked.Exchange(ref cleanupRequested, 1);
        if (Detached) { ReleaseWhenDrained(); return; }
        if (WindowSubclassApi.GetCurrentThreadId() == Target.Thread)
        {
            TryRemove(false);
            if (Detached) return;
        }
        StartCleanupTimer();
    }

    private void StartCleanupTimer()
    {
        lock (lifetimeSync)
        {
            if (released != 0 || cleanupTimer != null) return;
            if (Detached ? drainPulses > 32 : cleanupPulses > 16 || removalAttempts >= 3 || postAttempts >= 3) return;
            cleanupTimer = new Timer(_ => TimerPulse(), null, 250, 250);
        }
    }
    private void TimerPulse()
    {
        try { if (Detached) ReleaseWhenDrained(); else PostCleanup(); }
        catch (Exception error) { StopTimer(); Note("Window cleanup timer stopped: " + error.Message); }
    }
    private void StopTimer()
    {
        lock (lifetimeSync) { cleanupTimer?.Dispose(); cleanupTimer = null; }
    }

    private void PostCleanup()
    {
        if (Detached) { ReleaseWhenDrained(); return; }
        if (Interlocked.Increment(ref cleanupPulses) > 16 || Volatile.Read(ref removalAttempts) >= 3 || Volatile.Read(ref postAttempts) >= 3)
        {
            StopTimer();
            Note("Cleanup pending: an inactive subclass remains rooted after bounded retries."); return;
        }
        if (Interlocked.CompareExchange(ref pendingPost, 1, 0) != 0) return;
        Interlocked.Increment(ref postAttempts);
        try
        {
            if (!WindowSubclassApi.PostMessage(Target.Handle, cleanupMessage, id, unchecked((nint)token)))
            { Interlocked.Exchange(ref pendingPost, 0); Note("Posting owned window cleanup failed."); }
        }
        catch (Exception error) { Interlocked.Exchange(ref pendingPost, 0); Note("Window cleanup posting failed: " + error.Message); }
    }

    private void TryRemove(bool destroying)
    {
        Deactivate();
        if (Detached) return;
        if (WindowSubclassApi.GetCurrentThreadId() != Target.Thread) { StartCleanupTimer(); return; }
        if (!destroying && Volatile.Read(ref removalAttempts) >= 3) return;
        Interlocked.Increment(ref removalAttempts);
        try
        {
            // Never replace a WNDPROC or remove another callback/ID from the chain.
            if (WindowSubclassApi.RemoveWindowSubclass(Target.Handle, gate.Entry, id)) MarkDetached();
            else Note("RemoveWindowSubclass failed; movement is disabled and callback state remains rooted.");
        }
        catch (Exception error) { Note("Window subclass removal failed: " + error.Message); }
    }

    private nint Dispatch(nint window, uint message, nuint wParam, nint lParam, nuint callbackId, nuint reference)
    {
        var owned = window == Target.Handle && callbackId == id && reference == token;
        var destroying = owned && message == 0x0082;
        try
        {
            if (!owned) { Deactivate(); Note("Window subclass callback identity changed."); }
            else if (destroying)
            {
                Interlocked.Exchange(ref destroyed, 1); Interlocked.Exchange(ref cleanupRequested, 1);
                TryRemove(true);
            }
            else if (message == cleanupMessage && wParam == id && unchecked((nuint)lParam) == token && Volatile.Read(ref cleanupRequested) != 0)
            {
                Interlocked.Exchange(ref pendingPost, 0); TryRemove(false);
            }
            else if (message == 0x0046 && lParam != 0 && BlocksMovement)
            {
                WindowSubclassApi.RequireWritableWindowPos(lParam);
                var flags = Marshal.ReadInt32(lParam, 32);
                Marshal.WriteInt32(lParam, 32, flags | 0x0002);
            }
        }
        catch (Exception error) { Deactivate(); Note("Window position mutation disabled: " + error.Message); }
        // Even cleanup messages and mutation failures traverse the existing chain once.
        try { return WindowSubclassApi.DefSubclassProc(window, message, wParam, lParam); }
        finally { if (destroying) MarkDetached(); }
    }

    private void MarkDetached()
    {
        Deactivate(); Interlocked.Exchange(ref detached, 1); Interlocked.Exchange(ref registered, 0);
        gate.Deactivate();
        ReleaseWhenDrained();
    }
    private void ReleaseWhenDrained()
    {
        try
        {
            lock (lifetimeSync)
            {
                if (!Detached || released != 0) return;
                if (gate.InFlight != 0)
                {
                    if (++drainPulses <= 32) StartCleanupTimer();
                    else { StopTimer(); Note("Detached callback remains in flight; its root is retained."); }
                    return;
                }
                released = 1; StopTimer(); gate.Dispose();
                if (root.IsAllocated) root.Free();
            }
        }
        catch (Exception error) { Note("Detached callback root retained: " + error.Message); }
    }
    private void Note(string text) => Interlocked.CompareExchange(ref diagnostic, text.Length <= 256 ? text : text[..256], null);
    internal string? TakeDiagnostic() => Interlocked.Exchange(ref diagnostic, null);
}

internal static class WindowSubclassApi
{
    private static readonly Lazy<(nint Module, nint Forward, uint Message)> EntryPoints = new(() =>
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || Marshal.SizeOf<WindowPos>() != 40 || Marshal.OffsetOf<WindowPos>(nameof(WindowPos.Flags)).ToInt32() != 32)
            throw new PlatformNotSupportedException("Unsupported window subclass platform/layout.");
        var module = NativeLibrary.Load("comctl32.dll");
        // Retain this native module with the inactive forwarding gates for process lifetime.
        var forward = NativeLibrary.GetExport(module, "DefSubclassProc");
        NativeLibrary.GetExport(module, "SetWindowSubclass"); NativeLibrary.GetExport(module, "RemoveWindowSubclass");
        var message = RegisterWindowMessage("XASlave.WindowLock." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N"));
        if (message == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot register owned window cleanup message.");
        return (module, forward, message);
    });
    internal static nint ForwardAddress => EntryPoints.Value.Forward;
    internal static uint CleanupMessage => EntryPoints.Value.Message;
    internal static void RequireOwnerThread(WindowLockTarget target)
    {
        if (target.Handle == 0 || !IsWindow(target.Handle)) throw new InvalidOperationException("Waiting for game window");
        var thread = GetWindowThreadProcessId(target.Handle, out var process);
        if (process != (uint)Environment.ProcessId || process != target.Process) throw new InvalidOperationException("Unsupported game window process");
        if (thread == 0 || thread != target.Thread || thread != GetCurrentThreadId()) throw new InvalidOperationException("Unsupported window thread");
    }
    internal static void RequireWritableWindowPos(nint address)
    {
        if (VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0 || region.State != 0x1000
            || (region.Protect & 0x100) != 0 || (region.Protect & 0xFF & 0xCC) == 0)
            throw new InvalidOperationException("WINDOWPOS buffer is not writable committed memory.");
        var start = unchecked((ulong)address); var begin = unchecked((ulong)region.Base);
        if (start < begin || checked(start + 40) > checked(begin + (ulong)region.Size)) throw new InvalidOperationException("WINDOWPOS buffer crosses its memory region.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPos { public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryRegion { public nint Base, AllocationBase; public uint AllocationProtect; public nuint Size; public uint State, Protect, Type; }
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(nint window, nint callback, nuint id, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(nint window, nint callback, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQuery(nint address, out MemoryRegion region, nuint size);
}
