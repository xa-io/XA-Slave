using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Services;

internal readonly record struct NearbyPlayerPresenceKey(long Login, string Name, uint World, Guid Group);
internal sealed record NearbyPlayerEmission(NearbyPlayerPresenceKey Key, NearbyPlayerObservation Player,
    NearbyPlayerCompiledRule[] Rules, long Episode, int ActorOrder, int RuleOrder);

// Pure incremental scan reducer. No incomplete pass changes published presence or
// cooldowns. The service owns framework snapshots, generation cancellation and I/O.
internal sealed class NearbyPlayerScanEngine
{
    private sealed class Presence
    {
        internal long CooldownUntil, LastSeen, Episode;
        internal int AbsentScans = 2, CooldownSeconds;
        internal bool Present;
        internal Presence Copy() => (Presence)MemberwiseClone();
    }
    private sealed class Match
    {
        internal readonly NearbyPlayerObservation Player;
        internal readonly int ActorOrder, RuleOrder;
        internal readonly List<NearbyPlayerCompiledRule> Rules = new();
        internal Match(NearbyPlayerObservation player, int actor, int rule) { Player = player; ActorOrder = actor; RuleOrder = rule; }
    }
    private Dictionary<NearbyPlayerPresenceKey, Presence> records = new();
    private Dictionary<NearbyPlayerPresenceKey, Match> published = new();
    private Dictionary<NearbyPlayerPresenceKey, Presence> staged = new();
    private Dictionary<NearbyPlayerPresenceKey, Match> matches = new();
    private readonly List<NearbyPlayerEmission> emissions = new();
    private IEnumerator<KeyValuePair<NearbyPlayerPresenceKey, Presence>>? oldCursor;
    private IEnumerator<KeyValuePair<NearbyPlayerPresenceKey, Match>>? newCursor;
    private NearbyPlayerRuleSnapshot? rules;
    private IReadOnlyList<NearbyPlayerObservation> players = Array.Empty<NearbyPlayerObservation>();
    private int actorIndex, ruleIndex, phase, availableJobs, newMatches;
    private long login, passTime;
    private uint territory, pendingTerritory;
    internal bool Pending { get; private set; }
    internal string Status { get; private set; } = string.Empty;
    internal int RecordCount => records.Count;

    internal void Reset(long loginGeneration)
    {
        CancelPass(); login = loginGeneration; territory = 0;
        records.Clear(); published.Clear(); Status = string.Empty;
    }

    internal void CancelPass(bool invalidatePublished = true)
    {
        Pending = false; oldCursor?.Dispose(); newCursor?.Dispose(); oldCursor = null; newCursor = null;
        staged = new(); matches = new(); emissions.Clear(); players = Array.Empty<NearbyPlayerObservation>(); rules = null;
        // Cancellation is not an unmatched scan and never alters cooldown records.
        if (invalidatePublished) published.Clear();
    }

    internal void ChangeRule(Guid ruleId)
    {
        CancelPass(invalidatePublished: false);
        foreach (var key in published.Where(entry => entry.Value.Rules.Any(rule => rule.Id == ruleId)).Select(entry => entry.Key).ToArray())
            published.Remove(key);
    }

    internal bool Begin(NearbyPlayerRuleSnapshot snapshot, IReadOnlyList<NearbyPlayerObservation> observations,
        long loginGeneration, uint territoryId, long monotonicMilliseconds, int freeActionJobs)
    {
        if (Pending) return false;
        if (login != loginGeneration) Reset(loginGeneration);
        rules = snapshot; players = observations; pendingTerritory = territoryId; passTime = monotonicMilliseconds;
        actorIndex = 0; ruleIndex = 0; phase = 0; newMatches = 0; availableJobs = Math.Clamp(freeActionJobs, 0, 256);
        staged = new(); matches = new(); emissions.Clear(); Status = string.Empty;
        Pending = true;
        return true;
    }

    internal IReadOnlyList<NearbyPlayerEmission>? Advance(Func<long> monotonicMilliseconds)
    {
        if (!Pending || rules == null) return null;
        var started = monotonicMilliseconds();
        do
        {
            if (phase == 0)
            {
                if (actorIndex >= players.Count)
                {
                    // Long incremental matching must not consume the whole cooldown
                    // before its completed scan can publish an appearance.
                    passTime = monotonicMilliseconds();
                    oldCursor = records.GetEnumerator(); phase = 1; continue;
                }
                if (ruleIndex >= rules.Rules.Count) { actorIndex++; ruleIndex = 0; continue; }
                var player = players[actorIndex]; var rule = rules.Rules[ruleIndex++];
                if (!rule.Matches(player, pendingTerritory)) continue;
                var key = new NearbyPlayerPresenceKey(login, player.Name.Trim().ToUpperInvariant(), player.HomeWorld, rule.CooldownGroup);
                if (!matches.TryGetValue(key, out var match))
                {
                    // The input crowd may exceed record capacity. Preserve all old
                    // records and bound new match admission without evicting cooldowns.
                    if (!records.ContainsKey(key))
                    {
                        if (records.Count + newMatches >= 8192) { Status = "Capacity reached: additional matches suppressed."; continue; }
                        newMatches++;
                    }
                    matches[key] = match = new Match(player, actorIndex, ruleIndex - 1);
                }
                // An advanced group is one RuleId; the legacy group intentionally
                // retains only its first match, preserving the old Any-rule behavior.
                if (match.Rules.Count == 0) match.Rules.Add(rule);
            }
            else if (phase == 1)
            {
                if (!oldCursor!.MoveNext()) { oldCursor.Dispose(); oldCursor = null; newCursor = matches.GetEnumerator(); phase = 2; continue; }
                var entry = oldCursor.Current;
                matches.TryGetValue(entry.Key, out var match);
                Merge(entry.Key, entry.Value, match);
            }
            else
            {
                if (!newCursor!.MoveNext())
                {
                    newCursor.Dispose(); newCursor = null;
                    records = staged; published = matches; territory = pendingTerritory;
                    Pending = false; players = Array.Empty<NearbyPlayerObservation>(); rules = null;
                    emissions.Sort((left, right) => left.ActorOrder != right.ActorOrder ? left.ActorOrder.CompareTo(right.ActorOrder) : left.RuleOrder.CompareTo(right.RuleOrder));
                    return emissions.ToArray();
                }
                var entry = newCursor.Current;
                if (records.ContainsKey(entry.Key)) continue;
                if (staged.Count >= 8192) { Status = "Capacity reached: additional actors/rules suppressed."; continue; }
                Merge(entry.Key, null, entry.Value);
            }
        } while (monotonicMilliseconds() - started < 2);
        return null;
    }

    private void Merge(NearbyPlayerPresenceKey key, Presence? previous, Match? match)
    {
        var state = previous?.Copy() ?? new Presence();
        if (match == null)
        {
            state.Present = false; state.AbsentScans = Math.Min(2, state.AbsentScans + 1);
            if (passTime - state.LastSeen > Math.Max(state.CooldownSeconds * 1000L, 600000L)) return;
            staged[key] = state; return;
        }
        if (territory != pendingTerritory) { state.Present = false; state.AbsentScans = 2; }
        var appearance = state.AbsentScans >= 2;
        if (appearance) state.Episode++;
        state.Present = true; state.AbsentScans = 0; state.LastSeen = passTime;
        state.CooldownSeconds = match.Rules.Max(rule => rule.CooldownSeconds);
        var periodic = match.Rules.Any(rule => rule.Trigger == NearbyPlayerTriggerMode.Periodic);
        var admitted = (appearance || periodic) && passTime >= state.CooldownUntil;
        if (admitted) state.CooldownUntil = passTime + state.CooldownSeconds * 1000L;
        staged[key] = state;
        if (!admitted) return;
        // Reserve before returning any actions; failures/capacity cannot replay an edge.
        if (emissions.Count >= availableJobs) { Status = "Action capacity reached: additional transitions suppressed."; return; }
        emissions.Add(new(key, match.Player, match.Rules.ToArray(), state.Episode, match.ActorOrder, match.RuleOrder));
    }

    internal bool StillMatches(NearbyPlayerEmission emission, Guid ruleId)
    {
        return records.TryGetValue(emission.Key, out var state) && state.Present && state.Episode == emission.Episode
            && published.TryGetValue(emission.Key, out var match) && match.Rules.Any(rule => rule.Id == ruleId && rule.Enabled);
    }
}
