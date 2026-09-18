using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;

namespace XASlave.Data;

// Copied on the framework thread. No native object or account/content identifier survives capture.
public sealed record PlayerObservation(
    ulong GameObjectId,
    uint EntityId,
    string Name,
    uint HomeWorldId,
    uint CurrentWorldId,
    uint JobId,
    ulong TargetObjectId,
    StatusFlags StatusFlags,
    Vector3 Position,
    string CompanyTag = "",
    float Rotation = 0);

public sealed record PlayerObservationSnapshot(
    long Generation,
    uint TerritoryId,
    PlayerObservation? LocalPlayer,
    IReadOnlyList<PlayerObservation> Players);
