using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

internal enum InventoryShuttleOutcome { Requested, ClientUpdated, Partial, Failed, Unconfirmed }
internal readonly record struct InventoryShuttleTarget(InventoryShuttleSource Before, int Capacity);

internal interface IInventoryShuttleAdapter
{
    void Validate();
    InventoryShuttleSource Read(InventoryType inventory, ushort slot);
    InventoryShuttleTarget? Find(InventoryShuttleSource source);
    int Submit(InventoryShuttleSource source, InventoryShuttleTarget target);
    void CloseOwnedMenu();
}

// Each accepted call consumes one distinct chunk of the original quota. Nothing here
// retries a native call, sweeps another stack, or labels local mutations server-confirmed.
internal sealed class InventoryShuttleSequence
{
    private readonly IInventoryShuttleAdapter adapter;
    private readonly InventoryShuttleSource original;
    private readonly long started;
    private readonly Dictionary<(InventoryType, ushort), InventoryShuttleSource> expectedDestinations = [];
    private InventoryShuttleSource expectedSource;
    private InventoryShuttleTarget? pending;
    private int expectedDelta;
    private long lastSubmission;
    private bool submitted, stopped, menuClosed;
    internal int Moved { get; private set; }
    internal int Remaining => original.Quantity - Moved;
    internal int? NativeCode { get; private set; }
    internal bool Terminal { get; private set; }
    internal InventoryShuttleOutcome Outcome { get; private set; } = InventoryShuttleOutcome.Requested;
    internal string Detail { get; private set; } = "Waiting for a verified inventory chunk.";

    internal InventoryShuttleSequence(IInventoryShuttleAdapter adapter, InventoryShuttleSource original, long now)
    {
        if (original.Quantity <= 0) throw new ArgumentException("The selected stack is empty.");
        this.adapter = adapter; this.original = original; expectedSource = original; started = now;
    }

    internal void Cancel(string reason)
    {
        if (stopped) return;
        stopped = true; Terminal = true; pending = null;
        Outcome = submitted ? InventoryShuttleOutcome.Unconfirmed : InventoryShuttleOutcome.Failed;
        Detail = reason;
    }

    internal void Tick(long now)
    {
        if (stopped) return;
        try
        {
            adapter.Validate();
            // After a terminal local result, continue detecting corrections until the
            // original bounded observation window closes or a new user intent replaces us.
            if (Terminal)
            {
                ValidateObserved();
                if (now - started >= 10000) stopped = true;
                return;
            }
            if (now - started >= 10000) { Cancel("Transfer deadline reached; no chunk was replayed."); return; }
            if (pending is { } target)
            {
                var source = adapter.Read(original.Inventory, original.Slot);
                var destination = adapter.Read(target.Before.Inventory, target.Before.Slot);
                var sourceQuantity = expectedSource.Quantity - expectedDelta;
                if (source.Quantity != sourceQuantity || (sourceQuantity == 0 ? source.State.FullId != 0 : source.State != original.State)
                    || destination.Quantity != target.Before.Quantity + expectedDelta || destination.State != original.State)
                { Cancel("Native processing did not produce the exact expected local inventory delta."); return; }
                expectedSource = source;
                expectedDestinations[(destination.Inventory, destination.Slot)] = destination;
                Moved += expectedDelta; pending = null;
                ValidateObserved();
                if (Remaining == 0)
                {
                    Terminal = true; Outcome = InventoryShuttleOutcome.ClientUpdated;
                    Detail = "The full selected quantity was observed locally; server acceptance is not confirmed.";
                }
                else Detail = "Chunk observed locally; waiting before the next distinct chunk.";
                return;
            }
            ValidateObserved();
            if (submitted && now - lastSubmission < 250) return;
            var next = adapter.Find(expectedSource);
            if (next == null)
            {
                Terminal = true; Outcome = Moved > 0 ? InventoryShuttleOutcome.Partial : InventoryShuttleOutcome.Failed;
                Detail = "No further admitted capacity is available in the selected destination family.";
                return;
            }
            var candidate = next.Value;
            if (candidate.Before.Inventory == original.Inventory && candidate.Before.Slot == original.Slot)
                throw new InvalidOperationException("Source and destination cannot be the same slot.");
            expectedDelta = Math.Min(expectedSource.Quantity, candidate.Capacity - candidate.Before.Quantity);
            if (expectedDelta <= 0 || expectedDelta > Remaining) throw new InvalidOperationException("Invalid transfer quota or capacity.");
            adapter.Validate();
            ValidateObserved();
            if (adapter.Read(candidate.Before.Inventory, candidate.Before.Slot) != candidate.Before)
                throw new InvalidOperationException("The selected destination changed before submission.");
            if (stopped) return;
            pending = candidate; submitted = true; lastSubmission = now;
            NativeCode = adapter.Submit(expectedSource, candidate);
            if (stopped) return;
            if (NativeCode != 0)
            {
                pending = null; stopped = true; Terminal = true; Outcome = InventoryShuttleOutcome.Failed;
                Detail = $"Native result {NativeCode}; no retry was made.";
                return;
            }
            adapter.Validate();
            if (stopped) return;
            if (!menuClosed) { menuClosed = true; adapter.CloseOwnedMenu(); }
            Detail = "Native processing returned zero; awaiting a separate local inventory observation.";
        }
        catch (Exception error) { Cancel(error.Message); }
    }

    private void ValidateObserved()
    {
        if (adapter.Read(original.Inventory, original.Slot) != expectedSource)
            throw new InvalidOperationException("The source changed or was corrected outside the expected transfer.");
        foreach (var entry in expectedDestinations.Values)
            if (adapter.Read(entry.Inventory, entry.Slot) != entry)
                throw new InvalidOperationException("A previously observed destination changed or was corrected.");
    }
}
