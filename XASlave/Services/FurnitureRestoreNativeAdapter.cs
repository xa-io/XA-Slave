using System;
using System.Linq;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

// Connects the verified bounded sequence to complete native snapshots and the
// passive observer. It never synthesizes agent callbacks or writes selections.
internal sealed class FurnitureRestoreNativeAdapter : IFurnitureRestoreAdapter
{
    private readonly FurnitureRestoreNativeBinding binding;
    private readonly FurnitureRestoreContext context;
    private readonly FurnitureRestoreWorld world;
    private readonly FurnitureRestoreObserver observer;
    private readonly FurnitureRestoreEffects effects;
    private FurnitureRestoreSession session;
    private FurnitureRestoreMode mode;
    private CaptureSet? admitted;
    private FurnitureRestorePlanItem? admittedItem;
    private long admittedOperation;
    private bool acquired, planned, ended, disposed;

    internal FurnitureRestoreNativeAdapter(FurnitureRestoreContext context, MessageLogService messages, IToastGui toasts, IDataManager data)
    {
        this.context = context;
        binding = FurnitureRestoreNativeBinding.CreatePrepared(); world = new(binding, context); effects = new(data);
        observer = new(binding, world, context, messages, toasts);
    }

    public FurnitureRestoreSession Acquire(FurnitureRestoreMode requestedMode, long generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (acquired || ended) throw new InvalidOperationException("Furniture adapter cannot acquire another batch.");
        FurnitureRestoreCapacity.ValidateMode(requestedMode);
        try
        {
            context.ClaimHost(); mode = requestedMode;
            session = world.Capture(generation); acquired = true;
            context.BindSession(() => !ended && !disposed && world.Capture(generation) == session);
            observer.Begin(session, mode);
            if (ended || disposed) throw new InvalidOperationException("Furniture startup was invalidated.");
            return session;
        }
        catch { End(); throw; }
    }

    public FurnitureRestorePlan? LoadPlan(FurnitureRestoreSession owner, FurnitureRestoreMode requestedMode)
    {
        Require(owner);
        if (planned || requestedMode != mode) throw new InvalidOperationException("Furniture plan can be captured only once for its requested mode.");
        var observation = observer.Read();
        if (observation.Ambiguous) throw new InvalidOperationException(observation.Detail);
        var capture = Capture();
        if (capture == null) return null;
        Require(owner);
        var items = capture.Source.Occupied.Select(item => effects.Plan(mode, session.Exterior, item)).ToArray();
        planned = true;
        return new(items, capture.Source.Inventory, capture.Destination.Inventory);
    }

    public FurnitureRestoreFrame Read(FurnitureRestoreSession owner, FurnitureRestorePlanItem item, long operation)
    {
        Require(owner);
        if (!planned) throw new InvalidOperationException("Furniture plan is not ready.");
        admitted = null; admittedItem = null;
        var capture = Capture();
        var observation = observer.Read();
        if (capture == null)
            return new(owner, null, null, observation.Admission, observation.Decision, false, observation.Ambiguous,
                observation.CorrelatedRejection, operation, context.FrameworkSequence, "Waiting for complete native pages and the housing list refresh.");
        if (observation.Ambiguous)
            return Frame(owner, capture, observation, operation);
        if (!observer.Prepare(item.Source, operation))
            return Frame(owner, capture, new(FurnitureRestoreDecision.None, FurnitureRestoreAdmission.Waiting, false, false,
                "Waiting for the previous native request/dialog to finish its own cleanup."), operation);
        observation = observer.Read();
        if (!observer.HasSubmitted(operation) && !observation.Ambiguous && observation.Decision == FurnitureRestoreDecision.None
            && observation.Admission == FurnitureRestoreAdmission.Ready)
        {
            if (item.Effect == FurnitureRestoreEffect.Unknown)
                observation = new(FurnitureRestoreDecision.None, FurnitureRestoreAdmission.Rejected, false, false,
                    "The source item's native removal effect/result mapping is unsupported.");
            else if (!DestinationFits(capture.Destination, item))
                observation = new(FurnitureRestoreDecision.None, FurnitureRestoreAdmission.Rejected, false, false,
                    "The complete destination snapshot has insufficient capacity, a unique-item conflict or incompatible stacks.");
        }
        Require(owner);
        if (!observer.HasSubmitted(operation) && !observation.Ambiguous && observation.Decision == FurnitureRestoreDecision.None
            && observation.Admission == FurnitureRestoreAdmission.Ready)
        { admitted = capture; admittedItem = item; admittedOperation = operation; }
        return Frame(owner, capture, observation, operation);
    }

    private FurnitureRestoreFrame Frame(FurnitureRestoreSession owner, CaptureSet capture, FurnitureNativeObservation observation, long operation)
        => new(owner, capture.Source.Inventory, capture.Destination.Inventory, observation.Admission, observation.Decision, true,
            observation.Ambiguous, observation.CorrelatedRejection, operation, context.FrameworkSequence, observation.Detail);

    public void Submit(FurnitureRestoreSession owner, FurnitureRestoreMode requestedMode, FurnitureRestorePlanItem item, long operation)
    {
        Require(owner);
        if (!planned || requestedMode != mode || item.Effect == FurnitureRestoreEffect.Unknown || observer.HasSubmitted(operation)
            || admitted == null || admittedItem != item || admittedOperation != operation)
            throw new InvalidOperationException("Furniture native submission is not admitted.");
        var capture = Capture() ?? throw new InvalidOperationException("Furniture pages became incomplete before submission.");
        if (!capture.Source.Inventory.ContainsExact(item.Source) || !capture.Source.Inventory.SameSlots(admitted.Source.Inventory)
            || !capture.Destination.Inventory.AddsExactly(admitted.Destination.Inventory, null, 0) || !DestinationFits(capture.Destination, item))
            throw new InvalidOperationException("Furniture source or destination fit changed immediately before submission.");
        Require(owner);
        admitted = null; admittedItem = null;
        observer.Submit(item.Source, operation);
    }

    private bool DestinationFits(FurniturePageSnapshot destination, FurnitureRestorePlanItem item)
    {
        if (mode != FurnitureRestoreMode.PlacedToStoreroom) return effects.BagsFit(destination, item);
        var nativeSize = binding.ReadSize(session.Exterior, session.Estate, true);
        if (nativeSize != session.NativeSize) throw new InvalidOperationException("The native placed/stored estate size sources disagree.");
        var nativeLimit = FurnitureRestoreCapacity.Limit(session.Exterior, nativeSize);
        // CopyPages uses the same native IsEmpty predicate as the proven collector;
        // these loaded page counts include every vector-eligible stored item. They
        // do not borrow the currently visible placed-list numerator or visual rows.
        var nativeOccupied = destination.NominalSlots - destination.EmptySlots;
        return FurnitureRestoreCapacity.StoreroomUsableEmpty(session.Exterior, nativeSize, destination.NominalSlots, destination.EmptySlots,
            destination.Occupied.Length, nativeOccupied, nativeLimit, true, true) >= item.Source.Quantity;
    }

    private sealed record CaptureSet(FurniturePageSnapshot Source, FurniturePageSnapshot Destination);

    private CaptureSet? Capture()
    {
        Require(session);
        var storedSource = mode == FurnitureRestoreMode.StoredToInventory;
        var source = world.Furniture(session, storedSource);
        var destination = mode == FurnitureRestoreMode.PlacedToStoreroom ? world.Furniture(session, true) : world.Bags(session);
        if (source == null || destination == null) return null;
        var shownStored = session.NativeListMode >= 2;
        var shown = shownStored == storedSource ? source : mode == FurnitureRestoreMode.PlacedToStoreroom && shownStored
            ? destination : world.Furniture(session, shownStored);
        if (shown == null || world.NativeListCount(session, shown) == null) return null;
        Require(session); return new(source, destination);
    }

    private void Require(FurnitureRestoreSession owner)
    {
        if (disposed || ended || !acquired || owner != session) throw new InvalidOperationException("Furniture adapter no longer owns this batch.");
        world.Require(owner);
        if (disposed || ended || !acquired) throw new InvalidOperationException("Furniture ownership was invalidated during native observation.");
    }

    public void End()
    {
        if (ended) return;
        ended = true; admitted = null; admittedItem = null;
        try { observer.End(); }
        finally { context.End(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { End(); }
        finally { observer.Dispose(); }
    }
}
