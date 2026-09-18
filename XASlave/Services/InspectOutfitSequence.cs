using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal sealed record InspectOutfitPreview(nint Agent, long Session, long Frame, bool Changed, bool Transitioning,
    bool Ready, InspectPreviewRecord[] Records)
{
    internal bool SameSession(InspectOutfitPreview other) => Agent == other.Agent && Session == other.Session;
    internal bool SameRecords(InspectOutfitPreview other) => Records.SequenceEqual(other.Records);
}
internal sealed record InspectOutfitSubmission(bool Accepted, InspectOutfitPreview After);
internal interface IInspectOutfitAdapter
{
    InspectOutfitSnapshot? CaptureSource(InspectOutfitHost host, InspectOutfitIdentity identity);
    InspectOutfitPreview CapturePreview();
    bool HasItemData(uint id);
    InspectOutfitSubmission Submit(InspectOutfitSnapshot source, InspectOutfitItem item, InspectOutfitPreview before, long generation);
    // Validate the only permitted native resolution/compaction changes, including
    // item-sheet conflict masks. A foreign edit must not be blessed as resolution.
    bool IsOwnedResolution(InspectOutfitPreview inserted, InspectOutfitPreview current, InspectOutfitItem item);
}

internal sealed class InspectOutfitSequence
{
    private readonly IInspectOutfitAdapter adapter;
    private readonly InspectOutfitHost host;
    private readonly InspectOutfitIdentity identity;
    private readonly long generation, deadline;
    private readonly List<InspectOutfitItem> accepted = new();
    private InspectOutfitSnapshot? previousSource, frozen;
    private InspectOutfitItem[]? plan;
    private InspectOutfitPreview? initialPreview, expected, inserted;
    private InspectOutfitItem? pending;
    private long nextSend, terminalFrame = -1;
    private int next;
    internal bool Terminal { get; private set; }
    internal bool Completed { get; private set; }
    internal InspectOutfitSnapshot? CompletedSource => Completed ? frozen : null;
    internal Exception? Failure { get; private set; }
    internal int Applied { get; private set; }
    internal int AlreadyPresent { get; private set; }
    internal int Rejected { get; private set; }
    internal string Status { get; private set; } = "Waiting for two stable inspection frames.";

    internal InspectOutfitSequence(IInspectOutfitAdapter adapter, InspectOutfitHost host, InspectOutfitIdentity identity, long generation, long now)
    {
        if (!identity.IsPlayer) throw new InvalidOperationException("A published player inspection is required.");
        this.adapter = adapter; this.host = host; this.identity = identity; this.generation = generation;
        deadline = checked(now + 10000); nextSend = now;
    }

    internal void Cancel(string reason = "Try-on stopped; submitted native work may finish.")
    {
        if (Terminal) return;
        Terminal = true; Completed = false; Status = reason;
        previousSource = null; frozen = null; plan = null; initialPreview = null; expected = null; inserted = null; pending = null; accepted.Clear();
    }

    internal void Tick(long now)
    {
        if (Terminal) return;
        if (now >= deadline)
        {
            var reason = expected == null && initialPreview != null
                ? "Try-on timed out waiting for the fitting room to settle; no outfit items were sent."
                : "Try-on timed out with an unconfirmed partial result.";
            Cancel($"{reason} Last state: {Status} Applied: {Applied}; already present: {AlreadyPresent}.");
            return;
        }
        try
        {
            var source = adapter.CaptureSource(host, identity);
            if (Terminal) return;
            if (source == null)
            {
                if (frozen != null) Cancel("Inspection is no longer ready; outfit interrupted.");
                else previousSource = null;
                return;
            }
            if (source.Host != host || source.Identity != identity) { Cancel("The inspected player or window changed."); return; }
            if (frozen == null)
            {
                var stable = previousSource != null && previousSource.SameSource(source) && source.Frame == previousSource.Frame + 1;
                previousSource = source;
                if (!stable) return;
                frozen = source; plan = InspectOutfitPlan.Create(source.Equipment);
                if (plan.Length == 0) { Cancel("No inspected appearance items are available."); return; }
            }
            if (!frozen.SameSource(source)) { Cancel("The inspected equipment changed; outfit interrupted."); return; }
            var preview = adapter.CapturePreview();
            if (Terminal) return;
            if (initialPreview == null) initialPreview = preview;
            if (!initialPreview.SameSession(preview)) { Cancel("The fitting-room session changed."); return; }
            if (pending is { } waiting)
            {
                if (inserted == null || !adapter.IsOwnedResolution(inserted, preview, waiting))
                { Cancel("The fitting-room preview changed outside this request."); return; }
                if (Terminal) return;
                if (preview.Changed || preview.Transitioning || InspectOutfitPlan.HasPending(preview.Records)) return;
                var required = InspectOutfitPlan.RequiredCount(accepted, waiting.Appearance) + 1;
                if (InspectOutfitPlan.ResolvedCount(preview.Records, waiting.Appearance) < required)
                { Rejected++; Cancel("The requested appearance was rejected or replaced by the game."); return; }
                accepted.Add(waiting); Applied++; next++; pending = null; inserted = null; expected = preview;
            }
            else if (expected != null && !expected.SameRecords(preview))
            { Cancel("Manual or foreign fitting-room changes interrupted the outfit."); return; }
            if (!InspectOutfitPlan.ContainsAll(preview.Records, accepted))
            { Cancel("Native equipment restrictions replaced an earlier requested appearance."); return; }
            if (preview.Changed || preview.Transitioning || InspectOutfitPlan.HasPending(preview.Records))
            {
                Status = expected == null
                    ? "Waiting for the existing fitting-room preview to settle."
                    : "Waiting for the fitting-room preview to update.";
                return;
            }
            expected = preview;
            if (next == plan!.Length)
            {
                if (!preview.Ready) { terminalFrame = -1; return; }
                if (preview.Frame == terminalFrame) return;
                if (terminalFrame >= 0 && preview.Frame == terminalFrame + 1)
                {
                    Completed = true; Terminal = true;
                    Status = $"Outfit sent to fitting room: {Applied} applied, {AlreadyPresent} already present, {Rejected} rejected.";
                    return;
                }
                terminalFrame = preview.Frame; return;
            }
            var item = plan[next];
            if (InspectOutfitPlan.ResolvedCount(preview.Records, item.Appearance) > InspectOutfitPlan.RequiredCount(accepted, item.Appearance))
            { accepted.Add(item); AlreadyPresent++; next++; return; }
            if (!InspectOutfitPlan.HasCapacity(preview.Records)) { Cancel("The fitting room is full; outfit partially applied."); return; }
            if (now < nextSend) return;
            if (!adapter.HasItemData(item.Appearance.Item)) { Status = "Waiting for appearance item data."; return; }
            if (Terminal) return;
            var result = adapter.Submit(frozen, item, preview, generation);
            if (Terminal) return;
            nextSend = checked(now + 50);
            if (!result.Accepted) { Rejected++; Cancel("The game rejected the requested appearance."); return; }
            var after = result.After;
            var beforeCount = preview.Records.Count(record => record.Pending && record.Appearance == item.Appearance);
            var afterCount = after.Records.Count(record => record.Pending && record.Appearance == item.Appearance);
            if (afterCount != beforeCount + 1)
            { Cancel("Try-on submission was not confirmed in the native preview list."); return; }
            pending = item; inserted = after; expected = after;
            Status = "Waiting for the requested appearance to resolve.";
        }
        catch (Exception error)
        {
            Failure = error;
            Cancel("Try-on interrupted: " + error.Message);
        }
    }
}
