using System;
using System.Collections.Generic;

namespace XASlave.Data;

public sealed class NearbyPlayersSettings
{
    public bool Enabled { get; set; }
    public bool WindowOpen { get; set; }
    public bool WindowLocked { get; set; }
    public bool ShowSearchBar { get; set; } = true;
    public bool ShowFriendsOnTop { get; set; } = true;
    public bool ShowDtr { get; set; } = true;
    public bool ShowNearbyLines { get; set; }
    public bool ShowTargeterLines { get; set; } = true;
    public bool ChatAlerts { get; set; } = true;
    public bool ToastAlerts { get; set; } = true;
    public bool SpeechAlerts { get; set; } = true;
    public bool FilterFriend { get; set; }
    public float Scale { get; set; } = 1f;
    public List<NearbyTargeterHistory> History { get; set; } = new();
}

// Intentionally separate from Peep's database and free of content/account/object identifiers.
public sealed record NearbyTargeterHistory(string Name, uint HomeWorldId, uint JobId, uint TerritoryId,
    DateTime StartedUtc, double DurationSeconds);

// Session key is in memory only. Reused native IDs cannot join different characters or zones.
internal readonly record struct PlayerObservationKey(long Generation, ulong GameObjectId, uint EntityId, string Name, uint HomeWorldId)
{
    internal static PlayerObservationKey From(long generation, PlayerObservation player)
        => new(generation, player.GameObjectId, player.EntityId, player.Name, player.HomeWorldId);
}
