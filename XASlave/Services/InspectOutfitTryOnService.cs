using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

internal sealed class InspectOutfitTryOnService : IDisposable
{
    private readonly SlaveNativeUiLibrary library;
    private readonly Action unavailable;
    private InspectOutfitNativeBinding? binding;
    private InspectOutfitWorld? world;
    private InspectOutfitControls? controls;
    private InspectFittingRoomControls? fittingControls;
    private InspectOutfitSequence? queue;
    private long hostEpoch = 1, session = 1, generation, frame;
    private bool enabled, disposed, retiring;
    private bool resetPreviewOnClose, previewWasActive;
    private string lastReadinessReason = string.Empty;
    internal string StatusText { get; private set; } = "Disabled";
    internal bool IsRunning => queue is { Terminal: false };

    internal InspectOutfitTryOnService(SlaveNativeUiLibrary library, Action unavailable)
    { this.library = library; this.unavailable = unavailable; }

    internal bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (enabled == value) return enabled;
        enabled = value;
        if (value)
        {
            Plugin.Framework.Update += Update;
            Plugin.ClientState.Logout += Logout;
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "CharacterInspect", Observe);
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "CharacterInspect", Observe);
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterInspect", Observe);
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Tryon", ObserveFittingRoom);
            StatusText = "Enabled: open a player inspection and choose Try On All.";
        }
        else
        {
            resetPreviewOnClose = false; previewWasActive = false;
            Stop("Disabled");
            Plugin.Framework.Update -= Update;
            Plugin.ClientState.Logout -= Logout;
            Plugin.AddonLifecycle.UnregisterListener(Observe);
            Plugin.AddonLifecycle.UnregisterListener(ObserveFittingRoom);
            QueueRetirement();
        }
        return enabled;
    }

    internal void Stop(string reason = "Try-on stopped; submitted native work may finish.")
    {
        generation++;
        queue?.Cancel(reason); queue = null;
        StatusText = reason;
    }

    private void Logout(int type, int code)
    {
        resetPreviewOnClose = false; previewWasActive = false;
        session++;
        Stop("Logged out; inspection try-on cancelled.");
        controls?.StopCallbacks();
        fittingControls?.StopCallbacks();
        QueueRetirement();
    }

    private void ObservePreviewClose()
    {
        if (!resetPreviewOnClose || binding == null || !Plugin.ClientState.IsLoggedIn) return;
        var active = binding.IsPreviewActive();
        if (previewWasActive && !active)
        {
            resetPreviewOnClose = false;
            session++;
            Stop("Fitting room closed; outfit cleared for the next inspection.");
            binding.ClearClosedPreview();
        }
        previewWasActive = active;
    }

    private void ObserveFittingRoom(AddonEvent kind, AddonArgs args)
    {
        if (kind == AddonEvent.PreFinalize) RetireFittingControls();
    }

    private void UpdateFittingControls()
    {
        if (fittingControls != null && !fittingControls.IsCurrent) RetireFittingControls();
        if (fittingControls == null)
        {
            if (!IsFittingRoomVisible() || !library.EnsureReady()) return;
            if (!enabled || disposed || retiring) return;
            binding ??= new InspectOutfitNativeBinding();
            var created = new InspectFittingRoomControls(ClearFittingRoom);
            if (!enabled || disposed || retiring || !created.IsCurrent) { created.Dispose(); return; }
            fittingControls = created;
        }
        fittingControls.Update();
    }

    private static unsafe bool IsFittingRoomVisible() => InspectFittingRoomControls.Resolve() != null;

    private void ClearFittingRoom()
    {
        if (!enabled || disposed || fittingControls == null || !fittingControls.IsCurrent || binding == null) return;
        try
        {
            session++; Stop("Clearing fitting-room preview.");
            binding.ClearPreview();
            resetPreviewOnClose = true; previewWasActive = true;
            StatusText = "Fitting room cleared; choose another outfit to try on.";
        }
        catch (Exception error) { Fail(error); }
    }

    private void RetireFittingControls()
    {
        var owned = fittingControls; fittingControls = null;
        owned?.Dispose();
    }

    private void Update(IFramework framework)
    {
        frame++;
        if (!enabled || disposed || retiring) return;
        try
        {
            ObservePreviewClose();
            UpdateFittingControls();
            var host = InspectOutfitControls.Resolve(hostEpoch, false);
            if (controls != null && controls.Host != host)
            {
                hostEpoch++; Stop("The inspection window changed."); RetireControls();
            }
            if (InspectOutfitControls.Resolve(hostEpoch) == null)
            {
                if (IsRunning) Stop("Inspection hidden; try-on cancelled.");
                controls?.Update(false, false);
                return;
            }
            var active = queue;
            if (active == null) return;
            active.Tick(Environment.TickCount64);
            if (queue != active || !enabled || disposed) return;
            StatusText = active.Status;
            if (active.Terminal)
            {
                // Retire before logging so the same failure is reported only once.
                queue = null;
                if (active.CompletedSource is { } source && binding != null && world != null)
                {
                    var owner = generation;
                    var epoch = session;
                    try
                    {
                        var result = binding.TryOnOwnedFacewear(world.Facewear(source),
                            () => enabled && !disposed && generation == owner && session == epoch);
                        if (generation == owner && session == epoch && !string.IsNullOrEmpty(result)) StatusText += " " + result;
                    }
                    catch (Exception error)
                    {
                        Plugin.Log.Warning(error, "Gear try-on completed, but optional facewear could not be applied.");
                        if (generation == owner && session == epoch) StatusText += " Facewear could not be applied.";
                    }
                }
                if (!active.Completed)
                {
                    if (active.Failure != null) Plugin.Log.Warning(active.Failure, "Inspection try-on failed: {Status}", active.Status);
                    else Plugin.Log.Warning("Inspection try-on stopped: {Status}", active.Status);
                }
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Observe(AddonEvent kind, AddonArgs args)
    {
        if (!enabled || disposed) return;
        try
        {
            if (kind == AddonEvent.PreFinalize)
            {
                if (controls != null && controls.Host.Addon != args.Addon.Address) return;
                Stop("Inspection finalized; try-on cancelled.");
                RetireControls(); hostEpoch++;
                return;
            }
            if (kind == AddonEvent.PostSetup)
            {
                hostEpoch++; Stop("Inspection ready; choose Try On All."); RetireControls(); return;
            }
            if (kind != AddonEvent.PostDraw || retiring) return;
            var host = InspectOutfitControls.Resolve(hostEpoch);
            if (host == null) return;
            if (controls != null && controls.Host != host)
            { hostEpoch++; Stop("The inspection layout changed."); RetireControls(); return; }
            if (!library.EnsureReady()) { StatusText = "Preparing native inspection controls."; return; }
            if (!enabled || disposed) return;
            binding ??= new InspectOutfitNativeBinding();
            world ??= new InspectOutfitWorld(binding.Validate, () => frame);
            if (controls == null)
            {
                var epoch = hostEpoch;
                var created = new InspectOutfitControls(host.Value, () => hostEpoch == epoch, Click);
                if (!enabled || disposed || hostEpoch != epoch) { created.Dispose(); return; }
                controls = created;
            }
            var identity = world.Identity();
            var ready = identity != null && world.Capture(controls.Host, identity.Value) != null;
            ReportReadiness(ready, world.UnavailableReason);
            controls.Update(IsRunning, ready);
        }
        catch (Exception error) { Fail(error); }
    }

    private void ReportReadiness(bool ready, string reason)
    {
        if (IsRunning) return;
        if (!ready)
        {
            StatusText = "Try On All unavailable: " + reason;
            if (lastReadinessReason != reason)
                Plugin.Log.Information("Inspection try-on unavailable: {Reason}", reason);
            lastReadinessReason = reason;
        }
        else if (lastReadinessReason != string.Empty)
        {
            lastReadinessReason = string.Empty;
            StatusText = "Inspection ready; choose Try On All.";
        }
    }

    private void Click()
    {
        if (!enabled || disposed || retiring || controls == null || world == null || binding == null) return;
        if (IsRunning) { Stop(); return; }
        try
        {
            var identity = world.Identity();
            if (identity == null || InspectOutfitControls.Resolve(hostEpoch) != controls.Host)
            { StatusText = "A ready player inspection is required."; return; }
            previewWasActive = binding.IsPreviewActive();
            if (!previewWasActive) binding.ClearClosedPreview();
            resetPreviewOnClose = true;
            var owner = ++generation;
            var access = new InspectOutfitNativeAccess(binding, world, () => session, () => frame,
                candidate => enabled && !disposed && generation == candidate);
            var adapter = new InspectOutfitNativeAdapter(world, access);
            queue = new InspectOutfitSequence(adapter, controls.Host, identity.Value, owner, Environment.TickCount64);
            StatusText = queue.Status;
        }
        catch (Exception error)
        {
            Stop("Try-on unavailable: " + error.Message);
            Plugin.Log.Warning(error, "Inspection try-on could not start.");
        }
    }

    private void Fail(Exception error)
    {
        SetEnabled(false); unavailable();
        StatusText = "Unavailable: " + error.Message;
        Plugin.Log.Warning(error, "Inspection try-on stopped at a native lifecycle boundary.");
    }

    private void QueueRetirement()
    {
        controls?.StopCallbacks();
        fittingControls?.StopCallbacks();
        if (Plugin.Framework.IsInFrameworkUpdateThread) { RetireControls(); RetireFittingControls(); return; }
        if (retiring) return;
        retiring = true;
        _ = Plugin.Framework.Run(() =>
        {
            try { RetireControls(); RetireFittingControls(); }
            catch (Exception error) { Plugin.Log.Error(error, "Inspection control retirement failed."); }
            finally { retiring = false; }
        });
    }

    internal void RetireControls()
    {
        NearbyZoneNativeBinding.RequireFramework();
        var owned = controls; controls = null;
        owned?.Dispose();
    }

    internal void RetireAllControls()
    {
        try { RetireControls(); }
        finally { RetireFittingControls(); }
    }

    internal void DrawOptions()
    {
        ImGui.TextWrapped(StatusText);
        ImGui.TextWrapped("Use Try On All on a player inspection. Both dyes and duplicate rings are retained; incompatible gear may be replaced by the game. Owned facewear is tried after the gear; unavailable facewear is skipped. Use Clear in the fitting room to start a fresh preview.");
        ImGui.BeginDisabled(!IsRunning);
        if (ImGui.Button("Stop##InspectOutfit")) Stop();
        ImGui.EndDisabled();
    }

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false); disposed = true; session++; Stop("Inspection try-on disposed.");
        controls?.StopCallbacks();
        // Plugin's shared library retires any remaining wrappers on framework
        // before toolkit shutdown, including late asynchronous initialization.
    }
}
