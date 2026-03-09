using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace XASlave.Helpers;

/// <summary>
/// Shared game math utilities — eliminates duplicated distance calculations across movement functions.
/// </summary>
public static class GameMath
{
    /// <summary>
    /// Calculates ring distance between two game objects (center-to-center minus both hitboxes).
    /// This is the effective interaction distance used by the game engine.
    /// </summary>
    public static float RingDistance(IGameObject a, IGameObject b)
    {
        var cd = CenterDistance(a.Position, b.Position);
        return cd - a.HitboxRadius - b.HitboxRadius;
    }

    /// <summary>
    /// Calculates ring distance from positions and hitbox radii directly.
    /// Use when you already have the raw values (e.g., inside RunOnFrameworkThread lambdas).
    /// </summary>
    public static float RingDistance(Vector3 posA, float hitboxA, Vector3 posB, float hitboxB)
    {
        return CenterDistance(posA, posB) - hitboxA - hitboxB;
    }

    /// <summary>
    /// Calculates center-to-center 3D distance between two positions.
    /// </summary>
    public static float CenterDistance(Vector3 a, Vector3 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dz = b.Z - a.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
