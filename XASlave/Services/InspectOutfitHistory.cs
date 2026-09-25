using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Services;

internal sealed class InspectOutfitHistory
{
    internal const int Capacity = 500;
    private readonly Configuration configuration;
    internal IReadOnlyList<InspectOutfitHistoryEntry> Entries => configuration.InspectOutfitHistory;
    internal int Revision { get; private set; }

    internal InspectOutfitHistory(Configuration configuration)
    {
        this.configuration = configuration;
        configuration.InspectOutfitHistory ??= new();
    }

    internal void Record(InspectOutfitSnapshot snapshot, ushort facewear, string worldName)
    {
        var entry = new InspectOutfitHistoryEntry
        {
            PlayerName = snapshot.Identity.Name,
            WorldId = snapshot.Identity.World,
            WorldName = worldName,
            SavedAtUtc = DateTime.UtcNow,
            FacewearId = facewear,
            Equipment = snapshot.Equipment.Select(item => new InspectOutfitHistoryItem
            {
                Slot = item.Slot, BaseId = item.BaseId, GlamourId = item.GlamourId,
                Stain0 = item.Stain0, Stain1 = item.Stain1,
            }).ToList(),
        };
        if (!TryGetEquipment(entry, out var equipment)) return;
        var entries = configuration.InspectOutfitHistory;
        var existing = entries.FindIndex(candidate => candidate != null
            && candidate.WorldId == entry.WorldId
            && string.Equals(candidate.PlayerName, entry.PlayerName, StringComparison.OrdinalIgnoreCase)
            && candidate.FacewearId == facewear
            && TryGetEquipment(candidate, out var other)
            && equipment.Select(item => item.Appearance).SequenceEqual(other.Select(item => item.Appearance)));
        if (existing >= 0)
        {
            entry.Id = entries[existing].Id;
            entries.RemoveAt(existing);
        }
        entries.Insert(0, entry);
        if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
        Revision++;
        configuration.SaveDeferred();
    }

    internal bool Remove(Guid id)
    {
        var entries = configuration.InspectOutfitHistory;
        var index = entries.FindIndex(entry => entry != null && entry.Id == id);
        if (index < 0) return false;
        entries.RemoveAt(index);
        Revision++;
        configuration.SaveDeferred();
        return true;
    }

    internal InspectOutfitHistoryEntry? Find(Guid id) => Entries.FirstOrDefault(entry => entry != null && entry.Id == id);

    internal static bool TryGetEquipment(InspectOutfitHistoryEntry entry, out InspectOutfitItem[] equipment)
    {
        equipment = Array.Empty<InspectOutfitItem>();
        if (entry == null || entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.PlayerName)
            || entry.PlayerName.Length > 64 || entry.WorldId == 0 || entry.Equipment == null
            || entry.Equipment.Count != InspectOutfitPlan.Slots.Length) return false;
        var result = new List<InspectOutfitItem>();
        foreach (var slot in InspectOutfitPlan.Slots)
        {
            var matches = entry.Equipment.Where(item => item != null && item.Slot == slot).ToArray();
            if (matches.Length != 1) return false;
            var item = matches[0];
            if (item.BaseId == 0 && item.GlamourId != 0) return false;
            result.Add(new(slot, item.BaseId, item.GlamourId, item.Stain0, item.Stain1));
        }
        if (!result.Any(item => item.BaseId != 0)) return false;
        equipment = result.ToArray();
        return true;
    }
}
