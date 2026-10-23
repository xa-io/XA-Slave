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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

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

    public bool TryExecuteEquipCommand(string arguments, out string message) => TryExecuteEquipCommandLocalizedCore(out _, arguments, out message);
    internal bool TryExecuteEquipCommandMessage(string arguments, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryExecuteEquipCommandLocalizedCore(out var presentation, arguments, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryExecuteEquipCommandLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string arguments, out string message)
    {
        replyPresentation = null;
        if (!enabled)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S0B6F506C8432A611"));
            return false;
        }

        if (!uint.TryParse(arguments.Trim(), out var itemId))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SF4032C09EE53EA17"));
            return false;
        }

        return TryEquipItemLocalizedCore(out replyPresentation, itemId, out message);
    }

    private bool TryEquipItem(uint itemId, out string message) => TryEquipItemLocalizedCore(out _, itemId, out message);
    private bool TryEquipItemMessage(uint itemId, out global::XASlave.Localization.UiMessage message)
    {
        var success = TryEquipItemLocalizedCore(out var presentation, itemId, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryEquipItemLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, uint itemId, out string message)
    {
        replyPresentation = null;
        if (!playerState.IsLoaded || !playerState.ClassJob.IsValid)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S6B38309E395E5688"));
            return false;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var itemRow))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S5BF1396D85DAD226", itemId));
            return false;
        }

        if (itemRow.EquipSlotCategory.RowId == 0)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S8FDC09B28F6A2E75", itemId));
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S6F9411C459B1B8ED"));
            return false;
        }

        if (!TryFindEquippableItemLocation(inventoryManager, itemId, out var sourceLocation))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S968367142E431157", itemId));
            return false;
        }

        if (!CanEquip(itemId))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S5DB3411D0E537413", itemId));
            return false;
        }

        if (!TryGetDestinationSlot(itemRow, inventoryManager, sourceLocation.Container, out var destinationSlot, out var destinationLabel))
        {
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("SF8E05B17CCA6F21B", itemId));
            return false;
        }

        inventoryManager->MoveItemSlot(
            sourceLocation.Container,
            sourceLocation.Slot,
            InventoryType.EquippedItems,
            destinationSlot,
            true);

        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S51469852549E82CB", itemId, destinationLabel));
        return true;
    }

    private bool TryFindEquippableItemLocation(InventoryManager* inventoryManager, uint itemId, out InventoryLocation location)
    {
        foreach (var containerType in EquipSourceContainers)
        {
            if (!NativeArrayAccess.TryGetInventoryContainer(inventoryManager, containerType, out var container))
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                if (!NativeArrayAccess.TryGetInventorySlot(container, slotIndex, out var item))
                    continue;

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
            var hasRightRing = NativeArrayAccess.TryGetInventorySlot(inventoryManager, InventoryType.EquippedItems, 11, out var rightRing);
            var hasLeftRing = NativeArrayAccess.TryGetInventorySlot(inventoryManager, InventoryType.EquippedItems, 12, out var leftRing);
            var rightEmpty = !hasRightRing || rightRing->ItemId == 0;
            var leftEmpty = !hasLeftRing || leftRing->ItemId == 0;

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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S0411B728D7F6F273"));
    }
}
