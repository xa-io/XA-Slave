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
    internal int Replaced { get; private set; }
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("S183C6E3589AFE6C5");
    internal string Status { get; private set; } = global::XASlave.Localization.UiLocale.Native("S183C6E3589AFE6C5");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, Status);

    internal InspectOutfitSequence(IInspectOutfitAdapter adapter, InspectOutfitHost host, InspectOutfitIdentity identity, long generation, long now)
    {
        if (!identity.IsPlayer) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB931C5DC8C351FAA"), originalMessageText => new InvalidOperationException(originalMessageText));
        this.adapter = adapter; this.host = host; this.identity = identity; this.generation = generation;
        deadline = checked(now + 10000); nextSend = now;
    }

    internal void Cancel() => Cancel(global::XASlave.Localization.UiMessage.Key("SE7CEF21ECCF72D44"));
    internal void Cancel(string reason) => Cancel(global::XASlave.Localization.UiMessage.Raw(reason));
    internal void Cancel(global::XASlave.Localization.UiMessage reason)
    {
        if (Terminal) return;
        Terminal = true; Completed = false; Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, reason);
        previousSource = null; frozen = null; plan = null; initialPreview = null; expected = null; inserted = null; pending = null; accepted.Clear();
    }

    internal void Tick(long now)
    {
        if (Terminal) return;
        if (now >= deadline)
        {
            var reason = expected == null && initialPreview != null
                ? global::XASlave.Localization.UiMessage.Key("S048515142C40C7AC")
                : global::XASlave.Localization.UiMessage.Key("S515DC742B7FB113B");
            Cancel(global::XASlave.Localization.UiMessage.Format("S29AD69D26A6DB4F6" , reason, StatusMessage, Applied, AlreadyPresent));
            return;
        }
        try
        {
            var source = adapter.CaptureSource(host, identity);
            if (Terminal) return;
            if (source == null)
            {
                if (frozen != null) Cancel(global::XASlave.Localization.UiMessage.Key("SC321630D7D18B9C5"));
                else previousSource = null;
                return;
            }
            if (source.Host != host || source.Identity != identity) { Cancel(global::XASlave.Localization.UiMessage.Key("S9F6D46DCDE920EB1")); return; }
            if (frozen == null)
            {
                var stable = previousSource != null && previousSource.SameSource(source) && source.Frame == previousSource.Frame + 1;
                previousSource = source;
                if (!stable) return;
                frozen = source; plan = InspectOutfitPlan.Create(source.Equipment);
                if (plan.Length == 0) { Cancel(global::XASlave.Localization.UiMessage.Key("SE86B5C0E8892A7E8")); return; }
            }
            if (!frozen.SameSource(source)) { Cancel(global::XASlave.Localization.UiMessage.Key("S1E029A3406E5A770")); return; }
            var preview = adapter.CapturePreview();
            if (Terminal) return;
            if (initialPreview == null) initialPreview = preview;
            if (!initialPreview.SameSession(preview)) { Cancel(global::XASlave.Localization.UiMessage.Key("S2E12A1BC74B22486")); return; }
            if (pending is { } waiting)
            {
                if (inserted == null || !adapter.IsOwnedResolution(inserted, preview, waiting))
                { Cancel(global::XASlave.Localization.UiMessage.Key("S353465EE9D0E97CA")); return; }
                if (Terminal) return;
                if (preview.Changed || preview.Transitioning || InspectOutfitPlan.HasPending(preview.Records)) return;
                var required = InspectOutfitPlan.RequiredCount(accepted, waiting.Appearance) + 1;
                if (InspectOutfitPlan.ResolvedCount(preview.Records, waiting.Appearance) < required)
                { Rejected++; Cancel(global::XASlave.Localization.UiMessage.Key("S3B4A341296DE6524")); return; }
                // Reconciliation has already proved any removed records conflict
                // with this insertion. Continue with the compatible remainder,
                // while preserving the new appearance's multiplicity check above.
                for (var index = accepted.Count - 1; index >= 0; index--)
                {
                    var appearance = accepted[index].Appearance;
                    if (InspectOutfitPlan.ResolvedCount(preview.Records, appearance) >= InspectOutfitPlan.RequiredCount(accepted, appearance)) continue;
                    accepted.RemoveAt(index); Replaced++;
                }
                accepted.Add(waiting); Applied++; next++; pending = null; inserted = null; expected = preview;
            }
            else if (expected != null && !expected.SameRecords(preview))
            { Cancel(global::XASlave.Localization.UiMessage.Key("S32E30E0B96116EDE")); return; }
            if (!InspectOutfitPlan.ContainsAll(preview.Records, accepted))
            { Cancel(global::XASlave.Localization.UiMessage.Key("S0A504FD432D65570")); return; }
            if (preview.Changed || preview.Transitioning || InspectOutfitPlan.HasPending(preview.Records))
            {
                Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (expected == null ? global::XASlave.Localization.UiMessage.Key("SBE6ACEEA634042D8") : global::XASlave.Localization.UiMessage.Key("SA71C9A145E8CB9B8")));
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
                    Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S45B512978C4EB7D3" , accepted.Count, Applied, AlreadyPresent, Replaced, Rejected));
                    return;
                }
                terminalFrame = preview.Frame; return;
            }
            var item = plan[next];
            if (InspectOutfitPlan.ResolvedCount(preview.Records, item.Appearance) > InspectOutfitPlan.RequiredCount(accepted, item.Appearance))
            { accepted.Add(item); AlreadyPresent++; next++; return; }
            if (!InspectOutfitPlan.HasCapacity(preview.Records)) { Cancel(global::XASlave.Localization.UiMessage.Key("S31C33D16F849817C")); return; }
            if (now < nextSend) return;
            if (!adapter.HasItemData(item.Appearance.Item)) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S56AAE269BADB2EDB")); return; }
            if (Terminal) return;
            var result = adapter.Submit(frozen, item, preview, generation);
            if (Terminal) return;
            nextSend = checked(now + 50);
            if (!result.Accepted) { Rejected++; Cancel(global::XASlave.Localization.UiMessage.Key("SC4811347E5F54FA8")); return; }
            var after = result.After;
            var beforeCount = preview.Records.Count(record => record.Pending && record.Appearance == item.Appearance);
            var afterCount = after.Records.Count(record => record.Pending && record.Appearance == item.Appearance);
            if (afterCount != beforeCount + 1)
            { Cancel(global::XASlave.Localization.UiMessage.Key("S6407F9157415F654")); return; }
            pending = item; inserted = after; expected = after;
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SEE37B6B44AF404F4"));
        }
        catch (Exception error)
        {
            Failure = error;
            Cancel(global::XASlave.Localization.UiMessage.Format("SE267FA48654A7160" , global::XASlave.Localization.UiMessage.FromException(error)));
        }
    }
}
