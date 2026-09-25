using System;
using XASlave.Data;

namespace XASlave.Services;

internal interface IInspectOutfitSource
{
    InspectOutfitSnapshot? Capture(InspectOutfitHost host, InspectOutfitIdentity identity);
}

internal sealed class InspectOutfitSavedSource : IInspectOutfitSource
{
    private readonly InspectOutfitItem[] equipment;
    private readonly Func<long> frame;
    internal InspectOutfitHost Host { get; }
    internal InspectOutfitIdentity Identity { get; }
    internal ushort Facewear { get; }
    internal Guid EntryId { get; }

    internal InspectOutfitSavedSource(InspectOutfitHistoryEntry entry, long generation, Func<long> frame)
    {
        if (!InspectOutfitHistory.TryGetEquipment(entry, out var captured))
            throw new InvalidOperationException("This saved outfit has incomplete equipment data.");
        equipment = captured; // Detached from configuration and subsequent removals.
        this.frame = frame;
        EntryId = entry.Id;
        Facewear = entry.FacewearId;
        Host = new(0, 0, 0, 0, generation);
        // Managed identity for the queue only; never resolved as a live entity.
        Identity = new(1, entry.WorldId, entry.PlayerName, 4);
    }

    public InspectOutfitSnapshot? Capture(InspectOutfitHost host, InspectOutfitIdentity identity) =>
        host == Host && identity == Identity ? new(Host, Identity, frame(), equipment) : null;
}
