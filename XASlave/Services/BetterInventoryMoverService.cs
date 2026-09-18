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
    private bool disposed;
    private long generation;
    private InventoryShuttleContext? access;
    private string preparingSourceAddon = string.Empty;
    private InventoryShuttleNativeBinding? mergeBinding;
    private bool mergeBindingUnavailable;
    private InventoryShuttleSequence? transfer;
    private long transferGeneration;
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
    public string OccupiedStackStatus { get; private set; } = "Occupied-stack binding has not been inspected.";

    public void ApplyConfiguration(BetterInventoryMoverModifierKey quickMoveModifier)
    {
        CancelTransfer("Configuration changed."); generation++;
        this.quickMoveModifier = NormalizeModifier(quickMoveModifier);
        if (enabled)
            StatusText = BuildStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (value == enabled)
            return enabled;

        CancelTransfer("Inventory mover enable state changed."); generation++;
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
        if (disposed) return;
        CancelTransfer("Inventory mover disposed."); disposed = true; generation++;
        enabled = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        access = new InventoryShuttleContext(Plugin.AddonLifecycle, () => { CancelTransfer("Inventory access was revoked."); generation++; });
        contextMenu.OnMenuOpened += OnMenuOpened;
        Plugin.Framework.Update += UpdateTransfer;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        contextMenu.OnMenuOpened -= OnMenuOpened;
        Plugin.Framework.Update -= UpdateTransfer;
        access?.Dispose(); access = null;
        subscribed = false;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        CancelTransfer("A new context menu revoked the previous transfer.");
        var menuGeneration = ++generation;
        access?.Reset();
        if (disposed || !enabled ||
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
            preparingSourceAddon = addonName;
            LastSourceAddon = addonName;
            LastItemId = item.ItemId;

            if (!TryCaptureSource(addonName, item, out var source)) return;
            var destinations = ResolveDestinations(addonName, source);
            AvailableDestinationCount = destinations.Count;
            if (destinations.Count > 0 && IsQuickMoveModifierHeld())
            {
                ExecuteMove(source, addonName, menuGeneration, destinations[0]);
                return;
            }

            foreach (var destination in destinations)
            {
                var sourceItem = source;
                var shuttleDestination = destination;
                args.AddMenuItem(new MenuItem
                {
                    Name = new SeStringBuilder().AddText(shuttleDestination.MenuLabel).Build(),
                    UseDefaultPrefix = true,
                    Priority = 1,
                    OnClicked = _ => ExecuteMove(sourceItem, addonName, menuGeneration, shuttleDestination),
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

    private List<ShuttleDestination> ResolveDestinations(string addonName, InventoryShuttleSource item)
    {
        var destinations = new List<ShuttleDestination>(3);

        if (PlayerAddonNames.Contains(addonName))
        {
            TryAddDestination("InventoryBuddy", item, SaddlebagInventories, destinations);
            TryAddDestination("InventoryBuddy2", item, PremiumSaddlebagInventories, destinations);
            TryAddDestination("InventoryRetainer", item, RetainerInventories, destinations);
            TryAddDestination("InventoryRetainerLarge", item, RetainerInventories, destinations);
        }
        else if (addonName.Equals("InventoryBuddy", StringComparison.Ordinal))
        {
            TryAddDestination("Inventory", item, PlayerInventories, destinations);
            TryAddDestination("InventoryLarge", item, PlayerInventories, destinations);
            TryAddDestination("InventoryExpansion", item, PlayerInventories, destinations);
        }
        else if (addonName.Equals("InventoryBuddy2", StringComparison.Ordinal))
        {
            TryAddDestination("Inventory", item, PlayerInventories, destinations);
            TryAddDestination("InventoryLarge", item, PlayerInventories, destinations);
            TryAddDestination("InventoryExpansion", item, PlayerInventories, destinations);
        }
        else if (RetainerAddonNames.Contains(addonName))
        {
            TryAddDestination("Inventory", item, PlayerInventories, destinations);
            TryAddDestination("InventoryLarge", item, PlayerInventories, destinations);
            TryAddDestination("InventoryExpansion", item, PlayerInventories, destinations);
        }

        return destinations;
    }

    private void TryAddDestination(
        string requiredAddonName,
        InventoryShuttleSource item,
        InventoryType[] destinationInventories,
        List<ShuttleDestination> destinations)
    {
        if (!AddonHelper.IsAddonVisible(requiredAddonName))
            return;
        if (access == null || !access.IsReady(requiredAddonName) || !access.IsReady(preparingSourceAddon))
            return;

        var family = Family(destinationInventories);
        if (destinations.Exists(destination => destination.Family == family))
            return;

        if (!TryFindTargetSlot(destinationInventories, item, out var targetSlot))
            return;

        var context = access!.Capture(preparingSourceAddon, requiredAddonName, family == InventoryShuttleFamily.Retainer || RetainerAddonNames.Contains(preparingSourceAddon));
        destinations.Add(new ShuttleDestination(family, InventoryShuttleLabels.Destination(family, Plugin.ClientState.ClientLanguage), destinationInventories, context));
    }

    private bool TryFindTargetSlot(InventoryType[] destinationInventories, InventoryShuttleSource item, out InventoryShuttleTarget targetSlot)
    {
        targetSlot = default;
        var manager = InventoryManager.Instance();
        if (manager == null || !dataManager.GetExcelSheet<Item>().TryGetRow(item.State.BaseId, out var data)) return false;
        var capacity = checked((int)data.StackSize);
        if (capacity < 1 || item.Quantity < 1 || item.Quantity > capacity) return false;
        var ordinary = capacity > 1 && !item.State.Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
        InventoryShuttleTarget? empty = null, partialTarget = null;
        foreach (var inventory in destinationInventories)
        {
            if (!NativeArrayAccess.TryGetInventoryContainer(manager, inventory, out var container)) continue;
            for (var index = 0; index < container->Size && index <= ushort.MaxValue; index++)
            {
                if (inventory == item.Inventory && index == item.Slot) continue;
                if (!NativeArrayAccess.TryGetInventorySlot(container, index, out var slot) || slot->IsSymbolic) continue;
                if (slot->GetItemId() == 0)
                {
                    empty ??= new InventoryShuttleTarget(new(inventory, (ushort)index, 0, default), capacity);
                    continue;
                }
                if (!ordinary || slot->Quantity <= 0 || slot->Quantity >= capacity || !IsSameItem(slot, item)) continue;
                var candidate = new InventoryShuttleTarget(new(inventory, (ushort)index, slot->Quantity, InventoryShuttleItemState.Read(slot)), capacity);
                if (capacity - slot->Quantity >= item.Quantity) { targetSlot = candidate; return true; }
                partialTarget ??= candidate;
            }
        }
        if (empty is { } vacant) { targetSlot = vacant; return true; }
        if (partialTarget is { } merge) { targetSlot = merge; return true; }
        return false;
    }

    private void ExecuteMove(InventoryShuttleSource sourceItem, string sourceAddon, long menuGeneration, ShuttleDestination destination)
    {
        if (disposed || !enabled || menuGeneration != generation || (transfer != null && !transfer.Terminal)) return;
        try
        {
            Plugin.AssertGameThread(); access!.Require(destination.Access);
            if (!AddonHelper.IsAddonVisible(sourceAddon) || !DestinationOpen(destination.Family)
                || !TryGetInventorySource(out var currentInventory, out var currentSlot)
                || currentInventory != sourceItem.Inventory || currentSlot != sourceItem.Slot)
                throw new InvalidOperationException("The captured inventory context changed.");
            if (ReadSlot(sourceItem.Inventory, sourceItem.Slot) != sourceItem) throw new InvalidOperationException("The captured source item changed.");
            transferGeneration = ++generation;
            transfer = new(new TransferAdapter(this, destination), sourceItem, Environment.TickCount64);
            LastDestinationLabel = destination.MenuLabel; LastItemId = sourceItem.State.FullId;
            PublishTransfer();
            // The first framework update follows menu setup, allowing an exact close stamp.
        }
        catch (Exception error) { LastActionText = "Failed: " + error.Message; log.Warning(error, "Inventory transfer admission refused."); }
    }

    private static InventoryShuttleSource ReadSlot(InventoryType inventory, ushort index)
    {
        if (!NativeArrayAccess.TryGetInventorySlot(InventoryManager.Instance(), inventory, index, out var slot) || slot->IsSymbolic)
            throw new InvalidOperationException("The required inventory slot is unavailable.");
        return slot->GetItemId() == 0 ? new(inventory, index, 0, default)
            : new(inventory, index, slot->Quantity, InventoryShuttleItemState.Read(slot));
    }

    private void UpdateTransfer(IFramework framework)
    {
        if (disposed || !enabled || transfer == null) return;
        if (transferGeneration != generation) transfer.Cancel("Transfer generation was revoked.");
        else transfer.Tick(Environment.TickCount64);
        PublishTransfer();
    }

    private void CancelTransfer(string reason)
    {
        if (transfer == null) return;
        transfer.Cancel(reason); PublishTransfer(); transfer = null;
    }

    private void PublishTransfer()
    {
        if (transfer == null) return;
        LastActionText = $"{transfer.Outcome}: {transfer.Moved} observed locally, {transfer.Remaining} remaining. {transfer.Detail}";
        if (transfer.NativeCode is { } code) LastActionText += $" Native code: {code}.";
    }

    private sealed class TransferAdapter(BetterInventoryMoverService owner, ShuttleDestination destination) : IInventoryShuttleAdapter
    {
        private bool menuBound;
        public void Validate()
        {
            if (owner.disposed || !owner.enabled || owner.transferGeneration != owner.generation)
                throw new InvalidOperationException("Inventory transfer ownership ended.");
            owner.access!.Require(destination.Access);
            if (!menuBound) { owner.access.BindMenu(); menuBound = true; }
        }
        public InventoryShuttleSource Read(InventoryType inventory, ushort slot) => ReadSlot(inventory, slot);
        public InventoryShuttleTarget? Find(InventoryShuttleSource source)
            => owner.TryFindTargetSlot(destination.Inventories, source, out var candidate) ? candidate : null;
        public int Submit(InventoryShuttleSource source, InventoryShuttleTarget target)
        {
            Validate();
            if (Read(source.Inventory, source.Slot) != source || Read(target.Before.Inventory, target.Before.Slot) != target.Before)
                throw new InvalidOperationException("Inventory changed before native entry.");
            var manager = InventoryManager.Instance();
            if (!owner.dataManager.GetExcelSheet<Item>().TryGetRow(source.State.BaseId, out var data) || data.StackSize != target.Capacity)
                throw new InvalidOperationException("Item stack capacity changed.");
            if (target.Before.Quantity > 0)
            {
                if (target.Capacity <= 1 || source.Quantity > target.Capacity || target.Before.Quantity >= target.Capacity
                    || !NativeArrayAccess.TryGetInventorySlot(manager, target.Before.Inventory, target.Before.Slot, out var slot) || !owner.IsSameItem(slot, source))
                    throw new InvalidOperationException("Occupied destination no longer permits an equivalent-state transfer.");
            }
            Validate();
            return manager->MoveItemSlot(source.Inventory, source.Slot, target.Before.Inventory, target.Before.Slot, true);
        }
        public void CloseOwnedMenu() { Validate(); owner.access!.CloseOwnedMenu(); }
    }

    private static InventoryShuttleFamily Family(InventoryType[] inventories)
        => ReferenceEquals(inventories, SaddlebagInventories) ? InventoryShuttleFamily.Saddlebag
            : ReferenceEquals(inventories, PremiumSaddlebagInventories) ? InventoryShuttleFamily.PremiumSaddlebag
            : ReferenceEquals(inventories, RetainerInventories) ? InventoryShuttleFamily.Retainer : InventoryShuttleFamily.Player;

    private static bool DestinationOpen(InventoryShuttleFamily family) => family switch
    {
        InventoryShuttleFamily.Saddlebag => AddonHelper.IsAddonVisible("InventoryBuddy"),
        InventoryShuttleFamily.PremiumSaddlebag => AddonHelper.IsAddonVisible("InventoryBuddy2"),
        InventoryShuttleFamily.Retainer => AddonHelper.IsAddonVisible("InventoryRetainer") || AddonHelper.IsAddonVisible("InventoryRetainerLarge"),
        _ => AddonHelper.IsAddonVisible("Inventory") || AddonHelper.IsAddonVisible("InventoryLarge") || AddonHelper.IsAddonVisible("InventoryExpansion"),
    };

    private static bool TryCaptureSource(string addon, GameInventoryItem item, out InventoryShuttleSource source)
    {
        source = default;
        var inventory = (InventoryType)item.ContainerType;
        var family = PlayerAddonNames.Contains(addon) ? PlayerInventories : RetainerAddonNames.Contains(addon) ? RetainerInventories
            : addon == "InventoryBuddy" ? SaddlebagInventories : addon == "InventoryBuddy2" ? PremiumSaddlebagInventories : Array.Empty<InventoryType>();
        if (Array.IndexOf(family, inventory) < 0 || item.InventorySlot > ushort.MaxValue || item.Quantity <= 0) return false;
        if (!NativeArrayAccess.TryGetInventorySlot(InventoryManager.Instance(), inventory, (int)item.InventorySlot, out var slot)
            || slot->IsSymbolic || slot->Quantity != item.Quantity || slot->GetItemId() != item.ItemId) return false;
        source = new(inventory, (ushort)item.InventorySlot, slot->Quantity, InventoryShuttleItemState.Read(slot));
        return true;
    }

    private static bool TryGetInventorySource(out InventoryType sourceInventory, out ushort sourceSlot)
    {
        sourceInventory = InventoryType.Invalid;
        sourceSlot = 0;

        var agent = AgentInventoryContext.Instance();
        if (agent == null || agent->TargetInventorySlot == null || agent->TargetInventorySlot->ItemId == 0
            || agent->TargetInventorySlotId < 0 || agent->TargetInventorySlotId > ushort.MaxValue)
            return false;

        sourceInventory = agent->TargetInventoryId;
        sourceSlot = (ushort)agent->TargetInventorySlotId;
        return sourceInventory != InventoryType.Invalid;
    }

    private bool IsSameItem(InventoryItem* slot, InventoryShuttleSource item)
    {
        if (slot == null || slot->IsSymbolic || slot->GetItemId() == 0 || slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable)
            || InventoryShuttleItemState.Read(slot) != item.State || mergeBindingUnavailable) return false;
        try
        {
            if (!NativeArrayAccess.TryGetInventorySlot(InventoryManager.Instance(), item.Inventory, item.Slot, out var source)
                || source->IsSymbolic || source->Quantity != item.Quantity || InventoryShuttleItemState.Read(source) != item.State) return false;
            mergeBinding ??= new InventoryShuttleNativeBinding();
            var equal = mergeBinding.ReadSlot20(source) == mergeBinding.ReadSlot20(slot);
            OccupiedStackStatus = "Pinned occupied-stack comparison available.";
            return equal;
        }
        catch (Exception error)
        {
            mergeBindingUnavailable = true;
            OccupiedStackStatus = "Occupied-stack transfer unavailable; empty destinations only: " + error.Message;
            return false;
        }
    }

    private readonly record struct ShuttleDestination(InventoryShuttleFamily Family, string MenuLabel, InventoryType[] Inventories, InventoryShuttleAccess Access);

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
