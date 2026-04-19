using System;
using Dalamud.Game.Player;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class ItemCommandsService : IDisposable
{
    private const ushort EquippedSoulCrystalSlot = 13;

    private static readonly InventoryType[] EquipSourceContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    };

    private readonly record struct InventoryLocation(InventoryType Container, ushort Slot);

    private readonly IDataManager dataManager;
    private readonly IPlayerState playerState;

    private bool enabled;

    public ItemCommandsService(
        IDataManager dataManager,
        IPlayerState playerState)
    {
        this.dataManager = dataManager;
        this.playerState = playerState;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            RefreshStatusText();
            return enabled;
        }

        if (!value)
        {
            enabled = false;
            RefreshStatusText();
            return false;
        }

        enabled = true;
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        RefreshStatusText();
    }

    public bool TryExecuteEquipCommand(string arguments, out string message)
    {
        if (!enabled)
        {
            message = "Item Commands is disabled. Enable it first with /xa itemcommands on.";
            return false;
        }

        if (!uint.TryParse(arguments.Trim(), out var itemId))
        {
            message = "Usage: /xa equip <itemId>.";
            return false;
        }

        return TryEquipItem(itemId, out message);
    }

    private bool TryEquipItem(uint itemId, out string message)
    {
        if (!playerState.IsLoaded || !playerState.ClassJob.IsValid)
        {
            message = "Local player state is not ready.";
            return false;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var itemRow))
        {
            message = $"Item {itemId} was not found in the local item sheet.";
            return false;
        }

        if (itemRow.EquipSlotCategory.RowId == 0)
        {
            message = $"Item {itemId} is not equippable.";
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            message = "Inventory manager is not available.";
            return false;
        }

        if (!TryFindEquippableItemLocation(inventoryManager, itemId, out var sourceLocation))
        {
            message = $"Item {itemId} was not found in the main inventory or armory chest.";
            return false;
        }

        if (!CanEquip(itemId))
        {
            message = $"Item {itemId} cannot be equipped by the current character or class/job.";
            return false;
        }

        if (!TryGetDestinationSlot(itemRow, inventoryManager, sourceLocation.Container, out var destinationSlot, out var destinationLabel))
        {
            message = $"Item {itemId} uses an unsupported equip slot.";
            return false;
        }

        inventoryManager->MoveItemSlot(
            sourceLocation.Container,
            sourceLocation.Slot,
            InventoryType.EquippedItems,
            destinationSlot,
            true);

        message = $"Queued item {itemId} for equip to {destinationLabel}.";
        return true;
    }

    private bool TryFindEquippableItemLocation(InventoryManager* inventoryManager, uint itemId, out InventoryLocation location)
    {
        foreach (var containerType in EquipSourceContainers)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = inventoryManager->GetInventorySlot(containerType, slotIndex);
                item = ResolveInventoryItem(item);
                if (item == null || item->ItemId == 0 || item->IsCollectable())
                    continue;

                if (item->GetBaseItemId() != itemId)
                    continue;

                location = new InventoryLocation(containerType, (ushort)slotIndex);
                return true;
            }
        }

        location = default;
        return false;
    }

    private bool CanEquip(uint itemId)
    {
        var playerStateStruct = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (playerStateStruct == null)
            return false;

        var sex = playerState.Sex == Sex.Female ? (byte)1 : (byte)0;
        var level = (ushort)Math.Max(playerState.EffectiveLevel, playerState.Level);
        return InventoryManager.CanEquip(
                   itemId,
                   (byte)playerState.Race.RowId,
                   sex,
                   level,
                   (byte)playerState.ClassJob.RowId,
                   playerStateStruct->GrandCompany,
                   0,
                   null) == 0;
    }

    private static bool TryGetDestinationSlot(
        Item itemRow,
        InventoryManager* inventoryManager,
        InventoryType sourceContainer,
        out ushort destinationSlot,
        out string destinationLabel)
    {
        var slotCategory = itemRow.EquipSlotCategory.ValueNullable;
        if (slotCategory == null)
        {
            destinationSlot = 0;
            destinationLabel = string.Empty;
            return false;
        }

        if (slotCategory.Value.MainHand == 1)
            return SetDestination(0, "Main Hand", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.OffHand == 1)
            return SetDestination(1, "Off Hand", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Head == 1)
            return SetDestination(2, "Head", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Body == 1)
            return SetDestination(3, "Body", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Gloves == 1)
            return SetDestination(4, "Hands", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Legs == 1)
            return SetDestination(6, "Legs", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Feet == 1)
            return SetDestination(7, "Feet", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Ears == 1)
            return SetDestination(8, "Earrings", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Neck == 1)
            return SetDestination(9, "Neck", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.Wrists == 1)
            return SetDestination(10, "Wrists", out destinationSlot, out destinationLabel);

        if (slotCategory.Value.FingerR == 1 || slotCategory.Value.FingerL == 1)
        {
            var rightRing = inventoryManager->GetInventorySlot(InventoryType.EquippedItems, 11);
            var leftRing = inventoryManager->GetInventorySlot(InventoryType.EquippedItems, 12);
            var rightEmpty = rightRing == null || rightRing->ItemId == 0;
            var leftEmpty = leftRing == null || leftRing->ItemId == 0;

            if (rightEmpty)
                return SetDestination(11, "Right Ring", out destinationSlot, out destinationLabel);

            if (leftEmpty)
                return SetDestination(12, "Left Ring", out destinationSlot, out destinationLabel);

            if (slotCategory.Value.FingerR == 1)
                return SetDestination(11, "Right Ring", out destinationSlot, out destinationLabel);

            return SetDestination(12, "Left Ring", out destinationSlot, out destinationLabel);
        }

        if (sourceContainer == InventoryType.ArmorySoulCrystal)
            return SetDestination(EquippedSoulCrystalSlot, "Soul Crystal", out destinationSlot, out destinationLabel);

        destinationSlot = 0;
        destinationLabel = string.Empty;
        return false;
    }

    private static InventoryItem* ResolveInventoryItem(InventoryItem* item)
    {
        if (item == null)
            return null;

        var resolved = item;
        while (resolved->IsSymbolic)
        {
            var linked = resolved->GetLinkedItem();
            if (linked == null || linked == resolved)
                break;

            resolved = linked;
        }

        return resolved;
    }

    private static bool SetDestination(ushort slot, string label, out ushort destinationSlot, out string destinationLabel)
    {
        destinationSlot = slot;
        destinationLabel = label;
        return true;
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        StatusText = "Enabled - /xa equip <itemId> is available.";
    }
}
