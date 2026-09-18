using System;

namespace XASlave.Services;

internal sealed class InspectOutfitNativeAdapter : IInspectOutfitAdapter
{
    private readonly InspectOutfitWorld world;
    private readonly InspectOutfitNativeAccess native;
    private InspectOutfitNativeState? last, insertion, settled;

    internal InspectOutfitNativeAdapter(InspectOutfitWorld world, InspectOutfitNativeAccess native)
    { this.world = world; this.native = native; }

    public InspectOutfitSnapshot? CaptureSource(InspectOutfitHost host, InspectOutfitIdentity identity) => world.Capture(host, identity);

    public InspectOutfitPreview CapturePreview()
    {
        var current = native.Capture();
        if (settled != null && (!settled.Preview.SameSession(current.Preview) || settled.SaveDeleteOutfit != current.SaveDeleteOutfit
            || settled.DisplayGear != current.DisplayGear || settled.Inventory != current.Inventory || settled.Opener != current.Opener
            || settled.Addon != current.Addon || settled.AddonId != current.AddonId
            || !settled.RawRecords.AsSpan().SequenceEqual(current.RawRecords)))
            throw new InvalidOperationException("The fitting-room preview changed outside this request.");
        last = current;
        // Do not freeze an in-progress initial preview or opening animation as
        // the settled baseline. Its records/addon may still resolve naturally.
        if (insertion == null && !current.Preview.Changed && !current.Preview.Transitioning && !InspectOutfitPlan.HasPending(current.Preview.Records))
            settled ??= current;
        return current.Preview;
    }

    public bool HasItemData(uint id) => InspectOutfitMetadata.Read(id) != null;

    public InspectOutfitSubmission Submit(InspectOutfitSnapshot source, InspectOutfitItem item, InspectOutfitPreview before, long generation)
    {
        if (last == null || !ReferenceEquals(last.Preview, before)) throw new InvalidOperationException("A fresh owned preview capture is required.");
        var result = native.Submit(source, item, last, generation);
        settled = null;
        if (result.Accepted && !InspectOutfitReconciliation.Inserted(last, result.After, item))
            throw new InvalidOperationException("The native insertion changed more than the requested appearance.");
        insertion = result.Accepted ? result.After : null;
        last = result.After;
        return new(result.Accepted, result.After.Preview);
    }

    public bool IsOwnedResolution(InspectOutfitPreview inserted, InspectOutfitPreview current, InspectOutfitItem item)
    {
        if (insertion == null || last == null || !ReferenceEquals(insertion.Preview, inserted) || !ReferenceEquals(last.Preview, current)
            || !InspectOutfitReconciliation.Resolved(insertion, last, item, InspectOutfitMetadata.Read)) return false;
        if (!current.Changed && !current.Transitioning && !InspectOutfitPlan.HasPending(current.Records))
        {
            settled = last;
            insertion = null;
        }
        return true;
    }
}
