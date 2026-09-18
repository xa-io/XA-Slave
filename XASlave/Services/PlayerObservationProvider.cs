using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>Framework-owned full object-table copies. Peep retains its cadence; Nearby reads each frame while open.</summary>
public sealed class PlayerObservationProvider
{
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private ulong localId;
    private string localName = string.Empty;
    private uint localWorld, territory;
    private long generation;
    private DateTime copiedFrame;
    private readonly Dictionary<ulong, PlayerObservation> frameCopies = new();

    public PlayerObservationProvider(IFramework framework, IClientState clientState, IObjectTable objects, ICondition conditions)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objects = objects;
        this.conditions = conditions;
    }

    public void Invalidate()
    {
        generation++;
        localId = 0;
        localName = string.Empty;
        localWorld = 0;
        frameCopies.Clear();
    }

    public PlayerObservationSnapshot CapturePeep()
    {
        var local = CaptureLocal();
        if (local == null) return Snapshot(null, Array.Empty<PlayerObservation>());
        var players = new List<PlayerObservation>();
        foreach (var obj in objects)
            if (obj is IPlayerCharacter player) players.Add(CopyInFrame(player));
        return Snapshot(local, players.AsReadOnly());
    }

    public PlayerObservationSnapshot CaptureNearby()
    {
        var local = CaptureLocal();
        if (local == null || conditions[ConditionFlag.WatchingCutscene] || conditions[ConditionFlag.WatchingCutscene78] || conditions[ConditionFlag.DutyRecorderPlayback])
            return Snapshot(local, Array.Empty<PlayerObservation>());
        var players = new List<PlayerObservation>();
        var seen = new HashSet<ulong>();
        // Enumerate the complete loaded object table, with no slot-index or displayed-player cap.
        foreach (var obj in objects)
        {
            if (obj is not IPlayerCharacter player || player.GameObjectId == local.GameObjectId
                || player.Level == 0 || player.ClassJob.RowId == 0
                || player.HomeWorld.RowId is 0 or ushort.MaxValue || player.CurrentWorld.RowId is 0 or ushort.MaxValue
                || !seen.Add(player.GameObjectId)) continue;
            var copy = CopyInFrame(player);
            if (!string.IsNullOrWhiteSpace(copy.Name)) players.Add(copy);
        }
        return Snapshot(local, players.AsReadOnly());
    }

    internal static bool QualifiesNearby(byte kind, uint entity, uint localEntity, uint job, uint nameId,
        bool hasContent, bool hasAccount, ushort home, ushort current)
        => kind == 1 && entity != localEntity && job != 0 && nameId == 0 && hasContent && hasAccount
           && home != ushort.MaxValue && current != ushort.MaxValue;

    private PlayerObservation? CaptureLocal()
    {
        if (!framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Not on main thread! Player observations require the framework update thread.");
        if (copiedFrame != framework.LastUpdateUTC)
        {
            copiedFrame = framework.LastUpdateUTC;
            frameCopies.Clear();
        }
        if (!clientState.IsLoggedIn || objects.LocalPlayer is not { } player)
        {
            if (localId != 0) Invalidate();
            return null;
        }
        var copy = Copy(player);
        if (copy.GameObjectId != localId || copy.Name != localName || copy.HomeWorldId != localWorld || territory != clientState.TerritoryType)
        {
            generation++;
            localId = copy.GameObjectId;
            localName = copy.Name;
            localWorld = copy.HomeWorldId;
            territory = clientState.TerritoryType;
            frameCopies.Clear();
        }
        return copy;
    }

    private PlayerObservationSnapshot Snapshot(PlayerObservation? local, IReadOnlyList<PlayerObservation> players)
        => new(generation, territory, local, players);

    private PlayerObservation CopyInFrame(IPlayerCharacter player)
    {
        var id = player.GameObjectId;
        if (frameCopies.TryGetValue(id, out var copied) && copied.EntityId == player.EntityId) return copied;
        return frameCopies[id] = Copy(player);
    }

    private static PlayerObservation Copy(IPlayerCharacter player)
        => new(player.GameObjectId, player.EntityId, player.Name.TextValue, player.HomeWorld.RowId,
            player.CurrentWorld.RowId, player.ClassJob.RowId, player.TargetObjectId, player.StatusFlags, player.Position, player.CompanyTag.TextValue, player.Rotation);
}
