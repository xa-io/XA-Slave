using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace XASlave.Services;

/// <summary>
/// Sends keyboard input via Win32 keybd_event.
/// Replaces SomethingNeedDoing's /send, /hold, /release commands which are not
/// available in a Dalamud plugin context.
///
/// Methods:
///   PressKey(vk)                   — tap key (down + up immediately)     → SND: /send KEY
///   HoldKey(vk)                    — key down only (must release later)  → SND: /hold KEY
///   ReleaseKey(vk)                 — key up only                         → SND: /release KEY
///   HoldKeyForDuration(vk, ms)     — hold key, auto-release after ms     → SND: /hold KEY + /wait + /release KEY
///
/// Available VK Constants (use with any method above):
///   Movement:  VK_W (0x57), VK_A (0x41), VK_S (0x53), VK_D (0x44)
///   Function:  VK_F1–VK_F12 (0x70–0x7B)
///   Numpad:    VK_NUMPAD0–VK_NUMPAD9 (0x60–0x69)
///   Special:   VK_END (0x23), VK_HOME (0x24), VK_ESCAPE (0x1B), VK_RETURN (0x0D)
///              VK_SPACE (0x20), VK_TAB (0x09), VK_DELETE (0x2E), VK_INSERT (0x2D)
///   Arrow:     VK_LEFT (0x25), VK_UP (0x26), VK_RIGHT (0x27), VK_DOWN (0x28)
///   Modifier:  VK_SHIFT (0x10), VK_CONTROL (0x11), VK_MENU/ALT (0x12)
///   Letters:   0x41–0x5A (A–Z)
///   Numbers:   0x30–0x39 (0–9)
/// </summary>
public static class KeyInputHelper
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const string GameWindowClass = "FFXIVGAME";

    // ── Movement Keys ──
    public const byte VK_W = 0x57;
    public const byte VK_A = 0x41;
    public const byte VK_S = 0x53;
    public const byte VK_D = 0x44;

    // ── Special Keys ──
    public const byte VK_END = 0x23;
    public const byte VK_HOME = 0x24;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_SPACE = 0x20;
    public const byte VK_TAB = 0x09;
    public const byte VK_DELETE = 0x2E;
    public const byte VK_INSERT = 0x2D;

    // ── Arrow Keys ──
    public const byte VK_LEFT = 0x25;
    public const byte VK_UP = 0x26;
    public const byte VK_RIGHT = 0x27;
    public const byte VK_DOWN = 0x28;

    // ── Modifier Keys ──
    public const byte VK_SHIFT = 0x10;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_ALT = 0x12;

    // ── Numpad Keys ──
    public const byte VK_NUMPAD0 = 0x60;
    public const byte VK_NUMPAD1 = 0x61;
    public const byte VK_NUMPAD2 = 0x62;
    public const byte VK_NUMPAD3 = 0x63;
    public const byte VK_NUMPAD4 = 0x64;
    public const byte VK_NUMPAD5 = 0x65;
    public const byte VK_NUMPAD6 = 0x66;
    public const byte VK_NUMPAD7 = 0x67;
    public const byte VK_NUMPAD8 = 0x68;
    public const byte VK_NUMPAD9 = 0x69;

    // ── Function Keys ──
    public const byte VK_F1 = 0x70;
    public const byte VK_F2 = 0x71;
    public const byte VK_F3 = 0x72;
    public const byte VK_F4 = 0x73;
    public const byte VK_F5 = 0x74;
    public const byte VK_F6 = 0x75;
    public const byte VK_F7 = 0x76;
    public const byte VK_F8 = 0x77;
    public const byte VK_F9 = 0x78;
    public const byte VK_F10 = 0x79;
    public const byte VK_F11 = 0x7A;
    public const byte VK_F12 = 0x7B;

    private static bool TryGetGameWindow(out IntPtr hwnd)
    {
        try
        {
            hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd != IntPtr.Zero)
                return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[XASlave] KeyInput: Process.MainWindowHandle failed: {ex.Message}");
        }

        try
        {
            var currentProcessId = (uint)Environment.ProcessId;
            var previous = IntPtr.Zero;

            while (true)
            {
                previous = FindWindowEx(IntPtr.Zero, previous, GameWindowClass, null);
                if (previous == IntPtr.Zero)
                    break;

                _ = GetWindowThreadProcessId(previous, out var windowProcessId);
                if (windowProcessId == currentProcessId)
                {
                    hwnd = previous;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[XASlave] KeyInput: FindWindowEx fallback failed: {ex.Message}");
        }

        hwnd = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Presses and immediately releases a key (tap).
    /// Equivalent to SND's: /send KEY
    /// </summary>
    public static void PressKey(byte virtualKeyCode)
    {
        try
        {
            if (!TryGetGameWindow(out var hwnd))
            {
                Plugin.Log.Error("[XASlave] KeyInput.PressKey failed: Couldn't find game window!");
                return;
            }

            _ = SendMessage(hwnd, WM_KEYDOWN, new UIntPtr(virtualKeyCode), IntPtr.Zero);
            _ = SendMessage(hwnd, WM_KEYUP, new UIntPtr(virtualKeyCode), IntPtr.Zero);
            Plugin.Log.Information($"[XASlave] KeyInput: PressKey 0x{virtualKeyCode:X2}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] KeyInput.PressKey failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Holds a key down (key-down event only).
    /// Equivalent to SND's: /hold KEY
    /// IMPORTANT: Must call ReleaseKey() later to release.
    /// </summary>
    public static void HoldKey(byte virtualKeyCode)
    {
        try
        {
            if (!TryGetGameWindow(out var hwnd))
            {
                Plugin.Log.Error("[XASlave] KeyInput.HoldKey failed: Couldn't find game window!");
                return;
            }

            _ = SendMessage(hwnd, WM_KEYDOWN, new UIntPtr(virtualKeyCode), IntPtr.Zero);
            Plugin.Log.Information($"[XASlave] KeyInput: HoldKey 0x{virtualKeyCode:X2}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] KeyInput.HoldKey failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases a held key (key-up event only).
    /// Equivalent to SND's: /release KEY
    /// </summary>
    public static void ReleaseKey(byte virtualKeyCode)
    {
        try
        {
            if (!TryGetGameWindow(out var hwnd))
            {
                Plugin.Log.Error("[XASlave] KeyInput.ReleaseKey failed: Couldn't find game window!");
                return;
            }

            _ = SendMessage(hwnd, WM_KEYUP, new UIntPtr(virtualKeyCode), IntPtr.Zero);
            Plugin.Log.Information($"[XASlave] KeyInput: ReleaseKey 0x{virtualKeyCode:X2}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] KeyInput.ReleaseKey failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Holds a key down, then automatically releases it after the specified duration.
    /// Equivalent to SND's: /hold KEY → /wait N → /release KEY
    /// Fire-and-forget — returns immediately, release happens on a background thread.
    /// </summary>
    public static void HoldKeyForDuration(byte virtualKeyCode, int durationMs)
    {
        HoldKey(virtualKeyCode);
        Task.Run(async () =>
        {
            await Task.Delay(durationMs);
            ReleaseKey(virtualKeyCode);
            Plugin.Log.Information($"[XASlave] KeyInput: Auto-released 0x{virtualKeyCode:X2} after {durationMs}ms");
        });
    }
}
