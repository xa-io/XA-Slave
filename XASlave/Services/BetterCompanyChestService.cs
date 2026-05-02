using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class BetterCompanyChestService : IDisposable
{
    private const float ValueOverlayBaseWidth = 190f;
    private const float ValueOverlayBaseHeight = 42f;
    private const float ValueOverlayMinWidth = 150f;
    private const float ValueOverlayEdgePadding = 8f;
    private const float ValueOverlayWindowGap = 6f;
    private const float ValueOverlayRounding = 5f;

    private static readonly HashSet<string> PlayerInventoryAddons =
    [
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
    ];

    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    [StructLayout(LayoutKind.Explicit)]
    private struct FreeCompanyChestAgentContext
    {
        [FieldOffset(6956)]
        public InventoryType ContextInventoryType;

        [FieldOffset(6960)]
        public short ContextInventorySlot;
    }

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IContextMenu contextMenu;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly uint[] exchangeableItemIds;
    private bool enabled;
    private bool subscribed;
    private bool quickMoveEnabled = true;
    private bool autoConfirmNumericInput = true;
    private bool showExchangeableValue = true;
    private int defaultPageIndex;
    private bool awaitingNumericConfirm;
    private bool closeContextMenuOnDraw;
    private DateTime awaitingNumericConfirmUntilUtc = DateTime.MinValue;
    private long lastExchangeableValue;

    public BetterCompanyChestService(
        IAddonLifecycle addonLifecycle,
        IContextMenu contextMenu,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.contextMenu = contextMenu;
        this.dataManager = dataManager;
        this.log = log;
        exchangeableItemIds = dataManager
            .GetExcelSheet<Item>()
            .Where(item => item.RowId > 0 && item.ItemSortCategory.Value.Param == 150)
            .Select(item => item.RowId)
            .ToArray();
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public string CurrentPageLabel { get; private set; } = "Closed";
    public long CurrentExchangeableValue => lastExchangeableValue;
    public bool QuickMoveEnabled => quickMoveEnabled;
    public bool AutoConfirmNumericInputEnabled => autoConfirmNumericInput;
    public bool ShowExchangeableValueEnabled => showExchangeableValue;
    public int DefaultPageIndex => defaultPageIndex;

    public void ApplyConfiguration(int defaultPageIndex, bool quickMoveEnabled, bool autoConfirmNumericInput, bool showExchangeableValue)
    {
        this.defaultPageIndex = Math.Clamp(defaultPageIndex, 0, 6);
        this.quickMoveEnabled = quickMoveEnabled;
        this.autoConfirmNumericInput = autoConfirmNumericInput;
        this.showExchangeableValue = showExchangeableValue;

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
            awaitingNumericConfirm = false;
            awaitingNumericConfirmUntilUtc = DateTime.MinValue;
            CurrentPageLabel = "Closed";
            lastExchangeableValue = 0;
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
        awaitingNumericConfirm = false;
        Unsubscribe();
    }

    public void DrawOverlay()
    {
        if (!enabled || !showExchangeableValue || lastExchangeableValue <= 0 || !TryGetValueOverlayLayout(out var position, out var size, out var scale))
            return;

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (!ImGui.Begin("##XASlaveBetterCompanyChestValueOverlay", flags))
        {
            ImGui.End();
            return;
        }

        var min = ImGui.GetWindowPos();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var fillColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.05f, 0.04f, 0.82f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.92f, 0.76f, 0.34f, 0.82f));
        var titleColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.82f, 0.82f, 0.76f, 1.0f));
        var valueColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.86f, 0.38f, 1.0f));
        var rounding = ValueOverlayRounding * scale;
        var borderThickness = Math.Max(1f, scale);

        drawList.AddRectFilled(min, max, fillColor, rounding);
        drawList.AddRect(min, max, borderColor, rounding, ImDrawFlags.None, borderThickness);
        drawList.AddText(min + new Vector2(9f * scale, 6f * scale), titleColor, "Exchangeable value");
        drawList.AddText(min + new Vector2(9f * scale, 22f * scale), valueColor, $"{lastExchangeableValue:n0} gil");

        ImGui.End();
    }

    private string BuildStatusText()
    {
        var defaultPageText = defaultPageIndex == 0 ? "no default page" : DescribePage(GetConfiguredPage());
        return $"Enabled - default page: {defaultPageText}, quick move {(quickMoveEnabled ? "on" : "off")}, numeric confirm {(autoConfirmNumericInput ? "on" : "off")}, gil value overlay {(showExchangeableValue ? "on" : "off")}.";
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeCompanyChest", OnFreeCompanyChestAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "FreeCompanyChest", OnFreeCompanyChestAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnFreeCompanyChestAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputNumericAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ContextMenu", OnContextMenuAddon);
        contextMenu.OnMenuOpened += OnMenuOpened;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnFreeCompanyChestAddon);
        addonLifecycle.UnregisterListener(OnInputNumericAddon);
        addonLifecycle.UnregisterListener(OnContextMenuAddon);
        contextMenu.OnMenuOpened -= OnMenuOpened;
        subscribed = false;
    }

    private void OnFreeCompanyChestAddon(AddonEvent type, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            switch (type)
            {
                case AddonEvent.PostDraw:
                {
                    var addon = (AtkUnitBase*)args.Addon.Address;
                    if (addon == null || !addon->IsVisible)
                        return;

                    if (!TryGetCurrentPage(addon, out var currentPage))
                    {
                        CurrentPageLabel = "Unavailable";
                        lastExchangeableValue = 0;
                        return;
                    }

                    CurrentPageLabel = DescribePage(currentPage);
                    lastExchangeableValue = showExchangeableValue && TryGetExchangeableValue(currentPage, out var totalPrice)
                        ? totalPrice
                        : 0;
                    break;
                }
                case AddonEvent.PostSetup:
                {
                    var addon = (AtkUnitBase*)args.Addon.Address;
                    if (addon == null || !addon->IsVisible || defaultPageIndex == 0)
                        return;

                    var pageNodeId = GetConfiguredPageNodeId();
                    if (pageNodeId == 0)
                        return;

                    var component = addon->GetComponentByNodeId(pageNodeId);
                    if (component == null)
                        return;

                    AddonHelper.ClickComponent(addon, component);
                    LastActionText = $"Last action: selected {DescribePage(GetConfiguredPage())} when Free Company Chest opened at {DateTime.Now:HH:mm:ss}.";
                    break;
                }
                case AddonEvent.PreFinalize:
                    awaitingNumericConfirm = false;
                    closeContextMenuOnDraw = false;
                    awaitingNumericConfirmUntilUtc = DateTime.MinValue;
                    CurrentPageLabel = "Closed";
                    lastExchangeableValue = 0;
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Company Chest failed while handling FreeCompanyChest.");
        }
    }

    private void OnContextMenuAddon(AddonEvent _, AddonArgs args)
    {
        if (!closeContextMenuOnDraw || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon != null && addon->IsVisible)
            {
                addon->IsVisible = false;
                addon->Close(true);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Company Chest failed while closing the context menu after quick move.");
        }
        finally
        {
            closeContextMenuOnDraw = false;
        }
    }

    private void OnInputNumericAddon(AddonEvent _, AddonArgs args)
    {
        if (!enabled || !quickMoveEnabled || !autoConfirmNumericInput || args.Addon.IsNull)
            return;

        try
        {
            if (awaitingNumericConfirm && DateTime.UtcNow > awaitingNumericConfirmUntilUtc)
            {
                awaitingNumericConfirm = false;
                awaitingNumericConfirmUntilUtc = DateTime.MinValue;
            }

            if (!AddonHelper.IsAddonReady("FreeCompanyChest"))
                return;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount <= 3)
                return;

            awaitingNumericConfirm = false;
            awaitingNumericConfirmUntilUtc = DateTime.MinValue;
            addon->FireCallbackInt((int)addon->AtkValues[3].UInt);
            LastActionText = $"Last action: auto-confirmed the Free Company Chest quantity prompt at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Company Chest failed while confirming InputNumeric.");
        }
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!enabled || !quickMoveEnabled || !AddonHelper.IsAddonVisible("FreeCompanyChest"))
        {
            return;
        }

        var addonName = args.AddonName;
        if (string.IsNullOrEmpty(addonName))
            return;

        try
        {
            if (addonName.Equals("FreeCompanyChest", StringComparison.Ordinal))
            {
                TryWithdrawFromFreeCompanyChest();
                return;
            }

            if (!PlayerInventoryAddons.Contains(addonName) ||
                args.MenuType != ContextMenuType.Inventory ||
                args.Target is not MenuTargetInventory { TargetItem: { } item } ||
                item.IsEmpty ||
                item.ItemId == 0)
            {
                return;
            }

            if (!TryGetCurrentPage(AddonHelper.GetAddon("FreeCompanyChest"), out var currentPage) || currentPage == InventoryType.FreeCompanyCrystals)
                return;

            if (!TryGetInventorySource(out var inventorySourceInventory, out var inventorySourceSlot))
                return;

            var inventorySourceItem = TryGetInventoryItem(inventorySourceInventory, inventorySourceSlot);
            if (inventorySourceItem == null || inventorySourceItem->ItemId == 0)
                return;

            if (!TryFindTargetSlot([currentPage], inventorySourceItem, out _, out var destinationSlotInChest))
                return;

            ExecuteMove(
                inventorySourceInventory,
                inventorySourceSlot,
                currentPage,
                destinationSlotInChest,
                $"stored item {inventorySourceItem->GetItemId()} in {DescribePage(currentPage)}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Company Chest failed while handling quick move.");
        }
    }

    private bool TryWithdrawFromFreeCompanyChest()
    {
        if (!TryGetFreeCompanyChestContext(out var sourceInventory, out var sourceSlot, out var sourceItem))
            return false;

        if (sourceItem == null || sourceItem->ItemId == 0)
            return false;

        if (!TryFindTargetSlot(PlayerInventories, sourceItem, out var destinationInventory, out var destinationSlot))
            return false;

        if (!ExecuteMove(
            sourceInventory,
            sourceSlot,
            destinationInventory,
            destinationSlot,
            $"withdrew item {sourceItem->GetItemId()} to inventory"))
        {
            return false;
        }

        ClearFreeCompanyChestContext();
        return true;
    }

    private bool ExecuteMove(InventoryType sourceInventory, ushort sourceSlot, InventoryType destinationInventory, ushort destinationSlot, string actionDescription)
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return false;

            var agent = (AgentFreeCompanyChest*)agentModule->GetAgentByInternalId(AgentId.FreeCompanyChest);
            if (agent == null)
                return false;

            agent->MoveItemInChest(sourceInventory, sourceSlot, destinationInventory, destinationSlot);
            awaitingNumericConfirm = autoConfirmNumericInput;
            awaitingNumericConfirmUntilUtc = awaitingNumericConfirm ? DateTime.UtcNow.AddSeconds(2) : DateTime.MinValue;
            closeContextMenuOnDraw = true;
            LastActionText = $"Last action: {actionDescription} at {DateTime.Now:HH:mm:ss}.";
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Company Chest failed while moving an item.");
            return false;
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

    private static bool TryGetFreeCompanyChestContext(out InventoryType sourceInventory, out ushort sourceSlot, out InventoryItem* sourceItem)
    {
        sourceInventory = InventoryType.Invalid;
        sourceSlot = 0;
        sourceItem = null;

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        var agent = (FreeCompanyChestAgentContext*)agentModule->GetAgentByInternalId(AgentId.FreeCompanyChest);
        if (agent == null ||
            !IsFreeCompanyPage(agent->ContextInventoryType) ||
            agent->ContextInventorySlot < 0)
        {
            return false;
        }

        sourceInventory = agent->ContextInventoryType;
        sourceSlot = (ushort)agent->ContextInventorySlot;
        sourceItem = TryGetInventoryItem(sourceInventory, sourceSlot);
        return sourceItem != null && sourceItem->ItemId != 0;
    }

    private static void ClearFreeCompanyChestContext()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return;

        var agent = (FreeCompanyChestAgentContext*)agentModule->GetAgentByInternalId(AgentId.FreeCompanyChest);
        if (agent != null)
            agent->ContextInventoryType = InventoryType.Invalid;
    }

    private static InventoryItem* TryGetInventoryItem(InventoryType inventoryType, ushort slotIndex)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        var container = manager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded || slotIndex >= container->Size)
            return null;

        return container->GetInventorySlot(slotIndex);
    }

    private static bool IsFreeCompanyPage(InventoryType inventoryType)
    {
        return inventoryType is
            InventoryType.FreeCompanyPage1 or
            InventoryType.FreeCompanyPage2 or
            InventoryType.FreeCompanyPage3 or
            InventoryType.FreeCompanyPage4 or
            InventoryType.FreeCompanyPage5;
    }

    private bool TryFindTargetSlot(InventoryType[] destinationInventories, InventoryItem* sourceItem, out InventoryType destinationInventory, out ushort destinationSlot)
    {
        destinationInventory = InventoryType.Invalid;
        destinationSlot = 0;

        if (sourceItem == null || sourceItem->ItemId == 0)
            return false;

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(sourceItem->GetBaseItemId(), out var itemData))
            return false;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        foreach (var inventoryType in destinationInventories)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || !IsSameItem(slot, sourceItem))
                    continue;

                if (slot->Quantity + sourceItem->Quantity > itemData.StackSize)
                    continue;

                destinationInventory = inventoryType;
                destinationSlot = (ushort)slot->Slot;
                return true;
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

                destinationInventory = inventoryType;
                destinationSlot = (ushort)slot->Slot;
                return true;
            }
        }

        return false;
    }

    private bool TryGetExchangeableValue(InventoryType currentPage, out long totalPrice)
    {
        totalPrice = 0;
        if (currentPage == InventoryType.Invalid || currentPage == InventoryType.FreeCompanyCrystals)
            return false;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var itemSheet = dataManager.GetExcelSheet<Item>();
        foreach (var itemId in exchangeableItemIds)
        {
            if (!itemSheet.TryGetRow(itemId, out var itemData))
                continue;

            var itemCount = manager->GetItemCountInContainer(itemId, currentPage);
            if (itemCount <= 0)
                continue;

            totalPrice += (long)itemCount * itemData.PriceLow;
        }

        return totalPrice > 0;
    }

    private static bool TryGetValueOverlayLayout(out Vector2 position, out Vector2 size, out float scale)
    {
        position = Vector2.Zero;
        size = Vector2.Zero;
        scale = 1f;

        var addon = AddonHelper.GetAddon("FreeCompanyChest");
        if (addon == null || !addon->IsVisible || addon->RootNode == null)
            return false;

        var root = addon->RootNode;
        var rootScale = root->ScaleX > 0f ? root->ScaleX : 1f;
        scale = Math.Clamp(rootScale, 0.75f, 1.5f);
        var width = Math.Max(1f, root->Width * rootScale);
        var desiredWidth = ValueOverlayBaseWidth * scale;
        var availableWidth = Math.Max(1f, width - (ValueOverlayEdgePadding * 2f * scale));
        var minWidth = Math.Min(ValueOverlayMinWidth * scale, availableWidth);
        var overlayWidth = Math.Clamp(desiredWidth, minWidth, availableWidth);
        var overlayHeight = Math.Max(ValueOverlayBaseHeight, ValueOverlayBaseHeight * scale);

        size = new Vector2(overlayWidth, overlayHeight);
        position = new Vector2(
            root->ScreenX + ((width - overlayWidth) * 0.5f),
            Math.Max(0f, root->ScreenY - overlayHeight - (ValueOverlayWindowGap * scale)));
        return true;
    }

    private static bool IsSameItem(InventoryItem* slot, InventoryItem* sourceItem)
    {
        if (slot == null || sourceItem == null || slot->GetItemId() == 0 || sourceItem->GetItemId() == 0)
            return false;

        var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var isCollectable = slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
        var sourceIsHq = sourceItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var sourceIsCollectable = sourceItem->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
        return slot->GetBaseItemId() == sourceItem->GetBaseItemId() && isHq == sourceIsHq && isCollectable == sourceIsCollectable;
    }

    private static bool TryGetCurrentPage(AtkUnitBase* addon, out InventoryType page)
    {
        page = InventoryType.Invalid;
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 2)
            return false;

        var hiddenNode = addon->GetNodeById(106);
        if (hiddenNode != null && hiddenNode->IsVisible())
            return false;

        if (addon->AtkValues[1].UInt != 0)
        {
            page = InventoryType.FreeCompanyCrystals;
            return true;
        }

        page = (InventoryType)(20000 + addon->AtkValues[2].UInt);
        return true;
    }

    private InventoryType GetConfiguredPage()
    {
        return defaultPageIndex switch
        {
            1 => InventoryType.FreeCompanyPage1,
            2 => InventoryType.FreeCompanyPage2,
            3 => InventoryType.FreeCompanyPage3,
            4 => InventoryType.FreeCompanyPage4,
            5 => InventoryType.FreeCompanyPage5,
            6 => InventoryType.FreeCompanyCrystals,
            _ => InventoryType.Invalid,
        };
    }

    private uint GetConfiguredPageNodeId()
    {
        return defaultPageIndex switch
        {
            >= 1 and <= 5 => (uint)(9 + defaultPageIndex),
            6 => 15u,
            _ => 0u,
        };
    }

    private static string DescribePage(InventoryType page)
    {
        return page switch
        {
            InventoryType.FreeCompanyPage1 => "Company Chest Page 1",
            InventoryType.FreeCompanyPage2 => "Company Chest Page 2",
            InventoryType.FreeCompanyPage3 => "Company Chest Page 3",
            InventoryType.FreeCompanyPage4 => "Company Chest Page 4",
            InventoryType.FreeCompanyPage5 => "Company Chest Page 5",
            InventoryType.FreeCompanyCrystals => "Company Chest Crystals",
            _ => "None",
        };
    }
}
