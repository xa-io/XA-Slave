using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public static class CharacterSafetyHelper
{
    public static bool IsNormalCondition(ICondition condition)
    {
        return IsSafeCondition(condition, allowBoundByDuty: false);
    }

    public static bool IsDutySafeCondition(ICondition condition)
    {
        return IsSafeCondition(condition, allowBoundByDuty: true);
    }

    private static bool IsSafeCondition(ICondition condition, bool allowBoundByDuty)
    {
        return !condition[ConditionFlag.InCombat]
            && (allowBoundByDuty || !condition[ConditionFlag.BoundByDuty])
            && !condition[ConditionFlag.WatchingCutscene]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent]
            && !condition[ConditionFlag.Occupied]
            && !condition[ConditionFlag.Occupied30]
            && !condition[ConditionFlag.Occupied33]
            && !condition[ConditionFlag.Occupied38]
            && !condition[ConditionFlag.Occupied39]
            && !condition[ConditionFlag.OccupiedInEvent]
            && !condition[ConditionFlag.OccupiedInQuestEvent]
            && !condition[ConditionFlag.OccupiedSummoningBell]
            && !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51];
    }

    public static unsafe bool IsNamePlateReady()
    {
        try
        {
            var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("NamePlate");
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPlayerAvailable()
    {
        try
        {
            if (!Plugin.PlayerState.IsLoaded)
                return false;

            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null)
                return false;

            return IsSafeCondition(Plugin.Condition, allowBoundByDuty: false);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPlayerAvailableInDuty()
    {
        try
        {
            if (!Plugin.PlayerState.IsLoaded)
                return false;

            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null)
                return false;

            return IsSafeCondition(Plugin.Condition, allowBoundByDuty: true);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCharacterSafeWaitReady()
    {
        return IsNamePlateReady() && IsPlayerAvailable() && IsNotCasting();
    }

    public static bool IsCharacterSafeWaitReadyInDuty()
    {
        return IsNamePlateReady() && IsPlayerAvailableInDuty() && IsNotCasting();
    }

    private static bool IsNotCasting()
    {
        try
        {
            return !Plugin.Condition[ConditionFlag.Casting];
        }
        catch
        {
            return false;
        }
    }
}
