using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal enum FurnitureRestorePhase { AcquireContext, LoadSnapshot, AdmitItem, AwaitNativeDecision, AwaitTransfer, Reconcile, DeferredPass, Finished }
internal enum FurnitureRestoreAdmission { Ready, Waiting, Rejected, DependencyBlocked }
internal enum FurnitureRestoreDecision { None, Waiting, Accepted, Declined, Invalid }
internal sealed record FurnitureRestorePlanItem(FurnitureRestoreSource Source, FurnitureRestoreEffect Effect,
    InventoryShuttleItemState? ReturnedState, int ReturnedQuantity, bool NeedsNativeDecision);
internal sealed record FurnitureRestorePlan(FurnitureRestorePlanItem[] Items, FurnitureRestoreInventorySnapshot Source, FurnitureRestoreInventorySnapshot Destination);
internal sealed record FurnitureRestoreFrame(FurnitureRestoreSession Session, FurnitureRestoreInventorySnapshot? Source,
    FurnitureRestoreInventorySnapshot? Destination, FurnitureRestoreAdmission Admission, FurnitureRestoreDecision Decision,
    bool Complete, bool Ambiguous, bool CorrelatedRejection, long Operation, long FrameworkSequence, string Detail)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FurnitureRestoreFrame, global::XASlave.Localization.UiMessage> Messages = new();
    internal global::XASlave.Localization.UiMessage DetailMessage => Messages.TryGetValue(this, out var message) ? global::XASlave.Localization.UiMessage.Capture(message, Detail) : global::XASlave.Localization.UiMessage.Raw(Detail);
    internal FurnitureRestoreFrame WithDetailMessage(global::XASlave.Localization.UiMessage message)
    {
        Messages.AddOrUpdate(this, message);
        return this;
    }
}

internal interface IFurnitureRestoreAdapter : IDisposable
{
    string WaitReason { get; }
    global::XASlave.Localization.UiMessage WaitMessage => global::XASlave.Localization.UiMessage.Raw(WaitReason);
    FurnitureRestoreSession Acquire(FurnitureRestoreMode mode, long generation);
    // At most one owned normal storage-loading cycle; never during an outstanding move.
    FurnitureRestorePlan? LoadPlan(FurnitureRestoreSession session, FurnitureRestoreMode mode);
    FurnitureRestoreFrame Read(FurnitureRestoreSession session, FurnitureRestorePlanItem item, long operation);
    // Normal manager restore path only, after fresh source/session/native admission.
    // Entry is not an acknowledgment and may open an owned native decision surface.
    void Submit(FurnitureRestoreSession session, FurnitureRestoreMode mode, FurnitureRestorePlanItem item, long operation);
    // Ends only XA observations/controls. Never answer dialogs, reset native pending
    // fields, send compensation or retry a submitted operation on cleanup.
    void End();
}

internal sealed class FurnitureRestoreSequence
{
    private readonly IFurnitureRestoreAdapter adapter;
    private readonly FurnitureRestoreMode mode;
    private readonly long generation, started;
    private FurnitureRestoreSession session;
    private FurnitureRestorePlanItem[] items = Array.Empty<FurnitureRestorePlanItem>();
    private FurnitureRestoreItemResult[] results = Array.Empty<FurnitureRestoreItemResult>();
    private FurnitureRestoreInventorySnapshot? expectedSource, expectedDestination;
    private readonly Queue<int> remaining = new();
    private readonly Dictionary<int, int> deferredAt = new();
    private readonly HashSet<int> retried = new();
    private FurnitureRestoreReconciliation? reconciliation;
    private int current = -1, confirmed, passes;
    private long deadline, lastSubmission, operation, lastFrameSequence = -1;
    private bool attempted, submittedAny, ended, decisionAccepted, decisionWaitSeen;
    internal FurnitureRestorePhase Phase { get; private set; } = FurnitureRestorePhase.AcquireContext;
    internal bool Terminal => Phase == FurnitureRestorePhase.Finished;
    internal int Confirmed => confirmed;
    internal IReadOnlyList<FurnitureRestoreItemResult> Results => Array.AsReadOnly(results);
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("SD0D20147481BDAAA");
    internal string Status { get; private set; } = global::XASlave.Localization.UiLocale.Native("SD0D20147481BDAAA");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, Status);

    internal FurnitureRestoreSequence(IFurnitureRestoreAdapter adapter, FurnitureRestoreMode mode, long generation, long now)
    {
        FurnitureRestoreCapacity.ValidateMode(mode);
        this.adapter = adapter; this.mode = mode; this.generation = generation; started = now;
        deadline = now + 10000;
    }

    internal void Tick(long now)
    {
        if (Terminal) return;
        try
        {
            if (now - started >= 1800000) { Cancel(global::XASlave.Localization.UiMessage.Key("S222FD0EFD88E8E35")); return; }
            if (now >= deadline) { Cancel(global::XASlave.Localization.UiMessage.Key("S383F315913416A55")); return; }
            if (Phase == FurnitureRestorePhase.AcquireContext)
            {
                session = adapter.Acquire(mode, generation);
                if (Terminal) return;
                Phase = FurnitureRestorePhase.LoadSnapshot; Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S5E8CC38E4C96BC90")); return;
            }
            if (Phase == FurnitureRestorePhase.LoadSnapshot)
            {
                var plan = adapter.LoadPlan(session, mode);
                if (Terminal) return;
                if (plan == null)
                {
                    Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (string.IsNullOrEmpty(adapter.WaitReason) ? global::XASlave.Localization.UiMessage.Key("S5E8CC38E4C96BC90") : adapter.WaitMessage));
                    return;
                }
                items = plan.Items.ToArray(); expectedSource = plan.Source; expectedDestination = plan.Destination;
                var slots = new HashSet<(uint, ushort)>();
                foreach (var item in items)
                    if (!slots.Add((item.Source.Container, item.Source.Slot)) || !expectedSource.ContainsExact(item.Source))
                        throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF3627666DC870791"), originalMessageText => new InvalidOperationException(originalMessageText));
                results = items.Select(item => FurnitureRestoreItemResult.Create(item.Source, FurnitureRestoreOutcome.NotAttempted, global::XASlave.Localization.UiMessage.Key("S0FB06189DEF0F956"))).ToArray();
                for (var index = 0; index < items.Length; index++) remaining.Enqueue(index);
                Next(now); return;
            }
            if (Phase == FurnitureRestorePhase.DeferredPass) { Next(now); return; }
            if (current < 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S996199A8BDA8F9A0"), originalMessageText => new InvalidOperationException(originalMessageText));
            var itemNow = items[current];
            var frame = adapter.Read(session, itemNow, operation);
            if (Terminal) return;
            if (frame.Session != session || frame.Operation != operation || frame.Ambiguous)
            { Cancel(global::XASlave.Localization.UiMessage.Key("SE788642CCCC0C59F")); return; }
            // Adapter supplies the framework observation identity, not a polling count.
            if (frame.FrameworkSequence < lastFrameSequence)
            { Cancel(global::XASlave.Localization.UiMessage.Key("SC71AC868117F66EA")); return; }
            if (frame.FrameworkSequence == lastFrameSequence) return;
            lastFrameSequence = frame.FrameworkSequence;
            if (!frame.Complete || frame.Source == null || frame.Destination == null)
            {
                reconciliation?.Gap(frame.FrameworkSequence);
                Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (string.IsNullOrEmpty(frame.Detail) ? global::XASlave.Localization.UiMessage.Key("SC0DA85DE2962D2D3") : frame.DetailMessage));
                return;
            }
            var unchanged = frame.Source.SameSlots(expectedSource!) && frame.Destination.AddsExactly(expectedDestination!, null, 0);
            if (Phase == FurnitureRestorePhase.AdmitItem)
            {
                if (!unchanged || !frame.Source.ContainsExact(itemNow.Source)) { Cancel(global::XASlave.Localization.UiMessage.Key("S01EAAABF7F60821A")); return; }
                if (frame.Decision != FurnitureRestoreDecision.None) { Cancel(global::XASlave.Localization.UiMessage.Key("S0208A84B8F7CC55E")); return; }
                if (frame.Admission == FurnitureRestoreAdmission.Waiting) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, frame.DetailMessage); return; }
                if (frame.Admission is FurnitureRestoreAdmission.Rejected or FurnitureRestoreAdmission.DependencyBlocked)
                { Refuse(frame.Admission == FurnitureRestoreAdmission.DependencyBlocked, frame.DetailMessage, now); return; }
                if (frame.Admission != FurnitureRestoreAdmission.Ready)
                { Cancel(global::XASlave.Localization.UiMessage.Key("S3D6A8B04AD7CECC8")); return; }
                if (itemNow.Effect == FurnitureRestoreEffect.Unknown) { Refuse(false, global::XASlave.Localization.UiMessage.Key("S421CCBD01484BB64"), now); return; }
                if (submittedAny && now - lastSubmission < 500) return;
                reconciliation = new(frame.Source, frame.Destination, itemNow.Source, itemNow.Source.Quantity,
                    itemNow.ReturnedState, itemNow.ReturnedQuantity, itemNow.NeedsNativeDecision);
                attempted = true; submittedAny = true; lastSubmission = now; decisionAccepted = false;
                deadline = now + 10000; Phase = FurnitureRestorePhase.AwaitNativeDecision;
                adapter.Submit(session, mode, itemNow, operation);
                if (!Terminal) Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SC95819D7F1E53609"));
                return;
            }
            if (frame.Decision == FurnitureRestoreDecision.Invalid) { Cancel(global::XASlave.Localization.UiMessage.Key("S7C39546E18FC4192")); return; }
            if (frame.Decision == FurnitureRestoreDecision.Declined)
            {
                if (!unchanged || decisionAccepted) { Cancel(global::XASlave.Localization.UiMessage.Key("SC1864E6C898E75A1")); return; }
                Record(FurnitureRestoreOutcome.Declined, global::XASlave.Localization.UiMessage.Key("SF95AA2EA3248A56E")); Next(now); return;
            }
            if (frame.CorrelatedRejection)
            {
                if (!unchanged || decisionAccepted || frame.Decision != FurnitureRestoreDecision.None ||
                    frame.Admission is not (FurnitureRestoreAdmission.Rejected or FurnitureRestoreAdmission.DependencyBlocked))
                { Cancel(global::XASlave.Localization.UiMessage.Key("S8B98E7B40FE97604")); return; }
                Refuse(frame.Admission == FurnitureRestoreAdmission.DependencyBlocked, frame.DetailMessage, now); return;
            }
            if (frame.Decision == FurnitureRestoreDecision.Waiting)
            {
                if (!unchanged || decisionAccepted) { Cancel(global::XASlave.Localization.UiMessage.Key("SA2EA2AF0228C3A92")); return; }
                if (!decisionWaitSeen) { decisionWaitSeen = true; deadline = now + 120000; }
                Phase = FurnitureRestorePhase.AwaitNativeDecision; Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S1B41BA05ADFD55ED")); return;
            }
            if (frame.Decision == FurnitureRestoreDecision.Accepted && !decisionAccepted)
            { decisionAccepted = true; deadline = now + 10000; }
            Phase = FurnitureRestorePhase.AwaitTransfer;
            var delta = reconciliation!.Observe(frame.FrameworkSequence, frame.Source, frame.Destination, frame.Ambiguous, decisionAccepted);
            if (delta == FurnitureRestoreDelta.Conflict) { Cancel(global::XASlave.Localization.UiMessage.Key("SD7B6293EB99D918F")); return; }
            if (delta == FurnitureRestoreDelta.Confirmed)
            {
                expectedSource = frame.Source; expectedDestination = frame.Destination;
                Record(itemNow.Effect == FurnitureRestoreEffect.Ordinary ? FurnitureRestoreOutcome.Moved : FurnitureRestoreOutcome.Converted,
                    global::XASlave.Localization.UiMessage.Key("S02529034A238D913"));
                confirmed++; Next(now); return;
            }
            if (delta == FurnitureRestoreDelta.StableCandidate) Phase = FurnitureRestorePhase.Reconcile;
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SA3F36D3B7914BDD6"));
        }
        catch (Exception error)
        {
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Batch failed in phase {Phase}", Phase);
            Cancel(global::XASlave.Localization.UiMessage.FromException(error));
        }
    }

    private void Refuse(bool dependency, global::XASlave.Localization.UiMessage detail, long now)
    {
        Record(dependency ? FurnitureRestoreOutcome.DependencyBlocked : FurnitureRestoreOutcome.Rejected, detail);
        if (dependency && !retried.Contains(current)) deferredAt[current] = confirmed;
        Next(now);
    }

    private void Next(long now)
    {
        attempted = false; reconciliation = null; decisionAccepted = false; decisionWaitSeen = false; current = -1;
        if (remaining.Count == 0)
        {
            if (++passes <= items.Length + 1)
            {
                foreach (var pair in deferredAt.OrderBy(pair => pair.Key))
                {
                    if (retried.Contains(pair.Key) || confirmed <= pair.Value) continue;
                    retried.Add(pair.Key); remaining.Enqueue(pair.Key);
                }
            }
            if (remaining.Count == 0) { Finish(global::XASlave.Localization.UiMessage.Format("S946733EF141F9324" , confirmed, items.Length)); return; }
        }
        current = remaining.Dequeue(); operation++; deadline = now + 10000;
        Phase = FurnitureRestorePhase.AdmitItem; Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S97950EF88EAB1A15" , current + 1, items.Length));
    }

    private void Record(FurnitureRestoreOutcome outcome, global::XASlave.Localization.UiMessage detail)
    {
        if (current >= 0) results[current] = FurnitureRestoreItemResult.Create(items[current].Source, outcome, detail);
    }

    internal void Cancel(string reason) => Cancel(global::XASlave.Localization.UiMessage.Raw(reason));

    internal void Cancel(global::XASlave.Localization.UiMessage reason)
    {
        if (Terminal) return;
        Plugin.Log.Warning("[AutoRestoreFurniture] Batch stopped: {Reason}; mode {Mode}, phase {Phase}, last status {Status}, initial items {InitialItems}, confirmed {Confirmed}, operation {Operation}, submitted current item {Attempted}",
            reason.Original, mode, Phase, Status, items.Length, confirmed, operation, attempted);
        if (attempted) Record(FurnitureRestoreOutcome.Unconfirmed, global::XASlave.Localization.UiMessage.Format("S30A5046199924DB3" , reason));
        Finish(reason);
    }

    private void Finish(global::XASlave.Localization.UiMessage detail)
    {
        Phase = FurnitureRestorePhase.Finished; Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, detail);
        if (ended) return;
        ended = true;
        try { adapter.End(); }
        catch (Exception error)
        {
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S08A8E6CACD3277AA" , StatusMessage));
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Batch cleanup failed.");
        }
        Plugin.Log.Information("[AutoRestoreFurniture] {Status}", Status);
    }
}
