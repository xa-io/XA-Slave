using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class AutoLeaveDutyService : IDisposable
{
    public const int DelaySecondsMinimum = 1;
    public const int DelaySecondsMaximum = 10;
    public const int DelaySecondsDefault = 1;

    private const int RetryThrottleMilliseconds = 1500;
    private const int ContentsFinderMenuLeaveNodeIndex = 43;
    private const byte ContentsFinderMenuVirtualKey = 0x55;

    private readonly IDutyState dutyState;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;
    private bool pendingLeave;
    private int delaySeconds = DelaySecondsDefault;
    private long dutyCompletedAtTick;
    private long lastInteractionTick;

    public AutoLeaveDutyService(
        IDutyState dutyState,
        IClientState clientState,
        IPlayerState playerState,
        ICondition condition,
        IFramework framework,
        IPluginLog log)
    {
        this.dutyState = dutyState;
        this.clientState = clientState;
        this.playerState = playerState;
        this.condition = condition;
        this.framework = framework;
        this.log = log;
        UpdateStatusText();
    }

    public string StatusText { get; private set; } = "Disabled";

    public static int ClampDelaySeconds(int value)
    {
        return Math.Clamp(value, DelaySecondsMinimum, DelaySecondsMaximum);
    }

    public void ApplyConfiguration(int configuredDelaySeconds)
    {
        delaySeconds = ClampDelaySeconds(configuredDelaySeconds);
        UpdateStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        enabled = value;
        ResetPendingState();
        UpdateSubscription(enabled);
        UpdateStatusText();
        return enabled;
    }

    public void Dispose()
    {
        enabled = false;
        ResetPendingState();
        UpdateSubscription(false);
        UpdateStatusText();
    }

    private void UpdateSubscription(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
        {
            dutyState.DutyCompleted += OnDutyCompleted;
            clientState.TerritoryChanged += OnTerritoryChanged;
            framework.Update += OnFrameworkUpdate;
        }
        else
        {
            dutyState.DutyCompleted -= OnDutyCompleted;
            clientState.TerritoryChanged -= OnTerritoryChanged;
            framework.Update -= OnFrameworkUpdate;
        }

        subscribed = targetEnabled;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (!enabled || !clientState.IsLoggedIn)
            return;

        pendingLeave = true;
        dutyCompletedAtTick = Environment.TickCount64;
        lastInteractionTick = 0;
        UpdateStatusText();
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        if (!pendingLeave)
            return;

        ResetPendingState();
        UpdateStatusText();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled)
            return;

        if (!pendingLeave)
        {
            UpdateStatusText();
            return;
        }

        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            ResetPendingState();
            UpdateStatusText();
            return;
        }

        if (!condition[ConditionFlag.BoundByDuty])
        {
            ResetPendingState();
            UpdateStatusText();
            return;
        }

        var now = Environment.TickCount64;
        var configuredDelayMilliseconds = delaySeconds * 1000L;
        if (now - dutyCompletedAtTick < configuredDelayMilliseconds)
        {
            UpdateStatusText();
            return;
        }

        if (!CanLeaveDutyNow())
        {
            UpdateStatusText();
            return;
        }

        if (AddonHelper.IsAddonVisible("SelectYesno"))
        {
            if (AddonHelper.IsAddonReady("SelectYesno"))
                TryConfirmLeave(now);

            UpdateStatusText();
            return;
        }

        if (AddonHelper.IsAddonVisible("ContentsFinderMenu"))
        {
            if (AddonHelper.IsAddonReady("ContentsFinderMenu"))
                TryClickLeave(now);

            UpdateStatusText();
            return;
        }

        if (now - lastInteractionTick < RetryThrottleMilliseconds)
        {
            UpdateStatusText();
            return;
        }

        TryOpenDutyMenu(now);
    }

    private bool CanLeaveDutyNow()
    {
        return !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51]
            && !condition[ConditionFlag.WatchingCutscene]
            && !condition[ConditionFlag.Occupied]
            && !condition[ConditionFlag.Occupied30]
            && !condition[ConditionFlag.Occupied33]
            && !condition[ConditionFlag.Occupied38]
            && !condition[ConditionFlag.Occupied39]
            && !condition[ConditionFlag.OccupiedInEvent]
            && !condition[ConditionFlag.OccupiedInQuestEvent]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent];
    }

    private void ResetPendingState()
    {
        pendingLeave = false;
        dutyCompletedAtTick = 0;
        lastInteractionTick = 0;
    }

    private void TryOpenDutyMenu(long now)
    {
        try
        {
            KeyInputHelper.PressKey(ContentsFinderMenuVirtualKey);
            lastInteractionTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Leave Duty failed while opening the duty menu.");
        }
    }

    private void TryClickLeave(long now)
    {
        try
        {
            if (!AddonHelper.ClickAddonButton("ContentsFinderMenu", ContentsFinderMenuLeaveNodeIndex))
                return;

            lastInteractionTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Leave Duty failed while clicking Leave Duty.");
        }
    }

    private void TryConfirmLeave(long now)
    {
        try
        {
            if (!AddonHelper.ClickYesNo(true))
                return;

            lastInteractionTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Leave Duty failed while confirming Leave Duty.");
        }
    }

    private void UpdateStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!pendingLeave)
        {
            StatusText = $"Enabled - waits for duty completion, then leaves after {delaySeconds} sec once combat and blocking states end.";
            return;
        }

        if (!condition[ConditionFlag.BoundByDuty])
        {
            StatusText = "Enabled - pending leave cleared because the character is no longer in duty.";
            return;
        }

        var delayRemainingMs = (delaySeconds * 1000L) - (Environment.TickCount64 - dutyCompletedAtTick);
        if (delayRemainingMs > 0)
        {
            var delayRemainingSeconds = Math.Max(1, (int)Math.Ceiling(delayRemainingMs / 1000d));
            StatusText = $"Enabled - duty complete detected; waiting {delayRemainingSeconds} more sec before opening the duty menu.";
            return;
        }

        if (condition[ConditionFlag.InCombat])
        {
            StatusText = "Enabled - duty complete detected; waiting for combat to end before leaving.";
            return;
        }

        if (!CanLeaveDutyNow())
        {
            StatusText = "Enabled - duty complete detected; waiting for blocking duty UI or cutscene state to clear.";
            return;
        }

        if (AddonHelper.IsAddonVisible("SelectYesno"))
        {
            StatusText = AddonHelper.IsAddonReady("SelectYesno")
                ? "Enabled - duty complete detected; confirming Leave Duty."
                : "Enabled - duty complete detected; waiting for the leave confirmation dialog.";
            return;
        }

        if (AddonHelper.IsAddonVisible("ContentsFinderMenu"))
        {
            StatusText = AddonHelper.IsAddonReady("ContentsFinderMenu")
                ? "Enabled - duty complete detected; pressing Leave Duty in the duty menu."
                : "Enabled - duty complete detected; waiting for the duty menu to finish opening.";
            return;
        }

        StatusText = "Enabled - duty complete detected; opening the duty menu to leave.";
    }
}
