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
    internal string Status { get; private set; } = "Choose a furniture batch mode from the current HousingGoods session.";
    internal bool IsRunning => running;
    internal IReadOnlyList<FurnitureRestoreItemResult> Results => sequence?.Results ?? Array.Empty<FurnitureRestoreItemResult>();

    internal AutoRestoreFurnitureTask(TaskRunner runner, Func<IFurnitureRestoreAdapter> createAdapter)
    { this.runner = runner; this.createAdapter = createAdapter; }

    internal bool Start(FurnitureRestoreMode mode)
    {
        Plugin.Log.Information("[AutoRestoreFurniture] Start requested: {Mode}", mode);
        if (disposed || running || terminating || runner.IsRunning)
        {
            Status = "Another task is running or furniture restoration is unavailable.";
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
            var accepted = runner.Start("Auto Restore Furniture", [new TaskStep
            {
                Name = "Restore the fixed initial furniture set",
                IsComplete = () => Tick(owned), TimeoutSec = 1801, MaxRetries = 0,
                OnTimeout = () => Stop("Furniture batch timeout."),
            }], suppressCompletionReport: true, totalItems: 0, suppressLogoutCancel: false,
                onTerminal: () => Cleanup(owned), haltOnStepError: true);
            if (!accepted)
            {
                Cleanup(owned); Status = "The shared task runner rejected furniture restoration.";
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
                Status = error.Message;
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
        Status = sequence.Status;
        return sequence.Terminal;
    }

    internal void Stop(string reason = "Furniture restoration stopped by the operator.")
    {
        if (!running) return;
        terminating = true;
        generation++; running = false;
        var cancelRunner = ownsRunner;
        ownsRunner = false;
        try
        {
            sequence?.Cancel(reason); Status = sequence?.Status ?? reason;
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
            sequence?.Cancel("Furniture task ended; no additional native requests will be made.");
            Status = sequence?.Status ?? "Furniture task ended.";
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
            Status += " XA observer disposal reported a failure.";
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Adapter disposal failed.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop("Furniture restoration disposed."); generation++;
        ReleaseAdapter();
    }
}
