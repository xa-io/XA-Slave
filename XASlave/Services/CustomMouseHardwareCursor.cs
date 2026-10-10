using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace XASlave.Services;

/// <summary>Owns one process-local cursor image. Creation and retirement never belong in a native draw hook.</summary>
internal sealed class CustomMouseHardwareCursor : IDisposable
{
    internal const int MaximumSourceDimension = 256;
    internal const int MaximumOutputDimension = 1280;
    private const int MaximumHandles = 128;
    private const long MaximumReservedBytes = 64L * 1024 * 1024;
    private static readonly object RetirementSync = new();
    private static readonly List<RetiredCursor> Retired = new(MaximumHandles);
    private static int reservedHandles;
    private static long reservedBytes;
    private static int cleanupThread;
    private readonly int reservationBytes;
    private nint handle;

    internal nint Handle => Volatile.Read(ref handle);
    internal int Width { get; }
    internal int Height { get; }
    internal int HotspotX { get; }
    internal int HotspotY { get; }

    internal static bool IsSelected(nint cursor) => cursor != 0 && TryGetGlobalCursor(out var selected) && selected == cursor;

    private CustomMouseHardwareCursor(nint handle, ScaledPixels pixels, int reservationBytes)
    {
        this.handle = handle;
        this.reservationBytes = reservationBytes;
        Width = pixels.Width;
        Height = pixels.Height;
        HotspotX = pixels.HotspotX;
        HotspotY = pixels.HotspotY;
    }

    /// <summary>Copies straight-alpha BGRA pixels, scales them and creates one owned HCURSOR. Does not display it.</summary>
    internal static CustomMouseHardwareCursor Create(int width, int height, int hotspotX, int hotspotY,
        byte[] bgra, int pitch, float ratio)
    {
        var pixels = ScalePixels(width, height, hotspotX, hotspotY, bgra, pitch, ratio);
        var maskStride = checked(((pixels.Width + 31) / 32) * 4);
        var bytes = checked(pixels.Bgra.Length + maskStride * pixels.Height);
        Reserve(bytes);
        nint colorBitmap = 0;
        nint maskBitmap = 0;
        nint createdCursor = 0;
        var transferred = false;
        try
        {
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = pixels.Width,
                    Height = -pixels.Height,
                    Planes = 1,
                    BitCount = 32,
                    SizeImage = (uint)pixels.Bgra.Length,
                },
            };
            colorBitmap = CreateDIBSection(0, ref bitmapInfo, 0, out var colorBits, 0, 0);
            if (colorBitmap == 0 || colorBits == 0) throw NativeFailure("CreateDIBSection");
            Marshal.Copy(pixels.Bgra, 0, colorBits, pixels.Bgra.Length);

            // Both DIBs are top-down. Transparent pixels need a set AND-mask bit.
            var mask = new byte[checked(maskStride * pixels.Height)];
            for (var y = 0; y < pixels.Height; y++)
                for (var x = 0; x < pixels.Width; x++)
                    if (pixels.Bgra[(y * pixels.Width + x) * 4 + 3] == 0)
                        mask[y * maskStride + x / 8] |= (byte)(0x80 >> (x & 7));
            var maskInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = pixels.Width,
                    Height = -pixels.Height,
                    Planes = 1,
                    BitCount = 1,
                    SizeImage = (uint)mask.Length,
                    ColorsUsed = 2,
                },
                Color1 = 0x00FFFFFF,
            };
            maskBitmap = CreateDIBSection(0, ref maskInfo, 0, out var maskBits, 0, 0);
            if (maskBitmap == 0 || maskBits == 0) throw NativeFailure("CreateDIBSection (mask)");
            Marshal.Copy(mask, 0, maskBits, mask.Length);
            var iconInfo = new IconInfo
            {
                IsIcon = false,
                HotspotX = (uint)pixels.HotspotX,
                HotspotY = (uint)pixels.HotspotY,
                MaskBitmap = maskBitmap,
                ColorBitmap = colorBitmap,
            };
            createdCursor = CreateIconIndirect(ref iconInfo);
            if (createdCursor == 0) throw NativeFailure("CreateIconIndirect");
            var result = new CustomMouseHardwareCursor(createdCursor, pixels, bytes);
            transferred = true;
            return result;
        }
        finally
        {
            // CreateIconIndirect copies its input bitmaps; neither bitmap becomes cursor-owned.
            if (maskBitmap != 0) _ = DeleteObject(maskBitmap);
            if (colorBitmap != 0) _ = DeleteObject(colorBitmap);
            if (!transferred)
            {
                if (createdCursor != 0) _ = DestroyCursor(createdCursor);
                ReleaseReservation(bytes);
            }
        }
    }

    /// <summary>Bounded nearest-neighbor scaling preserves pixel artwork and premultiplies alpha exactly once.</summary>
    internal static ScaledPixels ScalePixels(int width, int height, int hotspotX, int hotspotY,
        byte[] bgra, int pitch, float ratio)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width is < 1 or > MaximumSourceDimension || height is < 1 or > MaximumSourceDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Cursor source dimensions must be between 1 and 256 pixels.");
        if (hotspotX < 0 || hotspotX >= width || hotspotY < 0 || hotspotY >= height)
            throw new ArgumentOutOfRangeException(nameof(hotspotX), "Cursor hotspot must be inside its image.");
        if (!float.IsFinite(ratio) || ratio is < 0.1f or > 5f)
            throw new ArgumentOutOfRangeException(nameof(ratio), "Cursor scale must be between 0.1x and 5x.");
        if (pitch < checked(width * 4) || pitch > MaximumSourceDimension * 4 + 4096
            || bgra.Length != checked(pitch * height))
            throw new ArgumentException("Cursor pixels must contain exactly the bounded positive-pitch BGRA image.", nameof(bgra));
        var scaledWidth = Math.Clamp((int)MathF.Round(width * ratio), 1, MaximumOutputDimension);
        var scaledHeight = Math.Clamp((int)MathF.Round(height * ratio), 1, MaximumOutputDimension);
        var scaledHotspotX = Math.Clamp((int)MathF.Round(hotspotX * ratio), 0, scaledWidth - 1);
        var scaledHotspotY = Math.Clamp((int)MathF.Round(hotspotY * ratio), 0, scaledHeight - 1);
        var result = new byte[checked(scaledWidth * scaledHeight * 4)];
        for (var y = 0; y < scaledHeight; y++)
        {
            var sourceY = (int)((long)y * height / scaledHeight);
            for (var x = 0; x < scaledWidth; x++)
            {
                var sourceX = (int)((long)x * width / scaledWidth);
                var source = checked(sourceY * pitch + sourceX * 4);
                var destination = checked((y * scaledWidth + x) * 4);
                var alpha = bgra[source + 3];
                result[destination] = Premultiply(bgra[source], alpha);
                result[destination + 1] = Premultiply(bgra[source + 1], alpha);
                result[destination + 2] = Premultiply(bgra[source + 2], alpha);
                result[destination + 3] = alpha;
            }
        }
        return new ScaledPixels(scaledWidth, scaledHeight, scaledHotspotX, scaledHotspotY, result);
    }

    private static byte Premultiply(byte channel, byte alpha) => (byte)((channel * alpha + 127) / 255);

    /// <summary>The owner must prevent future selection first. Selected/uncertain handles retire without changing another thread's cursor.</summary>
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref handle, 0);
        if (owned == 0) return;
        var retirement = new RetiredCursor(owned, reservationBytes);
        lock (RetirementSync)
        {
            if (!TryDestroyUnselected(retirement)) Retired.Add(retirement);
        }
    }

    /// <summary>Call only from Framework.Update to establish the cursor-owning thread before cleanup is attempted.</summary>
    internal static bool RegisterGameThread()
    {
        var thread = unchecked((int)GetCurrentThreadId());
        var registered = Interlocked.CompareExchange(ref cleanupThread, thread, 0);
        return registered == 0 || registered == thread;
    }

    /// <summary>Try during every Update, including disabled state. Off-thread final cleanup must queue this static call on Framework.</summary>
    internal static bool TryDrainRetiredOnGameThread(out int remaining)
    {
        var thread = unchecked((int)GetCurrentThreadId());
        lock (RetirementSync)
        {
            if (Volatile.Read(ref cleanupThread) != thread)
            {
                remaining = Retired.Count;
                return false;
            }
            for (var i = Retired.Count - 1; i >= 0; i--)
            {
                var cursor = Retired[i];
                if (!TryGetGlobalCursor(out var selected)) continue;
                if (selected == cursor.Handle)
                {
                    // Only replace our own currently selected handle. Never alter a newer plugin/desktop selection.
                    var replacement = LoadCursorW(0, (nint)32512); // IDC_ARROW, shared Windows scheme resource.
                    if (replacement == 0) continue;
                    _ = SetCursor(replacement);
                }
                if (TryDestroyUnselected(cursor)) Retired.RemoveAt(i);
            }
            remaining = Retired.Count;
            return true;
        }
    }

    private static bool TryDestroyUnselected(RetiredCursor cursor)
    {
        if (!TryGetGlobalCursor(out var selected) || selected == cursor.Handle || !DestroyCursor(cursor.Handle)) return false;
        reservedHandles--;
        reservedBytes -= cursor.Bytes;
        return true;
    }

    private static bool TryGetGlobalCursor(out nint handle)
    {
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        var succeeded = GetCursorInfo(ref info);
        handle = succeeded ? info.Cursor : 0;
        return succeeded;
    }

    private static void Reserve(int bytes)
    {
        lock (RetirementSync)
        {
            if (reservedHandles >= MaximumHandles || reservedBytes + bytes > MaximumReservedBytes)
                throw new InvalidOperationException("Custom hardware cursors exceed the 128-handle or 64 MiB budget.");
            reservedHandles++;
            reservedBytes += bytes;
        }
    }

    private static void ReleaseReservation(int bytes)
    {
        lock (RetirementSync) { reservedHandles--; reservedBytes -= bytes; }
    }

    private static Win32Exception NativeFailure(string operation) => new(Marshal.GetLastWin32Error(), operation + " failed.");

    internal readonly record struct ScaledPixels(int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra);
    private readonly record struct RetiredCursor(nint Handle, int Bytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Color0, Color1; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
        internal uint HotspotX, HotspotY;
        internal nint MaskBitmap, ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal uint Size, Flags;
        internal nint Cursor;
        internal int X, Y;
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyCursor(nint cursor);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern nint LoadCursorW(nint instance, nint resource);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
