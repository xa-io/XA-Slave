using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;

namespace XASlave.Services;

// Windows x64 six-argument SUBCLASSPROC relay; inactive calls forward via native DefSubclassProc.
// Admission precedes the active check; retirement retains every entering Original.
internal sealed unsafe class WindowSubclassNativeGate : IDisposable
{
    internal nint Entry { get; }
    private readonly nint state;
    private GCHandle root;
    private bool retired;
    private int retirementChecks;
    internal int InFlight => Volatile.Read(ref Unsafe.AsRef<int>((void*)(state + 4)));

    internal WindowSubclassNativeGate(Delegate callback)
    {
        state = (nint)NativeMemory.AllocZeroed(24);
        if (state == 0) throw new OutOfMemoryException();
        try
        {
            root = GCHandle.Alloc(callback);
            Marshal.WriteIntPtr(state, 16, Marshal.GetFunctionPointerForDelegate(callback));
            Entry = Allocate(Emit(state), 56);
        }
        catch { if (root.IsAllocated) root.Free(); NativeMemory.Free((void*)state); throw; }
    }

    internal static byte[] Emit(nint state)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        const int allocation = 56;
        writer.Write(new byte[] { 0x48, 0x83, 0xEC, (byte)allocation });
        // Copy the exact two stack arguments; preserve HWND/message/WPARAM/LPARAM registers.
        for (var index = 0; index < 2; index++)
        {
            writer.Write(new byte[] { 0x48, 0x8B, 0x84, 0x24 }); writer.Write(allocation + 40 + 8 * index);
            writer.Write(new byte[] { 0x48, 0x89, 0x84, 0x24 }); writer.Write(32 + 8 * index);
        }
        writer.Write(new byte[] { 0x49, 0xBB }); writer.Write((ulong)state); // mov r11,state
        writer.Write(new byte[] { 0xF0, 0x41, 0xFF, 0x43, 0x04 }); // lock inc dword [r11+4]
        writer.Write(new byte[] { 0x41, 0x83, 0x3B, 0x01 }); // cmp dword [r11],1
        writer.Write(new byte[] { 0x0F, 0x85 }); var originalJump = (int)stream.Position; writer.Write(0);
        writer.Write(new byte[] { 0x41, 0xFF, 0x53, 0x10 }); // call [r11+16], managed dispatch
        writer.Write((byte)0xE9); var doneJump = (int)stream.Position; writer.Write(0);
        var original = (int)stream.Position;
        writer.Write(new byte[] { 0x41, 0xFF, 0x53, 0x08 }); // call [r11+8], retained native Original
        var done = (int)stream.Position;
        writer.Write(new byte[] { 0x49, 0xBB }); writer.Write((ulong)state);
        writer.Write(new byte[] { 0xF0, 0x41, 0xFF, 0x4B, 0x04 }); // lock dec dword [r11+4]
        writer.Write(new byte[] { 0x48, 0x83, 0xC4, (byte)allocation, 0xC3 }); // Restore stack; RAX unchanged.
        var code = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(originalJump, 4), original - originalJump - 4);
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(doneJump, 4), done - doneJump - 4);
        return code;
    }

    internal void SetOriginal(nint original)
    {
        if (retired || original == 0 || Marshal.ReadIntPtr(state, 8) != 0) throw new InvalidOperationException("Initialize the window subclass Original once before exposure.");
        Marshal.WriteIntPtr(state, 8, original);
    }

    internal void Activate()
    {
        ObjectDisposedException.ThrowIf(retired, this);
        if (Marshal.ReadIntPtr(state, 8) == 0) throw new InvalidOperationException("Subclass forwarding is unavailable.");
        Volatile.Write(ref Unsafe.AsRef<int>((void*)state), 1);
    }

    // A full fence is required before ReleaseWhenIdle reads the admission count.
    internal void Deactivate() => Interlocked.Exchange(ref Unsafe.AsRef<int>((void*)state), 0);

    public void Dispose()
    {
        if (retired) return;
        Deactivate(); retired = true;
        ReleaseWhenIdle();
    }

    private void ReleaseWhenIdle()
    {
        if (InFlight == 0) { if (root.IsAllocated) root.Free(); return; }
        if (Interlocked.Increment(ref retirementChecks) > 32) return;
        // Native state/code/Original are retained by design. Only the managed callback root drains.
        // An abnormal native unwind conservatively retains that root rather than risking a use-after-free.
        try { ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(10); ReleaseWhenIdle(); }); }
        catch { /* Scheduling failure: preserve the admitted callback root. */ }
    }

    private static nint Allocate(byte[] code, int allocation)
    {
        var unwindAt = (code.Length + 3) & ~3;
        var tableAt = unwindAt + 8;
        var image = new byte[tableAt + 12];
        code.CopyTo(image, 0);
        // UNWIND_INFO v1, four-byte prologue, exact UWOP_ALLOC_SMALL allocation.
        new byte[] { 1, 4, 1, 0, 4, (byte)(((allocation - 8) / 8 << 4) | 2), 0, 0 }.CopyTo(image, unwindAt);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tableAt + 4), (uint)code.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tableAt + 8), (uint)unwindAt);
        var address = VirtualAlloc(0, (nuint)image.Length, 0x3000, 4);
        if (address == 0) throw new InvalidOperationException("Cannot allocate window subclass relay.");
        try
        {
            Marshal.Copy(image, 0, address, image.Length);
            if (!VirtualProtect(address, (nuint)image.Length, 0x20, out _) || !FlushInstructionCache(GetCurrentProcess(), address, (nuint)image.Length))
                throw new InvalidOperationException("Cannot publish window subclass relay code.");
            if (!RtlAddFunctionTable(address + tableAt, 1, (ulong)address)) throw new InvalidOperationException("Cannot register window subclass relay unwind data.");
            return address;
        }
        catch { VirtualFree(address, 0, 0x8000); throw; }
    }

    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool RtlAddFunctionTable(nint table, uint count, ulong baseAddress);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint type);
}
