using System;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class NoUiFadeService : IDisposable
{
    private readonly IFramework framework;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly object startupArmingLock = new();
    private const int TotalHookSurfaces = 5;

    private Hook<FadeMiddleBackDrawDelegate>? fadeMiddleBackDrawHook;
    private Hook<WhiteFadeInDelegate>? whiteFadeInHook;
    private Hook<WhiteFadeOutDelegate>? whiteFadeOutHook;
    private Hook<EventFadeInDelegate>? eventFadeInHook;
    private Hook<EventFadeOutDelegate>? eventFadeOutHook;

    private bool initialized;
    private bool enabled;
    private bool startupArmingPending;
    private bool startupArmingSubscribed;
    private bool disposed;
    private Task<StartupHookResult>? startupHookTask;
    private int availableHookSurfaces;
    private long suppressedFadeCalls;

    public NoUiFadeService(
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.framework = framework;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool IsStartupArmingPending => startupArmingPending;

    public bool SetEnabled(bool value)
    {
        if (value && startupArmingPending)
            return true;

        if (value == enabled)
        {
            RefreshStatusText();
            return enabled;
        }

        if (!value)
        {
            CancelStartupArming(disposeCompletedResult: true);
            enabled = false;
            ToggleAllHooks(false);
            StatusText = "Disabled";
            return false;
        }

        CancelStartupArming(disposeCompletedResult: true);
        EnsureInitialized();
        if (availableHookSurfaces == 0)
        {
            enabled = false;
            StatusText = "Unavailable - no UI fade hooks could be resolved.";
            return false;
        }

        var activeSurfaces = ToggleAllHooks(true);
        if (activeSurfaces == 0)
        {
            enabled = false;
            StatusText = "Unavailable - UI fade hooks could not be enabled.";
            return false;
        }

        enabled = true;
        RefreshStatusText(activeSurfaces);
        return true;
    }

    public bool RestoreEnabledOnStartup()
    {
        if (startupArmingPending)
            return true;

        if (initialized)
            return SetEnabled(true);

        enabled = true;
        StatusText = "Arming - UI fade hooks are initializing outside the framework tick.";
        StartStartupHookCreation();
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        CancelStartupArming(disposeCompletedResult: true);
        enabled = false;
        DisposeHook(ref fadeMiddleBackDrawHook);
        DisposeHook(ref whiteFadeInHook);
        DisposeHook(ref whiteFadeOutHook);
        DisposeHook(ref eventFadeInHook);
        DisposeHook(ref eventFadeOutHook);
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        fadeMiddleBackDrawHook = TryCreateHook<FadeMiddleBackDrawDelegate>(Sigs.FadeMiddleBackDrawSig, FadeMiddleBackDrawDetour, "FadeMiddleBackDraw");
        whiteFadeInHook = TryCreateHook<WhiteFadeInDelegate>(Sigs.WhiteFadeInSig, WhiteFadeInDetour, "WhiteFadeIn");
        whiteFadeOutHook = TryCreateHook<WhiteFadeOutDelegate>(Sigs.WhiteFadeOutSig, WhiteFadeOutDetour, "WhiteFadeOut");
        eventFadeInHook = TryCreateHook<EventFadeInDelegate>(Sigs.EventFadeInSig, EventFadeInDetour, "EventFadeIn");
        eventFadeOutHook = TryCreateHook<EventFadeOutDelegate>(Sigs.EventFadeOutSig, EventFadeOutDetour, "EventFadeOut");

        availableHookSurfaces = CountAvailableHookSurfaces();
        log.Debug($"[XASlave] No UI Fade scan resolved {availableHookSurfaces}/{TotalHookSurfaces} hook surface(s).");
    }

    private StartupHookResult CreateStartupHookResult()
    {
        return new StartupHookResult(
            TryCreateHook<FadeMiddleBackDrawDelegate>(Sigs.FadeMiddleBackDrawSig, FadeMiddleBackDrawDetour, "FadeMiddleBackDraw"),
            TryCreateHook<WhiteFadeInDelegate>(Sigs.WhiteFadeInSig, WhiteFadeInDetour, "WhiteFadeIn"),
            TryCreateHook<WhiteFadeOutDelegate>(Sigs.WhiteFadeOutSig, WhiteFadeOutDetour, "WhiteFadeOut"),
            TryCreateHook<EventFadeInDelegate>(Sigs.EventFadeInSig, EventFadeInDetour, "EventFadeIn"),
            TryCreateHook<EventFadeOutDelegate>(Sigs.EventFadeOutSig, EventFadeOutDetour, "EventFadeOut"));
    }

    private void StartStartupHookCreation()
    {
        lock (startupArmingLock)
        {
            startupArmingPending = true;
            startupHookTask ??= Task.Run(CreateStartupHookResult);
        }

        SubscribeStartupArming();
    }

    private void SubscribeStartupArming()
    {
        if (startupArmingSubscribed)
            return;

        framework.Update += OnStartupArmingFrameworkUpdate;
        startupArmingSubscribed = true;
    }

    private void UnsubscribeStartupArming()
    {
        if (!startupArmingSubscribed)
            return;

        framework.Update -= OnStartupArmingFrameworkUpdate;
        startupArmingSubscribed = false;
    }

    private void CancelStartupArming(bool disposeCompletedResult)
    {
        Task<StartupHookResult>? task;
        lock (startupArmingLock)
        {
            startupArmingPending = false;
            task = startupHookTask;
            startupHookTask = null;
        }

        UnsubscribeStartupArming();

        if (!disposeCompletedResult || task == null)
            return;

        DisposeStartupHookTaskResult(task);
    }

    private static void DisposeStartupHookTaskResult(Task<StartupHookResult> task)
    {
        if (task.IsCompleted)
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.DisposeHooks();
            return;
        }

        task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == TaskStatus.RanToCompletion)
                    completedTask.Result.DisposeHooks();
            },
            TaskScheduler.Default);
    }

    private void OnStartupArmingFrameworkUpdate(IFramework _)
    {
        Task<StartupHookResult>? task;
        lock (startupArmingLock)
            task = startupHookTask;

        if (task == null)
        {
            CancelStartupArming(disposeCompletedResult: false);
            return;
        }

        if (!task.IsCompleted)
            return;

        StartupHookResult? result = null;
        try
        {
            if (task.Status == TaskStatus.RanToCompletion)
                result = task.Result;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] No UI Fade startup hook initialization failed.");
        }

        lock (startupArmingLock)
        {
            startupHookTask = null;
            startupArmingPending = false;
        }

        UnsubscribeStartupArming();
        initialized = true;

        if (result == null)
        {
            enabled = false;
            StatusText = "Unavailable - UI fade hooks could not be initialized.";
            return;
        }

        if (disposed || !enabled)
        {
            result.DisposeHooks();
            return;
        }

        fadeMiddleBackDrawHook = result.FadeMiddleBackDrawHook;
        whiteFadeInHook = result.WhiteFadeInHook;
        whiteFadeOutHook = result.WhiteFadeOutHook;
        eventFadeInHook = result.EventFadeInHook;
        eventFadeOutHook = result.EventFadeOutHook;
        availableHookSurfaces = CountAvailableHookSurfaces();
        log.Debug($"[XASlave] No UI Fade scan resolved {availableHookSurfaces}/{TotalHookSurfaces} hook surface(s).");

        if (availableHookSurfaces == 0)
        {
            enabled = false;
            StatusText = "Unavailable - no UI fade hooks could be resolved.";
            return;
        }

        var activeSurfaces = ToggleAllHooks(true);
        if (activeSurfaces == 0)
        {
            enabled = false;
            StatusText = "Unavailable - UI fade hooks could not be enabled.";
            return;
        }

        enabled = true;
        RefreshStatusText(activeSurfaces);
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Debug($"[XASlave] No UI Fade could not find {label}; this hook surface will stay disabled.");
                return null;
            }

            return interopProvider.HookFromAddress<T>(address, detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] No UI Fade failed to create {label} hook.");
            return null;
        }
    }

    private int CountAvailableHookSurfaces()
    {
        var count = 0;
        count += IsHookAvailable(fadeMiddleBackDrawHook) ? 1 : 0;
        count += IsHookAvailable(whiteFadeInHook) ? 1 : 0;
        count += IsHookAvailable(whiteFadeOutHook) ? 1 : 0;
        count += IsHookAvailable(eventFadeInHook) ? 1 : 0;
        count += IsHookAvailable(eventFadeOutHook) ? 1 : 0;
        return count;
    }

    private int ToggleAllHooks(bool targetEnabled)
    {
        var activeSurfaces = 0;
        activeSurfaces += ToggleHook(fadeMiddleBackDrawHook, targetEnabled, "FadeMiddleBackDraw");
        activeSurfaces += ToggleHook(whiteFadeInHook, targetEnabled, "WhiteFadeIn");
        activeSurfaces += ToggleHook(whiteFadeOutHook, targetEnabled, "WhiteFadeOut");
        activeSurfaces += ToggleHook(eventFadeInHook, targetEnabled, "EventFadeIn");
        activeSurfaces += ToggleHook(eventFadeOutHook, targetEnabled, "EventFadeOut");
        return activeSurfaces;
    }

    private int ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (!IsHookAvailable(hook))
            return 0;

        try
        {
            if (targetEnabled)
            {
                if (!hook!.IsEnabled)
                    hook.Enable();

                return hook.IsEnabled ? 1 : 0;
            }

            if (hook!.IsEnabled)
                hook.Disable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] No UI Fade failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }

        return 0;
    }

    private void RefreshStatusText(int? activeSurfaces = null)
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        var surfaceCount = activeSurfaces ?? CountActiveHookSurfaces();
        StatusText = $"Enabled - suppressing {surfaceCount} UI fade surfaces. Suppressed fades: {suppressedFadeCalls}.";
    }

    private int CountActiveHookSurfaces()
    {
        var count = 0;
        count += IsHookActive(fadeMiddleBackDrawHook) ? 1 : 0;
        count += IsHookActive(whiteFadeInHook) ? 1 : 0;
        count += IsHookActive(whiteFadeOutHook) ? 1 : 0;
        count += IsHookActive(eventFadeInHook) ? 1 : 0;
        count += IsHookActive(eventFadeOutHook) ? 1 : 0;
        return count;
    }

    private static bool IsHookAvailable<T>(Hook<T>? hook)
        where T : Delegate
    {
        return hook is { IsDisposed: false };
    }

    private static bool IsHookActive<T>(Hook<T>? hook)
        where T : Delegate
    {
        return hook is { IsDisposed: false, IsEnabled: true };
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void SuppressFade()
    {
        if (!enabled)
            return;

        suppressedFadeCalls++;
    }

    private void FadeMiddleBackDrawDetour(AtkUnitBase* addon)
    {
        if (!enabled)
        {
            fadeMiddleBackDrawHook?.Original(addon);
            return;
        }

        SuppressFade();
    }

    private nint WhiteFadeInDetour()
    {
        if (!enabled)
            return whiteFadeInHook?.Original() ?? nint.Zero;

        SuppressFade();
        return nint.Zero;
    }

    private nint WhiteFadeOutDetour()
    {
        if (!enabled)
            return whiteFadeOutHook?.Original() ?? nint.Zero;

        SuppressFade();
        return nint.Zero;
    }

    private nint EventFadeInDetour(nint a1)
    {
        if (!enabled)
            return eventFadeInHook?.Original(a1) ?? nint.Zero;

        SuppressFade();
        return nint.Zero;
    }

    private nint EventFadeOutDetour(nint a1, int a2, int a3)
    {
        if (!enabled)
            return eventFadeOutHook?.Original(a1, a2, a3) ?? nint.Zero;

        SuppressFade();
        return nint.Zero;
    }

    private delegate void FadeMiddleBackDrawDelegate(AtkUnitBase* addon);

    private delegate nint WhiteFadeInDelegate();

    private delegate nint WhiteFadeOutDelegate();

    private delegate nint EventFadeInDelegate(nint a1);

    private delegate nint EventFadeOutDelegate(nint a1, int a2, int a3);

    private sealed record StartupHookResult(
        Hook<FadeMiddleBackDrawDelegate>? FadeMiddleBackDrawHook,
        Hook<WhiteFadeInDelegate>? WhiteFadeInHook,
        Hook<WhiteFadeOutDelegate>? WhiteFadeOutHook,
        Hook<EventFadeInDelegate>? EventFadeInHook,
        Hook<EventFadeOutDelegate>? EventFadeOutHook)
    {
        public void DisposeHooks()
        {
            DisposeHook(FadeMiddleBackDrawHook);
            DisposeHook(WhiteFadeInHook);
            DisposeHook(WhiteFadeOutHook);
            DisposeHook(EventFadeInHook);
            DisposeHook(EventFadeOutHook);
        }

        private static void DisposeHook<T>(Hook<T>? hook)
            where T : Delegate
        {
            if (hook is { IsDisposed: false })
                hook.Dispose();
        }
    }
}
