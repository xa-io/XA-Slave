using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class AutoLockGameWindowService : IDisposable
{
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private bool enabled;
    private bool subscribed;

    public AutoLockGameWindowService(ICondition condition, IPluginLog log)
    {
        this.condition = condition;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public bool IsLocked => WindowLock.IsLocked;

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            WindowLock.UnlockCurrentWindow();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        ApplyCombatState(condition[ConditionFlag.InCombat]);
        StatusText = "Enabled - the game window is locked in place while the local player is in combat.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
        WindowLock.Cleanup();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        condition.ConditionChange += OnConditionChange;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        condition.ConditionChange -= OnConditionChange;
        subscribed = false;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!enabled || flag != ConditionFlag.InCombat)
            return;

        ApplyCombatState(value);
    }

    private void ApplyCombatState(bool inCombat)
    {
        try
        {
            if (inCombat)
            {
                WindowLock.LockCurrentWindow();
                LastActionText = $"Last action: locked the game window at {DateTime.Now:HH:mm:ss}.";
            }
            else
            {
                WindowLock.UnlockCurrentWindow();
                LastActionText = $"Last action: unlocked the game window at {DateTime.Now:HH:mm:ss}.";
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Lock Game Window In Combat failed while changing the window lock state.");
        }
    }

    private static class WindowLock
    {
        private const int GwlWndProc = -4;
        private const int WmWindowPosChanging = 0x0046;
        private const uint SwpNoMove = 0x0002;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<nint, nint> OriginalWindowProcs = [];
        private static readonly Dictionary<nint, WindowProcDelegate> Delegates = [];

        public static bool IsLocked => OriginalWindowProcs.Count > 0;

        public static void LockCurrentWindow()
        {
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle == nint.Zero)
                return;

            lock (SyncRoot)
            {
                if (OriginalWindowProcs.ContainsKey(handle))
                    return;

                var newProc = new WindowProcDelegate(WindowProc);
                var newProcPtr = Marshal.GetFunctionPointerForDelegate(newProc);
                var oldProc = SetWindowLongPtr(handle, GwlWndProc, newProcPtr);
                if (oldProc == nint.Zero && Marshal.GetLastWin32Error() != 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to subclass the game window.");

                OriginalWindowProcs[handle] = oldProc;
                Delegates[handle] = newProc;
            }
        }

        public static void UnlockCurrentWindow()
        {
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle == nint.Zero)
                return;

            lock (SyncRoot)
            {
                if (!OriginalWindowProcs.TryGetValue(handle, out var oldProc))
                    return;

                SetWindowLongPtr(handle, GwlWndProc, oldProc);
                OriginalWindowProcs.Remove(handle);
                Delegates.Remove(handle);
            }
        }

        public static void Cleanup()
        {
            foreach (var handle in OriginalWindowProcs.Keys.ToList())
                SetWindowLongPtr(handle, GwlWndProc, OriginalWindowProcs[handle]);

            OriginalWindowProcs.Clear();
            Delegates.Clear();
        }

        private static nint WindowProc(nint hWnd, uint message, nint wParam, nint lParam)
        {
            // Resolve the saved original proc under the lock. UnlockCurrentWindow/Cleanup can
            // remove this entry while a window message is still in flight; indexing the dictionary
            // directly (the old code) would then throw a KeyNotFoundException *inside a native
            // window procedure*, which terminates the game process.
            nint originalProc;
            lock (SyncRoot)
            {
                if (!OriginalWindowProcs.TryGetValue(hWnd, out originalProc))
                    originalProc = nint.Zero;
            }

            try
            {
                if (message == WmWindowPosChanging)
                {
                    var windowPos = Marshal.PtrToStructure<WindowPos>(lParam);
                    if ((windowPos.Flags & SwpNoMove) == 0)
                    {
                        GetWindowRect(hWnd, out var rect);
                        windowPos.X = rect.Left;
                        windowPos.Y = rect.Top;
                        windowPos.Flags |= SwpNoMove;
                        Marshal.StructureToPtr(windowPos, lParam, true);
                    }
                }
            }
            catch
            {
                // Never let a managed exception escape into the native window procedure.
            }

            if (originalProc != nint.Zero)
                return CallWindowProc(originalProc, hWnd, message, wParam, lParam);

            // The window was un-subclassed while this message was in flight and we no longer have
            // the original proc; hand the message to the default handler instead of crashing.
            return DefWindowProc(hWnd, message, wParam, lParam);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc);

        [DllImport("user32.dll")]
        private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern nint DefWindowProc(nint hWnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint hWnd, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPos
        {
            public nint Hwnd;
            public nint HwndInsertAfter;
            public int X;
            public int Y;
            public int Cx;
            public int Cy;
            public uint Flags;
        }

        private delegate nint WindowProcDelegate(nint hWnd, uint message, nint wParam, nint lParam);
    }
}
