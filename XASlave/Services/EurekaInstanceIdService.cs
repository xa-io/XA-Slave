using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FrameworkSystem = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using XASlave.Data;

namespace XASlave.Services;

public sealed unsafe class EurekaInstanceIdService : IDisposable
{
    public enum EurekaZone
    {
        Anemos = 0,
        Pagos = 1,
        Pyros = 2,
        Hydatos = 3,
    }

    private enum EurekaHopState
    {
        Idle,
        WaitingForLeaveDutyDelay,
        LeavingDuty,
        WaitingForCharacterSafe,
        TargetingRodney,
        InteractingWithRodney,
        SelectingZone,
        ConfirmingEntry,
        WaitingForZoneLoad,
        WaitingForZoneReadiness,
        Completed,
    }

    public const int MinimumInstanceId = 0;
    public const int MaximumInstanceId = 999;
    public const int LeaveDutyDelaySecondsMinimum = 1;
    public const int LeaveDutyDelaySecondsMaximum = 10;
    public const int LeaveDutyDelaySecondsDefault = 1;
    public const float DefaultSoundVolume = 0.45f;
    public const EurekaZone DefaultZone = EurekaZone.Hydatos;

    private const uint KuganeTerritoryId = 628;
    private const uint AnemosTerritoryId = 732;
    private const uint PagosTerritoryId = 763;
    private const uint PyrosTerritoryId = 795;
    private const uint HydatosTerritoryId = 827;
    private const string RodneyName = "Rodney";
    private const int RodneyStopDistance = 4;
    private const int RetryThrottleMilliseconds = 1500;
    private const int StableReadyMilliseconds = 1000;
    private const int ContentsFinderMenuLeaveNodeIndex = 43;
    private const byte ContentsFinderMenuVirtualKey = 0x55;

    private static readonly EurekaZone[] OrderedZones =
    {
        EurekaZone.Anemos,
        EurekaZone.Pagos,
        EurekaZone.Pyros,
        EurekaZone.Hydatos,
    };

    private readonly object gate = new();
    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IDtrBar dtrBar;

    private Hook<UIModule.Delegates.HandlePacket>? uiModuleHandlePacketHook;
    private IDtrBarEntry? dtrEntry;

    private bool enabled;
    private bool subscribed;
    private bool scannerRunning;
    private bool displayRefreshPending;
    private bool zoneEvaluationPending;
    private EurekaHopState state;
    private EurekaZone currentTargetZone = DefaultZone;
    private long lastInteractionTick;
    private long readySinceTick;
    private long leaveDutyReadyAtTick;
    private string pendingLeaveReason = string.Empty;
    private string lastDtrText = string.Empty;
    private bool lastDtrShown;
    private EurekaZone lastObservedZone = DefaultZone;
    private uint lastObservedInstanceId;
    private uint lastNewInstanceId;
    private string lastObservedInstanceSource = "None";
    private ushort lastObservedServerId;
    private uint lastObservedPopRangeId;
    private string lastResolutionDiagnostics = string.Empty;
    private ZoneInitSnapshot lastZoneInitSnapshot;

    public EurekaInstanceIdService(
        Configuration configuration,
        IClientState clientState,
        IPlayerState playerState,
        ICondition condition,
        IFramework framework,
        IPluginLog log,
        IDtrBar dtrBar)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.playerState = playerState;
        this.condition = condition;
        this.framework = framework;
        this.log = log;
        this.dtrBar = dtrBar;
        lastZoneInitSnapshot = new ZoneInitSnapshot(
            HookActive: false,
            HasCapturedPacket: false,
            CapturedAtUtc: DateTime.MinValue,
            ServerId: 0,
            TerritoryTypeId: 0,
            PacketInstance: 0,
            ContentFinderConditionId: 0,
            PopRangeId: 0,
            Flags: ZoneInitFlags.None);
        ApplyConfiguration();
        TryInitializeZoneInitHook();
        UpdateStatusText();
    }

    public string StatusText { get; private set; } = "Disabled";

    public static EurekaZone NormalizeZone(int value)
    {
        return Enum.IsDefined(typeof(EurekaZone), value)
            ? (EurekaZone)value
            : DefaultZone;
    }

    public static int NormalizeInstanceId(int value)
    {
        if (value <= 0)
            return 0;

        return Math.Clamp(value, MinimumInstanceId, MaximumInstanceId);
    }

    public static int ClampLeaveDutyDelaySeconds(int value)
    {
        return Math.Clamp(value, LeaveDutyDelaySecondsMinimum, LeaveDutyDelaySecondsMaximum);
    }

    public static int ClampSoundEffectId(int value)
    {
        return XAPeepData.ClampSoundEffectId(value);
    }

    public static float ClampSoundVolume(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    public static string GetZoneLabel(EurekaZone zone)
    {
        return zone switch
        {
            EurekaZone.Anemos => "Anemos",
            EurekaZone.Pagos => "Pagos",
            EurekaZone.Pyros => "Pyros",
            EurekaZone.Hydatos => "Hydatos",
            _ => "Hydatos",
        };
    }

    public static string GetZoneDutyName(EurekaZone zone)
    {
        return zone switch
        {
            EurekaZone.Anemos => "Eureka Anemos",
            EurekaZone.Pagos => "Eureka Pagos",
            EurekaZone.Pyros => "Eureka Pyros",
            EurekaZone.Hydatos => "Eureka Hydatos",
            _ => "Eureka Hydatos",
        };
    }

    public void ApplyConfiguration()
    {
        configuration.EurekaInstanceIdZone = (int)NormalizeZone(configuration.EurekaInstanceIdZone);
        configuration.EurekaInstanceIdBaselineInstanceId = NormalizeInstanceId(configuration.EurekaInstanceIdBaselineInstanceId);
        configuration.EurekaInstanceIdLeaveDutyDelaySeconds = ClampLeaveDutyDelaySeconds(configuration.EurekaInstanceIdLeaveDutyDelaySeconds);
        configuration.EurekaInstanceIdSoundEffectId = ClampSoundEffectId(configuration.EurekaInstanceIdSoundEffectId);
        configuration.EurekaInstanceIdSoundVolume = ClampSoundVolume(configuration.EurekaInstanceIdSoundVolume);
        NormalizeZoneConfiguration(EurekaZone.Anemos);
        NormalizeZoneConfiguration(EurekaZone.Pagos);
        NormalizeZoneConfiguration(EurekaZone.Pyros);
        NormalizeZoneConfiguration(EurekaZone.Hydatos);
        EnsureCurrentTargetZone();
        UpdateSubscription(enabled);
        if (enabled)
            ArmDisplayRefresh();
        else
            SetDtrState(string.Empty, false);

        UpdateStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            if (!enabled)
            {
                UpdateStatusText();
                return false;
            }

            enabled = false;
            scannerRunning = false;
            displayRefreshPending = false;
            ResetProgress(false);
            UpdateSubscription(false);
            SetDtrState(string.Empty, false);
            UpdateStatusText();
            return false;
        }

        if (enabled)
        {
            UpdateStatusText();
            return true;
        }

        enabled = true;
        scannerRunning = false;
        ResetProgress(false);
        EnsureCurrentTargetZone();
        UpdateSubscription(true);
        ArmDisplayRefresh();
        UpdateStatusText();
        return true;
    }

    public bool IsScanning => scannerRunning;

    public bool StartScanning()
    {
        if (!enabled && !SetEnabled(true))
            return false;

        if (!HasEnabledZones())
        {
            UpdateStatusText();
            return false;
        }

        scannerRunning = true;
        ResetProgress(true);
        ResetCurrentTargetZoneToFirstEnabled();
        ArmDisplayRefresh();
        UpdateStatusText();
        return true;
    }

    public void StopScanning()
    {
        if (!scannerRunning)
        {
            UpdateStatusText();
            return;
        }

        scannerRunning = false;
        ResetProgress(false);
        ArmDisplayRefresh();
        UpdateStatusText();
    }

    public void Dispose()
    {
        enabled = false;
        scannerRunning = false;
        displayRefreshPending = false;
        ResetProgress(false);
        UpdateSubscription(false);
        uiModuleHandlePacketHook?.Dispose();
        uiModuleHandlePacketHook = null;
        RemoveDtrEntry();
        UpdateStatusText();
    }

    private void UpdateSubscription(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
        {
            framework.Update += OnFrameworkUpdate;
            clientState.TerritoryChanged += OnTerritoryChanged;
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
            clientState.TerritoryChanged -= OnTerritoryChanged;
        }

        subscribed = targetEnabled;
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        if (!enabled)
            return;

        if (TryResolveEurekaZoneByTerritoryType(territoryType, out var zone))
        {
            lastObservedZone = zone;
            ArmDisplayRefresh();
            return;
        }

        displayRefreshPending = false;
        ResetReadyHold();
        SetDtrState(string.Empty, false);
        if (!scannerRunning)
            UpdateStatusText();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (displayRefreshPending)
            TryRefreshDisplayedInstance();

        if (!enabled)
            return;

        if (!scannerRunning)
            return;

        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            ResetReadyHold();
            UpdateStatusText();
            return;
        }

        if (!HasEnabledZones())
        {
            scannerRunning = false;
            ResetProgress(false);
            ArmDisplayRefresh();
            UpdateStatusText();
            return;
        }

        EnsureCurrentTargetZone();
        if (state == EurekaHopState.Completed)
        {
            UpdateStatusText();
            return;
        }

        switch (state)
        {
            case EurekaHopState.WaitingForLeaveDutyDelay:
            case EurekaHopState.LeavingDuty:
                UpdateLeaveDutyState();
                return;
            case EurekaHopState.WaitingForCharacterSafe:
                UpdateCharacterSafeState();
                return;
            case EurekaHopState.TargetingRodney:
                UpdateTargetRodneyState();
                return;
            case EurekaHopState.InteractingWithRodney:
                UpdateInteractWithRodneyState();
                return;
            case EurekaHopState.SelectingZone:
                UpdateSelectZoneState();
                return;
            case EurekaHopState.ConfirmingEntry:
                UpdateConfirmEntryState();
                return;
            case EurekaHopState.WaitingForZoneLoad:
                UpdateWaitForZoneLoadState();
                return;
            case EurekaHopState.WaitingForZoneReadiness:
                UpdateWaitForZoneReadinessState();
                return;
            case EurekaHopState.Idle:
            default:
                UpdateIdleState();
                return;
        }
    }

    private void UpdateIdleState()
    {
        ResetReadyHold();
        if (TryEnterSelectingZoneFromOpenMenu())
        {
            UpdateStatusText();
            return;
        }

        if (zoneEvaluationPending && TryResolveCurrentEurekaZone(out var currentZone) && condition[ConditionFlag.BoundByDuty])
        {
            lastObservedZone = currentZone;
            state = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
                ? EurekaHopState.WaitingForZoneLoad
                : EurekaHopState.WaitingForZoneReadiness;
            UpdateStatusText();
            return;
        }

        if (IsInKugane())
        {
            state = CharacterSafetyHelper.IsCharacterSafeWaitReady()
                ? EurekaHopState.TargetingRodney
                : EurekaHopState.WaitingForCharacterSafe;
        }
        else if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            state = EurekaHopState.WaitingForZoneLoad;
        }

        UpdateStatusText();
    }

    private void UpdateCurrentEurekaZoneState(EurekaZone currentZone)
    {
        lastObservedZone = currentZone;

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            ResetReadyHold();
            state = EurekaHopState.WaitingForZoneLoad;
            UpdateStatusText();
            return;
        }

        if (!IsCurrentZoneReadyForInstanceRead())
        {
            state = EurekaHopState.WaitingForZoneReadiness;
            UpdateStatusText();
            return;
        }

        ResetReadyHold();
        var zoneLabel = GetZoneLabel(currentZone);
        if (!IsZoneEnabled(currentZone))
        {
            ScheduleLeaveFromCurrentZone(currentZone, $"Current Eureka {zoneLabel} is not enabled.");
            UpdateStatusText();
            return;
        }

        var resolution = ResolveCurrentInstanceResolution();
        ApplyResolution(currentZone, resolution);
        if (resolution.InstanceId == 0)
        {
            state = EurekaHopState.WaitingForZoneReadiness;
            UpdateStatusText();
            return;
        }

        var currentInstanceId = NormalizeInstanceId((int)resolution.InstanceId);
        var baselineInstanceId = GetZoneBaselineInstanceId(currentZone);
        if (baselineInstanceId <= 0)
        {
            SaveZoneBaseline(currentZone, currentInstanceId);
            pendingLeaveReason = $"Captured Eureka {zoneLabel} baseline {currentInstanceId}{BuildResolutionSourceSuffix(resolution)}.";
            Plugin.ChatGui.Print($"[XASlave] {zoneLabel} baseline instance set to {currentInstanceId} via {resolution.Source}{BuildResolutionDetailsSuffix(resolution)}");
            ScheduleLeaveFromCurrentZone(currentZone, pendingLeaveReason);
            UpdateStatusText();
            return;
        }

        if (currentInstanceId != baselineInstanceId)
        {
            SaveZoneBaseline(currentZone, currentInstanceId);
            scannerRunning = false;
            zoneEvaluationPending = false;
            lastNewInstanceId = (uint)currentInstanceId;
            state = EurekaHopState.Completed;
            AnnounceNewInstance(currentZone, resolution);
            UpdateStatusText();
            return;
        }

        ScheduleLeaveFromCurrentZone(
            currentZone,
            $"Eureka {zoneLabel} instance {currentInstanceId}{BuildResolutionSourceSuffix(resolution)} matched the stored baseline.");
        UpdateStatusText();
    }

    private void UpdateLeaveDutyState()
    {
        if (!condition[ConditionFlag.BoundByDuty])
        {
            ResetReadyHold();
            state = IsInKugane()
                ? EurekaHopState.WaitingForCharacterSafe
                : EurekaHopState.Idle;
            UpdateStatusText();
            return;
        }

        var now = Environment.TickCount64;
        if (state == EurekaHopState.WaitingForLeaveDutyDelay)
        {
            if (now < leaveDutyReadyAtTick)
            {
                UpdateStatusText();
                return;
            }

            state = EurekaHopState.LeavingDuty;
        }

        if (!CanLeaveDutyNow())
        {
            UpdateStatusText();
            return;
        }

        if (AddonHelper.IsAddonVisible("SelectYesno"))
        {
            if (AddonHelper.IsAddonReady("SelectYesno") && now - lastInteractionTick >= RetryThrottleMilliseconds)
                TryConfirmLeave(now);

            UpdateStatusText();
            return;
        }

        if (AddonHelper.IsAddonVisible("ContentsFinderMenu"))
        {
            if (AddonHelper.IsAddonReady("ContentsFinderMenu") && now - lastInteractionTick >= RetryThrottleMilliseconds)
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
        UpdateStatusText();
    }

    private void UpdateCharacterSafeState()
    {
        if (!IsInKugane())
        {
            ResetReadyHold();
            state = EurekaHopState.Idle;
            UpdateStatusText();
            return;
        }

        if (TryEnterSelectingZoneFromOpenMenu())
        {
            UpdateStatusText();
            return;
        }

        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
        {
            ResetReadyHold();
            UpdateStatusText();
            return;
        }

        if (!HoldReadyState())
        {
            UpdateStatusText();
            return;
        }

        ResetReadyHold();
        state = EurekaHopState.TargetingRodney;
        UpdateStatusText();
    }

    private void UpdateTargetRodneyState()
    {
        if (!IsInKugane())
        {
            state = EurekaHopState.Idle;
            UpdateStatusText();
            return;
        }

        if (TryEnterSelectingZoneFromOpenMenu())
        {
            UpdateStatusText();
            return;
        }

        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
        {
            ResetReadyHold();
            state = EurekaHopState.WaitingForCharacterSafe;
            UpdateStatusText();
            return;
        }

        if (!AddonHelper.IsCurrentTargetWithinStopDistanceAndStopped(RodneyName, RodneyStopDistance))
        {
            UpdateStatusText();
            return;
        }

        state = EurekaHopState.InteractingWithRodney;
        UpdateStatusText();
    }

    private void UpdateInteractWithRodneyState()
    {
        if (!IsInKugane())
        {
            state = EurekaHopState.Idle;
            UpdateStatusText();
            return;
        }

        if (TryEnterSelectingZoneFromOpenMenu())
        {
            UpdateStatusText();
            return;
        }

        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
        {
            ResetReadyHold();
            state = EurekaHopState.WaitingForCharacterSafe;
            UpdateStatusText();
            return;
        }

        var now = Environment.TickCount64;
        if (now - lastInteractionTick < RetryThrottleMilliseconds)
        {
            UpdateStatusText();
            return;
        }

        if (!AddonHelper.CurrentTargetMatches(RodneyName))
        {
            state = EurekaHopState.TargetingRodney;
            UpdateStatusText();
            return;
        }

        if (AddonHelper.InteractWithTarget())
            lastInteractionTick = now;

        UpdateStatusText();
    }

    private bool TryEnterSelectingZoneFromOpenMenu()
    {
        if (!IsInKugane() || !AddonHelper.IsAddonVisible("SelectString"))
            return false;

        ResetReadyHold();
        state = EurekaHopState.SelectingZone;
        return true;
    }

    private void UpdateSelectZoneState()
    {
        if (!AddonHelper.IsAddonVisible("SelectString"))
        {
            state = IsInZone(currentTargetZone)
                ? EurekaHopState.WaitingForZoneLoad
                : EurekaHopState.InteractingWithRodney;
            UpdateStatusText();
            return;
        }

        if (!AddonHelper.IsAddonReady("SelectString"))
        {
            UpdateStatusText();
            return;
        }

        var now = Environment.TickCount64;
        if (now - lastInteractionTick < RetryThrottleMilliseconds)
        {
            UpdateStatusText();
            return;
        }

        var targetZoneDutyName = GetZoneDutyName(currentTargetZone);
        var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("SelectString", targetZoneDutyName, true);
        if (callbackIndex < 0)
        {
            log.Warning("[XASlave] Eureka Instance ID could not resolve a SelectString callback index for {ZoneDutyName}.", targetZoneDutyName);
            UpdateStatusText();
            return;
        }

        if (AddonHelper.FireCallback("SelectString", callbackIndex))
        {
            log.Information("[XASlave] Eureka Instance ID fired Rodney SelectString callback {CallbackIndex} for {ZoneDutyName}.", callbackIndex, targetZoneDutyName);
            configuration.EurekaInstanceIdZone = (int)currentTargetZone;
            lastInteractionTick = now;
            state = EurekaHopState.ConfirmingEntry;
        }

        UpdateStatusText();
    }

    private void UpdateConfirmEntryState()
    {
        if (IsInZone(currentTargetZone))
        {
            ArmZoneEvaluation();
            state = EurekaHopState.WaitingForZoneLoad;
            UpdateStatusText();
            return;
        }

        if (AddonHelper.IsAddonVisible(AddonHelper.ContentsFinderConfirmAddonName))
        {
            if (!AddonHelper.IsAddonReady(AddonHelper.ContentsFinderConfirmAddonName))
            {
                UpdateStatusText();
                return;
            }

            var now = Environment.TickCount64;
            if (now - lastInteractionTick < RetryThrottleMilliseconds)
            {
                UpdateStatusText();
                return;
            }

            TryCommenceDuty(now);
            UpdateStatusText();
            return;
        }

        if (AddonHelper.IsAddonVisible("SelectYesno"))
        {
            if (!AddonHelper.IsAddonReady("SelectYesno"))
            {
                UpdateStatusText();
                return;
            }

            var now = Environment.TickCount64;
            if (now - lastInteractionTick < RetryThrottleMilliseconds)
            {
                UpdateStatusText();
                return;
            }

            if (AddonHelper.ClickYesNo(true))
            {
                lastInteractionTick = now;
                ArmZoneEvaluation();
            }

            UpdateStatusText();
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            ArmZoneEvaluation();
            state = EurekaHopState.WaitingForZoneLoad;
            UpdateStatusText();
            return;
        }

        UpdateStatusText();
    }

    private void UpdateWaitForZoneLoadState()
    {
        ResetReadyHold();
        if (TryResolveCurrentEurekaZone(out var currentZone) && condition[ConditionFlag.BoundByDuty])
        {
            lastObservedZone = currentZone;
            if (!condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51])
                state = EurekaHopState.WaitingForZoneReadiness;

            UpdateStatusText();
            return;
        }

        if (IsInKugane() && !condition[ConditionFlag.BoundByDuty] && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51])
        {
            state = EurekaHopState.WaitingForCharacterSafe;
            UpdateStatusText();
            return;
        }

        UpdateStatusText();
    }

    private void UpdateWaitForZoneReadinessState()
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            ResetReadyHold();
            state = EurekaHopState.WaitingForZoneLoad;
            UpdateStatusText();
            return;
        }

        if (TryResolveCurrentEurekaZone(out var currentZone) && condition[ConditionFlag.BoundByDuty])
        {
            if (!zoneEvaluationPending)
            {
                UpdateStatusText();
                return;
            }

            UpdateCurrentEurekaZoneState(currentZone);
            return;
        }

        ResetReadyHold();
        state = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            ? EurekaHopState.WaitingForZoneLoad
            : EurekaHopState.Idle;
        UpdateStatusText();
    }

    private bool IsCurrentZoneReadyForInstanceRead()
    {
        return condition[ConditionFlag.BoundByDuty]
            && CharacterSafetyHelper.IsCharacterSafeWaitReadyInDuty();
    }

    private bool HoldReadyState()
    {
        var now = Environment.TickCount64;
        if (readySinceTick == 0)
        {
            readySinceTick = now;
            return false;
        }

        return now - readySinceTick >= StableReadyMilliseconds;
    }

    private void ResetReadyHold()
    {
        readySinceTick = 0;
    }

    private void ScheduleLeaveFromCurrentZone(EurekaZone currentZone, string reason)
    {
        zoneEvaluationPending = false;
        pendingLeaveReason = reason;
        leaveDutyReadyAtTick = Environment.TickCount64 + (configuration.EurekaInstanceIdLeaveDutyDelaySeconds * 1000L);
        SetNextTargetZoneAfter(currentZone);
        state = EurekaHopState.WaitingForLeaveDutyDelay;
        ResetReadyHold();
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

    private void TryOpenDutyMenu(long now)
    {
        try
        {
            KeyInputHelper.PressKey(ContentsFinderMenuVirtualKey);
            lastInteractionTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Eureka Instance ID failed while opening the duty menu.");
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
            log.Warning(ex, "[XASlave] Eureka Instance ID failed while clicking Leave Duty.");
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
            log.Warning(ex, "[XASlave] Eureka Instance ID failed while confirming Leave Duty.");
        }
    }

    private void TryCommenceDuty(long now)
    {
        try
        {
            if (!AddonHelper.ClickContentsFinderConfirmCommence())
                return;

            lastInteractionTick = now;
            ArmZoneEvaluation();
            state = EurekaHopState.WaitingForZoneLoad;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Eureka Instance ID failed while commencing the Eureka duty.");
        }
    }

    private void ResetProgress(bool armZoneEvaluation)
    {
        zoneEvaluationPending = armZoneEvaluation;
        state = EurekaHopState.Idle;
        lastInteractionTick = 0;
        readySinceTick = 0;
        leaveDutyReadyAtTick = 0;
        pendingLeaveReason = string.Empty;
        lastObservedInstanceId = 0;
        lastNewInstanceId = 0;
        lastObservedInstanceSource = "None";
        lastObservedServerId = 0;
        lastObservedPopRangeId = 0;
        lastResolutionDiagnostics = string.Empty;
        EnsureCurrentTargetZone();
    }

    private void ArmZoneEvaluation()
    {
        zoneEvaluationPending = true;
        ResetReadyHold();
    }

    private void ArmDisplayRefresh()
    {
        displayRefreshPending = true;
        ResetReadyHold();
    }

    private void NormalizeZoneConfiguration(EurekaZone zone)
    {
        SetZoneBaselineInstanceId(zone, NormalizeInstanceId(GetZoneBaselineInstanceId(zone)));
    }

    private bool HasEnabledZones()
    {
        foreach (var zone in OrderedZones)
        {
            if (IsZoneEnabled(zone))
                return true;
        }

        return false;
    }

    private void EnsureCurrentTargetZone()
    {
        if (IsZoneEnabled(currentTargetZone))
            return;

        currentTargetZone = TryGetFirstEnabledZone(out var firstEnabledZone)
            ? firstEnabledZone
            : DefaultZone;
    }

    private void ResetCurrentTargetZoneToFirstEnabled()
    {
        currentTargetZone = TryGetFirstEnabledZone(out var firstEnabledZone)
            ? firstEnabledZone
            : DefaultZone;
        configuration.EurekaInstanceIdZone = (int)currentTargetZone;
    }

    private void SetNextTargetZoneAfter(EurekaZone currentZone)
    {
        currentTargetZone = TryGetNextEnabledZone(currentZone, out var nextZone)
            ? nextZone
            : currentZone;
        configuration.EurekaInstanceIdZone = (int)currentTargetZone;
    }

    private bool TryGetFirstEnabledZone(out EurekaZone zone)
    {
        foreach (var candidate in OrderedZones)
        {
            if (!IsZoneEnabled(candidate))
                continue;

            zone = candidate;
            return true;
        }

        zone = DefaultZone;
        return false;
    }

    private bool TryGetNextEnabledZone(EurekaZone currentZone, out EurekaZone zone)
    {
        var currentIndex = Array.IndexOf(OrderedZones, currentZone);
        if (currentIndex < 0)
            return TryGetFirstEnabledZone(out zone);

        for (var offset = 1; offset <= OrderedZones.Length; offset++)
        {
            var candidate = OrderedZones[(currentIndex + offset) % OrderedZones.Length];
            if (!IsZoneEnabled(candidate))
                continue;

            zone = candidate;
            return true;
        }

        zone = currentZone;
        return false;
    }

    private bool IsInKugane()
    {
        return clientState.TerritoryType == KuganeTerritoryId;
    }

    private bool IsInZone(EurekaZone zone)
    {
        return TryResolveCurrentEurekaZone(out var currentZone) && currentZone == zone;
    }

    private bool TryResolveCurrentEurekaZone(out EurekaZone zone)
    {
        if (TryResolveEurekaZoneByTerritoryType(clientState.TerritoryType, out zone))
            return true;

        zone = DefaultZone;
        var zoneName = AddonHelper.GetCurrentZoneName();
        if (string.IsNullOrWhiteSpace(zoneName))
            return false;

        if (zoneName.Contains("Anemos", StringComparison.OrdinalIgnoreCase))
        {
            zone = EurekaZone.Anemos;
            return true;
        }

        if (zoneName.Contains("Pagos", StringComparison.OrdinalIgnoreCase))
        {
            zone = EurekaZone.Pagos;
            return true;
        }

        if (zoneName.Contains("Pyros", StringComparison.OrdinalIgnoreCase))
        {
            zone = EurekaZone.Pyros;
            return true;
        }

        if (zoneName.Contains("Hydatos", StringComparison.OrdinalIgnoreCase))
        {
            zone = EurekaZone.Hydatos;
            return true;
        }

        return false;
    }

    private static bool TryResolveEurekaZoneByTerritoryType(uint territoryType, out EurekaZone zone)
    {
        switch (territoryType)
        {
            case AnemosTerritoryId:
                zone = EurekaZone.Anemos;
                return true;
            case PagosTerritoryId:
                zone = EurekaZone.Pagos;
                return true;
            case PyrosTerritoryId:
                zone = EurekaZone.Pyros;
                return true;
            case HydatosTerritoryId:
                zone = EurekaZone.Hydatos;
                return true;
            default:
                zone = DefaultZone;
                return false;
        }
    }

    private bool IsZoneEnabled(EurekaZone zone)
    {
        return zone switch
        {
            EurekaZone.Anemos => configuration.EurekaInstanceIdAnemosEnabled,
            EurekaZone.Pagos => configuration.EurekaInstanceIdPagosEnabled,
            EurekaZone.Pyros => configuration.EurekaInstanceIdPyrosEnabled,
            EurekaZone.Hydatos => configuration.EurekaInstanceIdHydatosEnabled,
            _ => false,
        };
    }

    private int GetZoneBaselineInstanceId(EurekaZone zone)
    {
        return zone switch
        {
            EurekaZone.Anemos => configuration.EurekaInstanceIdAnemosBaselineInstanceId,
            EurekaZone.Pagos => configuration.EurekaInstanceIdPagosBaselineInstanceId,
            EurekaZone.Pyros => configuration.EurekaInstanceIdPyrosBaselineInstanceId,
            EurekaZone.Hydatos => configuration.EurekaInstanceIdHydatosBaselineInstanceId,
            _ => 0,
        };
    }

    private void SetZoneBaselineInstanceId(EurekaZone zone, int value)
    {
        switch (zone)
        {
            case EurekaZone.Anemos:
                configuration.EurekaInstanceIdAnemosBaselineInstanceId = value;
                break;
            case EurekaZone.Pagos:
                configuration.EurekaInstanceIdPagosBaselineInstanceId = value;
                break;
            case EurekaZone.Pyros:
                configuration.EurekaInstanceIdPyrosBaselineInstanceId = value;
                break;
            case EurekaZone.Hydatos:
                configuration.EurekaInstanceIdHydatosBaselineInstanceId = value;
                break;
        }
    }

    private void SaveZoneBaseline(EurekaZone zone, int instanceId)
    {
        var normalizedInstanceId = NormalizeInstanceId(instanceId);
        SetZoneBaselineInstanceId(zone, normalizedInstanceId);
        configuration.EurekaInstanceIdZone = (int)zone;
        configuration.EurekaInstanceIdBaselineInstanceId = normalizedInstanceId;
        configuration.Save();
    }

    private void TryInitializeZoneInitHook()
    {
        try
        {
            if (UIModule.StaticVirtualTablePointer == null)
            {
                log.Warning("[XASlave] Eureka Instance ID could not start the zone-init packet hook because UIModule.StaticVirtualTablePointer was null.");
                return;
            }

            uiModuleHandlePacketHook = Plugin.GameInterop.HookFromAddress<UIModule.Delegates.HandlePacket>(
                (nint)UIModule.StaticVirtualTablePointer->HandlePacket,
                UIModuleHandlePacketDetour);
            uiModuleHandlePacketHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Eureka Instance ID failed to install the zone-init packet hook.");
        }
    }

    private void UpdateStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!scannerRunning)
        {
            if (state == EurekaHopState.Completed)
            {
                StatusText = $"Enabled - found new Eureka {GetZoneLabel(lastObservedZone)} instance {lastNewInstanceId}{FormatSourceLabel(lastObservedInstanceSource)} and updated the baseline. Press Start to scan again.";
                return;
            }

            if (TryResolveEurekaZoneByTerritoryType(clientState.TerritoryType, out var currentZone))
            {
                var zoneLabel = GetZoneLabel(currentZone);
                if (displayRefreshPending)
                {
                    StatusText = $"Enabled - waiting to read the live Eureka {zoneLabel} instance. Press Start to begin farming.";
                    return;
                }

                if (lastObservedZone == currentZone && lastObservedInstanceId != 0)
                {
                    StatusText = $"Enabled - showing Eureka {zoneLabel} instance {lastObservedInstanceId}{FormatSourceLabel(lastObservedInstanceSource)}. Press Start to begin farming.";
                    return;
                }

                StatusText = $"Enabled - Eureka {zoneLabel} instance is currently unavailable. Press Start to begin farming.";
                return;
            }

            StatusText = "Enabled - shows the live Eureka instance while you are in Anemos, Pagos, Pyros, or Hydatos. Press Start to begin farming.";
            return;
        }

        if (!HasEnabledZones())
        {
            StatusText = "Enabled - select at least one Eureka zone row to scan.";
            return;
        }

        var currentTargetLabel = GetZoneLabel(currentTargetZone);
        switch (state)
        {
            case EurekaHopState.Idle:
                StatusText = $"Enabled - waiting in Kugane or a selected Eureka zone. Next target: {currentTargetLabel}.";
                return;
            case EurekaHopState.WaitingForLeaveDutyDelay:
            {
                var remainingMs = Math.Max(0, leaveDutyReadyAtTick - Environment.TickCount64);
                var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remainingMs / 1000d));
                StatusText = string.IsNullOrWhiteSpace(pendingLeaveReason)
                    ? $"Enabled - waiting {remainingSeconds} more sec before leaving Eureka for {currentTargetLabel}."
                    : $"Enabled - {pendingLeaveReason} Leaving in {remainingSeconds} sec for {currentTargetLabel}.";
                return;
            }
            case EurekaHopState.LeavingDuty:
                if (!condition[ConditionFlag.BoundByDuty])
                {
                    StatusText = "Enabled - waiting to finish leaving the current Eureka duty.";
                    return;
                }

                if (!CanLeaveDutyNow())
                {
                    StatusText = string.IsNullOrWhiteSpace(pendingLeaveReason)
                        ? "Enabled - waiting for blockers to clear before leaving the current Eureka duty."
                        : $"Enabled - {pendingLeaveReason} Waiting for blockers to clear before leaving.";
                    return;
                }

                if (AddonHelper.IsAddonVisible("SelectYesno"))
                {
                    StatusText = AddonHelper.IsAddonReady("SelectYesno")
                        ? "Enabled - confirming Leave Duty."
                        : "Enabled - waiting for the Leave Duty confirmation.";
                    return;
                }

                if (AddonHelper.IsAddonVisible("ContentsFinderMenu"))
                {
                    StatusText = AddonHelper.IsAddonReady("ContentsFinderMenu")
                        ? "Enabled - pressing Leave Duty in the duty menu."
                        : "Enabled - waiting for the duty menu to open.";
                    return;
                }

                StatusText = "Enabled - opening the duty menu to leave Eureka.";
                return;
            case EurekaHopState.WaitingForCharacterSafe:
                StatusText = "Enabled - waiting for CharacterSafeWait in Kugane before targeting Rodney.";
                return;
            case EurekaHopState.TargetingRodney:
                StatusText = $"Enabled - targeting Rodney in Kugane for Eureka {currentTargetLabel}.";
                return;
            case EurekaHopState.InteractingWithRodney:
                StatusText = $"Enabled - interacting with Rodney for Eureka {currentTargetLabel}.";
                return;
            case EurekaHopState.SelectingZone:
                StatusText = $"Enabled - selecting Eureka {currentTargetLabel} from Rodney's menu.";
                return;
            case EurekaHopState.ConfirmingEntry:
                if (AddonHelper.IsAddonVisible(AddonHelper.ContentsFinderConfirmAddonName))
                {
                    StatusText = AddonHelper.IsAddonReady(AddonHelper.ContentsFinderConfirmAddonName)
                        ? $"Enabled - pressing Commence for Eureka {currentTargetLabel}."
                        : $"Enabled - waiting for the Eureka {currentTargetLabel} duty-ready prompt.";
                    return;
                }

                StatusText = $"Enabled - confirming entry into Eureka {currentTargetLabel}.";
                return;
            case EurekaHopState.WaitingForZoneLoad:
                StatusText = $"Enabled - waiting to zone into Eureka {currentTargetLabel}.";
                return;
            case EurekaHopState.WaitingForZoneReadiness:
                StatusText = $"Enabled - waiting for CharacterSafeWait after entering Eureka {GetZoneLabel(lastObservedZone)} before reading the instance ID.";
                return;
            default:
                StatusText = "Enabled";
                return;
        }
    }

    public bool TryUseCurrentInstance(out EurekaZone zone, out int instanceId, out string message)
    {
        zone = DefaultZone;
        instanceId = 0;

        if (!TryResolveCurrentEurekaZone(out zone))
        {
            message = "Current zone is not Anemos, Pagos, Pyros, or Hydatos.";
            return false;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            message = "Cannot read instance ID while loading. Wait a moment and try again.";
            return false;
        }

        const int maxRetries = 20;
        const int retryDelayMs = 200;
        var resolution = new EurekaInstanceResolution(0, "None", 0, 0, string.Empty);

        for (var i = 0; i < maxRetries; i++)
        {
            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            {
                message = "Cannot read instance ID while loading. Wait for zone load to complete.";
                return false;
            }

            resolution = ResolveCurrentInstanceResolution();
            ApplyResolution(zone, resolution);
            if (resolution.InstanceId != 0)
                break;

            if (i < maxRetries - 1)
                System.Threading.Thread.Sleep(retryDelayMs);
        }

        if (resolution.InstanceId == 0)
        {
            message = $"Could not read the current {GetZoneLabel(zone)} instance ID. Last candidate snapshot: {resolution.Diagnostics}";
            return false;
        }

        instanceId = NormalizeInstanceId((int)resolution.InstanceId);
        message = $"Captured {GetZoneLabel(zone)} instance {instanceId} via {resolution.Source}{BuildResolutionDetailsSuffix(resolution)}.";
        return true;
    }

    private ZoneInitSnapshot GetZoneInitSnapshot()
    {
        lock (gate)
        {
            return lastZoneInitSnapshot with
            {
                HookActive = uiModuleHandlePacketHook != null
            };
        }
    }

    private void UIModuleHandlePacketDetour(UIModule* thisPtr, UIModulePacketType type, uint uintParam, void* packet)
    {
        uiModuleHandlePacketHook!.Original(thisPtr, type, uintParam, packet);

        if (type != UIModulePacketType.ZoneInit || packet == null)
            return;

        var zoneInitPacket = (ZoneInitPacket*)packet;
        lock (gate)
        {
            lastZoneInitSnapshot = new ZoneInitSnapshot(
                HookActive: true,
                HasCapturedPacket: true,
                CapturedAtUtc: DateTime.UtcNow,
                ServerId: zoneInitPacket->ServerId,
                TerritoryTypeId: zoneInitPacket->TerritoryTypeId,
                PacketInstance: zoneInitPacket->Instance,
                ContentFinderConditionId: zoneInitPacket->ContentFinderConditionId,
                PopRangeId: zoneInitPacket->PopRangeId,
                Flags: zoneInitPacket->Flags);
        }

        if (enabled && TryResolveEurekaZoneByTerritoryType(zoneInitPacket->TerritoryTypeId, out _))
            displayRefreshPending = true;
    }

    private EurekaInstanceResolution ResolveCurrentInstanceResolution()
    {
        uint publicInstanceId = 0;
        uint publicTerritoryTypeId = 0;
        short proxyInstanceId = 0;
        short networkInstanceId = 0;
        var currentTerritoryTypeId = clientState.TerritoryType;
        var clientStateInstanceId = clientState.Instance;
        var liveZoneInitSnapshot = GetZoneInitSnapshot();
        var liveZoneInitMatchesCurrentTerritory = false;
        ushort liveZoneInitInstanceId = 0;
        ushort liveZoneInitServerId = 0;
        uint liveZoneInitPopRangeId = 0;
        var replayZoneInitMatchesCurrentTerritory = false;
        ushort replayZoneInitInstanceId = 0;
        ushort replayZoneInitServerId = 0;
        uint replayZoneInitPopRangeId = 0;

        try
        {
            var uiState = UIState.Instance();
            if (uiState != null)
            {
                publicInstanceId = uiState->PublicInstance.InstanceId;
                publicTerritoryTypeId = uiState->PublicInstance.TerritoryTypeId;
            }

            var frameworkSystem = FrameworkSystem.Instance();
            var proxy = frameworkSystem == null ? null : frameworkSystem->NetworkModuleProxy;
            if (proxy != null)
            {
                proxyInstanceId = proxy->GetCurrentInstance();
                var networkModule = proxy->NetworkModule;
                networkInstanceId = networkModule == null ? (short)0 : networkModule->CurrentInstance;
            }

            liveZoneInitMatchesCurrentTerritory = liveZoneInitSnapshot.HasCapturedPacket
                && liveZoneInitSnapshot.TerritoryTypeId != 0
                && liveZoneInitSnapshot.TerritoryTypeId == currentTerritoryTypeId;
            if (liveZoneInitMatchesCurrentTerritory)
            {
                liveZoneInitInstanceId = liveZoneInitSnapshot.PacketInstance;
                liveZoneInitServerId = liveZoneInitSnapshot.ServerId;
                liveZoneInitPopRangeId = liveZoneInitSnapshot.PopRangeId;
            }

            var replayManager = ContentsReplayManager.Instance();
            if (replayManager != null)
            {
                var replayZoneInit = replayManager->ZoneInitPacket;
                replayZoneInitMatchesCurrentTerritory = replayZoneInit.TerritoryTypeId != 0
                    && replayZoneInit.TerritoryTypeId == currentTerritoryTypeId;
                if (replayZoneInitMatchesCurrentTerritory)
                {
                    replayZoneInitInstanceId = replayZoneInit.Instance;
                    replayZoneInitServerId = replayZoneInit.ServerId;
                    replayZoneInitPopRangeId = replayZoneInit.PopRangeId;
                }
            }
        }
        catch
        {
            // Keep the last collected values and let the resolver fall through to a zero result.
        }

        uint instanceId;
        string source;
        if (publicInstanceId != 0)
        {
            instanceId = publicInstanceId;
            source = "UIState.PublicInstance.InstanceId";
        }
        else if (proxyInstanceId > 0)
        {
            instanceId = (uint)proxyInstanceId;
            source = "NetworkModuleProxy.GetCurrentInstance()";
        }
        else if (networkInstanceId > 0)
        {
            instanceId = (uint)networkInstanceId;
            source = "NetworkModule.CurrentInstance";
        }
        else if (liveZoneInitInstanceId != 0)
        {
            instanceId = liveZoneInitInstanceId;
            source = "Live ZoneInitPacket.Instance";
        }
        else if (clientStateInstanceId != 0)
        {
            instanceId = clientStateInstanceId;
            source = "IClientState.Instance";
        }
        else if (replayZoneInitInstanceId != 0)
        {
            instanceId = replayZoneInitInstanceId;
            source = "Replay ZoneInitPacket.Instance";
        }
        else if (liveZoneInitServerId != 0)
        {
            instanceId = liveZoneInitServerId;
            source = "Live ZoneInitPacket.ServerId";
        }
        else if (replayZoneInitServerId != 0)
        {
            instanceId = replayZoneInitServerId;
            source = "Replay ZoneInitPacket.ServerId";
        }
        else
        {
            instanceId = 0;
            source = "None";
        }

        var serverId = liveZoneInitServerId != 0 ? liveZoneInitServerId : replayZoneInitServerId;
        var popRangeId = liveZoneInitPopRangeId != 0 ? liveZoneInitPopRangeId : replayZoneInitPopRangeId;
        var diagnostics = $"Territory={currentTerritoryTypeId}, PublicInstance={publicInstanceId}, PublicTerritory={publicTerritoryTypeId}, Proxy={proxyInstanceId}, Network={networkInstanceId}, ClientState={clientStateInstanceId}, LiveZoneInitMatch={liveZoneInitMatchesCurrentTerritory}, LiveZoneInitInstance={liveZoneInitInstanceId}, LiveZoneInitServerId={liveZoneInitServerId}, LiveZoneInitPopRangeId={liveZoneInitPopRangeId}, ReplayZoneInitMatch={replayZoneInitMatchesCurrentTerritory}, ReplayZoneInitInstance={replayZoneInitInstanceId}, ReplayZoneInitServerId={replayZoneInitServerId}, ReplayZoneInitPopRangeId={replayZoneInitPopRangeId}";
        return new EurekaInstanceResolution(instanceId, source, serverId, popRangeId, diagnostics);
    }

    private void ApplyResolution(EurekaZone zone, EurekaInstanceResolution resolution)
    {
        lastObservedZone = zone;
        lastObservedInstanceId = resolution.InstanceId;
        lastObservedInstanceSource = resolution.Source;
        lastObservedServerId = resolution.ServerId;
        lastObservedPopRangeId = resolution.PopRangeId;
        lastResolutionDiagnostics = resolution.Diagnostics;
    }

    private static string FormatSourceLabel(string source)
        => string.IsNullOrWhiteSpace(source) || string.Equals(source, "None", StringComparison.Ordinal)
            ? string.Empty
            : $" via {source}";

    private static string BuildResolutionSourceSuffix(EurekaInstanceResolution resolution)
        => string.IsNullOrWhiteSpace(resolution.Source) || string.Equals(resolution.Source, "None", StringComparison.Ordinal)
            ? string.Empty
            : $" via {resolution.Source}";

    private static string BuildResolutionDetailsSuffix(EurekaInstanceResolution resolution)
    {
        var parts = new List<string>();
        if (resolution.ServerId != 0)
            parts.Add($"ServerId={resolution.ServerId}");
        if (resolution.PopRangeId != 0)
            parts.Add($"PopRangeId={resolution.PopRangeId}");

        return parts.Count == 0
            ? string.Empty
            : $" ({string.Join(", ", parts)})";
    }

    private void AnnounceNewInstance(EurekaZone zone, EurekaInstanceResolution resolution)
    {
        var zoneLabel = GetZoneLabel(zone);
        Plugin.ChatGui.Print($"[XASlave] {zoneLabel} Instance: {resolution.InstanceId} via {resolution.Source}{BuildResolutionDetailsSuffix(resolution)}. Baseline updated.");
        if (configuration.EurekaInstanceIdPlaySound)
            XAPeepSoundPlayer.TryPlayAlert(configuration.EurekaInstanceIdSoundEffectId, configuration.EurekaInstanceIdSoundVolume, log);
    }

    private void TryRefreshDisplayedInstance()
    {
        if (!enabled || !clientState.IsLoggedIn)
        {
            displayRefreshPending = false;
            SetDtrState(string.Empty, false);
            return;
        }

        if (!TryResolveEurekaZoneByTerritoryType(clientState.TerritoryType, out var currentZone))
        {
            displayRefreshPending = false;
            SetDtrState(string.Empty, false);
            UpdateStatusText();
            return;
        }

        if (!playerState.IsLoaded || !IsCurrentZoneReadyForInstanceRead())
            return;

        displayRefreshPending = false;
        var resolution = ResolveCurrentInstanceResolution();
        ApplyResolution(currentZone, resolution);
        UpdateDtrEntryFromCachedState();
        UpdateStatusText();
    }

    private void UpdateDtrEntryFromCachedState()
    {
        if (!enabled || !configuration.EurekaInstanceIdShowInDtr || !clientState.IsLoggedIn)
        {
            SetDtrState(string.Empty, false);
            return;
        }

        if (!TryResolveEurekaZoneByTerritoryType(clientState.TerritoryType, out var currentZone))
        {
            SetDtrState(string.Empty, false);
            return;
        }

        var zoneLabel = GetZoneLabel(currentZone);
        var text = lastObservedZone == currentZone && lastObservedInstanceId != 0
            ? $"{zoneLabel}: {lastObservedInstanceId}"
            : $"{zoneLabel}: ?";
        SetDtrState(text, true);
    }

    private void SetDtrState(string text, bool shown)
    {
        try
        {
            if (!shown)
            {
                if (lastDtrShown && dtrEntry != null)
                    dtrEntry.Shown = false;

                lastDtrText = string.Empty;
                lastDtrShown = false;
                return;
            }

            dtrEntry ??= dtrBar.Get("XA Eureka");
            if (!lastDtrShown || !string.Equals(lastDtrText, text, StringComparison.Ordinal))
                dtrEntry.Text = text;

            if (!lastDtrShown)
                dtrEntry.Shown = true;

            lastDtrText = text;
            lastDtrShown = true;
        }
        catch
        {
            // DTR is optional; keep the service logic alive even if the entry fails.
        }
    }

    private void RemoveDtrEntry()
    {
        try
        {
            if (dtrEntry != null)
            {
                dtrEntry.Shown = false;
                dtrEntry.Remove();
                dtrEntry = null;
            }
        }
        catch
        {
            // Ignore DTR cleanup failures.
        }
        finally
        {
            lastDtrText = string.Empty;
            lastDtrShown = false;
        }
    }

    private readonly record struct ZoneInitSnapshot(
        bool HookActive,
        bool HasCapturedPacket,
        DateTime CapturedAtUtc,
        ushort ServerId,
        ushort TerritoryTypeId,
        ushort PacketInstance,
        ushort ContentFinderConditionId,
        uint PopRangeId,
        ZoneInitFlags Flags);

    private readonly record struct EurekaInstanceResolution(
        uint InstanceId,
        string Source,
        ushort ServerId,
        uint PopRangeId,
        string Diagnostics);
}
