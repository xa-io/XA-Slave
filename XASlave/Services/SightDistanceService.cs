using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Dalamud;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace XASlave.Services;

public unsafe sealed class SightDistanceService : IDisposable
{
    private const double StartupArmingStepDebugThresholdMilliseconds = 5.0;

    private readonly IFramework framework;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly object startupArmingLock = new();

    private Hook<SetActiveCameraDelegate>? setActiveCameraHook;
    private Hook<CameraCurrentSightDistanceDelegate>? cameraCurrentSightDistanceHook;
    private nint cameraCollisionPatchAddress;
    private byte[]? cameraCollisionOriginalBytes;
    private bool cameraCollisionPatchApplied;
    private bool initialized;
    private bool enabled;
    private bool startupArmingPending;
    private bool startupArmingSubscribed;
    private bool disposed;
    private int startupArmingStep;
    private System.Threading.Tasks.Task<StartupHookResult>? startupHookTask;
    private System.Threading.CancellationTokenSource? startupHookCancellation;

    private float minDistance = 1.5f;
    private float maxDistance = 80f;
    private float minRotation = -1.569f;
    private float maxRotation = 1.569f;
    private float minFoV = 0.69f;
    private float maxFoV = 0.78f;
    private float currentFoV = 0.78f;
    private bool ignoreCollision = true;
    private nint capturedCameraAddress;
    private CameraSnapshot? capturedCamera;

    public SightDistanceService(
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

    public bool RestoreEnabledOnStartup()
    {
        if (enabled && startupArmingPending)
            return true;

        if (initialized)
            return SetEnabled(true);

        enabled = true;
        startupArmingPending = true;
        startupArmingStep = 0;
        StartStartupHookCreation();
        SubscribeStartupArming();
        StatusText = "Arming - camera hooks and patch surfaces are initializing on the framework thread.";
        return true;
    }

    public void ApplyConfiguration(
        float maxDistance,
        float minDistance,
        float maxRotation,
        float minRotation,
        float maxFoV,
        float minFoV,
        float currentFoV,
        bool ignoreCollision)
    {
        if (disposed)
            return;

        this.maxDistance = ClampFinite(maxDistance, 1f, 80f, 80f);
        this.minDistance = ClampFinite(minDistance, 0f, this.maxDistance, 1.5f);
        this.maxRotation = ClampFinite(maxRotation, -1.569f, 1.569f, 1.569f);
        this.minRotation = ClampFinite(minRotation, -1.569f, this.maxRotation, -1.483530f);
        this.maxFoV = ClampFinite(maxFoV, 0.01f, 3f, 0.78f);
        this.minFoV = ClampFinite(minFoV, 0.01f, this.maxFoV, 0.69f);
        this.currentFoV = ClampFinite(currentFoV, this.minFoV, this.maxFoV, this.maxFoV);
        this.ignoreCollision = ignoreCollision;

        if (enabled && !startupArmingPending)
        {
            if (!TryUpdateActiveCamera())
                StatusText = GetEnabledStatusText(activeCameraReady: false);

            UpdateCollisionPatch();
        }
    }

    public bool SetEnabled(bool value)
    {
        if (disposed)
        {
            StatusText = "Unavailable - Custom Sight Distance has been disposed.";
            return false;
        }

        if (value == enabled && !startupArmingPending)
            return enabled;

        if (value && startupArmingPending)
            return true;

        if (!value)
        {
            CancelStartupArming(disposeCompletedResult: true);
            enabled = false;
            ToggleHook(setActiveCameraHook, false, "SetActiveCamera");
            ToggleHook(cameraCurrentSightDistanceHook, false, "CameraCurrentSightDistance");
            RestoreCollisionPatch();
            RestoreCapturedCamera();
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized(retryMissing: initialized);
        CancelStartupArming(disposeCompletedResult: true);
        if (setActiveCameraHook == null && cameraCurrentSightDistanceHook == null && cameraCollisionPatchAddress == nint.Zero)
        {
            StatusText = "Unavailable - camera hooks and patch surfaces are missing.";
            return false;
        }

        enabled = true;
        ToggleHook(setActiveCameraHook, true, "SetActiveCamera");
        ToggleHook(cameraCurrentSightDistanceHook, true, "CameraCurrentSightDistance");
        UpdateCollisionPatch();
        StatusText = GetEnabledStatusText(TryUpdateActiveCamera());
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        CancelStartupArming(disposeCompletedResult: true);
        enabled = false;
        RestoreCollisionPatch();
        RestoreCapturedCamera();
        DisposeHook(ref setActiveCameraHook);
        DisposeHook(ref cameraCurrentSightDistanceHook);
    }

    private void EnsureInitialized(bool retryMissing = false)
    {
        if (initialized && !retryMissing)
            return;

        setActiveCameraHook ??= TryCreateHook<SetActiveCameraDelegate>(Sigs.SetActiveCameraSig, SetActiveCameraDetour, "SetActiveCamera");
        cameraCurrentSightDistanceHook ??= TryCreateHook<CameraCurrentSightDistanceDelegate>(Sigs.CameraCurrentSightDistanceSig, CameraCurrentSightDistanceDetour, "CameraCurrentSightDistance");
        if (cameraCollisionPatchAddress == nint.Zero)
            cameraCollisionPatchAddress = TryScanPatchAddress(Sigs.CameraCollisionPatchSig, "CameraCollisionPatch");
        initialized = true;
    }

    private StartupHookResult CreateStartupHookResult()
    {
        return new StartupHookResult(
            TryCreateHook<SetActiveCameraDelegate>(Sigs.SetActiveCameraSig, SetActiveCameraDetour, "SetActiveCamera"),
            TryCreateHook<CameraCurrentSightDistanceDelegate>(Sigs.CameraCurrentSightDistanceSig, CameraCurrentSightDistanceDetour, "CameraCurrentSightDistance"),
            TryScanPatchAddress(Sigs.CameraCollisionPatchSig, "CameraCollisionPatch"));
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] Custom Sight Distance could not resolve {label}; retry by disabling and re-enabling the feature.");
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

    private void ScanPatchAddress(ProtectedSig signature, ref nint address, string label)
    {
        if (address != nint.Zero)
            return;

        address = TryScanPatchAddress(signature, label);
    }

    private nint TryScanPatchAddress(ProtectedSig signature, string label)
    {
        try
        {
            if (sigScanner.TryScanText(signature, out var address) && address != nint.Zero)
                return address;

            log.Warning($"[XASlave] Custom Sight Distance could not resolve {label}; retry by disabling and re-enabling the feature.");
            return nint.Zero;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to scan {label} patch surface.");
            return nint.Zero;
        }
    }

    private void UpdateCollisionPatch()
    {
        if (!enabled || cameraCollisionPatchAddress == nint.Zero)
            return;

        if (ignoreCollision)
        {
            if (cameraCollisionOriginalBytes == null
                && !SafeMemory.ReadBytes((IntPtr)cameraCollisionPatchAddress, 8, out cameraCollisionOriginalBytes))
            {
                log.Warning("[XASlave] Failed to read the original CameraCollisionPatch branch bytes.");
                return;
            }

            if (cameraCollisionOriginalBytes is not { Length: 8 }
                || cameraCollisionOriginalBytes[0] != 0x84
                || cameraCollisionOriginalBytes[1] != 0xC0
                || cameraCollisionOriginalBytes[2] != 0x0F
                || cameraCollisionOriginalBytes[3] != 0x84)
            {
                log.Warning("[XASlave] CameraCollisionPatch matched an unexpected instruction; refusing to patch it.");
                return;
            }

            var originalDisplacement = BitConverter.ToInt32(cameraCollisionOriginalBytes, 4);
            var replacementDisplacement = (long)originalDisplacement + 1;
            if (replacementDisplacement is < int.MinValue or > int.MaxValue)
            {
                log.Warning("[XASlave] CameraCollisionPatch branch displacement is out of range; refusing to patch it.");
                return;
            }

            var replacement = new byte[] { 0x90, 0x90, 0xE9, 0, 0, 0, 0, 0x90 };
            BitConverter.TryWriteBytes(replacement.AsSpan(3, sizeof(int)), (int)replacementDisplacement);
            ApplyPatch(ref cameraCollisionPatchApplied, cameraCollisionPatchAddress, replacement, ref cameraCollisionOriginalBytes, "CameraCollisionPatch");
        }
        else
            RestoreCollisionPatch();
    }

    private void RestoreCollisionPatch()
    {
        RestorePatch(ref cameraCollisionPatchApplied, cameraCollisionPatchAddress, cameraCollisionOriginalBytes, "CameraCollisionPatch");
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        var hookToDispose = hook;
        hook = null;

        if (hookToDispose is { IsDisposed: false })
            hookToDispose.Dispose();
    }

    private void ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return;

        try
        {
            if (targetEnabled)
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
            log.Warning(ex, $"[XASlave] Failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }
    }

    private void ApplyPatch(ref bool applied, nint address, byte[] replacement, ref byte[]? originalBytes, string label)
    {
        if (applied || address == nint.Zero)
            return;

        try
        {
            if (originalBytes == null && !SafeMemory.ReadBytes((IntPtr)address, replacement.Length, out originalBytes))
            {
                log.Warning($"[XASlave] Failed to read original bytes for {label}.");
                return;
            }

            if (!SafeMemory.WriteBytes((IntPtr)address, replacement))
            {
                log.Warning($"[XASlave] Failed to write replacement bytes for {label}.");
                return;
            }

            applied = true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to apply {label} patch.");
        }
    }

    private void RestorePatch(ref bool applied, nint address, byte[]? originalBytes, string label)
    {
        if (!applied || address == nint.Zero || originalBytes == null)
            return;

        try
        {
            if (!SafeMemory.WriteBytes((IntPtr)address, originalBytes))
            {
                log.Warning($"[XASlave] Failed to restore bytes for {label}.");
                return;
            }

            applied = false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to restore {label} patch.");
        }
    }

    private void SubscribeStartupArming()
    {
        if (startupArmingSubscribed)
            return;

        framework.Update += OnStartupArmingFrameworkUpdate;
        startupArmingSubscribed = true;
    }

    private void StartStartupHookCreation()
    {
        lock (startupArmingLock)
        {
            if (startupHookTask != null)
                return;

            startupHookCancellation?.Dispose();
            startupHookCancellation = new System.Threading.CancellationTokenSource();
            startupHookTask = Plugin.RunOnGameThread(
                CreateStartupHookResult,
                "Sight Distance startup hook creation",
                startupHookCancellation.Token);
        }
    }

    private void CancelStartupArming(bool disposeCompletedResult = false)
    {
        System.Threading.Tasks.Task<StartupHookResult>? task;
        System.Threading.CancellationTokenSource? cancellation;
        lock (startupArmingLock)
        {
            task = startupHookTask;
            startupHookTask = null;
            cancellation = startupHookCancellation;
            startupHookCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();

        startupArmingPending = false;
        startupArmingStep = 0;
        if (!startupArmingSubscribed)
        {
            if (disposeCompletedResult && task != null)
                DisposeStartupHookTaskResult(task);
            return;
        }

        framework.Update -= OnStartupArmingFrameworkUpdate;
        startupArmingSubscribed = false;

        if (disposeCompletedResult && task != null)
            DisposeStartupHookTaskResult(task);
    }

    private static void DisposeStartupHookTaskResult(System.Threading.Tasks.Task<StartupHookResult> task)
    {
        if (task.IsCompleted)
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                _ = Plugin.RunOnGameThread(task.Result.DisposeHooks, "Dispose cancelled Sight Distance startup hooks");
            return;
        }

        task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    _ = Plugin.RunOnGameThread(completedTask.Result.DisposeHooks, "Dispose cancelled Sight Distance startup hooks");
            },
            System.Threading.Tasks.TaskScheduler.Default);
    }

    private void OnStartupArmingFrameworkUpdate(IFramework _)
    {
        if (!startupArmingPending)
        {
            CancelStartupArming();
            return;
        }

        ProcessStartupArmingStep();
    }

    private void ProcessStartupArmingStep()
    {
        if (startupArmingStep == 0 && !TryApplyStartupHookResult())
            return;

        var label = GetStartupArmingStepLabel(startupArmingStep);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            switch (startupArmingStep)
            {
                case 0:
                    break;
                case 1:
                    if (setActiveCameraHook == null && cameraCurrentSightDistanceHook == null && cameraCollisionPatchAddress == nint.Zero)
                    {
                        enabled = false;
                        StatusText = "Unavailable - camera hooks and patch surfaces are missing.";
                        CancelStartupArming();
                        return;
                    }

                    break;
                case 2:
                    ToggleHook(setActiveCameraHook, true, "SetActiveCamera");
                    break;
                case 3:
                    ToggleHook(cameraCurrentSightDistanceHook, true, "CameraCurrentSightDistance");
                    break;
                case 4:
                    UpdateCollisionPatch();
                    break;
                default:
                    StatusText = GetEnabledStatusText(TryUpdateActiveCamera());
                    CancelStartupArming();
                    return;
            }

            startupArmingStep++;
        }
        catch (Exception ex)
        {
            enabled = false;
            StatusText = $"Unavailable - startup arming failed at {label}.";
            CancelStartupArming();
            log.Warning(ex, $"[XASlave] Custom Sight Distance startup arming failed at {label}.");
        }
        finally
        {
            stopwatch.Stop();
            LogStartupArmingStepDuration(label, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private bool TryApplyStartupHookResult()
    {
        System.Threading.Tasks.Task<StartupHookResult>? task;
        lock (startupArmingLock)
            task = startupHookTask;

        if (task == null)
        {
            StartStartupHookCreation();
            return false;
        }

        if (!task.IsCompleted)
            return false;

        StartupHookResult? result = null;
        try
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                result = task.Result;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Custom Sight Distance startup hook initialization failed.");
        }

        System.Threading.CancellationTokenSource? completedCancellation;
        lock (startupArmingLock)
        {
            startupHookTask = null;
            completedCancellation = startupHookCancellation;
            startupHookCancellation = null;
        }
        completedCancellation?.Dispose();

        if (disposed || !enabled)
        {
            result?.DisposeHooks();
            CancelStartupArming();
            return false;
        }

        if (result != null)
        {
            setActiveCameraHook = result.SetActiveCameraHook;
            cameraCurrentSightDistanceHook = result.CameraCurrentSightDistanceHook;
            cameraCollisionPatchAddress = result.CameraCollisionPatchAddress;
        }

        initialized = true;
        return true;
    }

    private static string GetStartupArmingStepLabel(int step)
        => step switch
        {
            0 => "Apply prepared camera hook surfaces",
            1 => "Validate surfaces",
            2 => "Enable SetActiveCamera hook",
            3 => "Enable CameraCurrentSightDistance hook",
            4 => "Apply CameraCollisionPatch",
            _ => "Finalize",
        };

    private void LogStartupArmingStepDuration(string label, double elapsedMilliseconds)
    {
        if (!(Plugin.Instance?.Configuration.VerboseTaskLogging ?? false)
            || elapsedMilliseconds < StartupArmingStepDebugThresholdMilliseconds)
            return;

        log.Debug($"[XASlave] Custom Sight Distance startup arming step '{label}' took {elapsedMilliseconds:F1}ms.");
    }

    private string GetEnabledStatusText(bool activeCameraReady)
    {
        var resolvedSurfaces = (setActiveCameraHook != null ? 1 : 0)
            + (cameraCurrentSightDistanceHook != null ? 1 : 0)
            + (cameraCollisionPatchAddress != nint.Zero ? 1 : 0);
        var availabilityLabel = resolvedSurfaces < 3 ? "Partially enabled" : "Enabled";
        return activeCameraReady
            ? $"{availabilityLabel} - camera overrides are active on {resolvedSurfaces}/3 native surfaces."
            : $"{availabilityLabel} - {resolvedSurfaces}/3 native surfaces are ready; waiting for an active camera after a transition.";
    }

    private void SetActiveCameraDetour(CameraManager* manager, int cameraIndex, void* a3)
    {
        setActiveCameraHook?.OriginalDisposeSafe(manager, cameraIndex, a3);
        try
        {
            if (enabled && manager != null)
                UpdateCamera(manager->Camera);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Custom Sight Distance camera detour failed after the original call.");
        }
    }

    private float CameraCurrentSightDistanceDetour(
        nint a1,
        float minimumValue,
        float maximumValue,
        float upperBound,
        float lowerBound,
        int mode,
        float currentValue,
        float targetValue)
    {
        const float Epsilon = 0.001f;
        if (!enabled)
            return cameraCurrentSightDistanceHook?.OriginalDisposeSafe(a1, minimumValue, maximumValue, upperBound, lowerBound, mode, currentValue, targetValue) ?? currentValue;

        try
        {
            var frameworkInstance = Framework.Instance();
            var adjustedUpperBound = Math.Min(upperBound - Epsilon, maximumValue);
            var adjustedLowerBound = Math.Min(lowerBound - Epsilon, maximumValue);

            var newValue = mode switch
            {
                1 => Math.Min(adjustedUpperBound, Interpolate(adjustedLowerBound, 0.3f)),
                2 => Interpolate(adjustedUpperBound, 0.3f),
                3 => adjustedUpperBound,
                0 or 4 or 5 => Interpolate(adjustedUpperBound, 0.07f),
                _ => currentValue
            };

            return Math.Max(Math.Min(targetValue, newValue), minDistance);

            float Interpolate(float target, float multiplier)
            {
                if (frameworkInstance == null || Math.Abs(target - currentValue) < Epsilon)
                    return target;

                var delta = Math.Min(frameworkInstance->FrameDeltaTime * 60.0f * multiplier, 1.0f);
                if (currentValue < target && target > targetValue)
                    return Math.Min(currentValue + delta * (target - currentValue), targetValue);

                return currentValue + delta * (target - currentValue);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Custom Sight Distance interpolation detour failed; calling the original.");
            return cameraCurrentSightDistanceHook?.OriginalDisposeSafe(a1, minimumValue, maximumValue, upperBound, lowerBound, mode, currentValue, targetValue) ?? currentValue;
        }
    }

    private void UpdateCamera(Camera* camera)
    {
        if (!enabled || camera == null)
            return;

        var cameraAddress = (nint)camera;
        if (capturedCameraAddress != cameraAddress || capturedCamera == null)
        {
            capturedCameraAddress = cameraAddress;
            capturedCamera = new CameraSnapshot(
                camera->MinDistance,
                camera->MaxDistance,
                *(float*)((byte*)camera + 344),
                *(float*)((byte*)camera + 348),
                camera->MinFoV,
                camera->MaxFoV,
                camera->FoV);
        }

        camera->MinDistance = minDistance;
        camera->MaxDistance = maxDistance;
        *(float*)((byte*)camera + 344) = minRotation;
        *(float*)((byte*)camera + 348) = maxRotation;
        camera->MinFoV = minFoV;
        camera->MaxFoV = maxFoV;
        camera->FoV = currentFoV;
    }

    private bool TryUpdateActiveCamera()
    {
        if (!TryGetActiveCamera(out var camera))
            return false;

        UpdateCamera(camera);
        return true;
    }

    private void RestoreCapturedCamera()
    {
        if (capturedCamera == null
            || !TryGetActiveCamera(out var camera)
            || (nint)camera != capturedCameraAddress)
        {
            capturedCameraAddress = nint.Zero;
            capturedCamera = null;
            return;
        }

        var original = capturedCamera.Value;
        camera->MinDistance = original.MinDistance;
        camera->MaxDistance = original.MaxDistance;
        *(float*)((byte*)camera + 344) = original.MinRotation;
        *(float*)((byte*)camera + 348) = original.MaxRotation;
        camera->MinFoV = original.MinFoV;
        camera->MaxFoV = original.MaxFoV;
        camera->FoV = original.FoV;
        capturedCameraAddress = nint.Zero;
        capturedCamera = null;
    }

    private static float ClampFinite(float value, float minimum, float maximum, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);

    private static bool TryGetActiveCamera(out Camera* camera)
    {
        camera = null;

        var manager = CameraManager.Instance();
        if (manager == null || manager->Camera == null)
            return false;

        camera = manager->Camera;
        return true;
    }

    private readonly record struct CameraSnapshot(
        float MinDistance,
        float MaxDistance,
        float MinRotation,
        float MaxRotation,
        float MinFoV,
        float MaxFoV,
        float FoV);

    private delegate void SetActiveCameraDelegate(CameraManager* manager, int cameraIndex, void* a3);

    private delegate float CameraCurrentSightDistanceDelegate(
        nint a1,
        float minimumValue,
        float maximumValue,
        float upperBound,
        float lowerBound,
        int mode,
        float currentValue,
        float targetValue);

    private sealed record StartupHookResult(
        Hook<SetActiveCameraDelegate>? SetActiveCameraHook,
        Hook<CameraCurrentSightDistanceDelegate>? CameraCurrentSightDistanceHook,
        nint CameraCollisionPatchAddress)
    {
        public void DisposeHooks()
        {
            DisposeHook(SetActiveCameraHook);
            DisposeHook(CameraCurrentSightDistanceHook);
        }

        private static void DisposeHook<T>(Hook<T>? hook)
            where T : Delegate
        {
            if (hook is { IsDisposed: false })
                hook.Dispose();
        }
    }
}
