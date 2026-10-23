using System;
using World = Lumina.Excel.Sheets.World;

namespace XASlave.Services;

internal sealed partial class InspectOutfitTryOnService
{
    private readonly Action openHistory;
    private InspectOutfitSnapshot? previousHistory, recordedHistory;
    private ushort previousFacewear, recordedFacewear;
    private InspectOutfitSavedSource? savedSource;
    private Guid? pendingHistoryId;
    internal InspectOutfitHistory History { get; }
    internal bool CanTryHistory => enabled && !disposed && !retiring && !IsRunning && pendingHistoryId == null
        && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded;

    internal void RequestHistoryTryOn(Guid id)
    {
        if (!CanTryHistory) return;
        pendingHistoryId = id;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S0ADB7C39D89C1D1A"));
    }

    internal void RemoveHistory(Guid id)
    {
        if (savedSource?.EntryId == id || pendingHistoryId == id) Stop(global::XASlave.Localization.UiMessage.Key("SAECB7EB32440A7DD"));
        History.Remove(id);
        // Keep recordedHistory until this inspection changes, so deleting the
        // currently inspected outfit cannot immediately record it again.
    }

    private void StartPendingHistory()
    {
        if (pendingHistoryId is not { } id) return;
        pendingHistoryId = null;
        if (!CanTryHistory) return;
        var entry = History.Find(id);
        if (entry == null) { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF70EB456A90EC6F0")); return; }
        try
        {
            binding ??= new InspectOutfitNativeBinding();
            binding.Validate();
            var owner = ++generation;
            var epoch = session;
            var source = new InspectOutfitSavedSource(entry, owner, () => frame);
            previewWasActive = binding.IsPreviewActive();
            if (!previewWasActive) binding.ClearClosedPreview();
            if (!enabled || disposed || retiring || generation != owner || session != epoch
                || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded || History.Find(id) == null) return;
            resetPreviewOnClose = true;
            var access = new InspectOutfitNativeAccess(binding, source, () => session, () => frame,
                candidate => enabled && !disposed && generation == candidate);
            var adapter = new InspectOutfitNativeAdapter(source, access);
            savedSource = source;
            queue = new InspectOutfitSequence(adapter, source.Host, source.Identity, owner, Environment.TickCount64);
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, queue.StatusMessage);
        }
        catch (Exception error)
        {
            Stop(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SA6B6DCBAD898FEC6"), global::XASlave.Localization.UiMessage.FromException(error)));
            Plugin.Log.Warning(error, "Saved outfit try-on could not start.");
        }
    }

    private void ResetHistoryObservation()
    {
        previousHistory = null; recordedHistory = null;
        previousFacewear = 0; recordedFacewear = 0;
    }

    private void ObserveHistory(InspectOutfitHost? host)
    {
        if (host == null || world == null) { ResetHistoryObservation(); return; }
        var identity = world.Identity();
        var snapshot = identity == null ? null : world.Capture(host.Value, identity.Value);
        if (snapshot == null) { previousHistory = null; return; }
        var facewear = world.Facewear(snapshot);
        var stable = previousHistory != null && previousHistory.SameSource(snapshot)
            && snapshot.Frame == previousHistory.Frame + 1 && facewear == previousFacewear;
        previousHistory = snapshot; previousFacewear = facewear;
        if (!stable || (recordedHistory != null && recordedHistory.SameSource(snapshot) && recordedFacewear == facewear)) return;
        var worldName = Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(identity!.Value.World)?.Name.ToString()
            ?? identity.Value.World.ToString();
        History.Record(snapshot, facewear, worldName);
        recordedHistory = snapshot; recordedFacewear = facewear;
    }
}
