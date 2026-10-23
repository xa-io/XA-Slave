using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

public sealed class AutoLeaveDutyService : IDisposable
{
    public const int DelaySecondsMinimum = 1;
    public const int DelaySecondsMaximum = 10;
    public const int DelaySecondsDefault = 1;

    private const int RetryThrottleMilliseconds = 1500;

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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

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
                TryRequestLeave(now);

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
            if (!AddonHelper.ShowAgent(AgentId.ContentsFinderMenu))
            {
                log.Debug("[XASlave] Auto Leave Duty is waiting for the ContentsFinderMenu agent.");
            }

            lastInteractionTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Leave Duty failed while opening the duty menu.");
        }
    }

    private void TryRequestLeave(long now)
    {
        try
        {
            if (!AddonHelper.TryRequestLeaveDutyFromContentsFinderMenu())
                return;

            lastInteractionTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Leave Duty failed while sending the Leave Duty callbacks.");
        }
    }

    private void TryConfirmLeave(long now)
    {
        try
        {
            if (!AddonHelper.IsLeaveDutyConfirmationPrompt())
                return;

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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        if (!pendingLeave)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S4779ADE95D63BE22", delaySeconds));
            return;
        }

        if (!condition[ConditionFlag.BoundByDuty])
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SD85B9EF966510A41"));
            return;
        }

        var delayRemainingMs = (delaySeconds * 1000L) - (Environment.TickCount64 - dutyCompletedAtTick);
        if (delayRemainingMs > 0)
        {
            var delayRemainingSeconds = Math.Max(1, (int)Math.Ceiling(delayRemainingMs / 1000d));
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S0757E98B8E5319A0", delayRemainingSeconds));
            return;
        }

        if (condition[ConditionFlag.InCombat])
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S652D7AE525358252"));
            return;
        }

        if (!CanLeaveDutyNow())
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S09575AC542720001"));
            return;
        }

        if (AddonHelper.IsAddonVisible("SelectYesno"))
        {
            if (!AddonHelper.IsAddonReady("SelectYesno"))
            {
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S2BC086538753B11F"));
            }
            else if (AddonHelper.IsLeaveDutyConfirmationPrompt())
            {
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S2A84934CD17A2280"));
            }
            else
            {
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S87414C392E0F97B2"));
            }

            return;
        }

        if (AddonHelper.IsAddonVisible("ContentsFinderMenu"))
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (AddonHelper.IsAddonReady("ContentsFinderMenu") ? global::XASlave.Localization.UiMessage.Key("SC4EFF0CC5C9CD51F") : global::XASlave.Localization.UiMessage.Key("S90E2A39B47071BF8")));
            return;
        }

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S728C125EB5B5F804"));
    }
}
