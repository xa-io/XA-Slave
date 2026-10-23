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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S03F020CBB71754CC");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);
    public string LastSourceAddon { get; private set; } = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5").Original;
    private global::XASlave.Localization.UiMessage? uiLastSourceAddon = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5");
    internal global::XASlave.Localization.UiMessage LastSourceAddonMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastSourceAddon, LastSourceAddon);
    internal string LocalizedLastSourceAddon => global::XASlave.Localization.UiMessage.Resolve(ref uiLastSourceAddon, LastSourceAddon);
    public string LastDestinationLabel { get; private set; } = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5").Original;
    private InventoryShuttleFamily? lastDestinationFamily;
    private global::XASlave.Localization.UiMessage? uiLastDestinationLabel = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5");
    internal global::XASlave.Localization.UiMessage LastDestinationLabelMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastDestinationLabel, LastDestinationLabel);
    internal string LocalizedLastDestinationLabel => lastDestinationFamily is { } family ? DestinationMessage(family).Display : global::XASlave.Localization.UiMessage.Resolve(ref uiLastDestinationLabel, LastDestinationLabel);
    public uint LastItemId { get; private set; }
    public int AvailableDestinationCount { get; private set; }
    public BetterInventoryMoverModifierKey QuickMoveModifier => quickMoveModifier;
    public string QuickMoveModifierLabel => GetModifierLabel(quickMoveModifier);
    internal global::XASlave.Localization.UiMessage QuickMoveModifierLabelMessage => global::XASlave.Localization.UiMessage.Authored(QuickMoveModifierLabel);
    internal string LocalizedQuickMoveModifierLabel => QuickMoveModifierLabelMessage.Display;
    public string OccupiedStackStatus { get; private set; } = global::XASlave.Localization.UiMessage.Key("SB12D6DC32BFE9B3D").Original;
    private global::XASlave.Localization.UiMessage? uiOccupiedStackStatus = global::XASlave.Localization.UiMessage.Key("SB12D6DC32BFE9B3D");
    internal global::XASlave.Localization.UiMessage OccupiedStackStatusMessage => global::XASlave.Localization.UiMessage.Capture(ref uiOccupiedStackStatus, OccupiedStackStatus);
    internal string LocalizedOccupiedStackStatus => global::XASlave.Localization.UiMessage.Resolve(ref uiOccupiedStackStatus, OccupiedStackStatus);

    public void ApplyConfiguration(BetterInventoryMoverModifierKey quickMoveModifier)
    {
        CancelTransfer(global::XASlave.Localization.UiMessage.Key("S4AA7E5DBE5D6A9E8")); generation++;
        this.quickMoveModifier = NormalizeModifier(quickMoveModifier);
        if (enabled)
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, BuildStatusText());
    }

    public bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (value == enabled)
            return enabled;

        CancelTransfer(global::XASlave.Localization.UiMessage.Key("S338F03D99534E648")); generation++;
        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        enabled = true;
        Subscribe();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, BuildStatusText());
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        CancelTransfer(global::XASlave.Localization.UiMessage.Key("SCE33ACC2517C886C")); disposed = true; generation++;
        enabled = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        access = new InventoryShuttleContext(Plugin.AddonLifecycle, () => { CancelTransfer(global::XASlave.Localization.UiMessage.Key("SD03752294380D934")); generation++; });
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
        CancelTransfer(global::XASlave.Localization.UiMessage.Key("SB72DF9C37C6C1172"));
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
            LastSourceAddon = global::XASlave.Localization.UiMessage.Store(ref uiLastSourceAddon, global::XASlave.Localization.UiMessage.Raw(addonName));
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

    private global::XASlave.Localization.UiMessage BuildStatusText()
        => global::XASlave.Localization.UiMessage.Format("S4DC2CCADE89252D7", QuickMoveModifierLabelMessage);

    private bool IsQuickMoveModifierHeld()
        => (GetAsyncKeyState(GetVirtualKey(quickMoveModifier)) & 0x8000) != 0;

    public static BetterInventoryMoverModifierKey NormalizeModifier(BetterInventoryMoverModifierKey modifier)
        => Enum.IsDefined(modifier) ? modifier : BetterInventoryMoverModifierKey.LeftShift;


    private static global::XASlave.Localization.UiMessage DestinationMessage(InventoryShuttleFamily family) => family switch
    {
        InventoryShuttleFamily.Saddlebag => global::XASlave.Localization.UiMessage.Key("SD1F7B2FD72406658"),
        InventoryShuttleFamily.PremiumSaddlebag => global::XASlave.Localization.UiMessage.Key("SA7D97AE47D499B60"),
        InventoryShuttleFamily.Retainer => global::XASlave.Localization.UiMessage.Key("S5CCF2F9822C645DC"),
        _ => global::XASlave.Localization.UiMessage.Key("S38108F5329F4CC53"),
    };
    public static string GetModifierLabel(BetterInventoryMoverModifierKey modifier)
    {
        return NormalizeModifier(modifier) switch
        {
            BetterInventoryMoverModifierKey.LeftControl => global::XASlave.Localization.UiMessage.Key("S539598885559F790").Original,
            BetterInventoryMoverModifierKey.LeftAlt => global::XASlave.Localization.UiMessage.Key("SC975C1DD0C72505F").Original,
            BetterInventoryMoverModifierKey.RightShift => global::XASlave.Localization.UiMessage.Key("SB0EA7AEB7ECA95F9").Original,
            BetterInventoryMoverModifierKey.RightControl => global::XASlave.Localization.UiMessage.Key("SCCA19102E414EE3C").Original,
            BetterInventoryMoverModifierKey.RightAlt => global::XASlave.Localization.UiMessage.Key("S473CBE010F45A6A5").Original,
            _ => global::XASlave.Localization.UiMessage.Key("SF75AA1DEB96DFE23").Original,
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
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S6842F0249DCE04E9"), originalMessage => new InvalidOperationException(originalMessage));
            if (ReadSlot(sourceItem.Inventory, sourceItem.Slot) != sourceItem) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S56263FFFF5CD2258"), originalMessage => new InvalidOperationException(originalMessage));
            transferGeneration = ++generation;
            transfer = new(new TransferAdapter(this, destination), sourceItem, Environment.TickCount64);
            lastDestinationFamily = destination.Family;
            LastDestinationLabel = global::XASlave.Localization.UiMessage.Store(ref uiLastDestinationLabel, global::XASlave.Localization.UiMessage.Raw(destination.MenuLabel)); LastItemId = sourceItem.State.FullId;
            PublishTransfer();
            // The first framework update follows menu setup, allowing an exact close stamp.
        }
        catch (Exception error) { LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SC5BC264875D7526E"), global::XASlave.Localization.UiMessage.FromException(error))); log.Warning(error, "Inventory transfer admission refused."); }
    }

    private static InventoryShuttleSource ReadSlot(InventoryType inventory, ushort index)
    {
        if (!NativeArrayAccess.TryGetInventorySlot(InventoryManager.Instance(), inventory, index, out var slot) || slot->IsSymbolic)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SCDB0FE516F3DB20A"), originalMessage => new InvalidOperationException(originalMessage));
        return slot->GetItemId() == 0 ? new(inventory, index, 0, default)
            : new(inventory, index, slot->Quantity, InventoryShuttleItemState.Read(slot));
    }

    private void UpdateTransfer(IFramework framework)
    {
        if (disposed || !enabled || transfer == null) return;
        if (transferGeneration != generation) transfer.Cancel(global::XASlave.Localization.UiMessage.Key("S924FD5BD3A8EB883"));
        else transfer.Tick(Environment.TickCount64);
        PublishTransfer();
    }

    private void CancelTransfer(global::XASlave.Localization.UiMessage reason)
    {
        if (transfer == null) return;
        transfer.Cancel(reason); PublishTransfer(); transfer = null;
    }

    private void PublishTransfer()
    {
        if (transfer == null) return;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S79B2BE0E5AD1B88F", transfer.OutcomeMessage, transfer.Moved, transfer.Remaining, transfer.DetailMessage));
        if (transfer.NativeCode is { } code) LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(uiLastActionText, LastActionText), global::XASlave.Localization.UiMessage.Format("S78EBE4629C6A87F0", code)));
    }

    private sealed class TransferAdapter(BetterInventoryMoverService owner, ShuttleDestination destination) : IInventoryShuttleAdapter
    {
        private bool menuBound;
        public void Validate()
        {
            if (owner.disposed || !owner.enabled || owner.transferGeneration != owner.generation)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SAACDBA7AF55B9703"), originalMessage => new InvalidOperationException(originalMessage));
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
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S53FC8736B9CE9B09"), originalMessage => new InvalidOperationException(originalMessage));
            var manager = InventoryManager.Instance();
            if (!owner.dataManager.GetExcelSheet<Item>().TryGetRow(source.State.BaseId, out var data) || data.StackSize != target.Capacity)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S4EE783D4E60C83A0"), originalMessage => new InvalidOperationException(originalMessage));
            if (target.Before.Quantity > 0)
            {
                if (target.Capacity <= 1 || source.Quantity > target.Capacity || target.Before.Quantity >= target.Capacity
                    || !NativeArrayAccess.TryGetInventorySlot(manager, target.Before.Inventory, target.Before.Slot, out var slot) || !owner.IsSameItem(slot, source))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9A8738FA92EE47F1"), originalMessage => new InvalidOperationException(originalMessage));
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
            OccupiedStackStatus = global::XASlave.Localization.UiMessage.Store(ref uiOccupiedStackStatus, global::XASlave.Localization.UiMessage.Key("S85FB3F6CF400E735"));
            return equal;
        }
        catch (Exception error)
        {
            mergeBindingUnavailable = true;
            OccupiedStackStatus = global::XASlave.Localization.UiMessage.Store(ref uiOccupiedStackStatus, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S3A5CC536BEC15FA7"), global::XASlave.Localization.UiMessage.FromException(error)));
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
