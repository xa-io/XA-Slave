using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// Renames the FFXIV game window title using Win32 SetWindowText.
/// Finds the game window via Process.GetCurrentProcess().MainWindowHandle
/// with FindWindow("FFXIVGAME") fallback.
/// Restores original title ("FINAL FANTASY XIV") on disable/dispose.
/// </summary>
public sealed class WindowRenamerService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    private const string DefaultTitle = "FINAL FANTASY XIV";
    private const string GameWindowClass = "FFXIVGAME";

    private readonly IPluginLog log;
    private bool isRenamed;

    public WindowRenamerService(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Attempts to find the FFXIV game window handle.
    /// Primary: Process.GetCurrentProcess().MainWindowHandle
    /// Fallback: FindWindow with FFXIVGAME class name.
    /// </summary>
    public bool TryGetGameWindow(out IntPtr hwnd)
    {
        try
        {
            hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd != IntPtr.Zero)
                return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] WindowRenamer: Process.MainWindowHandle failed: {ex.Message}");
        }

        try
        {
            hwnd = FindWindow(GameWindowClass, null);
            if (hwnd != IntPtr.Zero)
                return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] WindowRenamer: FindWindow fallback failed: {ex.Message}");
        }

        hwnd = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Renames the game window to the specified title.
    /// If enabled, prepends the process ID and/or appends the current character name.
    /// </summary>
    public bool Rename(string title, bool useProcessId, bool showCurrentCharacter, string? currentCharacterNameOverride = null)
    {
        var finalTitle = BuildFinalTitle(title, useProcessId, showCurrentCharacter, currentCharacterNameOverride);

        if (!TryGetGameWindow(out var hwnd))
        {
            log.Error("[XASlave] WindowRenamer: Couldn't find game window!");
            return false;
        }

        try
        {
            if (SetWindowText(hwnd, finalTitle))
            {
                isRenamed = true;
                return true;
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                log.Error($"[XASlave] WindowRenamer: SetWindowText failed (Win32 error {err})");
                return false;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[XASlave] WindowRenamer: Exception during rename: {ex.Message}");
            return false;
        }
    }

    public string BuildPreviewTitle(string title, bool useProcessId, bool showCurrentCharacter, string? currentCharacterNameOverride = null)
    {
        return BuildFinalTitle(title, useProcessId, showCurrentCharacter, currentCharacterNameOverride);
    }

    /// <summary>
    /// Restores the game window to the default "FINAL FANTASY XIV" title.
    /// </summary>
    public void Restore()
    {
        if (!isRenamed) return;

        if (TryGetGameWindow(out var hwnd))
        {
            try
            {
                SetWindowText(hwnd, DefaultTitle);
            }
            catch (Exception ex)
            {
                log.Error($"[XASlave] WindowRenamer: Failed to restore title: {ex.Message}");
            }
        }

        isRenamed = false;
    }

    /// <summary>
    /// Apply the rename using the current configuration settings.
    /// Called on plugin load (if enabled) and when settings change.
    /// </summary>
    public void ApplyFromConfig(Configuration config)
    {
        ApplyFromConfig(config, null);
    }

    public void ApplyFromConfig(Configuration config, string? currentCharacterNameOverride)
    {
        if (config.WindowRenamerEnabled)
        {
            var title = string.IsNullOrWhiteSpace(config.WindowRenamerTitle)
                ? DefaultTitle
                : config.WindowRenamerTitle;
            Rename(title, config.WindowRenamerUseProcessId, config.WindowRenamerShowCurrentCharacter, currentCharacterNameOverride);
        }
        else
        {
            Restore();
        }
    }

    private static string BuildFinalTitle(string title, bool useProcessId, bool showCurrentCharacter, string? currentCharacterNameOverride)
    {
        var parts = new List<string>();
        if (useProcessId)
            parts.Add(Environment.ProcessId.ToString());

        parts.Add(title);

        if (showCurrentCharacter)
        {
            var currentCharacterName = ResolveCurrentCharacterName(currentCharacterNameOverride);
            if (!string.IsNullOrWhiteSpace(currentCharacterName))
                parts.Add(currentCharacterName);
        }

        return string.Join(" - ", parts);
    }

    private static string ResolveCurrentCharacterName(string? currentCharacterNameOverride)
    {
        if (currentCharacterNameOverride != null)
            return currentCharacterNameOverride.Trim();

        var playerState = Plugin.PlayerState;
        if (!playerState.IsLoaded)
            return string.Empty;

        var currentCharacterName = playerState.CharacterName.ToString();
        return string.IsNullOrWhiteSpace(currentCharacterName)
            ? string.Empty
            : currentCharacterName;
    }

    public void Dispose()
    {
        Restore();
    }
}
