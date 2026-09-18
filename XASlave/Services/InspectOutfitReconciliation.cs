using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal static class InspectOutfitReconciliation
{
    private const int Stride = 28;

    internal static bool Inserted(InspectOutfitNativeState before, InspectOutfitNativeState after, InspectOutfitItem item)
    {
        if (!before.Preview.SameSession(after.Preview) || before.SaveDeleteOutfit != after.SaveDeleteOutfit
            || after.Opener != 0 || before.RawRecords.Length != Stride * 14 || after.RawRecords.Length != Stride * 14) return false;
        if (before.Addon != 0 && (before.Addon != after.Addon || before.AddonId != after.AddonId
            || before.DisplayGear != after.DisplayGear || before.Inventory != after.Inventory)) return false;
        var slot = Array.FindIndex(before.Preview.Records, record => record.Item == 0);
        if (slot < 0) return false;
        var expected = (byte[])before.RawRecords.Clone(); var at = slot * Stride;
        expected[at] = 14; expected[at + 2] = item.Stain0; expected[at + 3] = item.Stain1; expected[at + 7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(at + 12), item.Appearance.Item);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(at + 16), 0);
        return expected.AsSpan().SequenceEqual(after.RawRecords);
    }

    internal static bool Resolved(InspectOutfitNativeState inserted, InspectOutfitNativeState current,
        InspectOutfitItem item, Func<uint, InspectOutfitItemData?> metadata)
    {
        if (!inserted.Preview.SameSession(current.Preview) || inserted.SaveDeleteOutfit != current.SaveDeleteOutfit
            || inserted.Inventory != current.Inventory || inserted.Opener != current.Opener
            || (inserted.Addon != 0 && (inserted.Addon != current.Addon || inserted.AddonId != current.AddonId))) return false;
        if (inserted.RawRecords.Length != Stride * 14 || current.RawRecords.Length != Stride * 14) return false;
        // AgentTryon.Update recomputes DisplayGear after resolving items
        // (0xC7B495/0xC7B49E). It is not a fixed user-mode identity here.
        var pending = Array.FindIndex(inserted.Preview.Records, record => record.Pending && record.Appearance == item.Appearance);
        if (pending < 0 || inserted.Preview.Records.Count(record => record.Pending) != 1) return false;
        var original = Bytes(inserted, pending);
        var data = metadata(item.Appearance.Item);
        if (data == null) return inserted.RawRecords.AsSpan().SequenceEqual(current.RawRecords);
        // Try each content match, including identical rings. Array indices are
        // never treated as item identities across native compaction.
        for (var candidate = -1; candidate < 14; candidate++)
        {
            var slot = -1;
            if (candidate >= 0)
            {
                var record = current.Preview.Records[candidate];
                if (record.Item != item.Appearance.Item || record.Glamour != 0 || record.Appearance != item.Appearance) continue;
                slot = record.EquipSlot;
                if (!NewRecord(original, Bytes(current, candidate), data)) continue;
            }
            var available = new List<int>(Enumerable.Range(0, 14).Where(index => index != candidate && current.Preview.Records[index].Item != 0));
            var valid = true;
            for (var index = 0; index < 14; index++)
            {
                var old = inserted.Preview.Records[index];
                if (index == pending || old.Item == 0) continue;
                var oldBytes = Bytes(inserted, index);
                var match = available.FindIndex(other => SameRetained(oldBytes, Bytes(current, other), metadata));
                if (match >= 0) { available.RemoveAt(match); continue; }
                var oldData = metadata(old.Item);
                if (slot < 0 || slot >= 14 || oldData == null || !Conflicts(old.EquipSlot, oldData, slot, data)) { valid = false; break; }
            }
            if (valid && available.Count == 0 && EmptyRecordsValid(inserted, current)) return true;
        }
        return false;
    }

    private static bool Conflicts(int oldSlot, InspectOutfitItemData old, int slot, InspectOutfitItemData added) => oldSlot == slot
        || (old.Conflicts & (1u << slot)) != 0 || (added.Conflicts & (1u << oldSlot)) != 0
        || (oldSlot <= 1 && slot <= 1 && old.CombatWeapon != added.CombatWeapon);

    private static byte[] Bytes(InspectOutfitNativeState state, int index) => state.RawRecords.AsSpan(index * Stride, Stride).ToArray();

    private static bool Presentation(byte[] bytes, InspectOutfitItemData data)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)) == data.Icon
            && bytes[8] == (data.Dyes > 0 ? 1 : 0) && bytes[9] == (data.Dyes == 2 ? 1 : 0) && bytes[11] <= 1;
    }

    private static bool SameRetained(byte[] before, byte[] after, Func<uint, InspectOutfitItemData?> metadata)
    {
        if (before.AsSpan().SequenceEqual(after)) return true;
        var id = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(12));
        var glamour = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(16));
        var item = metadata(id); var dyes = glamour == 0 ? item : metadata(glamour);
        if (item == null || dyes == null || !Presentation(after, item with { Dyes = dyes.Dyes })) return false;
        for (var index = 0; index < Stride; index++)
            if (index is not (8 or 9 or 11 or 20 or 21 or 22 or 23) && before[index] != after[index]) return false;
        return true;
    }

    private static bool NewRecord(byte[] before, byte[] after, InspectOutfitItemData data)
    {
        if (after[0] == 14) return SameRetained(before, after, _ => data);
        if (after[0] >= 14 || (data.Slots & (1u << after[0])) == 0 || after[1] != data.Category
            || after[6] != data.ClassJob || BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(24)) != data.Conflicts || !Presentation(after, data)) return false;
        for (var index = 0; index < Stride; index++)
            if (index is not (0 or 1 or 6 or 8 or 9 or 11 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27) && before[index] != after[index]) return false;
        return true;
    }

    private static bool EmptyRecordsValid(InspectOutfitNativeState before, InspectOutfitNativeState after)
    {
        var empty = new byte[Stride]; empty[0] = 14;
        for (var index = 0; index < 14; index++)
            if (after.Preview.Records[index].Item == 0)
            {
                var bytes = Bytes(after, index);
                if (bytes.AsSpan().SequenceEqual(empty)) continue;
                if (!Enumerable.Range(0, 14).Any(old => before.Preview.Records[old].Item == 0 && Bytes(before, old).AsSpan().SequenceEqual(bytes))) return false;
            }
        return true;
    }
}
