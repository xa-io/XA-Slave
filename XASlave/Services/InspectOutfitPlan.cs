using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal readonly record struct InspectOutfitIdentity(uint Entity, ushort World, string Name, byte Type)
{
    internal bool IsPlayer => Entity != 0 && Entity != 0xE0000000 && World != 0 && Type == 4 && !string.IsNullOrWhiteSpace(Name);
}

internal readonly record struct InspectOutfitHost(nint Addon, uint Id, nint Root, nint Anchor, long Generation);
internal readonly record struct InspectOutfitItem(ushort Slot, uint BaseId, uint GlamourId, byte Stain0, byte Stain1)
{
    internal InspectOutfitAppearance Appearance => new(GlamourId != 0 ? GlamourId : BaseId, Stain0, Stain1);
}
internal readonly record struct InspectOutfitAppearance(uint Item, byte Stain0, byte Stain1);
internal readonly record struct InspectPreviewRecord(uint Item, uint Glamour, byte Stain0, byte Stain1,
    byte GlamourStain0, byte GlamourStain1, byte EquipSlot, byte GlamourSlot, bool Crest)
{
    internal bool Pending => Item != 0 && EquipSlot == 14;
    internal InspectOutfitAppearance Appearance => Glamour != 0
        ? new(Glamour, GlamourStain0, GlamourStain1) : new(Item, Stain0, Stain1);
}

internal sealed record InspectOutfitSnapshot(InspectOutfitHost Host, InspectOutfitIdentity Identity, long Frame,
    InspectOutfitItem[] Equipment)
{
    internal bool SameSource(InspectOutfitSnapshot other) => Host == other.Host && Identity == other.Identity && Equipment.SequenceEqual(other.Equipment);
}

internal static class InspectOutfitPlan
{
    // Preserve the captured twelve mapped inspect entries, including both rings.
    // Belt 5, soul stone 13 and separately stored facewear are not appearances here.
    internal static readonly ushort[] Slots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];

    internal static InspectOutfitItem[] Create(IReadOnlyList<InspectOutfitItem> captured)
    {
        var result = new List<InspectOutfitItem>();
        foreach (var slot in Slots)
        {
            var matching = captured.Where(item => item.Slot == slot).ToArray();
            if (matching.Length != 1) throw new InvalidOperationException("The inspected equipment snapshot is incomplete or has duplicate slots.");
            var item = matching[0];
            if (item.BaseId == 0)
            {
                if (item.GlamourId != 0) throw new InvalidOperationException("An empty inspected slot contains conflicting glamour data.");
                continue;
            }
            result.Add(item);
        }
        return result.ToArray();
    }

    internal static int ResolvedCount(IReadOnlyList<InspectPreviewRecord> preview, InspectOutfitAppearance appearance)
    {
        var count = 0;
        foreach (var record in preview)
            if (record.Item != 0 && !record.Pending && record.Appearance == appearance) count++;
        return count;
    }

    internal static int RequiredCount(IReadOnlyList<InspectOutfitItem> accepted, InspectOutfitAppearance appearance)
    {
        var count = 0;
        foreach (var item in accepted) if (item.Appearance == appearance) count++;
        return count;
    }

    internal static bool ContainsAll(IReadOnlyList<InspectPreviewRecord> preview, IReadOnlyList<InspectOutfitItem> required)
    {
        foreach (var item in required)
            if (ResolvedCount(preview, item.Appearance) < RequiredCount(required, item.Appearance)) return false;
        return true;
    }

    internal static bool HasPending(IReadOnlyList<InspectPreviewRecord> preview)
    {
        foreach (var record in preview) if (record.Pending) return true;
        return false;
    }

    internal static bool HasCapacity(IReadOnlyList<InspectPreviewRecord> preview)
    {
        if (preview.Count != 14) throw new InvalidOperationException("The fitting-room preview collection changed size.");
        foreach (var record in preview) if (record.Item == 0) return true;
        return false;
    }
}
