using System;

namespace XASlave.Services;

internal enum ExpertDeliveryPhase { Idle, Selecting, Delivering, Confirming, Refreshing }
internal enum ExpertDeliveryAction { Select, Deliver, Confirm, SwitchPage }

/// <summary>Transaction timing and observations; the service owns native reads and callbacks.</summary>
internal sealed class ExpertDeliveryProgress
{
    internal const int SelectionTimeoutMilliseconds = 2000;
    internal const int TransactionTimeoutMilliseconds = 10000;
    internal const int ActionIntervalMilliseconds = 150;
    internal const int ScanIntervalMilliseconds = 100;
    internal const int EmptyConfirmationMilliseconds = 600;
    private readonly long[] nextActions = new long[4];
    private readonly int[] callbackFailures = new int[4];
    private long transactionStarted;
    private long? emptySince;
    private ulong emptyFingerprint;
    private int emptyObservations;

    public ExpertDeliveryPhase Phase { get; private set; }
    public bool DeliverySent { get; private set; }
    public bool ConfirmationSent { get; private set; }
    public bool Rejected { get; private set; }

    public void BeginSelection(long now)
    {
        FinishTransaction();
        transactionStarted = now;
        Phase = ExpertDeliveryPhase.Selecting;
    }

    public void MarkDeliverySent()
    {
        DeliverySent = true;
        Phase = ExpertDeliveryPhase.Delivering;
    }

    public void MarkConfirmationSent(bool rejected)
    {
        ConfirmationSent = true;
        Rejected = rejected;
        Phase = ExpertDeliveryPhase.Confirming;
    }

    public void MarkRefreshing()
    {
        Phase = ExpertDeliveryPhase.Refreshing;
    }

    public bool SelectionTimedOut(long now)
        => Phase == ExpertDeliveryPhase.Selecting && now - transactionStarted >= SelectionTimeoutMilliseconds;

    public bool TransactionTimedOut(long now)
        => Phase != ExpertDeliveryPhase.Idle && now - transactionStarted >= TransactionTimeoutMilliseconds;

    public bool TryBeginAction(ExpertDeliveryAction action, long now)
    {
        var index = (int)action;
        if (now < nextActions[index])
            return false;
        nextActions[index] = now + ActionIntervalMilliseconds;
        return true;
    }

    public bool ObserveEmpty(ulong fingerprint, long now)
    {
        if (Phase != ExpertDeliveryPhase.Idle)
        {
            ResetEmptyObservation();
            return false;
        }
        if (emptySince == null || emptyFingerprint != fingerprint)
        {
            emptySince = now;
            emptyFingerprint = fingerprint;
            emptyObservations = 1;
            return false;
        }
        emptyObservations = Math.Min(emptyObservations + 1, 3);
        return emptyObservations >= 3 && now - emptySince.Value >= EmptyConfirmationMilliseconds;
    }

    public bool RecordCallbackFailure(ExpertDeliveryAction action)
    {
        var index = (int)action;
        callbackFailures[index] = Math.Min(callbackFailures[index] + 1, 3);
        return callbackFailures[index] >= 3;
    }

    public void ClearCallbackFailures(ExpertDeliveryAction action)
    {
        callbackFailures[(int)action] = 0;
    }

    public void ResetEmptyObservation()
    {
        emptySince = null;
        emptyObservations = 0;
    }

    public void FinishTransaction()
    {
        Phase = ExpertDeliveryPhase.Idle;
        DeliverySent = false;
        ConfirmationSent = false;
        Rejected = false;
        transactionStarted = 0;
        Array.Clear(callbackFailures);
        ResetEmptyObservation();
    }

    public void Reset()
    {
        FinishTransaction();
        Array.Clear(nextActions);
    }

    public static bool ItemWasRemoved(uint itemId, int quantity, uint currentItemId, int currentQuantity)
        => currentItemId != itemId || currentQuantity < quantity;
}
