using System;
using System.Collections.Generic;
using System.Diagnostics;
using XASlave.Data;

namespace XASlave.Services;

internal sealed class NearbyTargetingState
{
    private sealed class Session(PlayerObservation player, uint territory, DateTime utc, long tick)
    {
        internal PlayerObservation Player = player;
        internal readonly uint Territory = territory;
        internal readonly DateTime StartedUtc = utc;
        internal readonly long StartedTick = tick;
    }
    private readonly Dictionary<PlayerObservationKey, Session> active = new();
    private readonly Dictionary<PlayerObservationKey, long> alertTimes = new();
    private readonly NearbyPlayersSettings settings;
    internal IReadOnlyList<PlayerObservation> Targeters { get; private set; } = Array.Empty<PlayerObservation>();
    internal IReadOnlyList<PlayerObservationKey> NewTargeters { get; private set; } = Array.Empty<PlayerObservationKey>();
    internal long Generation { get; private set; } = -1;

    internal NearbyTargetingState(NearbyPlayersSettings settings)
    {
        this.settings = settings;
        settings.History ??= new();
        settings.History.RemoveAll(row => row == null || !double.IsFinite(row.DurationSeconds) || row.DurationSeconds < 0);
        TrimHistory();
    }

    internal bool Update(PlayerObservationSnapshot snapshot, DateTime utc, long tick)
    {
        var changed = false;
        if (snapshot.Generation != Generation)
        {
            changed = Finish(tick);
            alertTimes.Clear();
            Generation = snapshot.Generation;
        }
        var present = new HashSet<PlayerObservationKey>();
        var current = new List<PlayerObservation>();
        var added = new List<PlayerObservationKey>();
        if (snapshot.LocalPlayer is { } local)
        {
            foreach (var player in snapshot.Players)
            {
                // The targeter predicate intentionally compares against the local EntityId.
                if (player.TargetObjectId != local.EntityId) continue;
                var key = PlayerObservationKey.From(snapshot.Generation, player);
                if (!present.Add(key)) continue;
                current.Add(player);
                if (active.TryGetValue(key, out var session)) session.Player = player;
                else { active.Add(key, new(player, snapshot.TerritoryId, utc, tick)); added.Add(key); }
            }
        }
        var removed = new List<PlayerObservationKey>();
        foreach (var key in active.Keys) if (!present.Contains(key)) removed.Add(key);
        foreach (var key in removed) { Complete(key, tick); changed = true; }
        current.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
        added.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
        Targeters = current.AsReadOnly();
        NewTargeters = added.AsReadOnly();
        var expired = new List<PlayerObservationKey>();
        foreach (var (key, time) in alertTimes) if (Elapsed(time, tick) >= 30) expired.Add(key);
        foreach (var key in expired) alertTimes.Remove(key);
        return changed;
    }

    internal bool ConsumeAlertThrottle(long tick)
    {
        // Source Any() consumes only the first eligible new actor's 30-second throttle.
        foreach (var key in NewTargeters)
        {
            if (alertTimes.TryGetValue(key, out var time) && Elapsed(time, tick) < 30) continue;
            alertTimes[key] = tick;
            return true;
        }
        return false;
    }

    internal bool Finish(long tick)
    {
        var changed = active.Count != 0;
        foreach (var key in new List<PlayerObservationKey>(active.Keys)) Complete(key, tick);
        Targeters = Array.Empty<PlayerObservation>();
        NewTargeters = Array.Empty<PlayerObservationKey>();
        return changed;
    }

    internal void ClearHistory() => settings.History.Clear();

    internal double Duration(PlayerObservationKey key, long tick)
        => active.TryGetValue(key, out var session) ? Elapsed(session.StartedTick, tick) : 0;

    private void Complete(PlayerObservationKey key, long tick)
    {
        if (!active.Remove(key, out var session)) return;
        var player = session.Player;
        settings.History.Insert(0, new(player.Name, player.HomeWorldId, player.JobId, session.Territory, session.StartedUtc, Elapsed(session.StartedTick, tick)));
        TrimHistory();
    }

    private void TrimHistory()
    {
        if (settings.History.Count > 100) settings.History.RemoveRange(100, settings.History.Count - 100);
    }

    private static double Elapsed(long start, long end) => Math.Max(0, (end - start) / (double)Stopwatch.Frequency);
}
