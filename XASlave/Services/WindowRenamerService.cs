using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// Renames the FFXIV game window title using Win32 SetWindowText.
/// Finds the game window via Process.GetCurrentProcess().MainWindowHandle
/// with a current-process-scoped FindWindowEx("FFXIVGAME") fallback.
/// Yields the exact native title while XIVWindowResizer is loaded because
/// that plugin's initial handle lookup depends on the native window title.
/// Restores original title ("FINAL FANTASY XIV") on disable/dispose.
/// </summary>
public sealed class WindowRenamerService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr hWndParent,
        IntPtr hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public const string NativeGameWindowTitle = "FINAL FANTASY XIV";

    private const string GameWindowClass = "FFXIVGAME";
    private const string XivWindowResizerInternalName = "xivWindowResizer";
    private const string XivWindowResizerDisplayName = "XIVWindowResizer";
    private const int MaxNativeTitleRestoreAttempts = 3;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<Configuration> getConfiguration;
    private bool isRenamed;
    private int compatibilityRefreshQueued;
    private int nativeTitleRestoreAttempts;
    private int windowRenamerEnabled;
    private int disposed;

    public WindowRenamerService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IPluginLog log,
        Func<Configuration> getConfiguration)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
        this.getConfiguration = getConfiguration;

        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    public bool IsXIVWindowResizerCompatibilityActive { get; private set; }
    public bool IsXIVWindowResizerNativeTitleConfirmed { get; private set; }
    public bool HasXIVWindowResizerCompatibilityRefreshError { get; private set; }

    /// <summary>
    /// Attempts to find the FFXIV game window handle.
    /// Primary: Process.GetCurrentProcess().MainWindowHandle
    /// Fallback: enumerate FFXIVGAME windows and accept only the current process.
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
            var previousWindow = IntPtr.Zero;
            var currentProcessId = (uint)Environment.ProcessId;
            while (true)
            {
                var candidate = FindWindowEx(IntPtr.Zero, previousWindow, GameWindowClass, null);
                if (candidate == IntPtr.Zero || candidate == previousWindow)
                    break;

                _ = GetWindowThreadProcessId(candidate, out var candidateProcessId);
                if (candidateProcessId == currentProcessId && IsWindowVisible(candidate))
                {
                    hwnd = candidate;
                    return true;
                }

                previousWindow = candidate;
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[XASlave] WindowRenamer: PID-scoped window fallback failed: {ex.Message}");
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
        RestoreNativeTitle(force: false);
    }

    private bool RestoreNativeTitle(bool force)
    {
        if (!force && !isRenamed)
            return true;

        if (!TryGetGameWindow(out var hwnd))
            return false;

        try
        {
            if (SetWindowText(hwnd, NativeGameWindowTitle))
            {
                isRenamed = false;
                return true;
            }

            var err = Marshal.GetLastWin32Error();
            log.Error($"[XASlave] WindowRenamer: Failed to restore native title (Win32 error {err})");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"[XASlave] WindowRenamer: Failed to restore native title: {ex.Message}");
            return false;
        }
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
        ApplyFromConfigCore(config, currentCharacterNameOverride, isCompatibilityRetry: false);
    }

    private void ApplyFromConfigCore(
        Configuration config,
        string? currentCharacterNameOverride,
        bool isCompatibilityRetry)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        HasXIVWindowResizerCompatibilityRefreshError = false;
        Interlocked.Exchange(ref windowRenamerEnabled, config.WindowRenamerEnabled ? 1 : 0);
        if (!config.WindowRenamerEnabled)
        {
            IsXIVWindowResizerCompatibilityActive = false;
            IsXIVWindowResizerNativeTitleConfirmed = false;
            Interlocked.Exchange(ref nativeTitleRestoreAttempts, 0);
            Restore();
            return;
        }

        if (IsXIVWindowResizerLoaded())
        {
            if (!isCompatibilityRetry)
                Interlocked.Exchange(ref nativeTitleRestoreAttempts, 0);

            var compatibilityWasActive = IsXIVWindowResizerCompatibilityActive;
            IsXIVWindowResizerCompatibilityActive = true;
            IsXIVWindowResizerNativeTitleConfirmed = RestoreNativeTitle(force: true);

            if (IsXIVWindowResizerNativeTitleConfirmed)
            {
                Interlocked.Exchange(ref nativeTitleRestoreAttempts, 0);
                if (!compatibilityWasActive)
                {
                    log.Information(
                        "[XASlave] WindowRenamer: Paused custom title while XIVWindowResizer is loaded; confirmed the native game-window title.");
                }
            }
            else
            {
                var attempt = Interlocked.Increment(ref nativeTitleRestoreAttempts);
                log.Warning(
                    $"[XASlave] WindowRenamer: Couldn't confirm the native title for XIVWindowResizer (attempt {attempt}/{MaxNativeTitleRestoreAttempts}).");
                if (attempt < MaxNativeTitleRestoreAttempts)
                    QueueCompatibilityRefresh(isCompatibilityRetry: true);
            }

            return;
        }

        if (IsXIVWindowResizerCompatibilityActive)
        {
            log.Information(
                "[XASlave] WindowRenamer: XIVWindowResizer unloaded; reapplying the saved custom title.");
        }

        IsXIVWindowResizerCompatibilityActive = false;
        IsXIVWindowResizerNativeTitleConfirmed = false;
        Interlocked.Exchange(ref nativeTitleRestoreAttempts, 0);
        var title = string.IsNullOrWhiteSpace(config.WindowRenamerTitle)
            ? NativeGameWindowTitle
            : config.WindowRenamerTitle;
        Rename(title, config.WindowRenamerUseProcessId, config.WindowRenamerShowCurrentCharacter, currentCharacterNameOverride);
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

    private bool IsXIVWindowResizerLoaded()
    {
        try
        {
            return pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded
                && (IsXIVWindowResizerIdentifier(plugin.InternalName)
                    || IsXIVWindowResizerIdentifier(plugin.Name)));
        }
        catch (Exception ex)
        {
            log.Warning(
                $"[XASlave] WindowRenamer: Couldn't read the active plugin list; retaining the native title for safety: {ex.Message}");
            return true;
        }
    }

    private static bool IsXIVWindowResizerIdentifier(string? value)
    {
        return string.Equals(value, XivWindowResizerInternalName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, XivWindowResizerDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        try
        {
            if (!args.AffectedInternalNames.Any(IsXIVWindowResizerIdentifier))
                return;

            Interlocked.Exchange(ref nativeTitleRestoreAttempts, 0);
        }
        catch (Exception ex)
        {
            log.Warning(
                $"[XASlave] WindowRenamer: Couldn't inspect the changed plugin names; scheduling a compatibility refresh for safety: {ex.Message}");
        }

        QueueCompatibilityRefresh(isCompatibilityRetry: false);
    }

    private void QueueCompatibilityRefresh(bool isCompatibilityRetry)
    {
        if (Interlocked.Exchange(ref compatibilityRefreshQueued, 1) != 0)
            return;

        try
        {
            framework.RunOnTick(() =>
            {
                Interlocked.Exchange(ref compatibilityRefreshQueued, 0);
                if (Volatile.Read(ref disposed) != 0)
                    return;

                try
                {
                    ApplyFromConfigCore(getConfiguration(), null, isCompatibilityRetry);
                }
                catch (Exception ex)
                {
                    log.Error($"[XASlave] WindowRenamer: Compatibility refresh failed: {ex.Message}");
                }
            }, delayTicks: 1);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref compatibilityRefreshQueued, 0);
            log.Error($"[XASlave] WindowRenamer: Couldn't schedule a compatibility refresh: {ex.Message}");

            if (Volatile.Read(ref disposed) == 0
                && Volatile.Read(ref windowRenamerEnabled) != 0)
            {
                HasXIVWindowResizerCompatibilityRefreshError = true;
                IsXIVWindowResizerNativeTitleConfirmed = RestoreNativeTitle(force: true);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        Interlocked.Exchange(ref compatibilityRefreshQueued, 0);
        Interlocked.Exchange(ref nativeTitleRestoreAttempts, 0);
        Interlocked.Exchange(ref windowRenamerEnabled, 0);
        IsXIVWindowResizerCompatibilityActive = false;
        IsXIVWindowResizerNativeTitleConfirmed = false;
        HasXIVWindowResizerCompatibilityRefreshError = false;
        Restore();
    }
}
