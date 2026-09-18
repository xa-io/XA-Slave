using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

internal enum InventoryShuttleFamily { Player, Saddlebag, PremiumSaddlebag, Retainer }

// No InventoryItem pointer, vtable or padding survives a framework callback.
internal readonly record struct InventoryShuttleItemState(uint FullId, uint BaseId, ushort Spiritbond, ushort Condition,
    InventoryItem.ItemFlags Flags, ulong Crafter, ulong Materia0To3, ushort Materia4, ulong Grades, ushort Stains, uint Glamour, uint Event)
{
    internal static unsafe InventoryShuttleItemState Read(InventoryItem* slot)
    {
        if (slot == null || slot->IsSymbolic) throw new InvalidOperationException("Symbolic or unavailable inventory slots are not supported.");
        ulong materia = 0, grades = 0;
        for (var index = 0; index < 5; index++)
        {
            if (index < 4) materia |= (ulong)slot->Materia[index] << (index * 16);
            grades |= (ulong)slot->MateriaGrades[index] << (index * 8);
        }
        return new(slot->GetItemId(), slot->GetBaseItemId(), slot->SpiritbondOrCollectability, slot->Condition,
            slot->Flags, slot->CrafterContentId, materia, slot->Materia[4], grades,
            (ushort)(slot->Stains[0] | (slot->Stains[1] << 8)), slot->GlamourId, slot->EventId.Id);
    }
}

internal readonly record struct InventoryShuttleSource(InventoryType Inventory, ushort Slot, int Quantity, InventoryShuttleItemState State);
