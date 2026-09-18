using System;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

internal sealed record InspectOutfitItemData(int PrimarySlot, uint Slots, uint Conflicts, byte Category,
    byte ClassJob, bool CombatWeapon, uint Icon, byte Dyes);

internal static class InspectOutfitMetadata
{
    internal static InspectOutfitItemData? Read(uint id)
    {
        var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
        if (row == null) return null;
        var category = row.Value.EquipSlotCategory.ValueNullable;
        if (category == null) return null;
        var value = category.Value;
        sbyte[] slots = [value.MainHand, value.OffHand, value.Head, value.Body, value.Gloves, value.Waist,
            value.Legs, value.Feet, value.Ears, value.Neck, value.Wrists, value.FingerL, value.FingerR, value.SoulCrystal];
        var primary = -1; uint occupied = 0, conflicts = 0;
        for (var index = 0; index < slots.Length; index++)
        {
            if (slots[index] > 0) { occupied |= 1u << index; if (primary < 0) primary = index; }
            if (slots[index] == -1) conflicts |= 1u << index;
        }
        if (primary is 11 or 12) occupied |= 0x1800;
        // Native C7D3CA assigns ClassJobUse to main hand; shield FilterGroup3
        // receives job1. C7D4AC compares ClassJobCategory via E0BB0 (32/33
        // noncombat; all remaining categories return combat group1).
        var job = primary == 0 ? checked((byte)row.Value.ClassJobUse.RowId) : (byte)(primary == 1 && row.Value.FilterGroup == 3 ? 1 : 0);
        var jobRow = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(job);
        if ((primary is 0 or 1) && jobRow == null) return null;
        var combat = jobRow != null && jobRow.Value.ClassJobCategory.RowId is not (32 or 33);
        return new(primary, occupied, conflicts, checked((byte)category.Value.RowId), job, combat, row.Value.Icon, row.Value.DyeCount);
    }
}
