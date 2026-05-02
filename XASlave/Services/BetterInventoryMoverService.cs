using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class BetterInventoryMoverService : IDisposable
{
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;
    private const int VirtualKeyLeftAlt = 0xA4;
    private const int VirtualKeyRightAlt = 0xA5;

    private static readonly HashSet<string> PlayerAddonNames =
    [
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
    ];

    private static readonly HashSet<string> RetainerAddonNames =
    [
        "InventoryRetainer",
        "InventoryRetainerLarge",
    ];

    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] SaddlebagInventories =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
    ];

    private static readonly InventoryType[] PremiumSaddlebagInventories =
    [
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] RetainerInventories =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private readonly IContextMenu contextMenu;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private bool enabled;
    private bool subscribed;
    private BetterInventoryMoverModifierKey quickMoveModifier = BetterInventoryMoverModifierKey.LeftShift;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public BetterInventoryMoverService(IContextMenu contextMenu, IDataManager dataManager, IPluginLog log)
    {
        this.contextMenu = contextMenu;
        this.dataManager = dataManager;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public string LastSourceAddon { get; private set; } = "None";
    public string LastDestinationLabel { get; private set; } = "None";
    public uint LastItemId { get; private set; }
    public int AvailableDestinationCount { get; private set; }
    public BetterInventoryMoverModifierKey QuickMoveModifier => quickMoveModifier;
    public string QuickMoveModifierLabel => GetModifierLabel(quickMoveModifier);

    public void ApplyConfiguration(BetterInventoryMoverModifierKey quickMoveModifier)
    {
        this.quickMoveModifier = NormalizeModifier(quickMoveModifier);
        if (enabled)
            StatusText = BuildStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        StatusText = BuildStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        contextMenu.OnMenuOpened += OnMenuOpened;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        contextMenu.OnMenuOpened -= OnMenuOpened;
        subscribed = false;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!enabled ||
            args is not
            {
                MenuType: ContextMenuType.Inventory,
                Target: MenuTargetInventory { TargetItem: { } item },
                AddonName: { } addonName
            })
        {
            return;
        }

        if (item.IsEmpty || item.ItemId == 0)
            return;

        try
        {
            LastSourceAddon = addonName;
            LastItemId = item.ItemId;

            var destinations = ResolveDestinations(addonName, item);
            AvailableDestinationCount = destinations.Count;
            if (destinations.Count > 0 && IsQuickMoveModifierHeld())
            {
                ExecuteMove(item, destinations[0]);
                return;
            }

            foreach (var destination in destinations)
            {
                var sourceItem = item;
                var shuttleDestination = destination;
                args.AddMenuItem(new MenuItem
                {
                    Name = new SeStringBuilder().AddText(shuttleDestination.MenuLabel).Build(),
                    UseDefaultPrefix = true,
                    Priority = 1,
                    OnClicked = _ => ExecuteMove(sourceItem, shuttleDestination),
                });
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Inventory Mover failed while preparing inventory context menu entries.");
        }
    }

    private string BuildStatusText()
        => $"Enabled - hold {QuickMoveModifierLabel} while right-clicking an item to move it to the first available open destination, or use the added context-menu move actions.";

    private bool IsQuickMoveModifierHeld()
        => (GetAsyncKeyState(GetVirtualKey(quickMoveModifier)) & 0x8000) != 0;

    public static BetterInventoryMoverModifierKey NormalizeModifier(BetterInventoryMoverModifierKey modifier)
        => Enum.IsDefined(modifier) ? modifier : BetterInventoryMoverModifierKey.LeftShift;

    public static string GetModifierLabel(BetterInventoryMoverModifierKey modifier)
    {
        return NormalizeModifier(modifier) switch
        {
            BetterInventoryMoverModifierKey.LeftControl => "Left Ctrl",
            BetterInventoryMoverModifierKey.LeftAlt => "Left Alt",
            BetterInventoryMoverModifierKey.RightShift => "Right Shift",
            BetterInventoryMoverModifierKey.RightControl => "Right Ctrl",
            BetterInventoryMoverModifierKey.RightAlt => "Right Alt",
            _ => "Left Shift",
        };
    }

    private static int GetVirtualKey(BetterInventoryMoverModifierKey modifier)
    {
        return NormalizeModifier(modifier) switch
        {
            BetterInventoryMoverModifierKey.LeftControl => VirtualKeyLeftControl,
            BetterInventoryMoverModifierKey.LeftAlt => VirtualKeyLeftAlt,
            BetterInventoryMoverModifierKey.RightShift => VirtualKeyRightShift,
            BetterInventoryMoverModifierKey.RightControl => VirtualKeyRightControl,
            BetterInventoryMoverModifierKey.RightAlt => VirtualKeyRightAlt,
            _ => VirtualKeyLeftShift,
        };
    }

    private List<ShuttleDestination> ResolveDestinations(string addonName, GameInventoryItem item)
    {
        var destinations = new List<ShuttleDestination>(3);

        if (PlayerAddonNames.Contains(addonName))
        {
            TryAddDestination("InventoryBuddy", item, "Move To Saddlebag", SaddlebagInventories, destinations);
            TryAddDestination("InventoryBuddy2", item, "Move To Premium Saddlebag", PremiumSaddlebagInventories, destinations);
            TryAddDestination("InventoryRetainer", item, "Move To Retainer", RetainerInventories, destinations);
            TryAddDestination("InventoryRetainerLarge", item, "Move To Retainer", RetainerInventories, destinations, requireDistinctLabel: false);
        }
        else if (addonName.Equals("InventoryBuddy", StringComparison.Ordinal))
        {
            TryAddDestination("Inventory", item, "Move To Inventory", PlayerInventories, destinations);
            TryAddDestination("InventoryLarge", item, "Move To Inventory", PlayerInventories, destinations, requireDistinctLabel: false);
            TryAddDestination("InventoryExpansion", item, "Move To Inventory", PlayerInventories, destinations, requireDistinctLabel: false);
        }
        else if (addonName.Equals("InventoryBuddy2", StringComparison.Ordinal))
        {
            TryAddDestination("Inventory", item, "Move To Inventory", PlayerInventories, destinations);
            TryAddDestination("InventoryLarge", item, "Move To Inventory", PlayerInventories, destinations, requireDistinctLabel: false);
            TryAddDestination("InventoryExpansion", item, "Move To Inventory", PlayerInventories, destinations, requireDistinctLabel: false);
        }
        else if (RetainerAddonNames.Contains(addonName))
        {
            TryAddDestination("Inventory", item, "Move To Inventory", PlayerInventories, destinations);
            TryAddDestination("InventoryLarge", item, "Move To Inventory", PlayerInventories, destinations, requireDistinctLabel: false);
            TryAddDestination("InventoryExpansion", item, "Move To Inventory", PlayerInventories, destinations, requireDistinctLabel: false);
        }

        return destinations;
    }

    private void TryAddDestination(
        string requiredAddonName,
        GameInventoryItem item,
        string menuLabel,
        InventoryType[] destinationInventories,
        List<ShuttleDestination> destinations,
        bool requireDistinctLabel = true)
    {
        if (!AddonHelper.IsAddonVisible(requiredAddonName))
            return;

        if (requireDistinctLabel && destinations.Exists(destination => destination.MenuLabel.Equals(menuLabel, StringComparison.Ordinal)))
            return;

        if (!TryFindTargetSlot(destinationInventories, item, out var targetSlot))
            return;

        destinations.Add(new ShuttleDestination(menuLabel, targetSlot.InventoryType, targetSlot.Slot));
    }

    private bool TryFindTargetSlot(InventoryType[] destinationInventories, GameInventoryItem item, out TargetSlot targetSlot)
    {
        targetSlot = default;
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(item.BaseItemId, out var itemData))
            return false;

        foreach (var inventoryType in destinationInventories)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || !IsSameItem(slot, item))
                    continue;

                if (slot->Quantity < itemData.StackSize)
                {
                    targetSlot = new TargetSlot(inventoryType, (ushort)slot->Slot);
                    return true;
                }
            }
        }

        foreach (var inventoryType in destinationInventories)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || slot->GetItemId() != 0)
                    continue;

                targetSlot = new TargetSlot(inventoryType, (ushort)slot->Slot);
                return true;
            }
        }

        return false;
    }

    private void ExecuteMove(GameInventoryItem sourceItem, ShuttleDestination destination)
    {
        try
        {
            if (!TryFindTargetSlot([destination.InventoryType], sourceItem, out var refreshedTarget))
            {
                LastActionText = $"Last action: no destination slot was available for item {sourceItem.ItemId} at {DateTime.Now:HH:mm:ss}.";
                return;
            }

            var manager = InventoryManager.Instance();
            if (manager == null)
                return;

            var sourceInventory = (InventoryType)sourceItem.ContainerType;
            var sourceSlot = (ushort)sourceItem.InventorySlot;
            if (TryGetInventorySource(out var liveSourceInventory, out var liveSourceSlot))
            {
                sourceInventory = liveSourceInventory;
                sourceSlot = liveSourceSlot;
            }

            manager->MoveItemSlot(sourceInventory, sourceSlot, refreshedTarget.InventoryType, refreshedTarget.Slot, true);
            AddonHelper.CloseAddon("ContextMenu");
            LastDestinationLabel = destination.MenuLabel;
            LastItemId = sourceItem.ItemId;
            LastActionText = $"Last action: moved item {sourceItem.ItemId} from {sourceInventory} to {destination.MenuLabel} at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Inventory Mover failed while moving an item.");
        }
    }

    private static bool TryGetInventorySource(out InventoryType sourceInventory, out ushort sourceSlot)
    {
        sourceInventory = InventoryType.Invalid;
        sourceSlot = 0;

        var agent = AgentInventoryContext.Instance();
        if (agent == null || agent->TargetInventorySlot == null || agent->TargetInventorySlot->ItemId == 0)
            return false;

        sourceInventory = agent->TargetInventoryId;
        sourceSlot = (ushort)agent->TargetInventorySlotId;
        return sourceInventory != InventoryType.Invalid;
    }

    private static bool IsSameItem(InventoryItem* slot, GameInventoryItem item)
    {
        if (slot == null || slot->GetItemId() == 0)
            return false;

        var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var isCollectable = slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
        return slot->GetBaseItemId() == item.BaseItemId && isHq == item.IsHq && isCollectable == item.IsCollectable;
    }

    private readonly record struct ShuttleDestination(string MenuLabel, InventoryType InventoryType, ushort Slot);

    private readonly record struct TargetSlot(InventoryType InventoryType, ushort Slot);
}

public enum BetterInventoryMoverModifierKey
{
    LeftShift = 0,
    LeftControl = 1,
    LeftAlt = 2,
    RightShift = 3,
    RightControl = 4,
    RightAlt = 5,
}
