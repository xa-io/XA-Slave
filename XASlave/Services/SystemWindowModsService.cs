using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
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
    private const int DefaultGameMinimumWindowWidth = 1024;
    private const int DefaultGameMinimumWindowHeight = 720;
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
    private const double FrameworkUpdateTimingDebugThresholdMilliseconds = 10.0;
    private const float MinimumLowResolutionScale = 0.01f;
    private const float MaximumLowResolutionScale = 1.00f;
    private const float DisabledLowResolutionScale = 1.00f;
    private const int LowResolutionDisableResetFrames = 1;
    private const byte MaximumSupportedLowResolutionUpscaleType = 1;
    private const byte LowResolutionFallbackUpscaleType = 1;
    private static readonly string GraphicsResScaleConfigKey = string.Concat("Graphics", (char)0x52, (char)0x65, (char)0x7A, (char)0x6F, "Scale");
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly IGameConfig gameConfig;
    private readonly IClientState clientState;
    private readonly Func<bool> isAutoRetainerMultiModeEnabled;
    private readonly object allowMultipleGameInstancesStartupLock = new();
    private readonly object cancelLoginCooldownStartupArmingLock = new();
    private readonly object preventLobbyExitStartupArmingLock = new();

    private Hook<AgentLobbyUpdateDelegate>? agentLobbyUpdateHook;
    private Hook<AtkMessageBoxReceiveEventDelegate>? atkMessageBoxReceiveEventHook;
    private Hook<AgentMapUpdateDelegate>? agentMapUpdateHook;
    private Hook<DeviceDx11PostTickDelegate>? deviceDx11PostTickHook;
    private Hook<NamePlateDrawDelegate>? namePlateDrawHook;
    private ToggleFadeDelegate? toggleFadeDelegate;
    private bool agentLobbyHookInitialized;
    private bool atkMessageBoxHookInitialized;
    private bool agentMapHookInitialized;
    private bool deviceDx11PostTickHookInitialized;
    private bool namePlateDrawHookInitialized;
    private bool toggleFadeDelegateInitialized;
    private bool cancelLoginCooldownEnabled;
    private bool customResolutionsEnabled;
    private bool disableTitleScreenMovieEnabled;
    private bool ignoreMinimumWindowSizeEnabled;
    private bool lowResolutionEnabled;
    private bool lowResolutionDisablePending;
    private bool capturedLowResolutionUpscaleType;
    private bool preventLobbyExitEnabled;
    private bool revealUndiscoveredAreasEnabled;
    private bool disableBackgroundRenderingEnabled;
    private bool disableBackgroundRenderingOnlyWhenMinimized;
    private bool disableBackgroundRenderingDisableWhenArMultiIsOn;
    private bool backgroundRenderingSuppressed;
    private bool capturedWindowSize;
    private bool capturedOriginalClientSize;
    private int originalMinWidth;
    private int originalMinHeight;
    private int originalClientWidth;
    private int originalClientHeight;
    private long nextBackgroundRenderTick;
    private long autoRetainerMultiModeCacheExpiresAt;
    private bool cachedAutoRetainerMultiModeEnabled;
    private bool hasObservedClientSize;
    private bool pendingWindowSizeSynchronization;
    private bool allowMultipleGameInstancesStartupPending;
    private bool cancelLoginCooldownStartupArmingPending;
    private bool preventLobbyExitStartupArmingPending;
    private bool disposed;
    private int lastObservedClientWidth;
    private int lastObservedClientHeight;
    private int lastSynchronizedClientWidth;
    private int lastSynchronizedClientHeight;
    private long lastWindowSizeChangeTick;
    private int lastAppliedCustomResolutionWidth;
    private int lastAppliedCustomResolutionHeight;
    private int lowResolutionDisableResetFramesRemaining;
    private byte originalLowResolutionUpscaleType;
    private float lowResolutionScale = 0.25f;
    private System.Threading.Tasks.Task<int>? allowMultipleGameInstancesStartupTask;
    private System.Threading.Tasks.Task<Hook<AgentLobbyUpdateDelegate>?>? cancelLoginCooldownStartupHookTask;
    private System.Threading.Tasks.Task<Hook<AtkMessageBoxReceiveEventDelegate>?>? preventLobbyExitStartupHookTask;
    private System.Threading.CancellationTokenSource? cancelLoginCooldownStartupHookCancellation;
    private System.Threading.CancellationTokenSource? preventLobbyExitStartupHookCancellation;
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

    public string AllowMultipleGameInstancesStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiAllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedAllowMultipleGameInstancesStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiAllowMultipleGameInstancesStatusText, AllowMultipleGameInstancesStatusText);
    internal global::XASlave.Localization.UiMessage AllowMultipleGameInstancesStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiAllowMultipleGameInstancesStatusText, AllowMultipleGameInstancesStatusText);

    public bool IsAllowMultipleGameInstancesStartupPending => allowMultipleGameInstancesStartupPending;

    public string CancelLoginCooldownStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiCancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedCancelLoginCooldownStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiCancelLoginCooldownStatusText, CancelLoginCooldownStatusText);
    internal global::XASlave.Localization.UiMessage CancelLoginCooldownStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiCancelLoginCooldownStatusText, CancelLoginCooldownStatusText);

    public bool IsCancelLoginCooldownStartupArmingPending => cancelLoginCooldownStartupArmingPending;

    public string CustomResolutionsStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiCustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedCustomResolutionsStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiCustomResolutionsStatusText, CustomResolutionsStatusText);
    internal global::XASlave.Localization.UiMessage CustomResolutionsStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiCustomResolutionsStatusText, CustomResolutionsStatusText);

    public string DisableTitleScreenMovieStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiDisableTitleScreenMovieStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedDisableTitleScreenMovieStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiDisableTitleScreenMovieStatusText, DisableTitleScreenMovieStatusText);
    internal global::XASlave.Localization.UiMessage DisableTitleScreenMovieStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiDisableTitleScreenMovieStatusText, DisableTitleScreenMovieStatusText);

    public string IgnoreMinimumWindowSizeStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiIgnoreMinimumWindowSizeStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedIgnoreMinimumWindowSizeStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiIgnoreMinimumWindowSizeStatusText, IgnoreMinimumWindowSizeStatusText);
    internal global::XASlave.Localization.UiMessage IgnoreMinimumWindowSizeStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiIgnoreMinimumWindowSizeStatusText, IgnoreMinimumWindowSizeStatusText);

    public string LowResolutionStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiLowResolutionStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedLowResolutionStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiLowResolutionStatusText, LowResolutionStatusText);
    internal global::XASlave.Localization.UiMessage LowResolutionStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLowResolutionStatusText, LowResolutionStatusText);

    public string PreventLobbyExitStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiPreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedPreventLobbyExitStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiPreventLobbyExitStatusText, PreventLobbyExitStatusText);
    internal global::XASlave.Localization.UiMessage PreventLobbyExitStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiPreventLobbyExitStatusText, PreventLobbyExitStatusText);

    public bool IsPreventLobbyExitStartupArmingPending => preventLobbyExitStartupArmingPending;

    public string RevealUndiscoveredAreasStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiRevealUndiscoveredAreasStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedRevealUndiscoveredAreasStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiRevealUndiscoveredAreasStatusText, RevealUndiscoveredAreasStatusText);
    internal global::XASlave.Localization.UiMessage RevealUndiscoveredAreasStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiRevealUndiscoveredAreasStatusText, RevealUndiscoveredAreasStatusText);

    public string DisableBackgroundRenderingStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiDisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedDisableBackgroundRenderingStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiDisableBackgroundRenderingStatusText, DisableBackgroundRenderingStatusText);
    internal global::XASlave.Localization.UiMessage DisableBackgroundRenderingStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiDisableBackgroundRenderingStatusText, DisableBackgroundRenderingStatusText);

    public bool IsSpecialRenderWorldFadeAvailable
    {
        get
        {
            EnsureToggleFadeDelegateInitialized();
            return toggleFadeDelegate != null;
        }
    }

    internal global::XASlave.Localization.UiMessage SpecialRenderModesStatusTextMessage
    {
        get
        {
            if (UIModule.Instance() == null)
                return global::XASlave.Localization.UiMessage.Key("S15F659DA614392B9");

            return (IsSpecialRenderWorldFadeAvailable ? global::XASlave.Localization.UiMessage.Key("SEFA151BB9C3DDE70") : global::XASlave.Localization.UiMessage.Key("S103A3C8E99B627B1"));
        }
    }
    public string SpecialRenderModesStatusText => SpecialRenderModesStatusTextMessage.Original;
    internal string LocalizedSpecialRenderModesStatusText => SpecialRenderModesStatusTextMessage.Display;

    public static float ClampLowResolutionScale(float value)
    {
        return Math.Clamp(value, MinimumLowResolutionScale, MaximumLowResolutionScale);
    }

    public static bool TryNormalizeCustomResolution(int width, int height, out int normalizedWidth, out int normalizedHeight, out string message)
    {
        var success = TryNormalizeCustomResolutionMessage(width, height, out normalizedWidth, out normalizedHeight, out var presentation);
        message = presentation.Original;
        return success;
    }

    internal static bool TryNormalizeCustomResolutionMessage(int width, int height, out int normalizedWidth, out int normalizedHeight, out global::XASlave.Localization.UiMessage message)
    {
        normalizedWidth = width;
        normalizedHeight = height;

        if (width < SafeMinimumWindowWidth || height < SafeMinimumWindowHeight)
        {
            message = global::XASlave.Localization.UiMessage.Format("S1435993620DE22AD", SafeMinimumWindowWidth, SafeMinimumWindowHeight);
            return false;
        }

        if (width > MaximumCustomWindowWidth || height > MaximumCustomWindowHeight)
        {
            message = global::XASlave.Localization.UiMessage.Format("S67F4FEED5A150C3F", MaximumCustomWindowWidth, MaximumCustomWindowHeight);
            return false;
        }

        message = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        return true;
    }

    public bool SetAllowMultipleGameInstancesEnabled(bool value)
    {
        if (!value)
        {
            CancelAllowMultipleGameInstancesStartupCleanup();
            AllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Store(ref uiAllowMultipleGameInstancesStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        if (allowMultipleGameInstancesStartupPending)
            return true;

        try
        {
            var closedHandles = ReleaseMultipleGameInstanceHandles();
            AllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Store(ref uiAllowMultipleGameInstancesStatusText, (closedHandles > 0 ? global::XASlave.Localization.UiMessage.Format("S955879300418F185", closedHandles) : global::XASlave.Localization.UiMessage.Key("S90189F98F4D237CD")));
            return true;
        }
        catch (Exception ex)
        {
            AllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Store(ref uiAllowMultipleGameInstancesStatusText, global::XASlave.Localization.UiMessage.Key("SF12C4858E9A42384"));
            log.Warning(ex, "[XASlave] Failed to clear the multi-instance launch lock.");
            return false;
        }
    }

    public bool RestoreAllowMultipleGameInstancesOnStartup()
    {
        if (allowMultipleGameInstancesStartupPending)
            return true;

        AllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Store(ref uiAllowMultipleGameInstancesStatusText, global::XASlave.Localization.UiMessage.Key("SA9D6B8EF826F6700"));
        StartAllowMultipleGameInstancesStartupCleanup();
        return true;
    }

    public bool SetCancelLoginCooldownEnabled(bool value)
    {
        if (!value)
        {
            cancelLoginCooldownEnabled = false;
            CancelCancelLoginCooldownStartupArming(disposeCompletedResult: true);
            UpdateAgentLobbyHookState();
            CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        if (cancelLoginCooldownStartupArmingPending)
            return true;

        CancelCancelLoginCooldownStartupArming(disposeCompletedResult: true);
        cancelLoginCooldownEnabled = true;
        if (clientState.IsLoggedIn)
        {
            CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("S898FC0DDDCCAC820"));
            return true;
        }

        if (!agentLobbyHookInitialized || agentLobbyUpdateHook == null)
        {
            agentLobbyHookInitialized = false;
            CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("S4B3A10AB78A29118"));
            StartCancelLoginCooldownStartupHookCreation();
            return true;
        }

        if (agentLobbyUpdateHook == null)
        {
            cancelLoginCooldownEnabled = false;
            CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("SE715688E4C90D34A"));
            return false;
        }

        UpdateAgentLobbyHookState();
        CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("S28F95D3E4A862F89"));
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
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero)
        {
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("S576E8B6032A3B92C"));
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        CaptureOriginalClientSize(gameWindow);
        customResolutionsEnabled = true;
        RefreshWindowSizeLimits(gameWindow);
        PrimeWindowSizeSynchronization(gameWindow);
        CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("S0D576AD79B356D79"));
        return true;
    }

    public bool SetDisableTitleScreenMovieEnabled(bool value)
    {
        if (!value)
        {
            disableTitleScreenMovieEnabled = false;
            DisableTitleScreenMovieStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableTitleScreenMovieStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        disableTitleScreenMovieEnabled = true;
        UpdateDisableTitleScreenMovie();
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

            IgnoreMinimumWindowSizeStatusText = global::XASlave.Localization.UiMessage.Store(ref uiIgnoreMinimumWindowSizeStatusText, GetIgnoreMinimumWindowSizeDisabledStatusText());
            return false;
        }

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null)
        {
            IgnoreMinimumWindowSizeStatusText = global::XASlave.Localization.UiMessage.Store(ref uiIgnoreMinimumWindowSizeStatusText, global::XASlave.Localization.UiMessage.Key("S576E8B6032A3B92C"));
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        ignoreMinimumWindowSizeEnabled = true;
        RefreshWindowSizeLimits(gameWindow);
        PrimeWindowSizeSynchronization(gameWindow);
        IgnoreMinimumWindowSizeStatusText = global::XASlave.Localization.UiMessage.Store(ref uiIgnoreMinimumWindowSizeStatusText, GetIgnoreMinimumWindowSizeStatusText());
        return true;
    }

    public void ApplyLowResolutionConfiguration(float scale)
    {
        lowResolutionScale = ClampLowResolutionScale(scale);
        if (lowResolutionEnabled)
        {
            if (lowResolutionDisablePending)
                UpdateLowResolutionDisableResetScale();
            else
                UpdateLowResolutionScale();
        }
    }

    public bool SetLowResolutionEnabled(bool value)
    {
        if (!value)
        {
            BeginLowResolutionDisable();
            return false;
        }

        var graphicsConfig = GraphicsConfig.Instance();
        if (graphicsConfig == null)
        {
            LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, global::XASlave.Localization.UiMessage.Key("S55FF6F244E28A5C8"));
            return false;
        }

        lowResolutionDisablePending = false;
        lowResolutionDisableResetFramesRemaining = 0;
        lowResolutionEnabled = true;
        UpdateLowResolutionScale();
        return true;
    }

    public bool SetPreventLobbyExitEnabled(bool value)
    {
        if (!value)
        {
            preventLobbyExitEnabled = false;
            CancelPreventLobbyExitStartupArming(disposeCompletedResult: true);
            UpdateAtkMessageBoxHookState();
            PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        if (preventLobbyExitStartupArmingPending)
            return true;

        CancelPreventLobbyExitStartupArming(disposeCompletedResult: true);
        preventLobbyExitEnabled = true;
        if (clientState.IsLoggedIn)
        {
            PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("SFB72D4B40FD08B82"));
            return true;
        }

        if (!atkMessageBoxHookInitialized || atkMessageBoxReceiveEventHook == null)
        {
            atkMessageBoxHookInitialized = false;
            PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("SD98EB49BAF2BF0F4"));
            StartPreventLobbyExitStartupHookCreation();
            return true;
        }

        if (atkMessageBoxReceiveEventHook == null)
        {
            preventLobbyExitEnabled = false;
            PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("SA05686B931194474"));
            return false;
        }

        UpdateAtkMessageBoxHookState();
        PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("S6F3A6B8B0896251C"));
        return true;
    }

    public bool SetRevealUndiscoveredAreasEnabled(bool value)
    {
        if (!value)
        {
            revealUndiscoveredAreasEnabled = false;
            UpdateAgentMapHookState();
            RevealUndiscoveredAreasStatusText = global::XASlave.Localization.UiMessage.Store(ref uiRevealUndiscoveredAreasStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        EnsureAgentMapHookInitialized();
        if (agentMapUpdateHook == null)
        {
            RevealUndiscoveredAreasStatusText = global::XASlave.Localization.UiMessage.Store(ref uiRevealUndiscoveredAreasStatusText, global::XASlave.Localization.UiMessage.Key("S9C10E11F85941317"));
            return false;
        }

        revealUndiscoveredAreasEnabled = true;
        UpdateAgentMapHookState();
        RevealUndiscoveredAreasStatusText = global::XASlave.Localization.UiMessage.Store(ref uiRevealUndiscoveredAreasStatusText, global::XASlave.Localization.UiMessage.Key("SDA50894BBA33F8DD"));
        return true;
    }

    public void SetDisableBackgroundRenderingOnlyWhenMinimized(bool value)
    {
        disableBackgroundRenderingOnlyWhenMinimized = value;
        if (disableBackgroundRenderingEnabled)
            DisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableBackgroundRenderingStatusText, GetBackgroundRenderingStatusText());
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
            DisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableBackgroundRenderingStatusText, GetBackgroundRenderingStatusText());
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
            DisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableBackgroundRenderingStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        EnsureBackgroundRenderingHooksInitialized();
        if (deviceDx11PostTickHook == null || namePlateDrawHook == null)
        {
            DisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableBackgroundRenderingStatusText, global::XASlave.Localization.UiMessage.Key("SCDAD43067CED4691"));
            return false;
        }

        disableBackgroundRenderingEnabled = true;
        RefreshAutoRetainerMultiModeCache(true);
        UpdateBackgroundRenderingHookState();
        DisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableBackgroundRenderingStatusText, GetBackgroundRenderingStatusText());
        return true;
    }

    public bool TryApplyCustomResolution(int width, int height, out string message)
    {
        var success = TryApplyCustomResolutionMessage(width, height, out var presentation);
        message = presentation.Original;
        return success;
    }

    internal bool TryApplyCustomResolutionMessage(int width, int height, out global::XASlave.Localization.UiMessage message)
    {
        if (!customResolutionsEnabled)
        {
            message = global::XASlave.Localization.UiMessage.Key("SAC53FE4900891E3E");
            return false;
        }

        if (!TryNormalizeCustomResolutionMessage(width, height, out var normalizedWidth, out var normalizedHeight, out message))
            return false;

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero)
        {
            message = global::XASlave.Localization.UiMessage.Key("SA33325F441CF44C8");
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("S576E8B6032A3B92C"));
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        CaptureOriginalClientSize(gameWindow);
        RefreshWindowSizeLimits(gameWindow);

        if (!TryResizeClientWindow(gameWindow->WindowHandle, normalizedWidth, normalizedHeight))
        {
            message = global::XASlave.Localization.UiMessage.Format("SCC684B632CC71B0A", normalizedWidth, normalizedHeight);
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("S981E6BBEF8B03ED3"));
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
        CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, GetCustomResolutionsStatusText());
        message = global::XASlave.Localization.UiMessage.Format("S64F836C832F0A6EB", synchronizedWidth, synchronizedHeight);
        return true;
    }

    public bool TryResetCustomResolution(out string message)
    {
        var success = TryResetCustomResolutionMessage(out var presentation);
        message = presentation.Original;
        return success;
    }

    internal bool TryResetCustomResolutionMessage(out global::XASlave.Localization.UiMessage message)
    {
        if (!customResolutionsEnabled)
        {
            message = global::XASlave.Localization.UiMessage.Key("SAC53FE4900891E3E");
            return false;
        }

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero)
        {
            message = global::XASlave.Localization.UiMessage.Key("SA33325F441CF44C8");
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("S576E8B6032A3B92C"));
            return false;
        }

        CaptureOriginalWindowSize(gameWindow);
        CaptureOriginalClientSize(gameWindow);
        if (!capturedOriginalClientSize)
        {
            message = global::XASlave.Localization.UiMessage.Key("S760D1BED0A89636C");
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("SF73A2DCAF792D055"));
            return false;
        }

        RefreshWindowSizeLimits(gameWindow);
        if (!TryResizeClientWindow(gameWindow->WindowHandle, originalClientWidth, originalClientHeight))
        {
            message = global::XASlave.Localization.UiMessage.Format("SA777AA7A1054614C", originalClientWidth, originalClientHeight);
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Key("SCE182654B5F1C7BB"));
            return false;
        }

        var synchronizedWidth = originalClientWidth;
        var synchronizedHeight = originalClientHeight;
        if (TryGetClientSize(gameWindow->WindowHandle, out var actualWidth, out var actualHeight))
        {
            synchronizedWidth = actualWidth;
            synchronizedHeight = actualHeight;
        }

        SynchronizeGameWindowSize(gameWindow, synchronizedWidth, synchronizedHeight);
        UpdateObservedWindowSizeState(synchronizedWidth, synchronizedHeight);
        lastAppliedCustomResolutionWidth = 0;
        lastAppliedCustomResolutionHeight = 0;
        CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, global::XASlave.Localization.UiMessage.Format("SDA60E48783D1F6C0", synchronizedWidth, synchronizedHeight));
        message = global::XASlave.Localization.UiMessage.Format("SE2326451344C4818", synchronizedWidth, synchronizedHeight);
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        this.framework.Update -= OnFrameworkUpdate;
        CancelAllowMultipleGameInstancesStartupCleanup();
        CancelCancelLoginCooldownStartupArming(disposeCompletedResult: true);
        CancelPreventLobbyExitStartupArming(disposeCompletedResult: true);
        DisableLowResolutionForPluginUnload();

        cancelLoginCooldownEnabled = false;
        customResolutionsEnabled = false;
        disableTitleScreenMovieEnabled = false;
        ignoreMinimumWindowSizeEnabled = false;
        lowResolutionEnabled = false;
        lowResolutionDisablePending = false;
        lowResolutionDisableResetFramesRemaining = 0;
        preventLobbyExitEnabled = false;
        revealUndiscoveredAreasEnabled = false;
        disableBackgroundRenderingEnabled = false;
        disableBackgroundRenderingDisableWhenArMultiIsOn = false;
        ResetWindowSizeSynchronizationState();

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

    private global::XASlave.Localization.UiMessage GetIgnoreMinimumWindowSizeStatusText()
    {
        return global::XASlave.Localization.UiMessage.Format("S5E4F34D4818AF7D1", SafeMinimumWindowWidth, SafeMinimumWindowHeight);
    }

    private global::XASlave.Localization.UiMessage GetIgnoreMinimumWindowSizeDisabledStatusText()
    {
        var restoredMinimumWidth = GetRestoredMinimumWindowWidth();
        var restoredMinimumHeight = GetRestoredMinimumWindowHeight();
        if (restoredMinimumWidth > 0 && restoredMinimumHeight > 0)
            return global::XASlave.Localization.UiMessage.Format("S0E33701B4C73EA19", restoredMinimumWidth, restoredMinimumHeight);

        return global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    }

    private global::XASlave.Localization.UiMessage GetCustomResolutionsStatusText()
    {
        return (lastAppliedCustomResolutionWidth > 0 && lastAppliedCustomResolutionHeight > 0 ? global::XASlave.Localization.UiMessage.Format("S99B3FD6AAE8E9D18", lastAppliedCustomResolutionWidth, lastAppliedCustomResolutionHeight) : global::XASlave.Localization.UiMessage.Key("S0D576AD79B356D79"));
    }

    private global::XASlave.Localization.UiMessage GetBackgroundRenderingStatusText()
    {
        if (disableBackgroundRenderingDisableWhenArMultiIsOn)
        {
            return (cachedAutoRetainerMultiModeEnabled ? global::XASlave.Localization.UiMessage.Key("S13FFA59B2556B9E0") : (disableBackgroundRenderingOnlyWhenMinimized ? global::XASlave.Localization.UiMessage.Key("S356A428674D68FE2") : global::XASlave.Localization.UiMessage.Key("SD0C2CACE8AC104CC")));
        }

        return (disableBackgroundRenderingOnlyWhenMinimized ? global::XASlave.Localization.UiMessage.Key("SBF069764EC64FDBB") : global::XASlave.Localization.UiMessage.Key("SE4CE22ED4EC75A4A"));
    }

    private void StartAllowMultipleGameInstancesStartupCleanup()
    {
        lock (allowMultipleGameInstancesStartupLock)
        {
            allowMultipleGameInstancesStartupPending = true;
            allowMultipleGameInstancesStartupTask ??= System.Threading.Tasks.Task.Run(ReleaseMultipleGameInstanceHandles);
        }
    }

    private void CancelAllowMultipleGameInstancesStartupCleanup()
    {
        lock (allowMultipleGameInstancesStartupLock)
        {
            allowMultipleGameInstancesStartupPending = false;
            allowMultipleGameInstancesStartupTask = null;
        }
    }

    private void ProcessAllowMultipleGameInstancesStartupCleanup()
    {
        System.Threading.Tasks.Task<int>? task;
        lock (allowMultipleGameInstancesStartupLock)
            task = allowMultipleGameInstancesStartupTask;

        if (task == null)
        {
            CancelAllowMultipleGameInstancesStartupCleanup();
            return;
        }

        if (!task.IsCompleted)
            return;

        var completed = false;
        var closedHandles = 0;
        try
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
            {
                completed = true;
                closedHandles = task.Result;
            }
            else if (task.IsFaulted && task.Exception != null)
            {
                log.Warning(task.Exception.GetBaseException(), "[XASlave] Failed to clear the multi-instance launch lock during startup.");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to complete multi-instance launch lock startup cleanup.");
        }

        lock (allowMultipleGameInstancesStartupLock)
        {
            allowMultipleGameInstancesStartupPending = false;
            allowMultipleGameInstancesStartupTask = null;
        }

        if (disposed)
            return;

        if (completed)
        {
            AllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Store(ref uiAllowMultipleGameInstancesStatusText, (closedHandles > 0 ? global::XASlave.Localization.UiMessage.Format("S955879300418F185", closedHandles) : global::XASlave.Localization.UiMessage.Key("S90189F98F4D237CD")));
        }
        else
        {
            AllowMultipleGameInstancesStatusText = global::XASlave.Localization.UiMessage.Store(ref uiAllowMultipleGameInstancesStatusText, global::XASlave.Localization.UiMessage.Key("SF12C4858E9A42384"));
        }
    }

    private void EnsureAgentLobbyHookInitialized()
    {
        if (agentLobbyHookInitialized && agentLobbyUpdateHook != null)
            return;

        agentLobbyHookInitialized = true;
        agentLobbyUpdateHook ??= TryCreateHook<AgentLobbyUpdateDelegate>(Sigs.AgentLobbyUpdateSig, AgentLobbyUpdateDetour, "AgentLobbyUpdate");
    }

    private Hook<AgentLobbyUpdateDelegate>? CreateAgentLobbyHook()
    {
        return TryCreateHook<AgentLobbyUpdateDelegate>(Sigs.AgentLobbyUpdateSig, AgentLobbyUpdateDetour, "AgentLobbyUpdate");
    }

    private void StartCancelLoginCooldownStartupHookCreation()
    {
        lock (cancelLoginCooldownStartupArmingLock)
        {
            cancelLoginCooldownStartupArmingPending = true;
            if (cancelLoginCooldownStartupHookTask == null)
            {
                cancelLoginCooldownStartupHookCancellation?.Dispose();
                cancelLoginCooldownStartupHookCancellation = new System.Threading.CancellationTokenSource();
                cancelLoginCooldownStartupHookTask = Plugin.RunOnGameThread(
                    CreateAgentLobbyHook,
                    "Cancel Login Cooldown startup hook creation",
                    cancelLoginCooldownStartupHookCancellation.Token);
            }
        }
    }

    private void CancelCancelLoginCooldownStartupArming(bool disposeCompletedResult)
    {
        System.Threading.Tasks.Task<Hook<AgentLobbyUpdateDelegate>?>? task;
        System.Threading.CancellationTokenSource? cancellation;
        lock (cancelLoginCooldownStartupArmingLock)
        {
            cancelLoginCooldownStartupArmingPending = false;
            task = cancelLoginCooldownStartupHookTask;
            cancelLoginCooldownStartupHookTask = null;
            cancellation = cancelLoginCooldownStartupHookCancellation;
            cancelLoginCooldownStartupHookCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();

        if (!disposeCompletedResult || task == null)
            return;

        DisposeCancelLoginCooldownStartupHookTaskResult(task);
    }

    private static void DisposeCancelLoginCooldownStartupHookTaskResult(System.Threading.Tasks.Task<Hook<AgentLobbyUpdateDelegate>?> task)
    {
        if (task.IsCompleted)
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                _ = Plugin.RunOnGameThread(() => DisposeHook(task.Result), "Dispose cancelled Cancel Login Cooldown startup hook");
            return;
        }

        task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    _ = Plugin.RunOnGameThread(() => DisposeHook(completedTask.Result), "Dispose cancelled Cancel Login Cooldown startup hook");
            },
            System.Threading.Tasks.TaskScheduler.Default);
    }

    private void ProcessCancelLoginCooldownStartupArming()
    {
        System.Threading.Tasks.Task<Hook<AgentLobbyUpdateDelegate>?>? task;
        lock (cancelLoginCooldownStartupArmingLock)
            task = cancelLoginCooldownStartupHookTask;

        if (task == null)
        {
            CancelCancelLoginCooldownStartupArming(disposeCompletedResult: false);
            return;
        }

        if (!task.IsCompleted)
            return;

        Hook<AgentLobbyUpdateDelegate>? createdHook = null;
        try
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                createdHook = task.Result;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Cancel Login Cooldown startup hook initialization failed.");
        }

        System.Threading.CancellationTokenSource? completedCancellation;
        lock (cancelLoginCooldownStartupArmingLock)
        {
            cancelLoginCooldownStartupHookTask = null;
            cancelLoginCooldownStartupArmingPending = false;
            completedCancellation = cancelLoginCooldownStartupHookCancellation;
            cancelLoginCooldownStartupHookCancellation = null;
        }
        completedCancellation?.Dispose();

        agentLobbyHookInitialized = true;

        if (disposed || !cancelLoginCooldownEnabled)
        {
            DisposeHook(createdHook);
            return;
        }

        agentLobbyUpdateHook = createdHook;
        if (agentLobbyUpdateHook == null)
        {
            cancelLoginCooldownEnabled = false;
            CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("SE715688E4C90D34A"));
            return;
        }

        UpdateAgentLobbyHookState();
        CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, (agentLobbyUpdateHook.IsEnabled ? global::XASlave.Localization.UiMessage.Key("S28F95D3E4A862F89") : global::XASlave.Localization.UiMessage.Key("SF7DB9382398CA51A")));
    }

    private void EnsureAtkMessageBoxHookInitialized()
    {
        if (atkMessageBoxHookInitialized && atkMessageBoxReceiveEventHook != null)
            return;

        atkMessageBoxHookInitialized = true;
        atkMessageBoxReceiveEventHook ??= TryCreateHook<AtkMessageBoxReceiveEventDelegate>(Sigs.AtkMessageBoxReceiveEventSig, AtkMessageBoxReceiveEventDetour, "AtkMessageBoxReceiveEvent");
    }

    private Hook<AtkMessageBoxReceiveEventDelegate>? CreateAtkMessageBoxHook()
    {
        return TryCreateHook<AtkMessageBoxReceiveEventDelegate>(Sigs.AtkMessageBoxReceiveEventSig, AtkMessageBoxReceiveEventDetour, "AtkMessageBoxReceiveEvent");
    }

    private void StartPreventLobbyExitStartupHookCreation()
    {
        lock (preventLobbyExitStartupArmingLock)
        {
            preventLobbyExitStartupArmingPending = true;
            if (preventLobbyExitStartupHookTask == null)
            {
                preventLobbyExitStartupHookCancellation?.Dispose();
                preventLobbyExitStartupHookCancellation = new System.Threading.CancellationTokenSource();
                preventLobbyExitStartupHookTask = Plugin.RunOnGameThread(
                    CreateAtkMessageBoxHook,
                    "Prevent Lobby Exit startup hook creation",
                    preventLobbyExitStartupHookCancellation.Token);
            }
        }
    }

    private void CancelPreventLobbyExitStartupArming(bool disposeCompletedResult)
    {
        System.Threading.Tasks.Task<Hook<AtkMessageBoxReceiveEventDelegate>?>? task;
        System.Threading.CancellationTokenSource? cancellation;
        lock (preventLobbyExitStartupArmingLock)
        {
            preventLobbyExitStartupArmingPending = false;
            task = preventLobbyExitStartupHookTask;
            preventLobbyExitStartupHookTask = null;
            cancellation = preventLobbyExitStartupHookCancellation;
            preventLobbyExitStartupHookCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();

        if (!disposeCompletedResult || task == null)
            return;

        DisposePreventLobbyExitStartupHookTaskResult(task);
    }

    private static void DisposePreventLobbyExitStartupHookTaskResult(System.Threading.Tasks.Task<Hook<AtkMessageBoxReceiveEventDelegate>?> task)
    {
        if (task.IsCompleted)
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                _ = Plugin.RunOnGameThread(() => DisposeHook(task.Result), "Dispose cancelled Prevent Lobby Exit startup hook");
            return;
        }

        task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    _ = Plugin.RunOnGameThread(() => DisposeHook(completedTask.Result), "Dispose cancelled Prevent Lobby Exit startup hook");
            },
            System.Threading.Tasks.TaskScheduler.Default);
    }

    private void ProcessPreventLobbyExitStartupArming()
    {
        System.Threading.Tasks.Task<Hook<AtkMessageBoxReceiveEventDelegate>?>? task;
        lock (preventLobbyExitStartupArmingLock)
            task = preventLobbyExitStartupHookTask;

        if (task == null)
        {
            CancelPreventLobbyExitStartupArming(disposeCompletedResult: false);
            return;
        }

        if (!task.IsCompleted)
            return;

        Hook<AtkMessageBoxReceiveEventDelegate>? createdHook = null;
        try
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                createdHook = task.Result;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Prevent Lobby Exit startup hook initialization failed.");
        }

        System.Threading.CancellationTokenSource? completedCancellation;
        lock (preventLobbyExitStartupArmingLock)
        {
            preventLobbyExitStartupHookTask = null;
            preventLobbyExitStartupArmingPending = false;
            completedCancellation = preventLobbyExitStartupHookCancellation;
            preventLobbyExitStartupHookCancellation = null;
        }
        completedCancellation?.Dispose();

        atkMessageBoxHookInitialized = true;

        if (disposed || !preventLobbyExitEnabled)
        {
            DisposeHook(createdHook);
            return;
        }

        atkMessageBoxReceiveEventHook = createdHook;
        if (atkMessageBoxReceiveEventHook == null)
        {
            preventLobbyExitEnabled = false;
            PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("SA05686B931194474"));
            return;
        }

        UpdateAtkMessageBoxHookState();
        PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, (atkMessageBoxReceiveEventHook.IsEnabled ? global::XASlave.Localization.UiMessage.Key("S6F3A6B8B0896251C") : global::XASlave.Localization.UiMessage.Key("SD254F9326D115E80")));
    }

    private void EnsureAgentMapHookInitialized()
    {
        if (agentMapHookInitialized && agentMapUpdateHook != null)
            return;

        agentMapHookInitialized = true;
        agentMapUpdateHook ??= TryCreateHook<AgentMapUpdateDelegate>(Sigs.AgentMapUpdateSig, AgentMapUpdateDetour, "AgentMapUpdate");
    }

    private void EnsureBackgroundRenderingHooksInitialized()
    {
        if (!deviceDx11PostTickHookInitialized || deviceDx11PostTickHook == null)
        {
            deviceDx11PostTickHookInitialized = true;
            deviceDx11PostTickHook ??= TryCreateHook<DeviceDx11PostTickDelegate>(Sigs.DeviceDx11PostTickSig, DeviceDx11PostTickDetour, "DeviceDX11PostTick");
        }

        if (!namePlateDrawHookInitialized || namePlateDrawHook == null)
        {
            namePlateDrawHookInitialized = true;
            namePlateDrawHook ??= TryCreateHook<NamePlateDrawDelegate>(Sigs.NamePlateDrawSig, NamePlateDrawDetour, "NamePlateDraw");
        }
    }

    private void EnsureToggleFadeDelegateInitialized(bool retryMissing = false)
    {
        if (toggleFadeDelegateInitialized && !(retryMissing && toggleFadeDelegate == null))
            return;

        toggleFadeDelegateInitialized = true;

        try
        {
            if (sigScanner.TryScanText(Sigs.ToggleFadeSig, out var toggleFadeAddress) && toggleFadeAddress != nint.Zero)
            {
                toggleFadeDelegate = Marshal.GetDelegateForFunctionPointer<ToggleFadeDelegate>(toggleFadeAddress);
            }
            else
            {
                log.Warning("[XASlave] Special Rendering Modes could not resolve the world fade delegate; UI visibility controls remain available. Retry with a world fade action.");
            }
        }
        catch (Exception ex)
        {
            toggleFadeDelegate = null;
            log.Warning(ex, "[XASlave] Failed to resolve SpecialRenderMode toggle delegate.");
        }
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] System Window Mods could not resolve {label}; retry by disabling and re-enabling the feature.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(address, detour);
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
        EnsureToggleFadeDelegateInitialized(retryMissing: true);
        var frameworkInstance = Framework.Instance();
        if (toggleFadeDelegate == null || frameworkInstance == null)
            return false;

        var environmentManager = frameworkInstance->EnvironmentManager;
        if (environmentManager == null)
            return false;

        toggleFadeDelegate(environmentManager, hidden ? 1 : 0, 0.1f, &color);
        return true;
    }

    public bool SetSpecialRenderUiVisibility(UiFlags flags, bool visible)
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

    private static void DisposeHook<T>(Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var totalStopwatch = (Plugin.Instance?.Configuration.VerboseTaskLogging ?? false)
            ? Stopwatch.StartNew()
            : null;
        if (allowMultipleGameInstancesStartupPending)
            MeasureFrameworkUpdateStep("SystemWindowMods.AllowMultipleGameInstancesStartupCleanup", ProcessAllowMultipleGameInstancesStartupCleanup);

        if (cancelLoginCooldownStartupArmingPending)
            MeasureFrameworkUpdateStep("SystemWindowMods.CancelLoginCooldownStartupArming", ProcessCancelLoginCooldownStartupArming);

        if (preventLobbyExitStartupArmingPending)
            MeasureFrameworkUpdateStep("SystemWindowMods.PreventLobbyExitStartupArming", ProcessPreventLobbyExitStartupArming);

        if (cancelLoginCooldownEnabled
            && !clientState.IsLoggedIn
            && (agentLobbyUpdateHook == null || agentLobbyUpdateHook.IsDisposed || !agentLobbyUpdateHook.IsEnabled))
        {
            if (!agentLobbyHookInitialized)
            {
                CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("S4B3A10AB78A29118"));
                StartCancelLoginCooldownStartupHookCreation();
            }
            else
            {
                MeasureFrameworkUpdateStep("SystemWindowMods.CancelLoginCooldownLobbyArm", UpdateAgentLobbyHookState);
                if (agentLobbyUpdateHook != null && !agentLobbyUpdateHook.IsDisposed && agentLobbyUpdateHook.IsEnabled)
                    CancelLoginCooldownStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCancelLoginCooldownStatusText, global::XASlave.Localization.UiMessage.Key("S28F95D3E4A862F89"));
            }
        }

        if (preventLobbyExitEnabled
            && !clientState.IsLoggedIn
            && (atkMessageBoxReceiveEventHook == null || atkMessageBoxReceiveEventHook.IsDisposed || !atkMessageBoxReceiveEventHook.IsEnabled))
        {
            if (!atkMessageBoxHookInitialized)
            {
                PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("SD98EB49BAF2BF0F4"));
                StartPreventLobbyExitStartupHookCreation();
            }
            else
            {
                MeasureFrameworkUpdateStep("SystemWindowMods.PreventLobbyExitLobbyArm", UpdateAtkMessageBoxHookState);
                if (atkMessageBoxReceiveEventHook != null && !atkMessageBoxReceiveEventHook.IsDisposed && atkMessageBoxReceiveEventHook.IsEnabled)
                    PreventLobbyExitStatusText = global::XASlave.Localization.UiMessage.Store(ref uiPreventLobbyExitStatusText, global::XASlave.Localization.UiMessage.Key("S6F3A6B8B0896251C"));
            }
        }

        if (ShouldSynchronizeWindowSize())
            MeasureFrameworkUpdateStep("SystemWindowMods.UpdateWindowSizeSynchronization", UpdateWindowSizeSynchronization);

        if (disableTitleScreenMovieEnabled)
            MeasureFrameworkUpdateStep("SystemWindowMods.UpdateDisableTitleScreenMovie", UpdateDisableTitleScreenMovie);

        if (lowResolutionEnabled)
        {
            if (lowResolutionDisablePending)
                MeasureFrameworkUpdateStep("SystemWindowMods.UpdateLowResolutionDisableReset", UpdateLowResolutionDisableReset);
            else
                MeasureFrameworkUpdateStep("SystemWindowMods.UpdateLowResolutionScale", UpdateLowResolutionScale);
        }

        if (disableBackgroundRenderingEnabled
            && disableBackgroundRenderingDisableWhenArMultiIsOn
            && MeasureFrameworkUpdateStep("SystemWindowMods.RefreshAutoRetainerMultiModeCache", () => RefreshAutoRetainerMultiModeCache(false)))
        {
            DisableBackgroundRenderingStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableBackgroundRenderingStatusText, GetBackgroundRenderingStatusText());
        }

        if (totalStopwatch != null)
        {
            totalStopwatch.Stop();
            LogFrameworkUpdateStepDuration("SystemWindowMods.OnFrameworkUpdate", totalStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void MeasureFrameworkUpdateStep(string label, Action action)
    {
        if (!(Plugin.Instance?.Configuration.VerboseTaskLogging ?? false))
        {
            action();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        LogFrameworkUpdateStepDuration(label, stopwatch.Elapsed.TotalMilliseconds);
    }

    private bool MeasureFrameworkUpdateStep(string label, Func<bool> func)
    {
        if (!(Plugin.Instance?.Configuration.VerboseTaskLogging ?? false))
            return func();

        var stopwatch = Stopwatch.StartNew();
        var result = func();
        stopwatch.Stop();
        LogFrameworkUpdateStepDuration(label, stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    private void LogFrameworkUpdateStepDuration(string label, double elapsedMilliseconds)
    {
        if (!(Plugin.Instance?.Configuration.VerboseTaskLogging ?? false)
            || elapsedMilliseconds < FrameworkUpdateTimingDebugThresholdMilliseconds)
            return;

        log.Debug($"[XASlave] Framework tick step '{label}' took {elapsedMilliseconds:F1}ms.");
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

    private bool ShouldSynchronizeWindowSize()
    {
        return ignoreMinimumWindowSizeEnabled || customResolutionsEnabled;
    }

    private bool ShouldUseLoweredMinimumWindowSize()
    {
        return ignoreMinimumWindowSizeEnabled;
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

        if (ShouldUseLoweredMinimumWindowSize())
        {
            gameWindow->MinWidth = SafeMinimumWindowWidth;
            gameWindow->MinHeight = SafeMinimumWindowHeight;

            if (gameWindow->WindowHandle != nint.Zero
                && TryGetClientSize(gameWindow->WindowHandle, out var clientWidth, out var clientHeight))
            {
                TryClampClientSizeToSafeMinimum(gameWindow, clientWidth, clientHeight);
            }

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

            if (ShouldUseLoweredMinimumWindowSize() && !IsIconic(gameWindow->WindowHandle))
                TryClampClientSizeToSafeMinimum(gameWindow, clientWidth, clientHeight);

            return;
        }

        if (clientWidth != lastObservedClientWidth || clientHeight != lastObservedClientHeight)
        {
            lastObservedClientWidth = clientWidth;
            lastObservedClientHeight = clientHeight;
            lastWindowSizeChangeTick = currentTick;
            pendingWindowSizeSynchronization = true;

            if (ShouldUseLoweredMinimumWindowSize()
                && !IsIconic(gameWindow->WindowHandle)
                && TryClampClientSizeToSafeMinimum(gameWindow, clientWidth, clientHeight))
                return;

            return;
        }

        if (!pendingWindowSizeSynchronization || currentTick - lastWindowSizeChangeTick < WindowSizeSyncDebounceMilliseconds)
            return;

        if (IsIconic(gameWindow->WindowHandle))
            return;

        if (ShouldUseLoweredMinimumWindowSize() && TryClampClientSizeToSafeMinimum(gameWindow, clientWidth, clientHeight))
            return;

        if (ShouldUseLoweredMinimumWindowSize() && (clientWidth < SafeMinimumWindowWidth || clientHeight < SafeMinimumWindowHeight))
            return;

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
            IgnoreMinimumWindowSizeStatusText = global::XASlave.Localization.UiMessage.Store(ref uiIgnoreMinimumWindowSizeStatusText, GetIgnoreMinimumWindowSizeStatusText());

        if (customResolutionsEnabled)
            CustomResolutionsStatusText = global::XASlave.Localization.UiMessage.Store(ref uiCustomResolutionsStatusText, GetCustomResolutionsStatusText());

    }

    private void UpdateDisableTitleScreenMovie()
    {
        if (!disableTitleScreenMovieEnabled)
            return;

        if (clientState.IsLoggedIn)
        {
            DisableTitleScreenMovieStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableTitleScreenMovieStatusText, global::XASlave.Localization.UiMessage.Key("S61655DD6836FCCF7"));
            return;
        }

        var agentLobby = AgentLobby.Instance();
        if (agentLobby == null)
        {
            DisableTitleScreenMovieStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableTitleScreenMovieStatusText, global::XASlave.Localization.UiMessage.Key("S182CA192262426CD"));
            return;
        }

        agentLobby->IdleTime = 0;
        DisableTitleScreenMovieStatusText = global::XASlave.Localization.UiMessage.Store(ref uiDisableTitleScreenMovieStatusText, global::XASlave.Localization.UiMessage.Key("SF17B5DCEF6D7D2D8"));
    }

    private bool TryClampClientSizeToSafeMinimum(GameWindow* gameWindow, int clientWidth, int clientHeight)
    {
        return TryClampClientSizeToMinimum(gameWindow, clientWidth, clientHeight, SafeMinimumWindowWidth, SafeMinimumWindowHeight, "clamp the undersized client window");
    }

    private void UpdateLowResolutionScale()
    {
        var graphicsConfig = GraphicsConfig.Instance();
        if (graphicsConfig == null)
        {
            LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, global::XASlave.Localization.UiMessage.Key("S55FF6F244E28A5C8"));
            return;
        }

        var forcedFallbackUpscaler = EnsureLowResolutionUpscaleType(graphicsConfig);
        var normalizedScale = ClampLowResolutionScale(lowResolutionScale);
        if (Math.Abs(GetGraphicsResScale(graphicsConfig) - normalizedScale) > 0.0001f)
            SetGraphicsResScale(graphicsConfig, normalizedScale);

        LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, (forcedFallbackUpscaler ? global::XASlave.Localization.UiMessage.Format("S4E9D5F5AB60230A7", normalizedScale) : global::XASlave.Localization.UiMessage.Format("S66CD26E3EA384990", normalizedScale)));
    }

    private void BeginLowResolutionDisable()
    {
        lowResolutionEnabled = true;
        lowResolutionDisablePending = true;
        lowResolutionDisableResetFramesRemaining = LowResolutionDisableResetFrames;

        if (!UpdateLowResolutionDisableResetScale())
        {
            FinishLowResolutionDisable();
            return;
        }

        LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, global::XASlave.Localization.UiMessage.Key("S04742446DE218D53"));
    }

    private void UpdateLowResolutionDisableReset()
    {
        if (!UpdateLowResolutionDisableResetScale())
        {
            FinishLowResolutionDisable();
            return;
        }

        if (lowResolutionDisableResetFramesRemaining > 0)
        {
            lowResolutionDisableResetFramesRemaining--;
            LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, global::XASlave.Localization.UiMessage.Key("S04742446DE218D53"));
            return;
        }

        FinishLowResolutionDisable();
    }

    private bool UpdateLowResolutionDisableResetScale()
    {
        var graphicsConfig = GraphicsConfig.Instance();
        if (graphicsConfig == null)
        {
            LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, global::XASlave.Localization.UiMessage.Key("S55FF6F244E28A5C8"));
            return false;
        }

        if (Math.Abs(GetGraphicsResScale(graphicsConfig) - DisabledLowResolutionScale) > 0.0001f)
            SetGraphicsResScale(graphicsConfig, DisabledLowResolutionScale);

        return true;
    }

    private void FinishLowResolutionDisable(bool forceDisabledScale = false)
    {
        lowResolutionEnabled = false;
        lowResolutionDisablePending = false;
        lowResolutionDisableResetFramesRemaining = 0;
        RestoreLowResolutionConfiguration(forceDisabledScale);
        LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, (forceDisabledScale ? global::XASlave.Localization.UiMessage.Key("S087285C32FE87651") : global::XASlave.Localization.UiMessage.Key("S75DF19FEE3B3B4C3")));
    }

    private void DisableLowResolutionForPluginUnload()
    {
        if (!lowResolutionEnabled && !lowResolutionDisablePending && !capturedLowResolutionUpscaleType)
            return;

        UpdateLowResolutionDisableResetScale();
        FinishLowResolutionDisable(forceDisabledScale: true);
        LowResolutionStatusText = global::XASlave.Localization.UiMessage.Store(ref uiLowResolutionStatusText, global::XASlave.Localization.UiMessage.Key("SA629787850F336E4"));
    }

    private void RestoreLowResolutionConfiguration(bool forceDisabledScale = false)
    {
        try
        {
            var graphicsConfig = GraphicsConfig.Instance();
            if (graphicsConfig == null)
            {
                capturedLowResolutionUpscaleType = false;
                originalLowResolutionUpscaleType = 0;
                return;
            }

            var restoredScale = forceDisabledScale
                ? DisabledLowResolutionScale
                : Math.Max(ClampLowResolutionScale(gameConfig.System.GetUInt(GraphicsResScaleConfigKey) / 100f), DisabledLowResolutionScale);
            SetGraphicsResScale(graphicsConfig, restoredScale);

            if (capturedLowResolutionUpscaleType)
                SetGraphicsResUpscaleType(graphicsConfig, originalLowResolutionUpscaleType);

            if (forceDisabledScale)
                SetGraphicsResScale(graphicsConfig, DisabledLowResolutionScale);

            capturedLowResolutionUpscaleType = false;
            originalLowResolutionUpscaleType = 0;
        }
        catch (Exception ex)
        {
            capturedLowResolutionUpscaleType = false;
            originalLowResolutionUpscaleType = 0;
            log.Warning(ex, "[XASlave] Failed to restore Low Resolution graphics configuration.");
        }
    }

    private bool EnsureLowResolutionUpscaleType(GraphicsConfig* graphicsConfig)
    {
        if (!capturedLowResolutionUpscaleType)
        {
            capturedLowResolutionUpscaleType = true;
            originalLowResolutionUpscaleType = GetGraphicsResUpscaleType(graphicsConfig);
        }

        if (GetGraphicsResUpscaleType(graphicsConfig) <= MaximumSupportedLowResolutionUpscaleType)
            return false;

        SetGraphicsResUpscaleType(graphicsConfig, LowResolutionFallbackUpscaleType);
        return true;
    }

    private static float GetGraphicsResScale(GraphicsConfig* graphicsConfig)
    {
        return graphicsConfig->Graphics\u0052\u0065\u007A\u006FScale;
    }

    private static void SetGraphicsResScale(GraphicsConfig* graphicsConfig, float scale)
    {
        graphicsConfig->Graphics\u0052\u0065\u007A\u006FScale = scale;
    }

    private static byte GetGraphicsResUpscaleType(GraphicsConfig* graphicsConfig)
    {
        return graphicsConfig->Graphics\u0052\u0065\u007A\u006FUpscaleType;
    }

    private static void SetGraphicsResUpscaleType(GraphicsConfig* graphicsConfig, byte upscaleType)
    {
        graphicsConfig->Graphics\u0052\u0065\u007A\u006FUpscaleType = upscaleType;
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

    private void CaptureOriginalClientSize(GameWindow* gameWindow)
    {
        if (capturedOriginalClientSize
            || gameWindow->WindowHandle == nint.Zero
            || !TryGetClientSize(gameWindow->WindowHandle, out var clientWidth, out var clientHeight))
            return;

        capturedOriginalClientSize = true;
        originalClientWidth = clientWidth;
        originalClientHeight = clientHeight;
    }

    private void RestoreWindowSizeLimits(bool clampClientToMinimum = false)
    {
        if (!capturedWindowSize)
            return;

        var gameWindow = GameWindow.Instance();
        if (gameWindow == null)
            return;

        var restoredMinimumWidth = GetRestoredMinimumWindowWidth();
        var restoredMinimumHeight = GetRestoredMinimumWindowHeight();
        gameWindow->MinWidth = restoredMinimumWidth;
        gameWindow->MinHeight = restoredMinimumHeight;

        if (!clampClientToMinimum
            || gameWindow->WindowHandle == nint.Zero
            || restoredMinimumWidth <= 0
            || restoredMinimumHeight <= 0
            || IsIconic(gameWindow->WindowHandle)
            || !TryGetClientSize(gameWindow->WindowHandle, out var clientWidth, out var clientHeight))
            return;

        TryClampClientSizeToMinimum(gameWindow, clientWidth, clientHeight, restoredMinimumWidth, restoredMinimumHeight, "restore the original minimum client window size");
    }

    private int GetRestoredMinimumWindowWidth()
    {
        return capturedWindowSize
            ? Math.Max(originalMinWidth, DefaultGameMinimumWindowWidth)
            : DefaultGameMinimumWindowWidth;
    }

    private int GetRestoredMinimumWindowHeight()
    {
        return capturedWindowSize
            ? Math.Max(originalMinHeight, DefaultGameMinimumWindowHeight)
            : DefaultGameMinimumWindowHeight;
    }

    private bool TryClampClientSizeToMinimum(GameWindow* gameWindow, int clientWidth, int clientHeight, int minimumWidth, int minimumHeight, string actionDescription)
    {
        if (gameWindow == null || gameWindow->WindowHandle == nint.Zero || minimumWidth <= 0 || minimumHeight <= 0)
            return false;

        var clampedWidth = Math.Max(clientWidth, minimumWidth);
        var clampedHeight = Math.Max(clientHeight, minimumHeight);
        if (clampedWidth == clientWidth && clampedHeight == clientHeight)
            return false;

        if (!TryResizeClientWindow(gameWindow->WindowHandle, clampedWidth, clampedHeight))
        {
            log.Warning($"[XASlave] Failed to {actionDescription} from {clientWidth}x{clientHeight} back to {clampedWidth}x{clampedHeight}.");
            return false;
        }

        SynchronizeGameWindowSize(gameWindow, clampedWidth, clampedHeight);
        UpdateObservedWindowSizeState(clampedWidth, clampedHeight);
        return true;
    }

    private bool AtkMessageBoxReceiveEventDetour(AtkMessageBoxManager* manager, nint a2, AtkValue* values)
    {
        try
        {
            if (preventLobbyExitEnabled && values != null)
                values->UInt = 16000;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] System Window Mods message-box detour failed; calling the original.");
        }

        return atkMessageBoxReceiveEventHook?.OriginalDisposeSafe(manager, a2, values) ?? false;
    }

    private void AgentLobbyUpdateDetour(AgentLobby* agent, uint deltaTime)
    {
        try
        {
            if (cancelLoginCooldownEnabled && agent != null)
                agent->TemporaryLocked = false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] System Window Mods lobby detour failed before the original call.");
        }

        agentLobbyUpdateHook?.OriginalDisposeSafe(agent, deltaTime);

        try
        {
            if (cancelLoginCooldownEnabled && agent != null)
                agent->TemporaryLocked = false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] System Window Mods lobby detour failed after the original call.");
        }
    }

    private void AgentMapUpdateDetour(AgentMap* agent, uint updateCount)
    {
        try
        {
            if (revealUndiscoveredAreasEnabled && agent != null)
            {
                agent->CurrentMapDiscoveryFlag = 0;
                agent->SelectedMapDiscoveryFlag = 0;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] System Window Mods map detour failed before the original call.");
        }

        agentMapUpdateHook?.OriginalDisposeSafe(agent, updateCount);

        try
        {
            if (revealUndiscoveredAreasEnabled && agent != null)
            {
                agent->CurrentMapDiscoveryFlag = 0;
                agent->SelectedMapDiscoveryFlag = 0;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] System Window Mods map detour failed after the original call.");
        }
    }

    private void DeviceDx11PostTickDetour(nint instance)
    {
        var callOriginal = true;
        try
        {
            if (!disableBackgroundRenderingEnabled)
            {
                backgroundRenderingSuppressed = false;
            }
            else
            {
                var framework = Framework.Instance();
                if (framework == null || !clientState.IsLoggedIn)
                {
                    backgroundRenderingSuppressed = false;
                }
                // The cache is refreshed from Framework.Update. Detours must not invoke
                // cross-plugin IPC or perform any other unbounded external work.
                else if (disableBackgroundRenderingDisableWhenArMultiIsOn
                         && cachedAutoRetainerMultiModeEnabled)
                {
                    backgroundRenderingSuppressed = false;
                }
                else
                {
                    var currentTick = Environment.TickCount64;
                    if (nextBackgroundRenderTick - currentTick < 0)
                    {
                        nextBackgroundRenderTick = currentTick + 5_000;
                        backgroundRenderingSuppressed = false;
                    }
                    else
                    {
                        var shouldSuppress = disableBackgroundRenderingOnlyWhenMinimized
                            ? framework->GameWindow != null && IsIconic(framework->GameWindow->WindowHandle)
                            : framework->WindowInactive;

                        if (shouldSuppress)
                        {
                            backgroundRenderingSuppressed = true;
                            callOriginal = false;
                        }
                        else
                        {
                            backgroundRenderingSuppressed = false;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            backgroundRenderingSuppressed = false;
            callOriginal = true;
            log.Warning(ex, "[XASlave] System Window Mods background-render detour failed; calling the original.");
        }

        if (callOriginal)
            deviceDx11PostTickHook?.OriginalDisposeSafe(instance);
    }

    private void NamePlateDrawDetour(AtkUnitBase* addon)
    {
        try
        {
            if (disableBackgroundRenderingEnabled && backgroundRenderingSuppressed)
                return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] System Window Mods nameplate detour failed; calling the original.");
        }

        namePlateDrawHook?.OriginalDisposeSafe(addon);
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
                    if (!NativeBufferGrowthPolicy.TryGetNextSize(bufferSize, returnSize, out var nextBufferSize))
                        throw new InvalidOperationException($"NtQueryInformationProcess returned an invalid buffer growth request ({bufferSize} -> {returnSize}).");

                    bufferSize = nextBufferSize;
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
