using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XASlave.Data;
using Dalamud.Game.Text.SeStringHandling;

namespace XASlave.Services;

public enum NearbyPlayerAction { Target, Examine, AdventurerPlate, FocusTarget, Tell, Invite }

public sealed class NearbyPlayersService : IDisposable
{
    private readonly Plugin plugin;
    private readonly PlayerObservationProvider observations;
    private readonly PlayerTargetingCoordinator coordinator;
    private readonly ConcurrentQueue<System.Action> requests = new();
    private readonly HashSet<uint> searchPlaces = new();
    private IDtrBarEntry? dtr;
    private long nextScan;
    private long nextZoneScan;
    private string lastDtr = string.Empty;
    private string? dtrError;
    private bool inContent;
    private bool disposed;
    public bool Enabled { get; private set; }
    public string StatusText { get; private set; } = "Disabled";
    public NearbyPlayersSettings Settings => plugin.Configuration.NearbyPlayers;
    public PlayerObservationSnapshot Snapshot { get; private set; } = new(0, 0, null, Array.Empty<PlayerObservation>());
    public bool WindowOpen
    {
        get => Settings.WindowOpen;
        set
        {
            if (disposed || Settings.WindowOpen == value) return;
            Settings.WindowOpen = value;
            plugin.Configuration.Save();
        }
    }
    public bool DisplayAvailable { get; private set; }
    public bool ZoneEligible { get; private set; }
    public bool ContentMemberMode { get; private set; }
    public uint SearchPlace { get; private set; }
    public uint MapId { get; private set; }

    internal NearbyPlayersService(Plugin plugin, PlayerObservationProvider observations, PlayerTargetingCoordinator coordinator)
    {
        this.plugin = plugin;
        this.observations = observations;
        this.coordinator = coordinator;
        plugin.Configuration.NearbyPlayers ??= new();
        try
        {
            foreach (var row in Plugin.DataManager.GetExcelSheet<PlayerSearchSubLocation>())
                if (row.PlaceName.RowId is not (0 or 519)) searchPlaces.Add(row.PlaceName.RowId);
        }
        catch (Exception error) { Plugin.Log.Warning(error, "[XA Nearby] Area metadata unavailable."); }
        Plugin.Framework.Update += Update;
        Plugin.ClientState.Logout += Logout;
        Plugin.ClientState.TerritoryChanged += TerritoryChanged;
    }

    public bool SetEnabled(bool value)
    {
        if (disposed) return false;
        requests.Enqueue(() => ApplyEnabled(value));
        return value;
    }

    public void ChangeSettings(Action<NearbyPlayersSettings> change)
    {
        if (disposed) return;
        requests.Enqueue(() => { change(Settings); Settings.Scale = float.IsFinite(Settings.Scale) ? Math.Max(0.1f, Settings.Scale) : 1f; plugin.Configuration.Save(); });
    }

    public void ClearHistory()
    {
        if (!disposed) requests.Enqueue(() => { Settings.History.Clear(); plugin.Configuration.Save(); });
    }

    public void Locate(PlayerObservation selected)
    {
        var requestedGeneration = Snapshot.Generation;
        if (disposed) return;
        requests.Enqueue(() =>
        {
            if (!Enabled) return;
            var current = observations.CaptureNearby();
            if (current.Generation != requestedGeneration || current.TerritoryId == 0) return;
            foreach (var player in current.Players)
            {
                if (PlayerObservationKey.From(current.Generation, player) != PlayerObservationKey.From(requestedGeneration, selected)) continue;
                var position = player.Position;
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Z) || Math.Abs(position.X) > 100000 || Math.Abs(position.Z) > 100000) return;
                var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(current.TerritoryId);
                if (territory is not { } row || row.Map.RowId == 0) return;
                Plugin.GameGui.OpenMapWithMapLink(current.TerritoryId, row.Map.RowId, position);
                return;
            }
        });
    }

    public void PlayerAction(PlayerObservation selected, NearbyPlayerAction action)
    {
        var generation = Snapshot.Generation;
        if (disposed) return;
        requests.Enqueue(() =>
        {
            try
            {
                if (!Enabled || plugin.TaskRunner.IsRunning)
                {
                    Plugin.ChatGui.PrintError("[XA Nearby] Player actions are unavailable while disabled or a task is running.");
                    return;
                }
                var current = observations.CaptureNearby();
                if (current.Generation == generation && current.Players.Any(p => PlayerObservationKey.From(generation, p) == PlayerObservationKey.From(generation, selected)))
                {
                    foreach (var actor in Plugin.ObjectTable)
                    {
                        if (actor is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter
                            || actor.GameObjectId != selected.GameObjectId || actor.EntityId != selected.EntityId) continue;
                        ExecutePlayerAction(actor, selected, action);
                        return;
                    }
                }
                Plugin.ChatGui.PrintError("[XA Nearby] That player is no longer loaded here.");
            }
            catch (Exception error)
            {
                Plugin.Log.Error(error, "[XA Nearby] Player action failed.");
                Plugin.ChatGui.PrintError("[XA Nearby] Could not perform that player action.");
            }
        });
    }

    private static unsafe void ExecutePlayerAction(Dalamud.Game.ClientState.Objects.Types.IGameObject actor, PlayerObservation selected, NearbyPlayerAction action)
    {
        switch (action)
        {
            case NearbyPlayerAction.Target:
                Plugin.TargetManager.Target = actor;
                return;
            case NearbyPlayerAction.FocusTarget:
                Plugin.TargetManager.FocusTarget = actor;
                return;
            case NearbyPlayerAction.Examine:
                var inspect = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInspect.Instance();
                if (inspect == null) throw new InvalidOperationException("Examine is unavailable.");
                inspect->ExamineCharacter(actor.EntityId);
                return;
            case NearbyPlayerAction.AdventurerPlate:
                var plate = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentCharaCard.Instance();
                if (plate == null) throw new InvalidOperationException("Adventurer plate is unavailable.");
                plate->OpenCharaCard((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address);
                return;
            case NearbyPlayerAction.Tell:
                var world = Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(selected.HomeWorldId)?.Name.ToString();
                if (string.IsNullOrWhiteSpace(world)) throw new InvalidOperationException("Home world is unavailable.");
                // Select the recipient only. The user writes and sends the tell.
                if (!ChatHelper.TrySend($"/tell {selected.Name}@{world}")) throw new InvalidOperationException("Tell recipient could not be set.");
                return;
            case NearbyPlayerAction.Invite:
                var invite = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyPartyInvite.Instance();
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
                if (invite == null || character->ContentId == 0 || selected.HomeWorldId > ushort.MaxValue)
                    throw new InvalidOperationException("Party invite is unavailable.");
                if (!invite->InviteToParty(character->ContentId, selected.Name, (ushort)selected.HomeWorldId))
                    Plugin.ChatGui.PrintError("[XA Nearby] The game did not accept the party invite request.");
                return;
        }
    }

    private void ApplyEnabled(bool value)
    {
        if (Enabled == value)
        {
            if (Settings.Enabled != value) { Settings.Enabled = value; plugin.Configuration.Save(); }
            return;
        }
        if (!value) Reset();
        Enabled = value;
        Settings.Enabled = value;
        nextScan = nextZoneScan = 0;
        if (value)
        {
            try
            {
                dtr ??= Plugin.DtrBar.Get("XA Nearby Players");
                dtr.OnClick = _ => WindowOpen = !WindowOpen;
                dtr.Shown = false;
                dtrError = null;
            }
            catch (Exception error) { dtrError = "DTR unavailable: " + error.Message; }
        }
        else StatusText = "Disabled";
        plugin.Configuration.Save();
    }

    private void Update(IFramework framework)
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        try
        {
            while (requests.TryDequeue(out var action)) action();
            if (!Enabled) return;
            var now = Stopwatch.GetTimestamp();
            if (!WindowOpen && now < nextScan) return;
            // Match a live table's frame cadence while open; hidden count refreshes four times a second.
            nextScan = WindowOpen ? now : now + Stopwatch.Frequency / 4;
            Snapshot = observations.CaptureNearby();
            if (now >= nextZoneScan)
            {
                nextZoneScan = now + Stopwatch.Frequency;
                UpdateTerritory();
                // Local observations need no server-side player-search refresh or native zone hooks.
            }
            try { UpdateDtr(); }
            catch (Exception error) { dtrError = "DTR unavailable: " + error.Message; }
            StatusText = Snapshot.LocalPlayer == null ? "Waiting for a character" : $"{Snapshot.Players.Count} loaded nearby";
            if (dtrError != null) StatusText += "; " + dtrError;
        }
        catch (Exception error)
        {
            Reset();
            Enabled = false;
            Settings.Enabled = false;
            plugin.Configuration.Save();
            StatusText = "Unavailable: " + error.Message;
            Plugin.Log.Warning(error, "[XASlave] Nearby Players update failed.");
        }
    }

    private void UpdateTerritory()
    {
        try
        {
            var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
            SearchPlace = territory?.PlaceNameZone.RowId ?? 0;
            MapId = territory?.Map.RowId ?? 0;
            inContent = (territory?.ContentFinderCondition.RowId ?? 0) != 0;
            ContentMemberMode = territory?.TerritoryIntendedUse.RowId is 41 or 48 or 61;
            ZoneEligible = Plugin.ClientState.IsLoggedIn && Plugin.ClientState.TerritoryType != 0 && (ContentMemberMode || (!inContent && searchPlaces.Contains(SearchPlace)));
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "[XA Nearby] Could not read area metadata.");
            SearchPlace = MapId = 0;
            ContentMemberMode = ZoneEligible = false;
            inContent = true; // Preserve party suppression when territory metadata cannot be established.
        }
        DisplayAvailable = Snapshot.LocalPlayer != null && (ZoneEligible || !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] || Plugin.ClientState.IsPvP);
    }

    private void UpdateDtr()
    {
        if (dtr == null) return;
        var text = "\uE033 " + Snapshot.Players.Count;
        if (text != lastDtr) { dtr.Text = text; dtr.Tooltip = $"{Snapshot.Players.Count} nearby players"; lastDtr = text; }
        dtr.Shown = Settings.ShowDtr && DisplayAvailable;
    }

    private void Logout(int _, int __) { observations.Invalidate(); Reset(); }
    private void TerritoryChanged(uint _) { observations.Invalidate(); Reset(); nextScan = nextZoneScan = 0; }

    private void Reset()
    {
        coordinator.Release(TargetingConsumer.Nearby);
        Snapshot = new(Snapshot.Generation, 0, null, Array.Empty<PlayerObservation>());
        DisplayAvailable = false;
        ZoneEligible = false;
        // Login, zone changes, disable and disposal clear observations, not the saved window preference.
        if (dtr != null) dtr.Shown = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        requests.Clear();
        Plugin.Framework.Update -= Update;
        Plugin.ClientState.Logout -= Logout;
        Plugin.ClientState.TerritoryChanged -= TerritoryChanged;
        try { Reset(); }
        finally
        {
            dtr?.Remove(); dtr = null; Enabled = false;
        }
    }
}
