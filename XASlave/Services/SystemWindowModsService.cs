using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class SystemWindowModsService : IDisposable
{
    private const int SafeMinimumWindowWidth = 250;
    private const int SafeMinimumWindowHeight = 200;
    private const int MaximumCustomWindowWidth = 16384;
    private const int MaximumCustomWindowHeight = 16384;
    private const int WindowSizeSyncDebounceMilliseconds = 150;
    private const int AutoRetainerMultiModeCacheMilliseconds = 1000;
    private const int WindowStyleIndex = -16;
    private const int WindowExStyleIndex = -20;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const float MinimumLowResolutionScale = 0.01f;
    private const float MaximumLowResolutionScale = 1.00f;
    private const byte MaximumSupportedLowResolutionUpscaleType = 1;
    private const byte LowResolutionFallbackUpscaleType = 1;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly IGameConfig gameConfig;
    private readonly IClientState clientState;
    private readonly Func<bool> isAutoRetainerMultiModeEnabled;

    private Hook<AgentLobbyUpdateDelegate>? agentLobbyUpdateHook;
    private Hook<AtkMessageBoxReceiveEventDelegate>? atkMessageBoxReceiveEventHook;
    private Hook<AgentMapUpdateDelegate>? agentMapUpdateHook;
    private Hook<DeviceDx11PostTickDelegate>? deviceDx11PostTickHook;
    private Hook<NamePlateDrawDelegate>? namePlateDrawHook;
    private ToggleFadeDelegate? toggleFadeDelegate;

    private bool initialized;
    private bool cancelLoginCooldownEnabled;
    private bool customResolutionsEnabled;
    private bool ignoreMinimumWindowSizeEnabled;
    private bool lowResolutionEnabled;
    private bool capturedLowResolutionUpscaleType;
    private bool preventLobbyExitEnabled;
    private bool revealUndiscoveredAreasEnabled;
    private bool disableBackgroundRenderingEnabled;
    private bool disableBackgroundRenderingOnlyWhenMinimized;
    private bool disableBackgroundRenderingDisableWhenArMultiIsOn;
    private bool backgroundRenderingSuppressed;
    private bool capturedWindowSize;
    private int originalMinWidth;
    private int originalMinHeight;
    private long nextBackgroundRenderTick;
    private long autoRetainerMultiModeCacheExpiresAt;
    private bool cachedAutoRetainerMultiModeEnabled;
    private bool hasObservedClientSize;
    private bool pendingWindowSizeSynchronization;
    private int lastObservedClientWidth;
    private int lastObservedClientHeight;
    private int lastSynchronizedClientWidth;
    private int lastSynchronizedClientHeight;
    private long lastWindowSizeChangeTick;
    private int lastAppliedCustomResolutionWidth;
    private int lastAppliedCustomResolutionHeight;
    private byte originalLowResolutionUpscaleType;
    private float lowResolutionScale = 0.25f;

    public SystemWindowModsService(
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log,
        IFramework framework,
        IGameConfig gameConfig,
        IClientState clientState,
        Func<bool> isAutoRetainerMultiModeEnabled)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
        this.framework = framework;
        this.gameConfig = gameConfig;
        this.clientState = clientState;
        this.isAutoRetainerMultiModeEnabled = isAutoRetainerMultiModeEnabled;

        this.framework.Update += OnFrameworkUpdate;
    }

    public string AllowMultipleGameInstancesStatusText { get; private set; } = "Disabled";

    public string CancelLoginCooldownStatusText { get; private set; } = "Disabled";

    public string CustomResolutionsStatusText { get; private set; } = "Disabled";

    public string IgnoreMinimumWindowSizeStatusText { get; private set; } = "Disabled";

    public string LowResolutionStatusText { get; private set; } = "Disabled";

    public string PreventLobbyExitStatusText { get; private set; } = "Disabled";

    public string RevealUndiscoveredAreasStatusText { get; private set; } = "Disabled";

    public string DisableBackgroundRenderingStatusText { get; private set; } = "Disabled";

    public string SpecialRenderModesStatusText => toggleFadeDelegate != null
        ? "Ready - render and UI mode controls are available."
        : "Unavailable - special render delegate is missing.";

    public static float ClampLowResolutionScale(float value)
    {
        return Math.Clamp(value, MinimumLowResolutionScale, MaximumLowResolutionScale);
    }

    public static bool TryNormalizeCustomResolution(int width, int height, out int normalizedWidth, out int normalizedHeight, out string message)
    {
        normalizedWidth = width;
        normalizedHeight = height;

        if (width < SafeMinimumWindowWidth || height < SafeMinimumWindowHeight)
        {
            message = $"Custom resolutions must be at least {SafeMinimumWindowWidth}x{SafeMinimumWindowHeight}.";
            return false;
        }

        if (width > MaximumCustomWindowWidth || height > MaximumCustomWindowHeight)
        {
            message = $"Custom resolutions must be at most {MaximumCustomWindowWidth}x{MaximumCustomWindowHeight}.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public bool SetAllowMultipleGameInstancesEnabled(bool value)
    {
        if (!value)
        {
            AllowMultipleGameInstancesStatusText = "Disabled";
            return false;
        }

        try
        {
            var closedHandles = ReleaseMultipleGameInstanceHandles();
            AllowMultipleGameInstancesStatusText = closedHandles > 0
                ? $"Enabled - cleared {closedHandles} multi-instance launch lock handle(s) for this client process."
                : "Enabled - no additional multi-instance launch lock handle was present in this client process.";
            return true;
        }
        catch (Exception ex)
        {
            AllowMultipleGameInstancesStatusText = "Unavailable - failed while clearing the multi-instance launch lock.";
            log.Warning(ex, "[XASlave] Failed to clear the multi-instance launch lock.");
            return false;
        }
    }

    public bool SetCancelLoginCooldownEnabled(bool value)
    {
        if (!value)
        {
            cancelLoginCooldownEnabled = false;
            UpdateAgentLobbyHookState();
            CancelLoginCooldownStatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (agentLobbyUpdateHook == null)
        {
            CancelLoginCooldownStatusText = "Unavailable - AgentLobby update hook missing.";
            return false;
        }

        cancelLoginCooldownEnabled = true;
        UpdateAgentLobbyHookState();
        CancelLoginCooldownStatusText = "Enabled - character-select login cooldown is cleared locally.";
        return true;
    }

    public bool SetCustomResolutionsEnabled(bool value)
    {
        if (!value)
        {
            customResolutionsEnabled = false;
            lastAppliedCustomResolutionWidth = 0;
            lastAppliedCustomResolutionHeight = 0;
            if (!ShouldSynchronizeWindowSize())
                ResetWindowSizeSynchronizationState();

            RefreshWindowSizeLimits();
            CustomResolutionsStatusText = "Disabled";
            return false;
        }

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero)
        {
            CustomResolutionsStatusText = "Unavailable - game window surface missing.";
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        customResolutionsEnabled = true;
        RefreshWindowSizeLimits(gameWindow);
        PrimeWindowSizeSynchronization(gameWindow);
        CustomResolutionsStatusText = "Enabled - preset buttons and `/xa res <width>x<height>` can change the client size locally.";
        return true;
    }

    public bool SetIgnoreMinimumWindowSizeEnabled(bool value)
    {
        if (!value)
        {
            ignoreMinimumWindowSizeEnabled = false;
            if (!ShouldSynchronizeWindowSize())
                ResetWindowSizeSynchronizationState();

            RefreshWindowSizeLimits();
            IgnoreMinimumWindowSizeStatusText = "Disabled";
            return false;
        }

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null)
        {
            IgnoreMinimumWindowSizeStatusText = "Unavailable - game window surface missing.";
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        ignoreMinimumWindowSizeEnabled = true;
        RefreshWindowSizeLimits(gameWindow);
        PrimeWindowSizeSynchronization(gameWindow);
        IgnoreMinimumWindowSizeStatusText = GetIgnoreMinimumWindowSizeStatusText();
        return true;
    }

    public void ApplyLowResolutionConfiguration(float scale)
    {
        lowResolutionScale = ClampLowResolutionScale(scale);
        if (lowResolutionEnabled)
            UpdateLowResolutionScale();
    }

    public bool SetLowResolutionEnabled(bool value)
    {
        if (!value)
        {
            lowResolutionEnabled = false;
            RestoreLowResolutionConfiguration();
            LowResolutionStatusText = "Disabled";
            return false;
        }

        var graphicsConfig = GraphicsConfig.Instance();
        if (graphicsConfig == null)
        {
            LowResolutionStatusText = "Unavailable - graphics configuration surface missing.";
            return false;
        }

        lowResolutionEnabled = true;
        UpdateLowResolutionScale();
        return true;
    }

    public bool SetPreventLobbyExitEnabled(bool value)
    {
        if (!value)
        {
            preventLobbyExitEnabled = false;
            UpdateAtkMessageBoxHookState();
            PreventLobbyExitStatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (atkMessageBoxReceiveEventHook == null)
        {
            PreventLobbyExitStatusText = "Unavailable - lobby error hook missing.";
            return false;
        }

        preventLobbyExitEnabled = true;
        UpdateAtkMessageBoxHookState();
        PreventLobbyExitStatusText = "Enabled - lobby shutdown timeout is overridden.";
        return true;
    }

    public bool SetRevealUndiscoveredAreasEnabled(bool value)
    {
        if (!value)
        {
            revealUndiscoveredAreasEnabled = false;
            UpdateAgentMapHookState();
            RevealUndiscoveredAreasStatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (agentMapUpdateHook == null)
        {
            RevealUndiscoveredAreasStatusText = "Unavailable - map update hook missing.";
            return false;
        }

        revealUndiscoveredAreasEnabled = true;
        UpdateAgentMapHookState();
        RevealUndiscoveredAreasStatusText = "Enabled - local map discovery is cleared.";
        return true;
    }

    public void SetDisableBackgroundRenderingOnlyWhenMinimized(bool value)
    {
        disableBackgroundRenderingOnlyWhenMinimized = value;
        if (disableBackgroundRenderingEnabled)
            DisableBackgroundRenderingStatusText = GetBackgroundRenderingStatusText();
    }

    public void SetDisableBackgroundRenderingDisableWhenArMultiIsOn(bool value)
    {
        disableBackgroundRenderingDisableWhenArMultiIsOn = value;
        if (value)
        {
            RefreshAutoRetainerMultiModeCache(true);
        }
        else
        {
            cachedAutoRetainerMultiModeEnabled = false;
            autoRetainerMultiModeCacheExpiresAt = 0;
        }

        if (disableBackgroundRenderingEnabled)
            DisableBackgroundRenderingStatusText = GetBackgroundRenderingStatusText();
    }

    public bool SetDisableBackgroundRenderingEnabled(bool value)
    {
        if (!value)
        {
            disableBackgroundRenderingEnabled = false;
            UpdateBackgroundRenderingHookState();
            backgroundRenderingSuppressed = false;
            cachedAutoRetainerMultiModeEnabled = false;
            autoRetainerMultiModeCacheExpiresAt = 0;
            DisableBackgroundRenderingStatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (deviceDx11PostTickHook == null || namePlateDrawHook == null)
        {
            DisableBackgroundRenderingStatusText = "Unavailable - render hooks are incomplete.";
            return false;
        }

        disableBackgroundRenderingEnabled = true;
        RefreshAutoRetainerMultiModeCache(true);
        UpdateBackgroundRenderingHookState();
        DisableBackgroundRenderingStatusText = GetBackgroundRenderingStatusText();
        return true;
    }

    public bool TryApplyCustomResolution(int width, int height, out string message)
    {
        if (!customResolutionsEnabled)
        {
            message = "Enable Custom Resolutions in XA Mods first.";
            return false;
        }

        if (!TryNormalizeCustomResolution(width, height, out var normalizedWidth, out var normalizedHeight, out message))
            return false;

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero)
        {
            message = "Game window surface missing.";
            CustomResolutionsStatusText = "Unavailable - game window surface missing.";
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        RefreshWindowSizeLimits(gameWindow);

        if (!TryResizeClientWindow(gameWindow->WindowHandle, normalizedWidth, normalizedHeight))
        {
            message = $"Could not resize the client window to {normalizedWidth}x{normalizedHeight}.";
            CustomResolutionsStatusText = "Unavailable - failed while resizing the client window.";
            return false;
        }

        var synchronizedWidth = normalizedWidth;
        var synchronizedHeight = normalizedHeight;
        if (TryGetClientSize(gameWindow->WindowHandle, out var actualWidth, out var actualHeight))
        {
            synchronizedWidth = actualWidth;
            synchronizedHeight = actualHeight;
        }

        SynchronizeGameWindowSize(gameWindow, synchronizedWidth, synchronizedHeight);
        UpdateObservedWindowSizeState(synchronizedWidth, synchronizedHeight);
        lastAppliedCustomResolutionWidth = synchronizedWidth;
        lastAppliedCustomResolutionHeight = synchronizedHeight;
        CustomResolutionsStatusText = GetCustomResolutionsStatusText();
        message = $"Applied custom resolution {synchronizedWidth}x{synchronizedHeight}.";
        return true;
    }

    public void Dispose()
    {
        this.framework.Update -= OnFrameworkUpdate;

        cancelLoginCooldownEnabled = false;
        customResolutionsEnabled = false;
        ignoreMinimumWindowSizeEnabled = false;
        lowResolutionEnabled = false;
        preventLobbyExitEnabled = false;
        revealUndiscoveredAreasEnabled = false;
        disableBackgroundRenderingEnabled = false;
        disableBackgroundRenderingDisableWhenArMultiIsOn = false;
        ResetWindowSizeSynchronizationState();

        RestoreLowResolutionConfiguration();
        RefreshWindowSizeLimits();
        UpdateAgentLobbyHookState();
        UpdateAtkMessageBoxHookState();
        UpdateAgentMapHookState();
        UpdateBackgroundRenderingHookState();

        DisposeHook(ref agentLobbyUpdateHook);
        DisposeHook(ref atkMessageBoxReceiveEventHook);
        DisposeHook(ref agentMapUpdateHook);
        DisposeHook(ref deviceDx11PostTickHook);
        DisposeHook(ref namePlateDrawHook);
    }

    private string GetIgnoreMinimumWindowSizeStatusText()
    {
        return $"Enabled - minimum size forced to {SafeMinimumWindowWidth}x{SafeMinimumWindowHeight}. Resize sync is armed for restore and maximize operations.";
    }

    private string GetCustomResolutionsStatusText()
    {
        return lastAppliedCustomResolutionWidth > 0 && lastAppliedCustomResolutionHeight > 0
            ? $"Enabled - last applied custom client size is {lastAppliedCustomResolutionWidth}x{lastAppliedCustomResolutionHeight}."
            : "Enabled - preset buttons and `/xa res <width>x<height>` can change the client size locally.";
    }

    private string GetBackgroundRenderingStatusText()
    {
        if (disableBackgroundRenderingDisableWhenArMultiIsOn)
        {
            return cachedAutoRetainerMultiModeEnabled
                ? "Enabled - render hooks are armed, but AutoRetainer Multi keeps background rendering active."
                : disableBackgroundRenderingOnlyWhenMinimized
                    ? "Enabled - DX11 and nameplate hooks pause rendering only while minimized. AR Multi override is armed."
                    : "Enabled - DX11 and nameplate hooks pause rendering while inactive. AR Multi override is armed.";
        }

        return disableBackgroundRenderingOnlyWhenMinimized
            ? "Enabled - DX11 and nameplate hooks pause rendering only while minimized."
            : "Enabled - DX11 and nameplate hooks pause rendering while inactive.";
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;
        agentLobbyUpdateHook = TryCreateHook<AgentLobbyUpdateDelegate>(Sigs.AgentLobbyUpdateSig, AgentLobbyUpdateDetour, "AgentLobbyUpdate");
        atkMessageBoxReceiveEventHook = TryCreateHook<AtkMessageBoxReceiveEventDelegate>(Sigs.AtkMessageBoxReceiveEventSig, AtkMessageBoxReceiveEventDetour, "AtkMessageBoxReceiveEvent");
        agentMapUpdateHook = TryCreateHook<AgentMapUpdateDelegate>(Sigs.AgentMapUpdateSig, AgentMapUpdateDetour, "AgentMapUpdate");
        deviceDx11PostTickHook = TryCreateHook<DeviceDx11PostTickDelegate>(Sigs.DeviceDx11PostTickSig, DeviceDx11PostTickDetour, "DeviceDX11PostTick");
        namePlateDrawHook = TryCreateHook<NamePlateDrawDelegate>(Sigs.NamePlateDrawSig, NamePlateDrawDetour, "NamePlateDraw");

        try
        {
            if (sigScanner.TryScanText(Sigs.ToggleFadeSig, out var toggleFadeAddress) && toggleFadeAddress != nint.Zero)
            {
                toggleFadeDelegate = Marshal.GetDelegateForFunctionPointer<ToggleFadeDelegate>(toggleFadeAddress);
                log.Information($"[XASlave] Resolved SpecialRenderMode toggle delegate at 0x{toggleFadeAddress:X}.");
            }
            else
            {
                log.Warning("[XASlave] SpecialRenderMode toggle delegate signature was not found.");
            }
        }
        catch (Exception ex)
        {
            toggleFadeDelegate = null;
            log.Warning(ex, "[XASlave] Failed to resolve SpecialRenderMode toggle delegate.");
        }
    }

    private Hook<T>? TryCreateHook<T>(string signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] {label} signature was not found.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(address, detour);
            log.Information($"[XASlave] Created {label} hook at 0x{address:X}.");
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to create {label} hook.");
            return null;
        }
    }

    public bool SetSpecialRenderWorldHidden(bool hidden, Vector4 color)
    {
        EnsureInitialized();
        var frameworkInstance = Framework.Instance();
        if (toggleFadeDelegate == null || frameworkInstance == null)
            return false;

        var environmentManager = frameworkInstance->EnvironmentManager;
        toggleFadeDelegate(environmentManager, hidden ? 1 : 0, 0.1f, &color);
        return true;
    }

    public bool SetSpecialRenderUiVisibility(UIModule.UiFlags flags, bool visible)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return false;

        uiModule->ToggleUi(flags, visible);
        return true;
    }

    private void UpdateAgentLobbyHookState()
    {
        ToggleHook(agentLobbyUpdateHook, cancelLoginCooldownEnabled, "AgentLobbyUpdate");
    }

    private void UpdateAtkMessageBoxHookState()
    {
        ToggleHook(atkMessageBoxReceiveEventHook, preventLobbyExitEnabled, "AtkMessageBoxReceiveEvent");
    }

    private void UpdateAgentMapHookState()
    {
        ToggleHook(agentMapUpdateHook, revealUndiscoveredAreasEnabled, "AgentMapUpdate");
    }

    private void UpdateBackgroundRenderingHookState()
    {
        ToggleHook(deviceDx11PostTickHook, disableBackgroundRenderingEnabled, "DeviceDX11PostTick");
        ToggleHook(namePlateDrawHook, disableBackgroundRenderingEnabled, "NamePlateDraw");
    }

    private void ToggleHook<T>(Hook<T>? hook, bool enabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return;

        try
        {
            if (enabled)
            {
                if (!hook.IsEnabled)
                    hook.Enable();
            }
            else if (hook.IsEnabled)
            {
                hook.Disable();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to {(enabled ? "enable" : "disable")} {label} hook.");
        }
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (ShouldSynchronizeWindowSize())
            UpdateWindowSizeSynchronization();

        if (lowResolutionEnabled)
            UpdateLowResolutionScale();

        if (disableBackgroundRenderingEnabled
            && disableBackgroundRenderingDisableWhenArMultiIsOn
            && RefreshAutoRetainerMultiModeCache(false))
        {
            DisableBackgroundRenderingStatusText = GetBackgroundRenderingStatusText();
        }
    }

    private bool RefreshAutoRetainerMultiModeCache(bool force)
    {
        var currentTick = Environment.TickCount64;
        if (!force && currentTick < autoRetainerMultiModeCacheExpiresAt)
            return false;

        autoRetainerMultiModeCacheExpiresAt = currentTick + AutoRetainerMultiModeCacheMilliseconds;
        var previousValue = cachedAutoRetainerMultiModeEnabled;
        cachedAutoRetainerMultiModeEnabled = disableBackgroundRenderingDisableWhenArMultiIsOn && isAutoRetainerMultiModeEnabled();
        return previousValue != cachedAutoRetainerMultiModeEnabled;
    }

    private bool ShouldKeepBackgroundRenderingActiveForAutoRetainer()
    {
        if (!disableBackgroundRenderingDisableWhenArMultiIsOn)
            return false;

        RefreshAutoRetainerMultiModeCache(false);
        return cachedAutoRetainerMultiModeEnabled;
    }

    private bool ShouldSynchronizeWindowSize()
    {
        return ignoreMinimumWindowSizeEnabled || customResolutionsEnabled;
    }

    private void PrimeWindowSizeSynchronization(GameWindow* gameWindow)
    {
        ResetWindowSizeSynchronizationState();

        if (!TryGetClientSize(gameWindow->WindowHandle, out var clientWidth, out var clientHeight))
            return;

        UpdateObservedWindowSizeState(clientWidth, clientHeight);
    }

    private void UpdateObservedWindowSizeState(int clientWidth, int clientHeight)
    {
        hasObservedClientSize = true;
        pendingWindowSizeSynchronization = false;
        lastObservedClientWidth = clientWidth;
        lastObservedClientHeight = clientHeight;
        lastSynchronizedClientWidth = clientWidth;
        lastSynchronizedClientHeight = clientHeight;
        lastWindowSizeChangeTick = Environment.TickCount64;
    }

    private void ResetWindowSizeSynchronizationState()
    {
        hasObservedClientSize = false;
        pendingWindowSizeSynchronization = false;
        lastObservedClientWidth = 0;
        lastObservedClientHeight = 0;
        lastSynchronizedClientWidth = 0;
        lastSynchronizedClientHeight = 0;
        lastWindowSizeChangeTick = 0;
    }

    private void RefreshWindowSizeLimits(GameWindow* gameWindow = null)
    {
        if (gameWindow == null)
            gameWindow = GameWindow.Instance();

        if (gameWindow == null)
            return;

        if (ShouldSynchronizeWindowSize())
        {
            gameWindow->MinWidth = SafeMinimumWindowWidth;
            gameWindow->MinHeight = SafeMinimumWindowHeight;
            return;
        }

        RestoreWindowSizeLimits();
    }

    private void UpdateWindowSizeSynchronization()
    {
        var gameWindow = GameWindow.Instance();
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero)
            return;

        if (!TryGetClientSize(gameWindow->WindowHandle, out var clientWidth, out var clientHeight))
            return;

        var currentTick = Environment.TickCount64;
        if (!hasObservedClientSize)
        {
            hasObservedClientSize = true;
            lastObservedClientWidth = clientWidth;
            lastObservedClientHeight = clientHeight;
            lastWindowSizeChangeTick = currentTick;
            return;
        }

        if (clientWidth != lastObservedClientWidth || clientHeight != lastObservedClientHeight)
        {
            lastObservedClientWidth = clientWidth;
            lastObservedClientHeight = clientHeight;
            lastWindowSizeChangeTick = currentTick;
            pendingWindowSizeSynchronization = true;
            return;
        }

        if (!pendingWindowSizeSynchronization || currentTick - lastWindowSizeChangeTick < WindowSizeSyncDebounceMilliseconds)
            return;

        if (IsIconic(gameWindow->WindowHandle)
            || clientWidth < SafeMinimumWindowWidth
            || clientHeight < SafeMinimumWindowHeight)
        {
            return;
        }

        if (clientWidth == lastSynchronizedClientWidth && clientHeight == lastSynchronizedClientHeight)
        {
            pendingWindowSizeSynchronization = false;
            return;
        }

        SynchronizeGameWindowSize(gameWindow, clientWidth, clientHeight);
        pendingWindowSizeSynchronization = false;
    }

    private void SynchronizeGameWindowSize(GameWindow* gameWindow, int clientWidth, int clientHeight)
    {
        var device = Device.Instance();
        if (device == null)
            return;

        gameWindow->WindowWidth = clientWidth;
        gameWindow->WindowHeight = clientHeight;
        device->NewWidth = (uint)clientWidth;
        device->NewHeight = (uint)clientHeight;
        device->RequestResolutionChange = 1;

        lastSynchronizedClientWidth = clientWidth;
        lastSynchronizedClientHeight = clientHeight;
        if (ignoreMinimumWindowSizeEnabled)
            IgnoreMinimumWindowSizeStatusText = GetIgnoreMinimumWindowSizeStatusText();

        if (customResolutionsEnabled)
            CustomResolutionsStatusText = GetCustomResolutionsStatusText();

        log.Information($"[XASlave] Re-synchronized game resolution to {clientWidth}x{clientHeight} after a window-size change.");
    }

    private void UpdateLowResolutionScale()
    {
        var graphicsConfig = GraphicsConfig.Instance();
        if (graphicsConfig == null)
        {
            LowResolutionStatusText = "Unavailable - graphics configuration surface missing.";
            return;
        }

        var forcedFallbackUpscaler = EnsureLowResolutionUpscaleType(graphicsConfig);
        var normalizedScale = ClampLowResolutionScale(lowResolutionScale);
        if (Math.Abs(graphicsConfig->GraphicsRezoScale - normalizedScale) > 0.0001f)
            graphicsConfig->GraphicsRezoScale = normalizedScale;

        LowResolutionStatusText = forcedFallbackUpscaler
            ? $"Enabled - 3D resolution scale is forced to {normalizedScale:0.00}, and DLSS is temporarily switched to AMD FSR while Low Resolution is active."
            : $"Enabled - 3D resolution scale is forced to {normalizedScale:0.00}.";
    }

    private void RestoreLowResolutionConfiguration()
    {
        var graphicsConfig = GraphicsConfig.Instance();
        if (graphicsConfig == null)
        {
            capturedLowResolutionUpscaleType = false;
            originalLowResolutionUpscaleType = 0;
            return;
        }

        var configuredScale = ClampLowResolutionScale(gameConfig.System.GetUInt("GraphicsRezoScale") / 100f);
        graphicsConfig->GraphicsRezoScale = configuredScale;

        if (capturedLowResolutionUpscaleType)
            graphicsConfig->GraphicsRezoUpscaleType = originalLowResolutionUpscaleType;

        capturedLowResolutionUpscaleType = false;
        originalLowResolutionUpscaleType = 0;
    }

    private bool EnsureLowResolutionUpscaleType(GraphicsConfig* graphicsConfig)
    {
        if (!capturedLowResolutionUpscaleType)
        {
            capturedLowResolutionUpscaleType = true;
            originalLowResolutionUpscaleType = graphicsConfig->GraphicsRezoUpscaleType;
        }

        if (graphicsConfig->GraphicsRezoUpscaleType <= MaximumSupportedLowResolutionUpscaleType)
            return false;

        graphicsConfig->GraphicsRezoUpscaleType = LowResolutionFallbackUpscaleType;
        return true;
    }

    private static bool TryGetClientSize(nint windowHandle, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (windowHandle == nint.Zero || !GetClientRect(windowHandle, out var rect))
            return false;

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    private static bool TryResizeClientWindow(nint windowHandle, int clientWidth, int clientHeight)
    {
        if (windowHandle == nint.Zero || !GetWindowRect(windowHandle, out var windowRect))
            return false;

        var style = GetWindowLongPtr(windowHandle, WindowStyleIndex);
        var exStyle = GetWindowLongPtr(windowHandle, WindowExStyleIndex);
        var desiredRect = new WindowRect
        {
            Left = 0,
            Top = 0,
            Right = clientWidth,
            Bottom = clientHeight,
        };

        if (!AdjustWindowRectEx(ref desiredRect, style.ToInt32(), false, exStyle.ToInt32()))
            return false;

        var outerWidth = desiredRect.Right - desiredRect.Left;
        var outerHeight = desiredRect.Bottom - desiredRect.Top;
        return SetWindowPos(
            windowHandle,
            nint.Zero,
            windowRect.Left,
            windowRect.Top,
            outerWidth,
            outerHeight,
            SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

    private void CaptureOriginalWindowSize(GameWindow* gameWindow)
    {
        if (capturedWindowSize)
            return;

        capturedWindowSize = true;
        originalMinWidth = gameWindow->MinWidth;
        originalMinHeight = gameWindow->MinHeight;
    }

    private void RestoreWindowSizeLimits()
    {
        if (!capturedWindowSize)
            return;

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null)
            return;

        gameWindow->MinWidth = originalMinWidth;
        gameWindow->MinHeight = originalMinHeight;
    }

    private bool AtkMessageBoxReceiveEventDetour(AtkMessageBoxManager* manager, nint a2, AtkValue* values)
    {
        if (preventLobbyExitEnabled && values != null)
            values->UInt = 16000;

        return atkMessageBoxReceiveEventHook?.Original(manager, a2, values) ?? false;
    }

    private void AgentLobbyUpdateDetour(AgentLobby* agent, uint deltaTime)
    {
        if (cancelLoginCooldownEnabled && agent != null)
            agent->TemporaryLocked = false;

        agentLobbyUpdateHook?.Original(agent, deltaTime);

        if (cancelLoginCooldownEnabled && agent != null)
            agent->TemporaryLocked = false;
    }

    private void AgentMapUpdateDetour(AgentMap* agent, uint updateCount)
    {
        if (revealUndiscoveredAreasEnabled && agent != null)
        {
            agent->CurrentMapDiscoveryFlag = 0;
            agent->SelectedMapDiscoveryFlag = 0;
        }

        agentMapUpdateHook?.Original(agent, updateCount);

        if (revealUndiscoveredAreasEnabled && agent != null)
        {
            agent->CurrentMapDiscoveryFlag = 0;
            agent->SelectedMapDiscoveryFlag = 0;
        }
    }

    private void DeviceDx11PostTickDetour(nint instance)
    {
        if (!disableBackgroundRenderingEnabled)
        {
            backgroundRenderingSuppressed = false;
            deviceDx11PostTickHook?.Original(instance);
            return;
        }

        var framework = Framework.Instance();
        if (framework == null || !clientState.IsLoggedIn)
        {
            backgroundRenderingSuppressed = false;
            deviceDx11PostTickHook?.Original(instance);
            return;
        }

        if (ShouldKeepBackgroundRenderingActiveForAutoRetainer())
        {
            backgroundRenderingSuppressed = false;
            deviceDx11PostTickHook?.Original(instance);
            return;
        }

        var currentTick = Environment.TickCount64;
        if (nextBackgroundRenderTick - currentTick < 0)
        {
            nextBackgroundRenderTick = currentTick + 5_000;
            backgroundRenderingSuppressed = false;
            deviceDx11PostTickHook?.Original(instance);
            return;
        }

        var shouldSuppress = disableBackgroundRenderingOnlyWhenMinimized
            ? framework->GameWindow != null && IsIconic(framework->GameWindow->WindowHandle)
            : framework->WindowInactive;

        if (shouldSuppress)
        {
            backgroundRenderingSuppressed = true;
            var uiModule = UIModule.Instance();
            if (uiModule != null && uiModule->ShouldLimitFps())
                Thread.Sleep(50);

            return;
        }

        backgroundRenderingSuppressed = false;
        deviceDx11PostTickHook?.Original(instance);
    }

    private void NamePlateDrawDetour(AtkUnitBase* addon)
    {
        if (disableBackgroundRenderingEnabled && backgroundRenderingSuppressed)
            return;

        namePlateDrawHook?.Original(addon);
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint windowHandle, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out WindowRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectEx(ref WindowRect rect, int style, bool hasMenu, int exStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(
        ulong processHandle,
        int processInformationClass,
        void* processInformation,
        uint processInformationLength,
        uint* returnLength);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryObject(
        ulong handle,
        int objectInformationClass,
        void* objectInformation,
        uint objectInformationLength,
        uint* returnLength);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(ulong handle);

    private static List<ulong> EnumerateProcessHandles()
    {
        List<ulong> result = [];
        uint bufferSize = 0x8000;

        while (true)
        {
            var buffer = new byte[bufferSize];
            fixed (byte* pBuffer = &buffer[0])
            {
                var snapshot = (ProcessHandleSnapshotInformation*)pBuffer;
                snapshot->NumberOfHandles = 0;
                uint returnSize = 0;
                var status = NtQueryInformationProcess(ulong.MaxValue, 51, pBuffer, bufferSize, &returnSize);
                if ((uint)status == 0xC0000004)
                {
                    bufferSize = returnSize;
                    continue;
                }

                if (status >= 0)
                {
                    var handles = (ProcessHandleTableEntryInfo*)(snapshot + 1);
                    for (ulong i = 0; i < snapshot->NumberOfHandles; i++)
                        result.Add(handles[i].HandleValue);
                }

                break;
            }
        }

        return result;
    }

    private static string GetObjectNameOrTypeName(ulong handle, bool typeName)
    {
        const uint BufferSize = 1024;
        var buffer = new byte[BufferSize];
        fixed (byte* pBuffer = &buffer[0])
        {
            uint returnSize = 0;
            var status = NtQueryObject(handle, typeName ? 2 : 1, pBuffer, BufferSize, &returnSize);
            if (status >= 0)
            {
                var name = (UnicodeString*)pBuffer;
                if (name->Buffer != null)
                    return Encoding.Unicode.GetString(name->Buffer, name->Length);
            }
        }

        return string.Empty;
    }

    private static int ReleaseMultipleGameInstanceHandles()
    {
        var closedHandles = 0;
        foreach (var handle in EnumerateProcessHandles())
        {
            if (!string.Equals(GetObjectNameOrTypeName(handle, true), "Mutant", StringComparison.Ordinal))
                continue;

            var name = GetObjectNameOrTypeName(handle, false);
            if (!name.Contains("6AA83AB5-BAC4-4a36-9F66-A309770760CB_ffxiv_game0", StringComparison.Ordinal))
                continue;

            if (CloseHandle(handle))
                closedHandles++;
        }

        return closedHandles;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessHandleTableEntryInfo
    {
        public ulong HandleValue;
        public ulong HandleCount;
        public ulong PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessHandleSnapshotInformation
    {
        public ulong NumberOfHandles;
        public ulong Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public byte* Buffer;
    }

    private delegate void AgentLobbyUpdateDelegate(AgentLobby* agent, uint deltaTime);
    private delegate bool AtkMessageBoxReceiveEventDelegate(AtkMessageBoxManager* manager, nint a2, AtkValue* values);

    private delegate void AgentMapUpdateDelegate(AgentMap* agent, uint updateCount);

    private delegate void DeviceDx11PostTickDelegate(nint instance);

    private delegate void NamePlateDrawDelegate(AtkUnitBase* addon);

    private delegate void ToggleFadeDelegate(EnvironmentManager* manager, int a2, float fadeDuration, Vector4* fadeColor);
}
