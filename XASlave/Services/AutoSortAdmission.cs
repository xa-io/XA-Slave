using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

internal enum AutoSortAdmissionResult { Wait, Reject, Ready }

internal static class AutoSortAdmission
{
    // Pinned field initializers for inventory sort admission.
    internal static readonly int[] IdleAllowed = [1, 48, 4, 94, 69];
    internal static readonly int[] OccupiedEvents = [25, 30, 33, 38, 39, 35, 31, 32, 50, 58, 78, 45, 51, 11, 37, 5, 2, 7, 6, 8, 65, 9, 70, 3, 10, 64, 71, 15, 14, 12, 13, 16, 41, 43, 68, 67];

    internal static AutoSortAdmissionResult Evaluate(bool loggedIn, bool screenReady, IReadOnlySet<int> conditions,
        uint territory, uint map, bool pvp, bool duty, bool taskBusy)
    {
        if (!loggedIn || !screenReady) return AutoSortAdmissionResult.Wait;
        foreach (var flag in OccupiedEvents) if (conditions.Contains(flag)) return AutoSortAdmissionResult.Wait;
        foreach (var flag in conditions)
        {
            var allowed = false;
            foreach (var idle in IdleAllowed) if (flag == idle) allowed = true;
            if (!allowed) return AutoSortAdmissionResult.Reject;
        }
        if (territory == 0 || map == 0 || pvp || duty || taskBusy) return AutoSortAdmissionResult.Reject;
        return AutoSortAdmissionResult.Ready;
    }

    internal static unsafe AutoSortAdmissionResult Capture(bool foreignTaskBusy)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var conditions = new HashSet<int>();
        foreach (ConditionFlag flag in Plugin.Condition.AsReadOnlySet()) conditions.Add((int)flag);
        var client = Plugin.ClientState;
        var game = GameMain.Instance();
        // Missing or mismatched world metadata is an invalid zone, never permission to send.
        var validWorld = game != null && game->CurrentTerritoryTypeId == client.TerritoryType && game->CurrentMapId == client.MapId;
        return Evaluate(client.IsLoggedIn, NearbyZoneRequestAdapter.ScreenReady(), conditions,
            validWorld ? client.TerritoryType : 0, client.MapId, client.IsPvP,
            game == null || game->CurrentContentFinderConditionId != 0, foreignTaskBusy);
    }
}
