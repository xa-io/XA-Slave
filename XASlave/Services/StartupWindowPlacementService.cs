using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using XASlave.Data;
using XASlave.Localization;

namespace XASlave.Services;

internal readonly record struct StartupWindowBounds(int Left, int Top, int Right, int Bottom);
internal sealed record StartupWindowMonitor(string DeviceName, StartupWindowBounds WorkArea, bool IsPrimary);
internal readonly record struct StartupWindowPlan(int X, int Y, bool UsedFallback);

internal static class StartupWindowPlacementGeometry
{
    internal static bool IsValid(StartupWindowPlacement? saved)
    {
        if (saved == null || string.IsNullOrWhiteSpace(saved.MonitorDeviceName) || saved.MonitorDeviceName.Length > 128)
            return false;
        foreach (var character in saved.MonitorDeviceName)
            if (char.IsControl(character) || char.IsSurrogate(character)) return false;
        return true;
    }

    internal static bool TryPlan(StartupWindowPlacement saved, IReadOnlyList<StartupWindowMonitor> monitors,
        int width, int height, out StartupWindowPlan plan)
    {
        plan = default;
        if (!IsValid(saved) || monitors == null || monitors.Count == 0 || width <= 0 || height <= 0)
            return false;
        StartupWindowMonitor? selected = null;
        StartupWindowMonitor? primary = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in monitors)
        {
            if (monitor == null || string.IsNullOrWhiteSpace(monitor.DeviceName) || !names.Add(monitor.DeviceName)
                || monitor.WorkArea.Right <= monitor.WorkArea.Left || monitor.WorkArea.Bottom <= monitor.WorkArea.Top)
                return false;
            if (monitor.IsPrimary)
            {
                if (primary != null) return false;
                primary = monitor;
            }
            if (string.Equals(monitor.DeviceName, saved.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                selected = monitor;
        }
        var fallback = selected == null;
        selected ??= primary;
        if (selected == null) return false;

        var work = selected.WorkArea;
        // Long arithmetic preserves negative displays and handles stale extreme saved coordinates.
        var x = (long)work.Left + saved.X - saved.MonitorWorkLeft;
        var y = (long)work.Top + saved.Y - saved.MonitorWorkTop;
        var titleWidth = Math.Min(32L, width);
        var titleHeight = Math.Min(32L, height);
        var reachable = x <= (long)work.Right - titleWidth && x + width >= (long)work.Left + titleWidth
            && y >= work.Top && y + titleHeight <= work.Bottom;
        if (fallback || !reachable)
        {
            // Oversized windows retain an accessible top-left rather than overflowing arithmetic.
            x = Math.Clamp(x, (long)work.Left, Math.Max((long)work.Left, (long)work.Right - width));
            y = Math.Clamp(y, (long)work.Top, Math.Max((long)work.Top, (long)work.Bottom - height));
        }
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue)
            return false;
        plan = new((int)x, (int)y, fallback);
        return true;
    }
}

/// <summary>One bounded startup transaction. Readiness polling never becomes continuous window enforcement.</summary>
public unsafe sealed class StartupWindowPlacementService : IDisposable
{
    internal const long ReadyTimeoutMilliseconds = 30_000;
    internal const long ReadyPollMilliseconds = 250;
    private const uint MoveFlags = 0x0001 | 0x0004 | 0x0010 | 0x0200; // NOSIZE, NOZORDER, NOACTIVATE, NOOWNERZORDER.
    private readonly IFramework framework;
    private readonly IGameConfig gameConfig;
    private readonly IPluginLog log;
    private readonly SystemWindowModsService windowMods;
    private readonly Func<nint> readWindow;
    private readonly Func<bool> isMovementBlocked;
    private readonly Func<bool> isOwnerDisposed;
    private readonly object sync = new();
    private StartupRequest? pending;
    private long generation;
    private long deadline;
    private long nextPoll;
    private int disposed;
    private bool subscribed;
    private bool startupAdmissionClosed;
    private UiMessage status = UiMessage.Authored("Startup window layout is idle.");

    private sealed record StartupRequest(bool Resize, int Width, int Height, bool IgnoreMinimum,
        bool Move, StartupWindowPlacement? Placement, bool PlacementSkipped, Func<bool>? IsCurrent);

    public StartupWindowPlacementService(IFramework framework, IGameConfig gameConfig, IPluginLog log,
        SystemWindowModsService windowMods, Func<nint> readWindow, Func<bool> isMovementBlocked,
        Func<bool>? isOwnerDisposed = null)
    {
        this.framework = framework;
        this.gameConfig = gameConfig;
        this.log = log;
        this.windowMods = windowMods;
        this.readWindow = readWindow;
        this.isMovementBlocked = isMovementBlocked;
        this.isOwnerDisposed = isOwnerDisposed ?? (() => false);
    }

    public bool IsPending => Volatile.Read(ref pending) != null;
    internal UiMessage StatusMessage => Volatile.Read(ref status);
    public string StatusText => StatusMessage.Original;

    public void ArmStartup(bool resize, int width, int height, bool ignoreMinimum, bool move,
        StartupWindowPlacement? placement, Func<bool>? isRequestCurrent = null)
    {
        lock (sync)
        {
            if (startupAdmissionClosed || Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading || isOwnerDisposed()) return;
            // Cancel can precede the deferred startup queue or reenter through the owner callback.
            if (startupAdmissionClosed || Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading) return;
            startupAdmissionClosed = true;
            Interlocked.Increment(ref generation);
            ClearPending();
            if (!resize && !move)
            {
                SetStatus(UiMessage.Authored("Startup window layout is idle."));
                return;
            }
            if (resize && !SystemWindowModsService.TryNormalizeCustomResolutionMessage(width, height, out width, out height, out var error))
            {
                SetStatus(error);
                return;
            }
            var placementSkipped = move && !StartupWindowPlacementGeometry.IsValid(placement);
            if (placementSkipped && !resize)
            {
                SetStatus(UiMessage.Authored("Startup window placement is invalid."));
                return;
            }
            if (placementSkipped) move = false;
            var detached = placement == null ? null : new StartupWindowPlacement
            {
                MonitorDeviceName = placement.MonitorDeviceName, X = placement.X, Y = placement.Y,
                MonitorWorkLeft = placement.MonitorWorkLeft, MonitorWorkTop = placement.MonitorWorkTop,
            };
            pending = new(resize, width, height, ignoreMinimum, move, detached, placementSkipped, isRequestCurrent);
            deadline = Environment.TickCount64 + ReadyTimeoutMilliseconds;
            nextPoll = 0;
            SetStatus(UiMessage.Authored("Waiting for a ready, unlocked window to apply the startup layout."));
            framework.Update += OnUpdate;
            subscribed = true;
        }
    }

    public void Cancel()
    {
        lock (sync)
        {
            startupAdmissionClosed = true;
            Interlocked.Increment(ref generation);
            ClearPending();
            if (Volatile.Read(ref disposed) == 0)
                SetStatus(UiMessage.Authored("Startup window layout was cancelled."));
        }
    }

    private void ClearPending()
    {
        pending = null;
        if (!subscribed) return;
        framework.Update -= OnUpdate;
        subscribed = false;
    }

    private bool IsCurrent(StartupRequest request, long expectedGeneration)
    {
        if (Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading || Volatile.Read(ref generation) != expectedGeneration)
            return false;
        // Both callbacks may reenter settings/disposal. Fence their return before admitting a write.
        if (isOwnerDisposed() || (request.IsCurrent != null && !request.IsCurrent())) return false;
        return Volatile.Read(ref disposed) == 0 && !framework.IsFrameworkUnloading && Volatile.Read(ref generation) == expectedGeneration;
    }

    private void OnUpdate(IFramework _)
    {
        StartupRequest? request;
        long expectedGeneration;
        lock (sync)
        {
            request = pending;
            expectedGeneration = generation;
            if (request == null || Volatile.Read(ref disposed) != 0 || !framework.IsInFrameworkUpdateThread)
                return;
            if (framework.IsFrameworkUnloading)
            {
                ClearPending();
                SetStatus(UiMessage.Authored("Startup window layout was cancelled."));
                return;
            }
            var now = Environment.TickCount64;
            if (now >= deadline)
            {
                ClearPending();
                SetStatus(UiMessage.Authored("Startup window layout timed out waiting for a ready, unlocked window."));
                log.Warning("[XASlave] {Status}", StatusText);
                return;
            }
            if (now < nextPoll) return;
            nextPoll = now + ReadyPollMilliseconds;
        }

        try
        {
            if (!IsCurrent(request, expectedGeneration))
            {
                StopChangedRequest(expectedGeneration);
                return;
            }
            if (!TryReadReadyWindow(true, out var window) || !IsCurrent(request, expectedGeneration))
                return;
            lock (sync)
            {
                if (!ReferenceEquals(request, pending) || generation != expectedGeneration) return;
                // Consume before any native mutation. Failure/readback mismatch never restarts the transaction.
                ClearPending();
            }
            Execute(request, expectedGeneration, window);
        }
        catch (Exception error)
        {
            lock (sync)
            {
                if (generation != expectedGeneration || Volatile.Read(ref disposed) != 0) return;
                ClearPending();
                SetStatus(UiMessage.Format(UiLocale.Key("Startup window layout failed: {0}"), UiMessage.FromException(error)));
            }
            log.Warning(error, "[XASlave] Startup window layout failed.");
        }
    }

    private void StopChangedRequest(long expectedGeneration)
    {
        lock (sync)
        {
            if (generation != expectedGeneration || Volatile.Read(ref disposed) != 0) return;
            ClearPending();
            SetStatus(UiMessage.Authored("Startup window layout stopped because the window, lock or settings changed."));
        }
    }

    private bool TryReadReadyWindow(bool checkLock, out nint window)
    {
        window = 0;
        if (Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading || isOwnerDisposed()) return false;
        var candidate = readWindow();
        var gameWindow = GameWindow.Instance();
        if (candidate == 0 || gameWindow == null || gameWindow->WindowHandle != candidate || !IsWindow(candidate)) return false;
        var thread = GetWindowThreadProcessId(candidate, out var process);
        if (process != (uint)Environment.ProcessId || thread == 0 || thread != GetCurrentThreadId()
            || !IsWindowVisible(candidate) || IsIconic(candidate) || IsZoomed(candidate)
            || gameConfig.System.GetUInt("ScreenMode") != 0 || (checkLock && isMovementBlocked()))
            return false;
        window = candidate;
        return true;
    }

    private bool BeforeWrite(StartupRequest request, long expectedGeneration, nint expectedWindow, out UiMessage message)
    {
        message = UiMessage.Authored("Startup window layout stopped because the window, lock or settings changed.");
        return framework.IsInFrameworkUpdateThread && IsCurrent(request, expectedGeneration)
            && TryReadReadyWindow(true, out var window) && window == expectedWindow
            && IsCurrent(request, expectedGeneration);
    }

    private void Execute(StartupRequest request, long expectedGeneration, nint window)
    {
        var message = UiMessage.Authored("Could not enter the desktop pixel coordinate context.");
        var success = false;
        var previousContext = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousContext != 0)
        {
            try { success = ApplyCore(request, expectedGeneration, window, out message); }
            catch (Exception error)
            {
                message = UiMessage.Format(UiLocale.Key("Startup window layout failed: {0}"), UiMessage.FromException(error));
            }
            finally
            {
                if (SetThreadDpiAwarenessContext(previousContext) == 0)
                {
                    success = false;
                    message = UiMessage.Authored("Could not restore the previous window coordinate context.");
                }
            }
        }
        if (!IsCurrent(request, expectedGeneration))
        {
            StopChangedRequest(expectedGeneration);
            return;
        }
        SetStatus(message);
        if (success && !request.PlacementSkipped) log.Info("[XASlave] {Status}", message.Original);
        else log.Warning("[XASlave] {Status}", message.Original);
    }

    private bool ApplyCore(StartupRequest request, long expectedGeneration, nint window, out UiMessage message)
    {
        if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
        var fallback = false;
        if (request.Move)
        {
            if (!TryBuildPlan(request.Placement!, window, out var initialPlan))
            {
                message = UiMessage.Authored("Could not read the current window or monitor information.");
                return false;
            }
            fallback = initialPlan.UsedFallback;
            // Reach the target monitor before sizing, allowing the DPI transition to complete synchronously.
            if (!TryMove(request, expectedGeneration, window, initialPlan, out message)) return false;
        }

        if (request.Resize)
        {
            if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
            if (!windowMods.SetCustomResolutionsEnabled(true))
            {
                message = UiMessage.Authored("Could not enable custom resolutions for the startup layout.");
                return false;
            }
            if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
            if (request.IgnoreMinimum)
            {
                if (!windowMods.SetIgnoreMinimumWindowSizeEnabled(true))
                {
                    message = UiMessage.Authored("Could not lower the minimum window size for the startup layout.");
                    return false;
                }
                if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
            }
            if (!windowMods.TryApplyCustomResolutionMessage(request.Width, request.Height, out message)) return false;
            if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
            if (!GetClientRect(window, out var client) || (long)client.Right - client.Left != request.Width
                || (long)client.Bottom - client.Top != request.Height)
            {
                message = UiMessage.Authored("Startup resolution did not match the requested client size.");
                return false;
            }
        }

        if (request.Move)
        {
            // New DPI/client dimensions may change the outer frame. Clamp only once more, then verify.
            if (!TryBuildPlan(request.Placement!, window, out var finalPlan))
            {
                message = UiMessage.Authored("Could not read the current window or monitor information.");
                return false;
            }
            fallback |= finalPlan.UsedFallback;
            if (!TryMove(request, expectedGeneration, window, finalPlan, out message)) return false;
            if (!GetWindowRect(window, out var actual) || actual.Left != finalPlan.X || actual.Top != finalPlan.Y)
            {
                message = UiMessage.Authored("Startup window position did not match the requested position.");
                return false;
            }
        }
        if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
        if (request.Resize && (!GetClientRect(window, out var finalClient)
            || (long)finalClient.Right - finalClient.Left != request.Width
            || (long)finalClient.Bottom - finalClient.Top != request.Height))
        {
            message = UiMessage.Authored("Startup resolution did not match the requested client size.");
            return false;
        }
        message = request.PlacementSkipped
            ? UiMessage.Authored("Startup resolution applied; saved window placement is invalid and was skipped.")
            : fallback
                ? UiMessage.Authored("Startup window layout applied using the primary display because the saved display was unavailable.")
                : UiMessage.Authored("Startup window layout applied.");
        return true;
    }

    private bool TryMove(StartupRequest request, long expectedGeneration, nint window, StartupWindowPlan plan, out UiMessage message)
    {
        if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
        if (!GetWindowRect(window, out var current))
        {
            message = UiMessage.Authored("Could not read the current window or monitor information.");
            return false;
        }
        if (current.Left == plan.X && current.Top == plan.Y)
            return BeforeWrite(request, expectedGeneration, window, out message);
        if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
        if (!SetWindowPos(window, 0, plan.X, plan.Y, 0, 0, MoveFlags))
        {
            message = UiMessage.Format(UiLocale.Key("Could not move the game window (Windows error {0})."), Marshal.GetLastWin32Error());
            return false;
        }
        if (!BeforeWrite(request, expectedGeneration, window, out message)) return false;
        if (!GetWindowRect(window, out var actual) || actual.Left != plan.X || actual.Top != plan.Y)
        {
            message = UiMessage.Authored("Startup window position did not match the requested position.");
            return false;
        }
        return BeforeWrite(request, expectedGeneration, window, out message);
    }

    private static bool TryBuildPlan(StartupWindowPlacement saved, nint window, out StartupWindowPlan plan)
    {
        plan = default;
        if (!GetWindowRect(window, out var bounds) || !TryReadMonitors(out var monitors)) return false;
        var width = (long)bounds.Right - bounds.Left;
        var height = (long)bounds.Bottom - bounds.Top;
        return width > 0 && width <= int.MaxValue && height > 0 && height <= int.MaxValue
            && StartupWindowPlacementGeometry.TryPlan(saved, monitors, (int)width, (int)height, out plan);
    }

    internal bool TryCapture(out StartupWindowPlacement placement, out UiMessage message)
    {
        placement = new();
        message = UiMessage.Authored("Startup window placement requires a normal window on its owning thread.");
        nint previousContext = 0;
        var success = false;
        try
        {
            if (!TryReadReadyWindow(false, out var window)) return false;
            previousContext = SetThreadDpiAwarenessContext(new nint(-4));
            if (previousContext == 0)
            {
                message = UiMessage.Authored("Could not enter the desktop pixel coordinate context.");
                return false;
            }
            var monitor = MonitorFromWindow(window, 2);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
            if (!TryReadReadyWindow(false, out var current) || current != window || !GetWindowRect(window, out var bounds)
                || monitor == 0 || !GetMonitorInfo(monitor, ref info))
                message = UiMessage.Authored("Could not read the current window or monitor information.");
            else
            {
                placement = new()
                {
                    MonitorDeviceName = info.DeviceName, X = bounds.Left, Y = bounds.Top,
                    MonitorWorkLeft = info.Work.Left, MonitorWorkTop = info.Work.Top,
                };
                success = StartupWindowPlacementGeometry.IsValid(placement)
                    && TryReadReadyWindow(false, out current) && current == window;
                message = success
                    ? UiMessage.Format(UiLocale.Key("Captured window position {0}, {1} on {2}."), placement.X, placement.Y, placement.MonitorDeviceName)
                    : UiMessage.Authored("Startup window placement is invalid.");
            }
        }
        catch (Exception error)
        {
            success = false;
            message = UiMessage.Format(UiLocale.Key("Startup window layout failed: {0}"), UiMessage.FromException(error));
        }
        finally
        {
            if (previousContext != 0 && SetThreadDpiAwarenessContext(previousContext) == 0)
            {
                success = false;
                message = UiMessage.Authored("Could not restore the previous window coordinate context.");
            }
        }
        if (!success) placement = new();
        return success;
    }

    private static bool TryReadMonitors(out List<StartupWindowMonitor> monitors)
    {
        var collected = new List<StartupWindowMonitor>();
        var failed = false;
        MonitorCallback callback = (nint monitor, nint _, ref NativeRect bounds, nint data) =>
        {
            try
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
                if (collected.Count >= 64 || !GetMonitorInfo(monitor, ref info)) { failed = true; return false; }
                collected.Add(new(info.DeviceName, new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), (info.Flags & 1) != 0));
                return true;
            }
            catch { failed = true; return false; }
        };
        var enumerated = EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        monitors = collected;
        return enumerated && !failed && collected.Count > 0;
    }

    private void SetStatus(UiMessage message) => Volatile.Write(ref status, message);

    public void Dispose()
    {
        lock (sync)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            startupAdmissionClosed = true;
            Interlocked.Increment(ref generation);
            ClearPending();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint data);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
