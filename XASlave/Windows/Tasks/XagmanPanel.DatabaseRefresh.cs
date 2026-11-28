using System;
using System.Collections.Generic;
using System.Threading;
using XASlave.Services;
using XASlave.Localization;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool xagmanRelogDatabasePullPending;
    private long xagmanRelogDatabasePullGeneration;

    private (ulong ContentId, Action? OnApplied)? xagmanCompletionPull;
    private bool xagmanCommittedRowsPending;
    private Action? xagmanCommittedRowsApplied;

    private bool XagmanDatabasePullBusy => Volatile.Read(ref xagmanManualXaDatabasePullRunning) != 0
        || Volatile.Read(ref xaDbPullRunning) != 0;

    private void PullXagmanDatabaseInfo()
    {
        if (Plugin.PlayerState.IsLoaded)
            BeginXagmanXaDatabaseRefreshAndPull();
        else
            PullXaDatabaseInfo(ClearXagmanMatchingSelectionCaches);
    }

    private void PullXagmanCommittedRows(Action? onApplied)
    {
        PullXaDatabaseInfo(() =>
        {
            ClearXagmanMatchingSelectionCaches();
            onApplied?.Invoke();
        });
    }

    private void QueueXagmanCompletionDatabasePull(Action? onApplied = null)
    {
        if (Plugin.PlayerState.IsLoaded && Plugin.PlayerState.ContentId == 0)
        {
            SetXagmanXaDatabaseRefreshFailure(UiMessage.Key("SB95322A82D33CEC6"));
            return;
        }
        xagmanCompletionPull = (Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0, onApplied);
        ProcessXagmanCompletionDatabasePull();
    }

    private void ProcessXagmanCompletionDatabasePull()
    {
        if (isDisposed || XagmanDatabasePullBusy) return;
        if (xagmanCommittedRowsPending)
        {
            xagmanCommittedRowsPending = false;
            var onApplied = xagmanCommittedRowsApplied;
            xagmanCommittedRowsApplied = null;
            PullXagmanCommittedRows(onApplied);
            return;
        }
        if (xagmanCompletionPull is not { } request) return;
        xagmanCompletionPull = null;
        var expected = request.ContentId;
        if (expected != 0 && (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != expected))
        {
            SetXagmanXaDatabaseRefreshFailure(UiMessage.Key("S343F4C1B322FBE69"));
            return;
        }
        if (expected == 0)
            PullXagmanCommittedRows(request.OnApplied);
        else
            BeginXagmanXaDatabaseRefreshAndPull(request.OnApplied);
    }

    private void AddXagmanCompletionDatabasePullStep(List<TaskStep> steps, Func<bool>? skip = null)
    {
        var applied = false;
        steps.Add(new TaskStep
        {
            NameMessage = UiMessage.Key("S658431DD798692FF"),
            ShouldSkip = skip,
            OnEnter = () => QueueXagmanCompletionDatabasePull(() => applied = true),
            IsComplete = () =>
            {
                if (applied) return true;
                if (xagmanCompletionPull != null || xagmanCommittedRowsPending || XagmanDatabasePullBusy) return false;
                MarkXagmanCompletionWarning();
                return true;
            },
            TimeoutSec = 60f,
            OnTimeout = () =>
            {
                MarkXagmanCompletionWarning();
                plugin.TaskRunner.AddLog("Xagman: database pull did not finish before completion cleanup.");
            },
        });
    }

    private void QueueXagmanRelogDatabasePull()
    {
        if (!isDisposed && xagmanRunning)
            xagmanRelogDatabasePullPending = true;
    }

    private void ResetXagmanRelogDatabasePull()
    {
        xagmanRelogDatabasePullPending = false;
        xagmanRelogDatabasePullGeneration++;
    }

    private void ProcessXagmanRelogDatabasePull()
    {
        if (isDisposed || !xagmanRunning || !xagmanRelogDatabasePullPending
            || Volatile.Read(ref xaDbPullRunning) != 0)
            return;

        // Wait for an earlier manual read to finish, then read committed rows afresh.
        // This never asks XA Database to collect/save an outgoing or missing character.
        xagmanRelogDatabasePullPending = false;
        var generation = xagmanRelogDatabasePullGeneration;
        bool IsCurrent() => !isDisposed && xagmanRunning && generation == xagmanRelogDatabasePullGeneration;
        PullXaDatabaseInfo(
            () => { if (IsCurrent()) ClearXagmanMatchingSelectionCaches(); },
            IsCurrent);
    }
}
