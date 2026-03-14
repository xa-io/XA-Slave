using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public static class CharacterSafetyHelper
{
    public static bool IsNormalCondition(ICondition condition)
    {
        return !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.BoundByDuty]
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

            return IsNormalCondition(Plugin.Condition);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCharacterSafeWaitReady()
    {
        return IsNamePlateReady() && IsPlayerAvailable();
    }
}
