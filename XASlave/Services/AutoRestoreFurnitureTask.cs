using System;
using System.Collections.Generic;

namespace XASlave.Services;

// One shared TaskRunner job; the native adapter and attached HousingGoods owner are
// supplied by the feature service. This owner never issues native furniture commands.
internal sealed class AutoRestoreFurnitureTask : IDisposable
{
    private readonly TaskRunner runner;
    private readonly Func<IFurnitureRestoreAdapter> createAdapter;
    private IFurnitureRestoreAdapter? adapter;
    private FurnitureRestoreSequence? sequence;
    private long generation;
    private bool disposed, running, terminating, ownsRunner;
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("S9204566911DD7ECF");
    internal string Status { get; private set; } = global::XASlave.Localization.UiLocale.Native("S9204566911DD7ECF");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, Status);
    internal bool IsRunning => running;
    internal IReadOnlyList<FurnitureRestoreItemResult> Results => sequence?.Results ?? Array.Empty<FurnitureRestoreItemResult>();

    internal AutoRestoreFurnitureTask(TaskRunner runner, Func<IFurnitureRestoreAdapter> createAdapter)
    { this.runner = runner; this.createAdapter = createAdapter; }

    internal bool Start(FurnitureRestoreMode mode)
    {
        Plugin.Log.Information("[AutoRestoreFurniture] Start requested: {Mode}", mode);
        if (disposed || running || terminating || runner.IsRunning)
        {
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S7A51CB5AEA3CD411"));
            Plugin.Log.Warning("[AutoRestoreFurniture] Start rejected: {Status}", Status);
            return false;
        }
        var owned = ++generation;
        running = true;
        try
        {
            FurnitureRestoreCapacity.ValidateMode(mode);
            ReleaseAdapter();
            if (disposed || !running || owned != generation) return false;
            var created = createAdapter();
            if (disposed || !running || owned != generation) { created.Dispose(); return false; }
            adapter = created;
            sequence = new(adapter, mode, owned, Environment.TickCount64);
            ownsRunner = true;
            var accepted = runner.Start(global::XASlave.Localization.UiMessage.Key("S62F6C455F98922D0"), [new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("SB5B724D0C38B0930"),
                IsComplete = () => Tick(owned), TimeoutSec = 1801, MaxRetries = 0,
                OnTimeout = () => Stop(global::XASlave.Localization.UiMessage.Key("S0289334B2CEC26B8")),
            }], suppressCompletionReport: true, totalItems: 0, suppressLogoutCancel: false,
                onTerminal: () => Cleanup(owned), haltOnStepError: true);
            if (!accepted)
            {
                Cleanup(owned); Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S21436434C4877F7F"));
                Plugin.Log.Warning("[AutoRestoreFurniture] Start rejected: {Status}", Status);
                return false;
            }
            Plugin.Log.Information("[AutoRestoreFurniture] Start accepted: {Mode}, generation {Generation}", mode, owned);
            return running;
        }
        catch (Exception error)
        {
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Start failed: {Mode}, generation {Generation}", mode, owned);
            if (owned == generation)
            {
                Cleanup(owned);
                Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.FromException(error));
            }
            return false;
        }
    }

    private bool Tick(long owned)
    {
        if (disposed || !running || owned != generation || sequence == null) return true;
        sequence.Tick(Environment.TickCount64);
        if (!running || owned != generation) return true;
        runner.TotalItems = sequence.Results.Count; runner.CompletedItems = sequence.Confirmed;
        if (Status != sequence.Status)
            Plugin.Log.Information("[AutoRestoreFurniture] Progress: phase {Phase}, status {Status}, initial items {InitialItems}, confirmed {Confirmed}",
                sequence.Phase, sequence.Status, sequence.Results.Count, sequence.Confirmed);
        Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, sequence.StatusMessage);
        return sequence.Terminal;
    }

    internal void Stop() => Stop(global::XASlave.Localization.UiMessage.Key("S22808EAC3BCF65C6"));
    internal void Stop(string reason) => Stop(global::XASlave.Localization.UiMessage.Raw(reason));
    internal void Stop(global::XASlave.Localization.UiMessage reason)
    {
        if (!running) return;
        terminating = true;
        generation++; running = false;
        var cancelRunner = ownsRunner;
        ownsRunner = false;
        try
        {
            sequence?.Cancel(reason); Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, sequence?.StatusMessage ?? reason);
            if (cancelRunner) runner.Cancel();
        }
        finally { ReleaseAdapter(); terminating = false; }
    }

    private void Cleanup(long owned)
    {
        if (owned != generation || !running) return;
        terminating = true;
        generation++; running = false; ownsRunner = false;
        try
        {
            sequence?.Cancel(global::XASlave.Localization.UiMessage.Key("SEE3064093A1B7550"));
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, sequence?.StatusMessage ?? global::XASlave.Localization.UiMessage.Key("SEF5140206674324B"));
        }
        finally { ReleaseAdapter(); terminating = false; }
    }

    private void ReleaseAdapter()
    {
        var retiring = adapter;
        adapter = null;
        try { retiring?.Dispose(); }
        catch (Exception error)
        {
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("SE9EE3F9E8133EEB0" , StatusMessage));
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Adapter disposal failed.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(global::XASlave.Localization.UiMessage.Key("S70E883B773879A48")); generation++;
        ReleaseAdapter();
    }
}
