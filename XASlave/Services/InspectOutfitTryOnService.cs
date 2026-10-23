using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

internal sealed partial class InspectOutfitTryOnService : IDisposable
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
    internal string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    internal bool IsRunning => queue is { Terminal: false };

    internal InspectOutfitTryOnService(SlaveNativeUiLibrary library, Action unavailable, Configuration configuration, Action openHistory)
    {
        this.library = library; this.unavailable = unavailable;
        History = new InspectOutfitHistory(configuration);
        this.openHistory = openHistory;
    }

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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S416EB39699F009B9"));
        }
        else
        {
            ResetHistoryObservation();
            resetPreviewOnClose = false; previewWasActive = false;
            Stop(global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            Plugin.Framework.Update -= Update;
            Plugin.ClientState.Logout -= Logout;
            Plugin.AddonLifecycle.UnregisterListener(Observe);
            Plugin.AddonLifecycle.UnregisterListener(ObserveFittingRoom);
            QueueRetirement();
        }
        return enabled;
    }

    internal void Stop() => Stop(global::XASlave.Localization.UiMessage.Key("SE7CEF21ECCF72D44"));
    internal void Stop(string reason) => Stop(global::XASlave.Localization.UiMessage.Raw(reason));
    internal void Stop(global::XASlave.Localization.UiMessage reason)
    {
        generation++;
        queue?.Cancel(reason); queue = null;
        savedSource = null; pendingHistoryId = null;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, reason);
    }

    private void Logout(int type, int code)
    {
        ResetHistoryObservation();
        resetPreviewOnClose = false; previewWasActive = false;
        session++;
        Stop(global::XASlave.Localization.UiMessage.Key("S4A7DD9A5E88CB628"));
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
            Stop(global::XASlave.Localization.UiMessage.Key("S6B20A6FCCFFAA439"));
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
            session++; Stop(global::XASlave.Localization.UiMessage.Key("S387C753060FB95B8"));
            binding.ClearPreview();
            resetPreviewOnClose = true; previewWasActive = true;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S0316829888AB9B79"));
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
            StartPendingHistory();
            var host = InspectOutfitControls.Resolve(hostEpoch, false);
            if (controls != null && controls.Host != host)
            {
                hostEpoch++;
                if (savedSource == null) Stop(global::XASlave.Localization.UiMessage.Key("S7E125B6913364B9F"));
                ResetHistoryObservation(); RetireControls();
            }
            var visibleHost = InspectOutfitControls.Resolve(hostEpoch);
            ObserveHistory(visibleHost);
            if (visibleHost == null)
            {
                if (IsRunning && savedSource == null) Stop(global::XASlave.Localization.UiMessage.Key("S2B41278DAE6B8B5A"));
                controls?.Update(false, false);
                if (savedSource == null) return;
            }
            var active = queue;
            if (active == null) return;
            active.Tick(Environment.TickCount64);
            if (queue != active || !enabled || disposed) return;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, active.StatusMessage);
            if (active.Terminal)
            {
                // Retire before logging so the same failure is reported only once.
                queue = null;
                if (active.CompletedSource is { } source && binding != null && (savedSource != null || world != null))
                {
                    var owner = generation;
                    var epoch = session;
                    try
                    {
                        var result = binding.TryOnOwnedFacewearMessage(savedSource?.Facewear ?? world!.Facewear(source),
                            () => enabled && !disposed && generation == owner && session == epoch);
                        if (generation == owner && session == epoch && !string.IsNullOrEmpty(result.Original)) StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(uiStatusText, StatusText), global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Raw(" "), result)));
                    }
                    catch (Exception error)
                    {
                        Plugin.Log.Warning(error, "Gear try-on completed, but optional facewear could not be applied.");
                        if (generation == owner && session == epoch) StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(uiStatusText, StatusText), global::XASlave.Localization.UiMessage.Key("S59A2E538F7CE375A")));
                    }
                }
                savedSource = null;
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
                if (savedSource == null) Stop(global::XASlave.Localization.UiMessage.Key("SAF534BB69CD86B1E"));
                ResetHistoryObservation();
                RetireControls(); hostEpoch++;
                return;
            }
            if (kind == AddonEvent.PostSetup)
            {
                hostEpoch++;
                if (savedSource == null) Stop(global::XASlave.Localization.UiMessage.Key("SB79D1B847619CD00"));
                ResetHistoryObservation(); RetireControls(); return;
            }
            if (kind != AddonEvent.PostDraw || retiring) return;
            var host = InspectOutfitControls.Resolve(hostEpoch);
            if (host == null) return;
            if (controls != null && controls.Host != host)
            {
                hostEpoch++;
                if (savedSource == null) Stop(global::XASlave.Localization.UiMessage.Key("S66A00A7CD4D6F582"));
                ResetHistoryObservation(); RetireControls(); return;
            }
            if (!library.EnsureReady()) { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SDE4DC91FFBBCF082")); return; }
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S5BE4EB35F0E19C02"), global::XASlave.Localization.UiMessage.Value(reason)));
            if (lastReadinessReason != reason)
                Plugin.Log.Information("Inspection try-on unavailable: {Reason}", reason);
            lastReadinessReason = reason;
        }
        else if (lastReadinessReason != string.Empty)
        {
            lastReadinessReason = string.Empty;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SB79D1B847619CD00"));
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
            { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S8823EEED33DAC183")); return; }
            previewWasActive = binding.IsPreviewActive();
            if (!previewWasActive) binding.ClearClosedPreview();
            resetPreviewOnClose = true;
            var owner = ++generation;
            var access = new InspectOutfitNativeAccess(binding, world, () => session, () => frame,
                candidate => enabled && !disposed && generation == candidate);
            var adapter = new InspectOutfitNativeAdapter(world, access);
            queue = new InspectOutfitSequence(adapter, controls.Host, identity.Value, owner, Environment.TickCount64);
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, queue.StatusMessage);
        }
        catch (Exception error)
        {
            Stop(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SF027662512726AC7"), global::XASlave.Localization.UiMessage.FromException(error)));
            Plugin.Log.Warning(error, "Inspection try-on could not start.");
        }
    }

    private void Fail(Exception error)
    {
        SetEnabled(false); unavailable();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SF5646BBF5EE343C7"), global::XASlave.Localization.UiMessage.FromException(error)));
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
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S36F5E06518DE4BA5"));
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S4005BB983C75D162") + "##InspectOutfit"))) openHistory();
        ImGui.SameLine();
        ImGui.BeginDisabled(!IsRunning);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SCAE7D57BC067A514") + "##InspectOutfit"))) Stop();
        ImGui.EndDisabled();
    }

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false); disposed = true; session++; Stop(global::XASlave.Localization.UiMessage.Key("S3BAF6D60439CAEF5"));
        controls?.StopCallbacks();
        // Plugin's shared library retires any remaining wrappers on framework
        // before toolkit shutdown, including late asynchronous initialization.
    }
}
