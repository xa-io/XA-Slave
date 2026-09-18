using System;
using System.Collections.Generic;
using XASlave.Data;

namespace XASlave.Services;

[Flags] internal enum TargetingConsumer { Peep = 1, Nearby = 2 }
[Flags] internal enum TargetingOutput { Chat = 1, Notification = 2, Speech = 4 }

/// <summary>One observed edge and output claim per actor, shared by independently paced consumers.</summary>
public sealed class PlayerTargetingCoordinator
{
    private sealed class Edge { internal TargetingConsumer Consumers; internal TargetingOutput Delivered; }
    private readonly Dictionary<PlayerObservationKey, Edge> edges = new();
    private long generation = -1;

    internal void Observe(TargetingConsumer consumer, long currentGeneration, IReadOnlyList<PlayerObservation> players)
    {
        if (generation != currentGeneration) { edges.Clear(); generation = currentGeneration; }
        var present = new HashSet<PlayerObservationKey>();
        foreach (var player in players)
        {
            var key = PlayerObservationKey.From(currentGeneration, player);
            present.Add(key);
            if (!edges.TryGetValue(key, out var edge)) edges[key] = edge = new();
            edge.Consumers |= consumer;
        }
        var remove = new List<PlayerObservationKey>();
        foreach (var (key, edge) in edges)
        {
            if (!present.Contains(key)) edge.Consumers &= ~consumer;
            if (edge.Consumers == 0) remove.Add(key);
        }
        foreach (var key in remove) edges.Remove(key);
    }

    internal bool TryClaim(PlayerObservationKey key, TargetingOutput output)
    {
        if (!edges.TryGetValue(key, out var edge) || (edge.Delivered & output) != 0) return false;
        edge.Delivered |= output;
        return true;
    }

    internal void Release(TargetingConsumer consumer)
    {
        var remove = new List<PlayerObservationKey>();
        foreach (var (key, edge) in edges)
        {
            edge.Consumers &= ~consumer;
            if (edge.Consumers == 0) remove.Add(key);
        }
        foreach (var key in remove) edges.Remove(key);
    }
}
