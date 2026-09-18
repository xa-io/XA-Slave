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
    bool Complete, bool Ambiguous, bool CorrelatedRejection, long Operation, long FrameworkSequence, string Detail);

internal interface IFurnitureRestoreAdapter : IDisposable
{
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
    internal string Status { get; private set; } = "Acquiring the current housing edit session.";

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
            if (now - started >= 1800000) { Cancel("The 30-minute batch deadline was reached."); return; }
            if (now >= deadline) { Cancel("The current loading, admission, decision or response deadline was reached."); return; }
            if (Phase == FurnitureRestorePhase.AcquireContext)
            {
                session = adapter.Acquire(mode, generation);
                if (Terminal) return;
                Phase = FurnitureRestorePhase.LoadSnapshot; Status = "Waiting for complete furniture and destination pages."; return;
            }
            if (Phase == FurnitureRestorePhase.LoadSnapshot)
            {
                var plan = adapter.LoadPlan(session, mode);
                if (Terminal || plan == null) return;
                items = plan.Items.ToArray(); expectedSource = plan.Source; expectedDestination = plan.Destination;
                var slots = new HashSet<(uint, ushort)>();
                foreach (var item in items)
                    if (!slots.Add((item.Source.Container, item.Source.Slot)) || !expectedSource.ContainsExact(item.Source))
                        throw new InvalidOperationException("The initial furniture selection is inconsistent.");
                results = items.Select(item => new FurnitureRestoreItemResult(item.Source, FurnitureRestoreOutcome.NotAttempted, "Not attempted.")).ToArray();
                for (var index = 0; index < items.Length; index++) remaining.Enqueue(index);
                Next(now); return;
            }
            if (Phase == FurnitureRestorePhase.DeferredPass) { Next(now); return; }
            if (current < 0) throw new InvalidOperationException("Furniture operation has no selected initial item.");
            var itemNow = items[current];
            var frame = adapter.Read(session, itemNow, operation);
            if (Terminal) return;
            if (frame.Session != session || frame.Operation != operation || frame.Ambiguous)
            { Cancel("The owned estate/item observation changed or became ambiguous."); return; }
            // Adapter supplies the framework observation identity, not a polling count.
            if (frame.FrameworkSequence < lastFrameSequence)
            { Cancel("The framework observation sequence moved backwards."); return; }
            if (frame.FrameworkSequence == lastFrameSequence) return;
            lastFrameSequence = frame.FrameworkSequence;
            if (!frame.Complete || frame.Source == null || frame.Destination == null)
            { reconciliation?.Gap(frame.FrameworkSequence); Status = "Waiting for a complete source/destination snapshot."; return; }
            var unchanged = frame.Source.SameSlots(expectedSource!) && frame.Destination.AddsExactly(expectedDestination!, null, 0);
            if (Phase == FurnitureRestorePhase.AdmitItem)
            {
                if (!unchanged || !frame.Source.ContainsExact(itemNow.Source)) { Cancel("The fixed source set or destination inventory changed before admission."); return; }
                if (frame.Decision != FurnitureRestoreDecision.None) { Cancel("Another native decision is active before furniture admission."); return; }
                if (frame.Admission == FurnitureRestoreAdmission.Waiting) { Status = frame.Detail; return; }
                if (frame.Admission is FurnitureRestoreAdmission.Rejected or FurnitureRestoreAdmission.DependencyBlocked)
                { Refuse(frame.Admission == FurnitureRestoreAdmission.DependencyBlocked, frame.Detail, now); return; }
                if (frame.Admission != FurnitureRestoreAdmission.Ready)
                { Cancel("The native admission classification is unsupported."); return; }
                if (itemNow.Effect == FurnitureRestoreEffect.Unknown) { Refuse(false, "The selected item's native effect/result mapping is unsupported.", now); return; }
                if (submittedAny && now - lastSubmission < 500) return;
                reconciliation = new(frame.Source, frame.Destination, itemNow.Source, itemNow.Source.Quantity,
                    itemNow.ReturnedState, itemNow.ReturnedQuantity, itemNow.NeedsNativeDecision);
                attempted = true; submittedAny = true; lastSubmission = now; decisionAccepted = false;
                deadline = now + 10000; Phase = FurnitureRestorePhase.AwaitNativeDecision;
                adapter.Submit(session, mode, itemNow, operation);
                if (!Terminal) Status = "Native furniture request entered; waiting for its decision or observed transfer.";
                return;
            }
            if (frame.Decision == FurnitureRestoreDecision.Invalid) { Cancel("The native decision no longer belongs to this exact item/state."); return; }
            if (frame.Decision == FurnitureRestoreDecision.Declined)
            {
                if (!unchanged || decisionAccepted) { Cancel("A declined native decision has conflicting acceptance or inventory changes."); return; }
                Record(FurnitureRestoreOutcome.Declined, "The owned native item decision was declined."); Next(now); return;
            }
            if (frame.CorrelatedRejection)
            {
                if (!unchanged || decisionAccepted || frame.Decision != FurnitureRestoreDecision.None ||
                    frame.Admission is not (FurnitureRestoreAdmission.Rejected or FurnitureRestoreAdmission.DependencyBlocked))
                { Cancel("A native rejection has conflicting ownership, classification or inventory changes."); return; }
                Refuse(frame.Admission == FurnitureRestoreAdmission.DependencyBlocked, frame.Detail, now); return;
            }
            if (frame.Decision == FurnitureRestoreDecision.Waiting)
            {
                if (!unchanged || decisionAccepted) { Cancel("Inventory or acceptance changed while the owned native decision was pending."); return; }
                if (!decisionWaitSeen) { decisionWaitSeen = true; deadline = now + 120000; }
                Phase = FurnitureRestorePhase.AwaitNativeDecision; Status = "Decide this item in the native warning; XA does not answer it."; return;
            }
            if (frame.Decision == FurnitureRestoreDecision.Accepted && !decisionAccepted)
            { decisionAccepted = true; deadline = now + 10000; }
            Phase = FurnitureRestorePhase.AwaitTransfer;
            var delta = reconciliation!.Observe(frame.FrameworkSequence, frame.Source, frame.Destination, frame.Ambiguous, decisionAccepted);
            if (delta == FurnitureRestoreDelta.Conflict) { Cancel("Furniture transfer observation conflicts with the owned expected result."); return; }
            if (delta == FurnitureRestoreDelta.Confirmed)
            {
                expectedSource = frame.Source; expectedDestination = frame.Destination;
                Record(itemNow.Effect == FurnitureRestoreEffect.Ordinary ? FurnitureRestoreOutcome.Moved : FurnitureRestoreOutcome.Converted,
                    "The expected source/result delta persisted in two consecutive framework snapshots.");
                confirmed++; Next(now); return;
            }
            if (delta == FurnitureRestoreDelta.StableCandidate) Phase = FurnitureRestorePhase.Reconcile;
            Status = "Waiting for a stable, matching source/destination transfer; no resend is permitted.";
        }
        catch (Exception error)
        {
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Batch failed in phase {Phase}", Phase);
            Cancel(error.Message);
        }
    }

    private void Refuse(bool dependency, string detail, long now)
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
            if (remaining.Count == 0) { Finish($"Furniture batch ended: {confirmed}/{items.Length} confirmed moves/conversions; inspect every item outcome."); return; }
        }
        current = remaining.Dequeue(); operation++; deadline = now + 10000;
        Phase = FurnitureRestorePhase.AdmitItem; Status = $"Admitting initial item {current + 1}/{items.Length}.";
    }

    private void Record(FurnitureRestoreOutcome outcome, string detail)
    {
        if (current >= 0) results[current] = new(items[current].Source, outcome, detail);
    }

    internal void Cancel(string reason)
    {
        if (Terminal) return;
        Plugin.Log.Warning("[AutoRestoreFurniture] Batch stopped: {Reason}", reason);
        if (attempted) Record(FurnitureRestoreOutcome.Unconfirmed, reason + " A native request/decision may still be outstanding; no retry or compensation was sent.");
        Finish(reason);
    }

    private void Finish(string detail)
    {
        Phase = FurnitureRestorePhase.Finished; Status = detail;
        if (ended) return;
        ended = true;
        try { adapter.End(); }
        catch (Exception error)
        {
            Status += " XA observer cleanup reported a failure; native pending state was not changed.";
            Plugin.Log.Error(error, "[AutoRestoreFurniture] Batch cleanup failed.");
        }
        Plugin.Log.Information("[AutoRestoreFurniture] {Status}", Status);
    }
}
