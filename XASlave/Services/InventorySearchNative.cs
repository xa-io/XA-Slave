using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace XASlave.Services;

// Layout mapping is adapted from VanillaPlus at 733087bcfaddd6151d88e2478d658a494bf19eee.
// Resolve every native address from the live addon before using it; stored addresses are identity only.
internal static unsafe class InventorySearchNative
{
    internal static readonly string[] Hosts = ["Inventory", "InventoryLarge", "InventoryExpansion", "InventoryRetainer", "InventoryRetainerLarge", "InventoryBuddy", "InventoryBuddy2", "FreeCompanyChest"];
    internal static readonly string[] Grids = ["InventoryGrid", "InventoryGrid0", "InventoryGrid1", "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E", "RetainerGrid", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4"];

    internal static InventorySearchHost? CaptureHost(string name, long epoch, bool visible = true)
    {
        Plugin.AssertGameThread();
        var addon = AddonHelper.GetAddon(name);
        if (addon == null) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)addon, sizeof(AtkUnitBase));
        // PreFinalize restoration may run after the readiness bit clears, while the addon
        // and its nodes are still alive. New controls require fully loaded visible hosts.
        if (addon->Id == 0 || addon->RootNode == null || visible && (!addon->IsFullyLoaded() || !addon->IsVisible)) return null;
        return new(name, epoch, (nint)addon, addon->Id, (nint)addon->RootNode);
    }

    internal static AtkUnitBase* Resolve(InventorySearchHost host, Func<string, long> epoch, bool visible = false)
    {
        if (!InventorySearchOwnership.IsCurrent(host, CaptureHost(host.Name, epoch(host.Name), visible), epoch(host.Name))) return null;
        return (AtkUnitBase*)host.Address;
    }

    internal static AtkResNode* ResolveSlot(InventorySearchSlot slot, Func<string, long> epoch)
    {
        var host = Resolve(slot.Host, epoch);
        if (host == null) return null;
        if (slot.Host == slot.Grid && (InventorySearchStoragePolicy.IsSaddlebag(slot.Host.Name) || slot.Host.Name == "FreeCompanyChest"))
        {
            // Pages reuse visual nodes. Restoration owns the node, not the item/page now in it.
            var storageNode = StorageNode(host, slot.Host.Name, slot.Index);
            return storageNode != null && (nint)storageNode == slot.Node && storageNode->NodeId == slot.NodeId ? storageNode : null;
        }
        var grid = Resolve(slot.Grid, epoch);
        if (grid == null || grid->ParentId != host->Id || slot.Index is < 0 or >= 35) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)grid, sizeof(AddonInventoryGrid));
        var dragDrop = ((AddonInventoryGrid*)grid)->Slots[slot.Index].Value;
        if (dragDrop == null) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)dragDrop, sizeof(AtkComponentDragDrop));
        var node = (AtkResNode*)dragDrop->OwnerNode;
        if (node == null || (nint)node != slot.Node) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)node, sizeof(AtkResNode));
        return node->NodeId == slot.NodeId ? node : null;
    }

    private static int Tab(AtkUnitBase* addon, string name)
    {
        var size = name switch
        {
            "Inventory" => sizeof(AddonInventory), "InventoryLarge" => sizeof(AddonInventoryLarge),
            "InventoryExpansion" => sizeof(AddonInventoryExpansion), "InventoryRetainer" => sizeof(AddonInventoryRetainer),
            "InventoryRetainerLarge" => sizeof(AddonInventoryRetainerLarge), _ => 0,
        };
        if (size == 0) return -1;
        NearbyZoneNativeBinding.RequireReadable((nint)addon, size);
        return name switch
        {
            "Inventory" => ((AddonInventory*)addon)->TabIndex, "InventoryLarge" => ((AddonInventoryLarge*)addon)->TabIndex,
            "InventoryExpansion" => ((AddonInventoryExpansion*)addon)->TabIndex,
            "InventoryRetainer" => ((AddonInventoryRetainer*)addon)->TabIndex,
            "InventoryRetainerLarge" => ((AddonInventoryRetainerLarge*)addon)->TabIndex, _ => -1,
        };
    }

    private static ItemOrderModuleSorter* Sorter(bool retainer)
    {
        var module = ItemOrderModule.Instance();
        if (module == null || !Plugin.PlayerState.IsLoaded) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)module, sizeof(ItemOrderModule));
        if (module->CharacterContentId == 0 || module->CharacterContentId != Plugin.PlayerState.ContentId) return null;
        if (retainer)
        {
            var retainers = RetainerManager.Instance();
            if (retainers == null || retainers->GetActiveRetainer() == null) return null;
        }
        var sorter = retainer ? module->GetActiveRetainerSorter() : module->InventorySorter;
        if (sorter == null) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)sorter, sizeof(ItemOrderModuleSorter));
        if (sorter->InventoryType != (retainer ? InventoryType.RetainerPage1 : InventoryType.Inventory1)
            || sorter->ItemsPerPage != (retainer ? 25 : 35) || sorter->SortFunctionIndex != -1) return null;
        var first = (nint)sorter->Items.First;
        var last = (nint)sorter->Items.Last;
        var end = (nint)sorter->Items.End;
        var maximum = retainer ? 175 : 140;
        if (first == 0 || last < first || end < last || (last - first) % IntPtr.Size != 0
            || (end - first) % IntPtr.Size != 0 || last - first > maximum * IntPtr.Size || end - first > 1024 * IntPtr.Size)
            return null;
        if (last > first) NearbyZoneNativeBinding.RequireReadable(first, checked((int)(last - first)));
        return sorter;
    }

    internal static List<InventorySearchMatch> CaptureMatches(InventorySearchHost host, Func<string, long> epoch,
        string query, Dictionary<uint, string> names, InventorySearchFilter filters = InventorySearchFilter.None)
    {
        var result = new List<InventorySearchMatch>();
        var addon = Resolve(host, epoch, true);
        if (addon == null) return result;
        if (InventorySearchStoragePolicy.IsSaddlebag(host.Name)) return CaptureSaddlebag(host, epoch, query, names, filters);
        if (host.Name == "FreeCompanyChest") return CaptureCompanyChest(host, epoch, query, names, filters);
        var tab = Tab(addon, host.Name);
        var isRetainer = host.Name.StartsWith("InventoryRetainer", StringComparison.Ordinal);
        var sorter = Sorter(isRetainer);
        if (sorter == null) return result;
        var sorterIdentity = SortIdentity(sorter);
        var manager = InventoryManager.Instance();
        if (manager == null) return result;
        foreach (var gridName in Grids)
        {
            var gridHost = CaptureHost(gridName, epoch(gridName));
            if (gridHost == null) continue;
            var grid = Resolve(gridHost.Value, epoch, true);
            if (grid == null || grid->ParentId != addon->Id) continue;
            NearbyZoneNativeBinding.RequireReadable((nint)grid, sizeof(AddonInventoryGrid));
            var slots = ((AddonInventoryGrid*)grid)->Slots;
            for (var index = 0; index < Math.Min(slots.Length, 35); index++)
            {
                if (!InventorySearchPolicy.TryMapSlot(host.Name, tab, gridName, index, out var page, out var slot)
                    || !InventorySearchPolicy.TryGetSorterIndex(page, slot, sorter->ItemsPerPage, sorter->Items.Count, out var sortedIndex)) continue;
                var dragDrop = slots[index].Value;
                if (dragDrop == null) continue;
                NearbyZoneNativeBinding.RequireReadable((nint)dragDrop, sizeof(AtkComponentDragDrop));
                var node = (AtkResNode*)dragDrop->OwnerNode;
                if (node == null) continue;
                NearbyZoneNativeBinding.RequireReadable((nint)node, sizeof(AtkResNode));
                var stamp = new InventorySearchSlot(host, gridHost.Value, index, (nint)node, node->NodeId);
                var entry = sorter->Items[sortedIndex].Value;
                if (entry == null) { result.Add(new(stamp, false)); continue; }
                NearbyZoneNativeBinding.RequireReadable((nint)entry, sizeof(ItemOrderModuleSorterItemEntry));
                if (entry->Page >= (isRetainer ? 7 : 4) || entry->Slot >= sorter->ItemsPerPage)
                { result.Add(new(stamp, false)); continue; }
                var type = (InventoryType)((uint)sorter->InventoryType + entry->Page);
                var container = manager->GetInventoryContainer(type);
                if (container == null) { result.Add(new(stamp, false)); continue; }
                NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
                if (!container->IsLoaded || container->Items == null || container->Size != sorter->ItemsPerPage
                    || entry->Slot >= container->Size) { result.Add(new(stamp, false)); continue; }
                var item = &container->Items[entry->Slot];
                result.Add(ItemMatch(stamp, item, (uint)type, entry->Slot, query, names, filters));
            }
        }
        var currentAddon = Resolve(host, epoch, true);
        var currentSorter = Sorter(isRetainer);
        if (currentAddon == null || Tab(currentAddon, host.Name) != tab || currentSorter == null
            || SortIdentity(currentSorter) != sorterIdentity) result.Clear();
        return result;
    }

    private static InventorySearchMatch ItemMatch(InventorySearchSlot stamp, InventoryItem* item, uint type, int slot,
        string query, Dictionary<uint, string> names, InventorySearchFilter filters)
    {
        NearbyZoneNativeBinding.RequireReadable((nint)item, sizeof(InventoryItem));
        if (item->IsSymbolic || item->Quantity <= 0 || (uint)item->Container != type || item->Slot != slot) return new(stamp, false);
        var id = item->GetBaseItemId();
        if (id == 0 || !Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var row)) return new(stamp, false);
        if (!names.TryGetValue(id, out var name))
        {
            name = row.Name.ExtractText();
            names[id] = name;
        }
        var traits = new InventorySearchItemTraits(row.IsUntradable, row.IsUnique,
            (item->Flags & InventoryItem.ItemFlags.Collectable) != 0, item->SpiritbondOrCollectability,
            item->GlamourId, (item->Flags & InventoryItem.ItemFlags.CompanyCrestApplied) != 0,
            row.ItemRepair.RowId != 0, item->Condition);
        var identity = new InventorySearchItemIdentity(type, slot, item->ItemId, item->Quantity,
            item->SpiritbondOrCollectability, item->Condition, (byte)item->Flags, item->GlamourId);
        return new(stamp, !InventorySearchFilterPolicy.Matches(name, id, query, filters, traits), identity);
    }

    private static AtkResNode* StorageNode(AtkUnitBase* addon, string name, int index)
    {
        uint expectedNode;
        if (InventorySearchStoragePolicy.IsSaddlebag(name))
        {
            if (!InventorySearchStoragePolicy.TryMapSaddlebagSlot(name, 0, index, out _, out _, out expectedNode)) return null;
            if (name == "InventoryBuddy")
            {
                NearbyZoneNativeBinding.RequireReadable((nint)addon, sizeof(AddonInventoryBuddy));
                var dragDrop = ((AddonInventoryBuddy*)addon)->Slots[index].Value;
                if (dragDrop == null) return null;
                NearbyZoneNativeBinding.RequireReadable((nint)dragDrop, sizeof(AtkComponentDragDrop));
                var owner = (AtkResNode*)dragDrop->OwnerNode;
                var node = ValidateDragDrop(owner);
                if (node == null || node->NodeId != expectedNode || ((AtkComponentNode*)node)->Component != (AtkComponentBase*)dragDrop) return null;
                return node;
            }
        }
        else if (name == "FreeCompanyChest")
        {
            if (!InventorySearchStoragePolicy.TryMapCompanyChestSlot(0, 0, false, index, out _, out _, out expectedNode)) return null;
        }
        else return null;
        // Buddy2 shares CriticalCommonLib's node map, but is not in the official typed addon attribute.
        var mapped = ValidateDragDrop(addon->GetNodeById(expectedNode));
        return mapped != null && mapped->NodeId == expectedNode ? mapped : null;
    }

    private static AtkResNode* ValidateDragDrop(AtkResNode* node)
    {
        if (node == null) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)node, sizeof(AtkResNode));
        if ((int)node->Type < 1000) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)node, sizeof(AtkComponentNode));
        var component = ((AtkComponentNode*)node)->Component;
        if (component == null) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)component, sizeof(AtkComponentBase));
        return component->GetComponentType() == ComponentType.DragDrop ? node : null;
    }

    private static int SaddlebagTab(AtkUnitBase* addon, string name)
    {
        if (name == "InventoryBuddy")
        {
            NearbyZoneNativeBinding.RequireReadable((nint)addon, sizeof(AddonInventoryBuddy));
            return ((AddonInventoryBuddy*)addon)->TabIndex;
        }
        if (name != "InventoryBuddy2") return -1;
        // CCL 34d364e, InventoryBuddyAddon.CurrentTab; Buddy2 inherits the same layout.
        NearbyZoneNativeBinding.RequireReadable((nint)addon, 0x4A1);
        return *((byte*)addon + 0x4A0);
    }

    private readonly record struct SorterIdentity(nint Address, nint First, nint Last, nint End, uint Type, int ItemsPerPage, int SortFunction);

    private static SorterIdentity SortIdentity(ItemOrderModuleSorter* sorter)
        => new((nint)sorter, (nint)sorter->Items.First, (nint)sorter->Items.Last, (nint)sorter->Items.End,
            (uint)sorter->InventoryType, sorter->ItemsPerPage, sorter->SortFunctionIndex);

    private readonly record struct SaddlebagIdentity(int Tab, ulong Owner, nint Player, nint Order, nint Manager,
        SorterIdentity Sorter, nint FirstBag, nint FirstItems, nint SecondBag, nint SecondItems);

    private static bool TryCaptureSaddlebagIdentity(InventorySearchHost host, Func<string, long> epoch, out SaddlebagIdentity identity)
    {
        identity = default;
        var addon = Resolve(host, epoch, true);
        if (addon == null) return false;
        var tab = SaddlebagTab(addon, host.Name);
        if (!InventorySearchStoragePolicy.TryMapSaddlebagSlot(host.Name, tab, 0, out var type, out _, out _)) return false;
        var player = NativePlayerState.Instance();
        var order = ItemOrderModule.Instance();
        var manager = InventoryManager.Instance();
        if (player == null || order == null || manager == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)player, sizeof(NativePlayerState));
        NearbyZoneNativeBinding.RequireReadable((nint)order, sizeof(ItemOrderModule));
        if (!player->IsLoaded || player->ContentId == 0 || order->CharacterContentId != player->ContentId
            || tab == 1 && !player->HasPremiumSaddlebag) return false;
        var sorter = tab == 0 ? order->SaddleBagSorter : order->PremiumSaddleBagSorter;
        if (sorter == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)sorter, sizeof(ItemOrderModuleSorter));
        if ((uint)sorter->InventoryType != type || sorter->ItemsPerPage != 35 || sorter->SortFunctionIndex != -1) return false;
        var sort = SortIdentity(sorter);
        if (sort.First == 0 || sort.Last < sort.First || sort.End < sort.Last
            || sort.Last - sort.First != 70 * IntPtr.Size || (sort.End - sort.First) % IntPtr.Size != 0
            || sort.End - sort.First > 1024 * IntPtr.Size) return false;
        NearbyZoneNativeBinding.RequireReadable(sort.First, 70 * IntPtr.Size);
        var firstBag = StorageContainer(manager, type, 35, tab == 0);
        var secondBag = StorageContainer(manager, type + 1, 35, tab == 0);
        if (firstBag == null || secondBag == null) return false;
        // Premium IsLoaded is unreliable. This exception requires current owner, entitlement,
        // selected visible addon, completed 70-slot sorter and both exact allocated buffers.
        identity = new(tab, player->ContentId, (nint)player, (nint)order, (nint)manager, sort,
            (nint)firstBag, (nint)firstBag->Items, (nint)secondBag, (nint)secondBag->Items);
        return true;
    }

    private static InventoryContainer* StorageContainer(InventoryManager* manager, uint type, int count, bool requireLoaded)
    {
        var container = manager->GetInventoryContainer((InventoryType)type);
        if (container == null) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)container, sizeof(InventoryContainer));
        if ((requireLoaded && !container->IsLoaded) || (uint)container->Type != type || container->Items == null || container->Size != count) return null;
        NearbyZoneNativeBinding.RequireReadable((nint)container->Items, checked(count * sizeof(InventoryItem)));
        return container;
    }

    private static List<InventorySearchMatch> CaptureSaddlebag(InventorySearchHost host, Func<string, long> epoch,
        string query, Dictionary<uint, string> names, InventorySearchFilter filters)
    {
        var result = new List<InventorySearchMatch>();
        if (!TryCaptureSaddlebagIdentity(host, epoch, out var identity)) return result;
        var addon = (AtkUnitBase*)host.Address;
        var sorter = (ItemOrderModuleSorter*)identity.Sorter.Address;
        for (var index = 0; index < 70; index++)
        {
            if (!InventorySearchStoragePolicy.TryMapSaddlebagSlot(host.Name, identity.Tab, index, out var type, out var sortedIndex, out _)) continue;
            var node = StorageNode(addon, host.Name, index);
            if (node == null) continue;
            var stamp = new InventorySearchSlot(host, host, index, (nint)node, node->NodeId);
            var entry = sorter->Items[sortedIndex].Value;
            if (entry == null) { result.Add(new(stamp, false)); continue; }
            NearbyZoneNativeBinding.RequireReadable((nint)entry, sizeof(ItemOrderModuleSorterItemEntry));
            if (entry->Page >= 2 || entry->Slot >= 35) { result.Add(new(stamp, false)); continue; }
            var items = (InventoryItem*)(entry->Page == 0 ? identity.FirstItems : identity.SecondItems);
            result.Add(ItemMatch(stamp, &items[entry->Slot], type + entry->Page, entry->Slot, query, names, filters));
        }
        if (!TryCaptureSaddlebagIdentity(host, epoch, out var current) || current != identity) result.Clear();
        return result;
    }

    private readonly record struct CompanyChestIdentity(uint Page, nint Manager, nint Container, nint Items);

    private static bool TryCaptureCompanyChestIdentity(InventorySearchHost host, Func<string, long> epoch, out CompanyChestIdentity identity)
    {
        identity = default;
        var addon = Resolve(host, epoch, true);
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 3) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)addon->AtkValues, 3 * sizeof(AtkValue));
        if (!NativeArrayAccess.TryGetAtkUInt(addon, 1, out var nonItemTab)
            || !NativeArrayAccess.TryGetAtkUInt(addon, 2, out var page)) return false;
        var blockedNode = addon->GetNodeById(106);
        if (blockedNode != null) NearbyZoneNativeBinding.RequireReadable((nint)blockedNode, sizeof(AtkResNode));
        if (!InventorySearchStoragePolicy.TryMapCompanyChestSlot(nonItemTab, page,
                blockedNode != null && blockedNode->IsVisible(), 0, out var type, out _, out _)) return false;
        var manager = InventoryManager.Instance();
        if (manager == null) return false;
        var container = StorageContainer(manager, type, 50, true);
        if (container == null) return false;
        identity = new(page, (nint)manager, (nint)container, (nint)container->Items);
        return true;
    }

    private static List<InventorySearchMatch> CaptureCompanyChest(InventorySearchHost host, Func<string, long> epoch,
        string query, Dictionary<uint, string> names, InventorySearchFilter filters)
    {
        var result = new List<InventorySearchMatch>();
        if (!TryCaptureCompanyChestIdentity(host, epoch, out var identity)) return result;
        var addon = (AtkUnitBase*)host.Address;
        var items = (InventoryItem*)identity.Items;
        for (var index = 0; index < 50; index++)
        {
            if (!InventorySearchStoragePolicy.TryMapCompanyChestSlot(0, identity.Page, false, index, out var type, out var slot, out _)) continue;
            var node = StorageNode(addon, host.Name, index);
            if (node == null) continue;
            var stamp = new InventorySearchSlot(host, host, index, (nint)node, node->NodeId);
            result.Add(ItemMatch(stamp, &items[slot], type, slot, query, names, filters));
        }
        if (!TryCaptureCompanyChestIdentity(host, epoch, out var current) || current != identity) result.Clear();
        return result;
    }
}
