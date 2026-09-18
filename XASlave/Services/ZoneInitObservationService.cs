using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

// Managed publication seam for the existing always-owned Eureka UIModule observer; this service installs no hook.
public sealed class ZoneInitObservationService : IDisposable
{
    private readonly object gate = new();
    private readonly ZoneInitIdentityState state = new();
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly IFramework framework;
    private bool disposed, hookAvailable;
    public event Action? Invalidated;

    public ZoneInitObservationService(IClientState client, IPlayerState player, IObjectTable objects, ICondition conditions, IFramework framework)
    {
        this.client = client; this.player = player; this.objects = objects; this.conditions = conditions; this.framework = framework;
        // Plugin construction runs on a loader thread. Publish readiness on the first framework update.
        framework.Update += Update;
        client.TerritoryChanged += TerritoryChanged;
        client.Logout += Logout;
        conditions.ConditionChange += ConditionChanged;
    }
    public ZoneIdentityView Read()
    {
        lock (gate) return state.Read(hookAvailable);
    }
    public void SetHookAvailable(bool value)
    {
        lock (gate) hookAvailable = value;
        if (!value) NotifyInvalidated();
    }
    public void Capture(ushort server, ushort instance, ushort territory, bool replay)
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        try
        {
            // Scalar identity is copied now; no DTR, ImGui or consumer callbacks run in the packet path.
            var contentId = client.IsLoggedIn ? player.ContentId : 0;
            var world = objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
            lock (gate) { if (!disposed) state.Stage(server, instance, territory, replay, contentId, world); }
        }
        catch { lock (gate) hookAvailable = false; }
    }
    private void Update(IFramework _) => Refresh();
    public void Refresh()
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        ZoneIdentityContext before;
        lock (gate) before = state.Context;
        var loggedIn = client.IsLoggedIn;
        var actor = objects.LocalPlayer;
        var loading = conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51];
        var replay = conditions[ConditionFlag.DutyRecorderPlayback];
        // During a known load retain only login/world metadata; Ready remains false. Logout always zeros it.
        var content = loggedIn ? (player.ContentId != 0 ? player.ContentId : before.ContentId) : 0;
        var world = loggedIn ? (actor?.CurrentWorld.RowId ?? before.World) : 0;
        var dc = actor == null ? (loggedIn ? before.DataCenter : 0) : (actor.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.RowId ?? 0);
        var ready = loggedIn && player.IsLoaded && actor != null && !loading && !replay;
        lock (gate) state.SetContext(content, client.TerritoryType, world, dc, ready, replay, loading);
        bool changed;
        lock (gate) changed = state.Context.EntryGeneration != before.EntryGeneration;
        if (changed) NotifyInvalidated();
    }
    private void TerritoryChanged(uint territory)
    {
        lock (gate) state.Invalidate(territory);
        NotifyInvalidated();
    }
    private void Logout(int _, int __)
    {
        lock (gate) state.SetContext(0, 0, 0, 0, false, false, true);
        NotifyInvalidated();
    }
    private void ConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.BetweenAreas && flag != ConditionFlag.BetweenAreas51 && flag != ConditionFlag.DutyRecorderPlayback) return;
        // Clear clickable publication at the event, not at the next DTR refresh.
        if (value || flag == ConditionFlag.DutyRecorderPlayback)
        {
            lock (gate) state.Invalidate(client.TerritoryType, flag == ConditionFlag.DutyRecorderPlayback);
            NotifyInvalidated();
        }
    }
    private void NotifyInvalidated()
    {
        try { Invalidated?.Invoke(); } catch { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= Update; client.TerritoryChanged -= TerritoryChanged; client.Logout -= Logout; conditions.ConditionChange -= ConditionChanged;
        lock (gate) { state.Invalidate(0); hookAvailable = false; }
        NotifyInvalidated(); Invalidated = null;
    }
}
