using System;

namespace XASlave.Services;

internal enum NpcSellPendingAction { Wait, Confirmed, Retry, Stop }

// One rejection per native invocation, never one per poll of a still-visible toast.
internal sealed class NpcSellRetryState
{
    internal const string InventoryRetrievalMessage = "Retrieving items from inventory. Please wait.";
    internal const int MaximumConsecutiveFailures = 3;
    private bool rejected;
    private DateTime retryAfter;
    internal int ConsecutiveFailures { get; private set; }

    internal static bool IsInventoryRetrievalError(string text) =>
        string.Equals(text.Trim(), InventoryRetrievalMessage, StringComparison.Ordinal);

    internal void BeginAttempt() => rejected = false;

    internal bool RecordRetrievalError(DateTime now)
    {
        if (rejected) return false;
        rejected = true;
        ConsecutiveFailures++;
        retryAfter = now.AddSeconds(2);
        return true;
    }

    internal NpcSellPendingAction Evaluate(DateTime now, bool inventoryChanged, bool gilIncreased, bool unchanged, bool operationPending)
    {
        // A delayed acknowledgement takes precedence, including on the third rejection.
        if (inventoryChanged && gilIncreased) return NpcSellPendingAction.Confirmed;
        if (!rejected) return NpcSellPendingAction.Wait;
        // Even the third error must not close the shop midway through a split acknowledgement.
        if (!unchanged || operationPending)
        {
            retryAfter = now.AddSeconds(2);
            return NpcSellPendingAction.Wait;
        }
        if (now < retryAfter) return NpcSellPendingAction.Wait;
        if (ConsecutiveFailures >= MaximumConsecutiveFailures) return NpcSellPendingAction.Stop;
        return NpcSellPendingAction.Retry;
    }

    internal void ConfirmProgress()
    {
        ConsecutiveFailures = 0;
        rejected = false;
    }
}
