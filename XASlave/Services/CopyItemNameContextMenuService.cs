using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public sealed class CopyItemNameContextMenuService : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;

    public CopyItemNameContextMenuService(
        IContextMenu contextMenu,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.contextMenu = contextMenu;
        this.dataManager = dataManager;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

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

        Subscribe();
        enabled = true;
        StatusText = "Enabled - inventory item names can be copied directly from context menus.";
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
        if (!enabled || args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory target)
            return;

        if (target.TargetItem is not { } item || item.ItemId == 0)
            return;

        args.AddMenuItem(BuildItem("Copy Item Name", () => CopyItemName(item.ItemId)));

        if (item.GlamourId != 0)
            args.AddMenuItem(BuildItem("Copy Item Name (Glamour)", () => CopyItemName(item.GlamourId)));
    }

    private MenuItem BuildItem(string name, System.Action onClick)
    {
        return new MenuItem
        {
            Name = new SeStringBuilder().AddText(name).Build(),
            PrefixChar = 'X',
            PrefixColor = 539,
            UseDefaultPrefix = false,
            OnClicked = _ => onClick(),
        };
    }

    private void CopyItemName(uint rawItemId)
    {
        try
        {
            var itemName = ResolveItemName(rawItemId);
            if (string.IsNullOrWhiteSpace(itemName))
                return;

            ImGui.SetClipboardText(itemName);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to copy item name for raw item id {rawItemId}.");
        }
    }

    private string ResolveItemName(uint rawItemId)
    {
        if (rawItemId >= 2_000_000)
        {
            var eventItems = dataManager.GetExcelSheet<EventItem>();
            if (eventItems != null && eventItems.TryGetRow(rawItemId, out var eventItem))
                return eventItem.Singular.ToString();
        }

        var itemId = rawItemId % 500_000;
        var items = dataManager.GetExcelSheet<Item>();
        return items != null && items.TryGetRow(itemId, out var item)
            ? item.Name.ToString()
            : string.Empty;
    }
}
