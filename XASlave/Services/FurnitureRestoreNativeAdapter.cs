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
    public string WaitReason { get; private set; } = string.Empty;
    private global::XASlave.Localization.UiMessage? waitMessage;
    public global::XASlave.Localization.UiMessage WaitMessage => global::XASlave.Localization.UiMessage.Capture(waitMessage, WaitReason);

    internal FurnitureRestoreNativeAdapter(FurnitureRestoreContext context, MessageLogService messages, IToastGui toasts, IDataManager data)
    {
        this.context = context;
        binding = FurnitureRestoreNativeBinding.CreatePrepared(); world = new(binding, context); effects = new(data);
        observer = new(binding, world, context, messages, toasts);
    }

    public FurnitureRestoreSession Acquire(FurnitureRestoreMode requestedMode, long generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (acquired || ended) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S57006333CA5C7638"), originalMessageText => new InvalidOperationException(originalMessageText));
        FurnitureRestoreCapacity.ValidateMode(requestedMode);
        try
        {
            context.ClaimHost(); mode = requestedMode;
            session = world.Capture(generation); acquired = true;
            context.BindSession(() => !ended && !disposed && world.Capture(generation) == session);
            observer.Begin(session, mode);
            if (ended || disposed) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE110AA5164245F29"), originalMessageText => new InvalidOperationException(originalMessageText));
            return session;
        }
        catch { End(); throw; }
    }

    public FurnitureRestorePlan? LoadPlan(FurnitureRestoreSession owner, FurnitureRestoreMode requestedMode)
    {
        Require(owner);
        if (planned || requestedMode != mode) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S4C34932E59C84665"), originalMessageText => new InvalidOperationException(originalMessageText));
        var observation = observer.Read();
        if (observation.Ambiguous) throw global::XASlave.Localization.UiMessage.CreateException(observation.DetailMessage, originalMessageText => new InvalidOperationException(originalMessageText));
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
        if (!planned) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC16514DF3078C056"), originalMessageText => new InvalidOperationException(originalMessageText));
        admitted = null; admittedItem = null;
        var capture = Capture();
        var observation = observer.Read();
        if (capture == null)
            return new FurnitureRestoreFrame(owner, null, null, observation.Admission, observation.Decision, false, observation.Ambiguous, observation.CorrelatedRejection, operation, context.FrameworkSequence, WaitReason).WithDetailMessage(WaitMessage);
        if (observation.Ambiguous)
            return Frame(owner, capture, observation, operation);
        if (!observer.Prepare(item.Source, operation))
            return Frame(owner, capture, FurnitureNativeObservation.Create(FurnitureRestoreDecision.None, FurnitureRestoreAdmission.Waiting, false, false, global::XASlave.Localization.UiMessage.Key("S5794005DBD12E83E")), operation);
        observation = observer.Read();
        if (!observer.HasSubmitted(operation) && !observation.Ambiguous && observation.Decision == FurnitureRestoreDecision.None
            && observation.Admission == FurnitureRestoreAdmission.Ready)
        {
            if (item.Effect == FurnitureRestoreEffect.Unknown)
                observation = FurnitureNativeObservation.Create(FurnitureRestoreDecision.None, FurnitureRestoreAdmission.Rejected, false, false, global::XASlave.Localization.UiMessage.Key("SF148C5C0D518A529"));
            else if (!DestinationFits(capture.Destination, item))
                observation = FurnitureNativeObservation.Create(FurnitureRestoreDecision.None, FurnitureRestoreAdmission.Rejected, false, false, global::XASlave.Localization.UiMessage.Key("SC71512D59ABB601A"));
        }
        Require(owner);
        if (!observer.HasSubmitted(operation) && !observation.Ambiguous && observation.Decision == FurnitureRestoreDecision.None
            && observation.Admission == FurnitureRestoreAdmission.Ready)
        { admitted = capture; admittedItem = item; admittedOperation = operation; }
        return Frame(owner, capture, observation, operation);
    }

    private FurnitureRestoreFrame Frame(FurnitureRestoreSession owner, CaptureSet capture, FurnitureNativeObservation observation, long operation)
        => new FurnitureRestoreFrame(owner, capture.Source.Inventory, capture.Destination.Inventory, observation.Admission, observation.Decision, true, observation.Ambiguous, observation.CorrelatedRejection, operation, context.FrameworkSequence, observation.Detail).WithDetailMessage(observation.DetailMessage);

    public void Submit(FurnitureRestoreSession owner, FurnitureRestoreMode requestedMode, FurnitureRestorePlanItem item, long operation)
    {
        Require(owner);
        if (!planned || requestedMode != mode || item.Effect == FurnitureRestoreEffect.Unknown || observer.HasSubmitted(operation)
            || admitted == null || admittedItem != item || admittedOperation != operation)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC37AF95019F2729E"), originalMessageText => new InvalidOperationException(originalMessageText));
        var capture = Capture() ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S521FF2AFC18EFB70"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (!capture.Source.Inventory.ContainsExact(item.Source) || !capture.Source.Inventory.SameSlots(admitted.Source.Inventory)
            || !capture.Destination.Inventory.AddsExactly(admitted.Destination.Inventory, null, 0) || !DestinationFits(capture.Destination, item))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5004A6EDF2C582F9"), originalMessageText => new InvalidOperationException(originalMessageText));
        Require(owner);
        admitted = null; admittedItem = null;
        observer.Submit(item.Source, operation);
    }

    private bool DestinationFits(FurniturePageSnapshot destination, FurnitureRestorePlanItem item)
    {
        if (mode != FurnitureRestoreMode.PlacedToStoreroom) return effects.BagsFit(destination, item);
        var nativeSize = binding.ReadSize(session.Exterior, session.Estate, true);
        if (nativeSize != session.NativeSize) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S05C2BA0F31DDCBEE"), originalMessageText => new InvalidOperationException(originalMessageText));
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
        WaitReason = global::XASlave.Localization.UiMessage.Store(ref waitMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        Require(session);
        var storedSource = mode == FurnitureRestoreMode.StoredToInventory;
        var source = world.Furniture(session, storedSource);
        if (source == null) return Wait(global::XASlave.Localization.UiMessage.Format("SCB79BC036143CCB2" , world.SnapshotWaitMessage));
        var destination = mode == FurnitureRestoreMode.PlacedToStoreroom ? world.Furniture(session, true) : world.Bags(session);
        if (destination == null) return Wait(global::XASlave.Localization.UiMessage.Format("S2C3794C09DD98380" , world.SnapshotWaitMessage));
        var shownStored = session.NativeListMode >= 2;
        var shown = shownStored == storedSource ? source : mode == FurnitureRestoreMode.PlacedToStoreroom && shownStored
            ? destination : world.Furniture(session, shownStored);
        if (shown == null) return Wait(global::XASlave.Localization.UiMessage.Format("S2BC94BFBFC699551" , world.SnapshotWaitMessage));
        if (world.NativeListCount(session, shown) == null) return Wait(global::XASlave.Localization.UiMessage.Format("SFF55F5E103FDAE24" , world.SnapshotWaitMessage));
        Require(session); return new(source, destination);
    }

    private CaptureSet? Wait(global::XASlave.Localization.UiMessage reason)
    {
        WaitReason = global::XASlave.Localization.UiMessage.Store(ref waitMessage, reason);
        return null;
    }

    private void Require(FurnitureRestoreSession owner)
    {
        if (disposed || ended || !acquired || owner != session) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE00911BD6568F802"), originalMessageText => new InvalidOperationException(originalMessageText));
        world.Require(owner);
        if (disposed || ended || !acquired) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S289E4205DBC9B679"), originalMessageText => new InvalidOperationException(originalMessageText));
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
