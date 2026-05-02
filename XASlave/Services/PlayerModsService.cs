using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

public unsafe sealed class PlayerModsService : IDisposable
{
    private const double MovePermissionHookReloadGuardSeconds = 5.0;
    private const int SprintActionId = 3;
    private const uint SprintStatusId = 50;
    private const uint CosmicSprintStatusId = 4398;
    private const float InfiniteSprintMovementThresholdSquared = 0.0004f;
    private const int InfiniteSprintRecentMovementWindowMilliseconds = 250;
    private const int InfiniteSprintAttemptThrottleMilliseconds = 2_000;

    public const float InfiniteSprintDelaySecondsDefault = 2.0f;
    public const float InfiniteSprintDelaySecondsMinimum = 0f;
    public const float InfiniteSprintDelaySecondsMaximum = 30f;

    private static readonly uint[] MovePermissionIds = [96, 97, 98, 99, 0x3E9, 0x3EE, 0x3EF, 0x3F0];

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<MovePermissionDelegate>? movePermissionHook;

    private bool initialized;
    private bool frameworkSubscribed;
    private bool infiniteSprintEnabled;
    private bool moveableAfterDeathEnabled;
    private bool moveableAfterDeathEnablePending;

    private DateTime lastSprintAttemptUtc = DateTime.MinValue;
    private DateTime lastObservedMovementUtc = DateTime.MinValue;
    private DateTime currentMovementStartedUtc = DateTime.MinValue;
    private readonly DateTime movePermissionHookCreationAllowedAtUtc = DateTime.UtcNow.AddSeconds(MovePermissionHookReloadGuardSeconds);
    private Vector3 lastObservedLocalPosition;
    private bool hasObservedLocalPosition;
    private float infiniteSprintDelaySeconds = InfiniteSprintDelaySecondsDefault;

    public PlayerModsService(
        IFramework framework,
        ICondition condition,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.framework = framework;
        this.condition = condition;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string InfiniteSprintStatusText { get; private set; } = "Disabled";

    public string MoveableAfterDeathStatusText { get; private set; } = "Disabled";

    public static float ClampInfiniteSprintDelaySeconds(float value)
    {
        return Math.Clamp(value, InfiniteSprintDelaySecondsMinimum, InfiniteSprintDelaySecondsMaximum);
    }

    public void ApplyInfiniteSprintConfiguration(float delaySeconds)
    {
        infiniteSprintDelaySeconds = ClampInfiniteSprintDelaySeconds(delaySeconds);

        if (infiniteSprintEnabled)
            RefreshInfiniteSprintStatusText();
    }

    public bool SetInfiniteSprintEnabled(bool value)
    {
        infiniteSprintEnabled = value;
        if (!value)
            ResetInfiniteSprintMovementTracking();

        UpdateFrameworkSubscription();
        RefreshInfiniteSprintStatusText();
        return infiniteSprintEnabled;
    }

    public bool SetMoveableAfterDeathEnabled(bool value)
    {
        if (!value)
        {
            moveableAfterDeathEnabled = false;
            moveableAfterDeathEnablePending = false;
            UpdateMovePermissionHookState();
            UpdateFrameworkSubscription();
            MoveableAfterDeathStatusText = "Disabled";
            return false;
        }

        if (!TryEnsureMovePermissionHookReady(out var pendingStatusText))
        {
            moveableAfterDeathEnabled = false;
            moveableAfterDeathEnablePending = true;
            UpdateFrameworkSubscription();
            MoveableAfterDeathStatusText = pendingStatusText;
            return true;
        }

        moveableAfterDeathEnablePending = false;
        if (movePermissionHook == null)
        {
            MoveableAfterDeathStatusText = "Unavailable - movement permission hook missing.";
            return false;
        }

        moveableAfterDeathEnabled = true;
        UpdateMovePermissionHookState();
        MoveableAfterDeathStatusText = "Enabled - local movement permission stays open after death.";
        return true;
    }

    public void Dispose()
    {
        infiniteSprintEnabled = false;
        moveableAfterDeathEnabled = false;
        moveableAfterDeathEnablePending = false;
        ResetInfiniteSprintMovementTracking();

        UpdateFrameworkSubscription();
        UpdateMovePermissionHookState();
        DisposeHook(ref movePermissionHook);
    }

    private bool TryEnsureMovePermissionHookReady(out string statusText)
    {
        statusText = string.Empty;
        if (movePermissionHook is { IsDisposed: false })
            return true;

        var guardRemainingSeconds = (movePermissionHookCreationAllowedAtUtc - DateTime.UtcNow).TotalSeconds;
        if (guardRemainingSeconds > 0)
        {
            statusText = $"Pending - waiting {guardRemainingSeconds:0.0}s after reload before arming the movement permission hook.";
            return false;
        }

        if (initialized)
        {
            statusText = "Unavailable - movement permission hook missing.";
            return false;
        }

        initialized = true;
        movePermissionHook = TryCreateHook<MovePermissionDelegate>(Sigs.MovePermissionSig, MovePermissionDetour, "MovePermission");
        if (movePermissionHook != null)
            return true;

        statusText = "Unavailable - movement permission hook missing.";
        return false;
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
                return null;

            var hook = interopProvider.HookFromAddress<T>(address, detour);
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to create {label} hook.");
            return null;
        }
    }

    private void UpdateFrameworkSubscription()
    {
        var shouldSubscribe = infiniteSprintEnabled || moveableAfterDeathEnablePending;
        if (shouldSubscribe == frameworkSubscribed)
            return;

        if (shouldSubscribe)
            framework.Update += OnFrameworkUpdate;
        else
            framework.Update -= OnFrameworkUpdate;

        frameworkSubscribed = shouldSubscribe;
    }

    private void UpdateMovePermissionHookState()
    {
        ToggleHook(movePermissionHook, moveableAfterDeathEnabled, "MovePermission");
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
        if (infiniteSprintEnabled)
            TryApplySprint();

        if (!moveableAfterDeathEnablePending)
            return;

        if (!TryEnsureMovePermissionHookReady(out var pendingStatusText))
        {
            MoveableAfterDeathStatusText = pendingStatusText;
            return;
        }

        moveableAfterDeathEnablePending = false;
        moveableAfterDeathEnabled = true;
        UpdateMovePermissionHookState();
        UpdateFrameworkSubscription();
        MoveableAfterDeathStatusText = "Enabled - local movement permission stays open after death.";
    }

    private void RefreshInfiniteSprintStatusText()
    {
        InfiniteSprintStatusText = infiniteSprintEnabled
            ? $"Enabled - Sprint is reapplied only after movement continues for {infiniteSprintDelaySeconds:0.0}s."
            : "Disabled";
    }

    private void TryApplySprint()
    {
        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (!Plugin.PlayerState.IsLoaded || localPlayer == null)
            {
                ResetInfiniteSprintMovementTracking();
                return;
            }

            var now = DateTime.UtcNow;
            UpdateLocalMovementState(localPlayer.Position, now);

            if (condition[ConditionFlag.Mounted]
                || condition[ConditionFlag.RidingPillion]
                || condition[ConditionFlag.BetweenAreas]
                || condition[ConditionFlag.BetweenAreas51]
                || condition[ConditionFlag.Occupied]
                || condition[ConditionFlag.Occupied30]
                || condition[ConditionFlag.Occupied33]
                || condition[ConditionFlag.Occupied38]
                || condition[ConditionFlag.Occupied39]
                || condition[ConditionFlag.OccupiedInEvent]
                || condition[ConditionFlag.OccupiedInQuestEvent]
                || condition[ConditionFlag.Casting])
                return;

            if (HasSprintStatus(localPlayer))
                return;

            if (!HasRecentLocalMovement(now))
                return;

            if (!HasMetInfiniteSprintDelay(now))
                return;

            if ((now - lastSprintAttemptUtc).TotalMilliseconds < InfiniteSprintAttemptThrottleMilliseconds)
                return;

            lastSprintAttemptUtc = now;
            var actionManager = ActionManager.Instance();
            if (actionManager == null || actionManager->GetActionStatus(ActionType.Action, SprintActionId) != 0)
                return;

            actionManager->UseAction(ActionType.Action, SprintActionId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Infinite Sprint failed while attempting to reapply Sprint.");
        }
    }

    private void UpdateLocalMovementState(Vector3 currentPosition, DateTime now)
    {
        if (!hasObservedLocalPosition)
        {
            lastObservedLocalPosition = currentPosition;
            hasObservedLocalPosition = true;
            return;
        }

        if (Vector3.DistanceSquared(currentPosition, lastObservedLocalPosition) > InfiniteSprintMovementThresholdSquared)
        {
            if (!HasRecentLocalMovement(now))
                currentMovementStartedUtc = now;

            lastObservedMovementUtc = now;
        }

        lastObservedLocalPosition = currentPosition;
    }

    private bool HasRecentLocalMovement(DateTime now)
    {
        return hasObservedLocalPosition
            && (now - lastObservedMovementUtc).TotalMilliseconds <= InfiniteSprintRecentMovementWindowMilliseconds;
    }

    private bool HasMetInfiniteSprintDelay(DateTime now)
    {
        return currentMovementStartedUtc != DateTime.MinValue
            && (now - currentMovementStartedUtc).TotalSeconds >= infiniteSprintDelaySeconds;
    }

    private void ResetInfiniteSprintMovementTracking()
    {
        hasObservedLocalPosition = false;
        lastObservedLocalPosition = default;
        lastObservedMovementUtc = DateTime.MinValue;
        currentMovementStartedUtc = DateTime.MinValue;
    }

    private static bool HasSprintStatus(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter localPlayer)
    {
        return localPlayer.StatusList.Any(status => status.StatusId is SprintStatusId or CosmicSprintStatusId);
    }

    private nint MovePermissionDetour(nint a1, uint permissionId, int a3, int a4)
    {
        if (moveableAfterDeathEnabled && MovePermissionIds.Contains(permissionId))
            return 1;

        return movePermissionHook?.Original(a1, permissionId, a3, a4) ?? 0;
    }

    private delegate nint MovePermissionDelegate(nint a1, uint permissionId, int a3, int a4);
}
