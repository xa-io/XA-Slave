using System;
using System.Collections.Generic;

namespace XASlave.Services;

// Construct only after the adapter has proved complete loaded page coverage. Copies
// contain exposed inventory state, never InventoryItem pointers retained over a wait.
internal sealed class FurnitureRestoreInventorySnapshot
{
    private readonly Dictionary<(uint Container, ushort Slot), FurnitureRestoreSource> slots = new();
    private readonly Dictionary<InventoryShuttleItemState, long> quantities = new();
    internal FurnitureRestoreInventorySnapshot(IEnumerable<FurnitureRestoreSource> occupied)
    {
        foreach (var source in occupied)
        {
            if (source.Quantity <= 0 || source.State.FullId == 0 || source.State.BaseId == 0 || !slots.TryAdd((source.Container, source.Slot), source))
                throw new ArgumentException("Invalid or duplicate furniture inventory descriptor.");
            quantities.TryGetValue(source.State, out var existing);
            quantities[source.State] = checked(existing + source.Quantity);
        }
    }

    internal bool ContainsExact(FurnitureRestoreSource selected)
        => slots.TryGetValue((selected.Container, selected.Slot), out var current) && current == selected;

    internal bool SameSlots(FurnitureRestoreInventorySnapshot other)
    {
        if (slots.Count != other.slots.Count) return false;
        foreach (var pair in slots)
            if (!other.slots.TryGetValue(pair.Key, out var item) || item != pair.Value) return false;
        return true;
    }

    internal bool RemovesExactly(FurnitureRestoreInventorySnapshot before, FurnitureRestoreSource selected, int amount)
    {
        if (amount <= 0 || amount > selected.Quantity || !before.ContainsExact(selected)) return false;
        var key = (selected.Container, selected.Slot);
        var remaining = selected.Quantity - amount;
        if (remaining == 0)
        {
            if (slots.ContainsKey(key) || slots.Count != before.slots.Count - 1) return false;
        }
        else if (!slots.TryGetValue(key, out var after) || after != (selected with { Quantity = remaining }) || slots.Count != before.slots.Count) return false;
        foreach (var pair in before.slots)
        {
            if (pair.Key == key) continue;
            if (!slots.TryGetValue(pair.Key, out var item) || item != pair.Value) return false;
        }
        return true;
    }

    internal bool AddsExactly(FurnitureRestoreInventorySnapshot before, InventoryShuttleItemState? returnedState, int amount)
    {
        if (amount < 0 || (amount == 0) != !returnedState.HasValue) return false;
        foreach (var pair in before.quantities)
        {
            quantities.TryGetValue(pair.Key, out var current);
            var expected = checked(pair.Value + (returnedState.HasValue && pair.Key == returnedState.Value ? amount : 0));
            if (current != expected) return false;
        }
        foreach (var pair in quantities)
        {
            before.quantities.TryGetValue(pair.Key, out var previous);
            var expected = checked(previous + (returnedState.HasValue && pair.Key == returnedState.Value ? amount : 0));
            if (pair.Value != expected) return false;
        }
        // A completely missing returned item must not pass two empty dictionary loops.
        if (returnedState.HasValue)
        {
            before.quantities.TryGetValue(returnedState.Value, out var previous);
            if (!quantities.TryGetValue(returnedState.Value, out var current) || current != checked(previous + amount)) return false;
        }
        return true;
    }
}

internal enum FurnitureRestoreDelta { Waiting, SourceOnly, DestinationOnly, StableCandidate, Confirmed, Conflict }

internal sealed class FurnitureRestoreReconciliation
{
    private readonly FurnitureRestoreInventorySnapshot beforeSource, beforeDestination;
    private readonly FurnitureRestoreSource selected;
    private readonly InventoryShuttleItemState? returnedState;
    private readonly int removedQuantity, returnedQuantity;
    private readonly bool needsOwnedDecision;
    private long lastSequence = -1;
    private int stableSnapshots;
    private bool sawExact;
    internal FurnitureRestoreDelta State { get; private set; } = FurnitureRestoreDelta.Waiting;

    internal FurnitureRestoreReconciliation(FurnitureRestoreInventorySnapshot beforeSource, FurnitureRestoreInventorySnapshot beforeDestination,
        FurnitureRestoreSource selected, int removedQuantity, InventoryShuttleItemState? returnedState, int returnedQuantity, bool needsOwnedDecision)
    {
        if (!beforeSource.ContainsExact(selected) || removedQuantity <= 0 || removedQuantity > selected.Quantity || returnedQuantity < 0
            || (returnedQuantity == 0) != !returnedState.HasValue || (returnedQuantity == 0 && !needsOwnedDecision)
            || (returnedState.HasValue && (returnedState.Value.FullId == 0 || returnedState.Value.BaseId == 0)))
            throw new ArgumentException("The furniture result expectation is incomplete or does not match its owned source.");
        this.beforeSource = beforeSource; this.beforeDestination = beforeDestination; this.selected = selected;
        this.removedQuantity = removedQuantity; this.returnedState = returnedState; this.returnedQuantity = returnedQuantity;
        this.needsOwnedDecision = needsOwnedDecision;
    }

    internal void Gap(long sequence)
    {
        if (sequence <= lastSequence || State is FurnitureRestoreDelta.Confirmed or FurnitureRestoreDelta.Conflict) return;
        lastSequence = sequence; stableSnapshots = 0;
    }

    internal FurnitureRestoreDelta Observe(long sequence, FurnitureRestoreInventorySnapshot source, FurnitureRestoreInventorySnapshot destination,
        bool ambiguous, bool ownedDecisionAccepted)
    {
        if (sequence <= lastSequence || State is FurnitureRestoreDelta.Confirmed or FurnitureRestoreDelta.Conflict) return State;
        if (lastSequence >= 0 && sequence != lastSequence + 1) stableSnapshots = 0;
        lastSequence = sequence;
        if (ambiguous) return State = FurnitureRestoreDelta.Conflict;
        var sourceSame = source.SameSlots(beforeSource);
        var destinationSame = destination.AddsExactly(beforeDestination, null, 0);
        var sourceExact = source.RemovesExactly(beforeSource, selected, removedQuantity);
        var destinationExact = destination.AddsExactly(beforeDestination, returnedState, returnedQuantity);
        if (sourceExact && destinationExact)
        {
            if (needsOwnedDecision && !ownedDecisionAccepted) return State = FurnitureRestoreDelta.Conflict;
            sawExact = true;
            return State = ++stableSnapshots >= 2 ? FurnitureRestoreDelta.Confirmed : FurnitureRestoreDelta.StableCandidate;
        }
        stableSnapshots = 0;
        if (sawExact) return State = FurnitureRestoreDelta.Conflict;
        if (sourceSame && destinationSame) return State = FurnitureRestoreDelta.Waiting;
        if (sourceExact && destinationSame) return State = FurnitureRestoreDelta.SourceOnly;
        if (sourceSame && destinationExact) return State = FurnitureRestoreDelta.DestinationOnly;
        return State = FurnitureRestoreDelta.Conflict;
    }
}
