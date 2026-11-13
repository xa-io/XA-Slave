namespace XASlave.Services;

internal readonly record struct BusyStatusObservation(ulong ContentId, bool Readable, bool Busy, bool Safe);
internal enum BusyStatusCommand { None, On, Off }
internal enum BusyStatusState { Disabled, Waiting, Active, Preserved, ManualChange, Confirming, Failed, Retry, Suppressed }

/// <summary>Pure per-character ownership and finite command observation; no game or clock access.</summary>
internal sealed class BusyStatusCoordinator
{
    internal const long ConfirmationMilliseconds = 10_000;
    internal const long RetryMilliseconds = 2_000;
    internal const int MaximumAttempts = 3;
    internal ulong ContentId;
    internal bool Owned, Pending, PendingTarget, Failed, ManualChange;
    internal BusyStatusState State = BusyStatusState.Disabled;
    private bool externalBusy, submitted;
    private int attempts;
    private long deadline, notBefore;

    internal void ResetForCharacter(ulong contentId)
    {
        ContentId = contentId;
        Owned = false; Pending = false; PendingTarget = false;
        externalBusy = false; submitted = false; attempts = 0; deadline = 0; notBefore = 0;
        State = BusyStatusState.Waiting;
    }

    internal void ResetIntent()
    {
        Failed = false; ManualChange = false;
        if (!Owned && !Pending) externalBusy = false;
        if (!Pending) { attempts = 0; notBefore = 0; }
    }

    internal void BeginTemporaryIntent(BusyStatusObservation observed)
    {
        if (Pending || Failed || observed.ContentId == 0 || !observed.Readable || !observed.Safe) return;
        if (ContentId != observed.ContentId) ResetForCharacter(observed.ContentId);
        // Reconcile the idle interval before applying a new temporary demand. Preserve Busy
        // we still own, but never claim an existing manual Busy or inherit an old manual-off latch.
        if (!observed.Busy) Owned = false;
        externalBusy = observed.Busy && !Owned;
        ManualChange = false;
        attempts = 0; notBefore = 0;
        State = BusyStatusState.Waiting;
    }

    internal void CancelUnsent()
    {
        if (!Pending) return;
        Pending = false;
        attempts--;
        State = BusyStatusState.Waiting;
    }

    internal void Submitted(bool accepted)
    {
        if (Pending) submitted = accepted;
    }

    internal BusyStatusCommand Advance(BusyStatusObservation observed, bool demand, bool suppressed, long now)
    {
        if (observed.ContentId == 0 || observed.ContentId != ContentId)
            ResetForCharacter(observed.ContentId);
        if (!observed.Readable || observed.ContentId == 0)
        {
            if (Pending && now >= deadline) { Pending = false; Failed = true; }
            State = Failed ? BusyStatusState.Failed : BusyStatusState.Waiting;
            return BusyStatusCommand.None;
        }
        if (Failed) { State = BusyStatusState.Failed; return BusyStatusCommand.None; }
        if (Pending)
        {
            if (observed.Busy == PendingTarget)
            {
                // A rejected/uncertain submission cannot establish ownership of a later manual Busy.
                Owned = PendingTarget && submitted;
                externalBusy = PendingTarget && !submitted;
                if (!PendingTarget && !submitted) ManualChange = true;
                Pending = false;
                attempts = 0;
            }
            else if (now >= deadline)
            {
                Pending = false;
                if (submitted || attempts >= MaximumAttempts)
                {
                    Failed = true; State = BusyStatusState.Failed;
                    return BusyStatusCommand.None;
                }
                notBefore = now + RetryMilliseconds;
                State = BusyStatusState.Retry;
                return BusyStatusCommand.None;
            }
            else { State = BusyStatusState.Confirming; return BusyStatusCommand.None; }
        }
        if ((Owned || externalBusy) && !observed.Busy)
        {
            Owned = false; externalBusy = false; ManualChange = true;
        }
        if (observed.Busy && !Owned) externalBusy = true;
        if (suppressed)
        {
            if (observed.Busy && Owned)
                return Begin(false, observed.Safe, now);
            State = observed.Busy ? BusyStatusState.Preserved : BusyStatusState.Suppressed;
            return BusyStatusCommand.None;
        }
        if (!demand)
        {
            if (observed.Busy && Owned)
                return Begin(false, observed.Safe, now);
            State = BusyStatusState.Disabled;
            return BusyStatusCommand.None;
        }
        if (observed.Busy)
        {
            State = Owned ? BusyStatusState.Active : BusyStatusState.Preserved;
            return BusyStatusCommand.None;
        }
        if (ManualChange) { State = BusyStatusState.ManualChange; return BusyStatusCommand.None; }
        return Begin(true, observed.Safe, now);
    }

    private BusyStatusCommand Begin(bool target, bool safe, long now)
    {
        if (!safe) { State = BusyStatusState.Waiting; return BusyStatusCommand.None; }
        if (now < notBefore) { State = BusyStatusState.Retry; return BusyStatusCommand.None; }
        Pending = true; PendingTarget = target; submitted = true;
        attempts++; deadline = now + ConfirmationMilliseconds;
        State = BusyStatusState.Confirming;
        return target ? BusyStatusCommand.On : BusyStatusCommand.Off;
    }
}
