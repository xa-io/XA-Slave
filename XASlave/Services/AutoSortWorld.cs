using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XASlave.Services;

internal sealed unsafe class AutoSortWorld
{
    private readonly AutoSortNativeBinding binding;
    private readonly Func<long> frameworkSequence;
    // Exact current module field order, with obsolete waist deliberately absent.
    private static readonly uint[] ArmouryTypes = [3500, 3201, 3202, 3203, 3205, 3206, 3200, 3207, 3208, 3209, 3300, 3400];
    internal AutoSortWorld(AutoSortNativeBinding binding, Func<long> frameworkSequence)
    { this.binding = binding; this.frameworkSequence = frameworkSequence; }

    private static AutoSortSession Session()
    {
        NearbyZoneNativeBinding.RequireFramework();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (!Plugin.PlayerState.IsLoaded || !Plugin.ClientState.IsLoggedIn || player == null)
            throw new InvalidOperationException("The sorting character is unavailable.");
        var character = (Character*)player.Address;
        NearbyZoneNativeBinding.RequireReadable((nint)character, sizeof(Character));
        var module = ItemOrderModule.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)module, sizeof(ItemOrderModule));
        if (character->ContentId == 0 || module->CharacterContentId != character->ContentId)
            throw new InvalidOperationException("The item-order file does not belong to the current character.");
        return new(character->ContentId, Plugin.ClientState.TerritoryType, Plugin.ClientState.MapId, (nint)module);
    }

    private static nint[] Sorters(ItemOrderModule* module) =>
    [
        (nint)module->ArmouryMainHandSorter, (nint)module->ArmouryHeadSorter, (nint)module->ArmouryBodySorter,
        (nint)module->ArmouryHandsSorter, (nint)module->ArmouryLegsSorter, (nint)module->ArmouryFeetSorter,
        (nint)module->ArmouryOffHandSorter, (nint)module->ArmouryEarsSorter, (nint)module->ArmouryNeckSorter,
        (nint)module->ArmouryWristsSorter, (nint)module->ArmouryRingsSorter, (nint)module->ArmourySoulCrystalSorter,
        (nint)module->InventorySorter,
    ];

    internal AutoSortTarget ReadTarget(nint address)
    {
        var session = Session();
        var pointers = Sorters((ItemOrderModule*)session.Module);
        var index = Array.IndexOf(pointers, address);
        if (index < 0 || address == 0 || pointers.Count(p => p == address) != 1)
            throw new InvalidOperationException("The observed sorter is no longer an exact current target.");
        var result = ReadTarget(address, index);
        if (Session() != session || !pointers.SequenceEqual(Sorters((ItemOrderModule*)session.Module)))
            throw new InvalidOperationException("Item-sort ownership changed while reading native admission.");
        return result;
    }

    private static nint Pointer(byte[] bytes, int offset) => checked((nint)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8)));

    private static int Vector(nint begin, nint end, nint capacity, int stride, int limit)
    {
        if (begin == 0)
        {
            if (end != 0 || capacity != 0) throw new InvalidOperationException("A native sort vector has inconsistent null storage.");
            return 0;
        }
        var used = checked(end - begin); var allocated = checked(capacity - begin);
        if (used < 0 || allocated < used || allocated > checked(stride * limit) || used % stride != 0 || allocated % stride != 0)
            throw new InvalidOperationException("A native sort vector has invalid bounds.");
        if (allocated > 0) NearbyZoneNativeBinding.RequireReadable(begin, checked((int)allocated));
        return checked((int)(used / stride));
    }

    private static AutoSortTarget ReadTarget(nint address, int index)
    {
        var bytes = NearbyZoneNativeBinding.Read(address, sizeof(ItemOrderModuleSorter));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (type != (index == 12 ? 0u : ArmouryTypes[index])) throw new InvalidOperationException("A required sorter has a different container type.");
        var itemsBegin = Pointer(bytes, 8); var itemsEnd = Pointer(bytes, 16); var itemsCapacity = Pointer(bytes, 24);
        _ = Vector(itemsBegin, itemsEnd, itemsCapacity, 8, 1024);
        var perPage = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
        var pass = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(56));
        var percent = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60));
        var begin = Pointer(bytes, 64); var end = Pointer(bytes, 72); var capacity = Pointer(bytes, 80);
        // Native vector allocation may exceed the sixteen-rule admission limit
        // after prior growth. Bound readable storage separately from live count.
        var count = Vector(begin, end, capacity, 16, 1024);
        if (count > 16 || perPage <= 0 || perPage > 140 || pass < -1 || pass >= 16 || percent < 0 || percent > 100)
            throw new InvalidOperationException("Native sort progress or page layout is unsupported.");
        var rules = new AutoSortNativeRule[count];
        if (count > 0)
        {
            var entries = NearbyZoneNativeBinding.Read(begin, checked(count * 16));
            for (var i = 0; i < count; i++)
            {
                var direction = entries[i * 16 + 8];
                if (direction > 1) throw new InvalidOperationException("A native sort direction is invalid.");
                rules[i] = new(Pointer(entries, i * 16), direction != 0);
            }
        }
        var after = NearbyZoneNativeBinding.Read(address, sizeof(ItemOrderModuleSorter));
        // These fields are stable during this immediate framework read. Padding,
        // callback and previous-order state are not command-rule fingerprints.
        foreach (var offset in new[] { 0, 8, 16, 24, 40, 56, 60, 64, 72, 80 })
        {
            var width = offset is 0 or 40 or 56 or 60 ? 4 : 8;
            if (!bytes.AsSpan(offset, width).SequenceEqual(after.AsSpan(offset, width)))
                throw new InvalidOperationException("Native sort state changed during capture.");
        }
        return new(address, index == 12 ? AutoSortGroup.Inventory : AutoSortGroup.Armoury, type, perPage,
            itemsBegin, itemsEnd, itemsCapacity, begin, capacity, pass, percent, rules);
    }

    internal AutoSortSnapshot Capture()
    {
        binding.Validate();
        var session = Session(); var sequence = frameworkSequence();
        var pointers = Sorters((ItemOrderModule*)session.Module);
        if (pointers.Any(p => p == 0) || pointers.Distinct().Count() != 13)
            throw new InvalidOperationException("All thirteen distinct sorters must be loaded.");
        var targets = new AutoSortTarget[13];
        for (var i = 0; i < targets.Length; i++) targets[i] = ReadTarget(pointers[i], i);
        var manager = InventoryManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(InventoryManager));
        var containers = new List<AutoSortContainer>(); var physical = new List<AutoSortPhysicalItem>();
        foreach (var type in new uint[] { 0, 1, 2, 3 }.Concat(ArmouryTypes))
        {
            var container = manager->GetInventoryContainer((InventoryType)type);
            NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
            var items = container->Items; var size = container->Size;
            if (!container->IsLoaded || (uint)container->Type != type || items == null || size <= 0 || size > 140 || (type < 4 && size != 35))
                throw new InvalidOperationException("Every normal bag and current armoury container must be loaded.");
            NearbyZoneNativeBinding.RequireReadable((nint)items, checked(size * sizeof(InventoryItem)));
            for (var slot = 0; slot < size; slot++)
            {
                var item = items + slot;
                if (item->IsSymbolic || (uint)item->Container != type || item->Slot != slot || item->Quantity < 0
                    || item->VirtualTable != InventoryItem.StaticVirtualTablePointer)
                    throw new InvalidOperationException("A physical inventory slot has an invalid identity.");
                physical.Add(new(type, checked((ushort)slot), checked((uint)item->Quantity), InventoryShuttleItemState.Read(item)));
            }
            if (manager != InventoryManager.Instance() || container != manager->GetInventoryContainer((InventoryType)type)
                || !container->IsLoaded || container->Items != items || container->Size != size)
                throw new InvalidOperationException("An inventory container changed while copying its physical slots.");
            containers.Add(new(type, (nint)container, (nint)items, size));
        }
        if (Session() != session || frameworkSequence() != sequence || !pointers.SequenceEqual(Sorters((ItemOrderModule*)session.Module)))
            throw new InvalidOperationException("The sorting world changed while taking its snapshot.");
        return new(session, sequence, targets, containers.ToArray(), physical.ToArray());
    }
}
