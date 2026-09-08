using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class EnableItemIconInShopsService : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly List<CollectablesShopItemData> collectablesShopItemDatas = [];
    private bool enabled;
    private bool subscribed;
    private DateTime lastCollectablesDrawUtc = DateTime.MinValue;

    public EnableItemIconInShopsService(
        IAddonLifecycle addonLifecycle,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.dataManager = dataManager;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public string LastAddonName { get; private set; } = "None";
    public int LastReplacementCount { get; private set; }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            collectablesShopItemDatas.Clear();
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        StatusText = "Enabled - shop category icons are replaced with actual item icons across the supported shop add-ons.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        collectablesShopItemDatas.Clear();
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        RegisterShopListeners("Shop", OnShop);
        RegisterShopListeners("InclusionShop", OnInclusionShop);
        RegisterShopListeners("GrandCompanyExchange", OnGrandCompanyExchange);
        RegisterShopListeners("ShopExchangeCurrency", OnShopExchange);
        RegisterShopListeners("ShopExchangeItem", OnShopExchange);
        RegisterShopListeners("ShopExchangeCoin", OnShopExchange);
        RegisterShopListeners("FreeShop", OnFreeShop);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "CollectablesShop", OnCollectablesShop);
        addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "CollectablesShop", OnCollectablesShop);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CollectablesShop", OnCollectablesShop);
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnShop);
        addonLifecycle.UnregisterListener(OnInclusionShop);
        addonLifecycle.UnregisterListener(OnGrandCompanyExchange);
        addonLifecycle.UnregisterListener(OnShopExchange);
        addonLifecycle.UnregisterListener(OnCollectablesShop);
        addonLifecycle.UnregisterListener(OnFreeShop);
        subscribed = false;
    }

    private void RegisterShopListeners(string addonName, IAddonLifecycle.AddonEventDelegate handler)
    {
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, [addonName], handler);
        addonLifecycle.RegisterListener(AddonEvent.PreRefresh, [addonName], handler);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, [addonName], handler);
    }

    private void OnFreeShop(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            const int FreeShopItemCountIndex = 76;
            const int FreeShopItemIdStartIndex = 138;
            const int FreeShopIconStartIndex = 199;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            var atkValuesCount = (int)addon->AtkValuesCount;
            if (atkValuesCount <= FreeShopIconStartIndex ||
                !NativeArrayAccess.TryGetAtkUInt(addon, FreeShopItemCountIndex, out var itemCount))
                return;

            if (itemCount == 0)
            {
                RecordReplacement("FreeShop", 0);
                return;
            }

            var safeItemCount = Math.Min(
                ClampItemCount(itemCount, atkValuesCount, FreeShopItemIdStartIndex),
                ClampItemCount(itemCount, atkValuesCount, FreeShopIconStartIndex));

            var replacementCount = 0;
            for (var index = 0; index < safeItemCount; index++)
            {
                if (!NativeArrayAccess.TryGetAtkUInt(addon, FreeShopItemIdStartIndex + index, out var itemId))
                    break;

                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                if (!NativeArrayAccess.TrySetAtkUInt(addon, FreeShopIconStartIndex + index, itemRow.Icon))
                    break;

                replacementCount++;
            }

            RecordReplacement("FreeShop", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing FreeShop.");
        }
    }

    private void OnCollectablesShop(AddonEvent type, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null)
                return;

            if (type == AddonEvent.PostRefresh)
            {
                collectablesShopItemDatas.Clear();
                // Read AtkValues[20] only when it is in range, and clamp the strided read
                // (34+11*index) to AtkValuesCount so a large reported count cannot read past the array.
                var atkValuesCount = (int)addon->AtkValuesCount;
                var safeItemCount = 0;
                if (atkValuesCount > 34 && NativeArrayAccess.TryGetAtkUInt(addon, 20, out var itemCount))
                {
                    safeItemCount = ClampItemCount(itemCount, atkValuesCount, 34, 11);
                }
                for (var index = 0; index < safeItemCount; index++)
                {
                    if (!NativeArrayAccess.TryGetAtkUInt(addon, 34 + 11 * index, out var rawItemId))
                        break;

                    var itemId = rawItemId % 500000;
                    if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                        continue;

                    collectablesShopItemDatas.Add(new CollectablesShopItemData(
                        itemId,
                        itemRow.Icon,
                        NormalizeText(itemRow.Name.ToString())));
                }

                LastAddonName = "CollectablesShop";
                return;
            }

            if (type != AddonEvent.PostDraw ||
                collectablesShopItemDatas.Count == 0 ||
                (DateTime.UtcNow - lastCollectablesDrawUtc).TotalMilliseconds < 100)
            {
                return;
            }

            lastCollectablesDrawUtc = DateTime.UtcNow;
            var listComponent = (AtkComponentNode*)addon->GetNodeById(28);
            if (listComponent == null || listComponent->Component == null)
                return;

            var replacementCount = 0;
            var nodeListCount = (int)listComponent->Component->UldManager.NodeListCount;
            for (var index = 0; index < 15; index++)
            {
                if (16 + index >= nodeListCount)
                    break;
                if (!NativeArrayAccess.TryGetNode(&listComponent->Component->UldManager, 16 + index, out var listItemNode))
                    break;

                var listItemComponent = (AtkComponentNode*)listItemNode;
                if (listItemComponent == null || listItemComponent->Component == null)
                    continue;

                var nameNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(4);
                var imageNode = (AtkImageNode*)listItemComponent->Component->UldManager.SearchNodeById(2);
                if (nameNode == null || imageNode == null)
                    continue;

                var normalizedName = NormalizeText(nameNode->NodeText.ToString());
                if (string.IsNullOrWhiteSpace(normalizedName))
                    continue;

                var itemData = collectablesShopItemDatas.Find(data =>
                    data.NormalizedName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                    normalizedName.Contains(data.NormalizedName, StringComparison.OrdinalIgnoreCase));
                if (itemData == default)
                    continue;

                imageNode->LoadIconTexture(itemData.IconId, 0);
                replacementCount++;
            }

            RecordReplacement("CollectablesShop", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing CollectablesShop.");
        }
    }

    private void OnShopExchange(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValuesCount <= 1064 ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 4, out var itemCount))
                return;

            // The addon-reported item count is not trusted for bounds: the highest slot read is
            // 1064+index, so clamp the loop to the available AtkValue range (matches OnFreeShop).
            var atkValuesCount = (int)addon->AtkValuesCount;
            var safeItemCount = ClampItemCount(itemCount, atkValuesCount, 1064);
            var replacementCount = 0;
            for (var index = 0; index < safeItemCount; index++)
            {
                if (!NativeArrayAccess.TryGetAtkUInt(addon, 1064 + index, out var itemId))
                    break;

                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                if (!NativeArrayAccess.TrySetAtkUInt(addon, 210 + index, itemRow.Icon))
                    break;

                replacementCount++;
            }

            RecordReplacement("ShopExchange", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing ShopExchange.");
        }
    }

    private void OnGrandCompanyExchange(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValuesCount <= 317 ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 1, out var itemCount))
                return;

            // Clamp to the available AtkValue range; highest slot read is 317+index.
            var atkValuesCount = (int)addon->AtkValuesCount;
            var safeItemCount = ClampItemCount(itemCount, atkValuesCount, 317);
            var replacementCount = 0;
            for (var index = 0; index < safeItemCount; index++)
            {
                if (!NativeArrayAccess.TryGetAtkUInt(addon, 317 + index, out var itemId))
                    break;

                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                if (!NativeArrayAccess.TrySetAtkUInt(addon, 167 + index, itemRow.Icon))
                    break;

                replacementCount++;
            }

            RecordReplacement("GrandCompanyExchange", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing GrandCompanyExchange.");
        }
    }

    private void OnInclusionShop(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValuesCount <= 301 ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 298, out var itemCount))
                return;

            // Clamp to the available AtkValue range; highest slot touched is the icon write at
            // 301+index*18, so the largest safe item count keeps that index within AtkValuesCount.
            var atkValuesCount = (int)addon->AtkValuesCount;
            var safeItemCount = ClampItemCount(itemCount, atkValuesCount, 301, 18);
            var replacementCount = 0;
            for (var index = 0; index < safeItemCount; index++)
            {
                if (!NativeArrayAccess.TryGetAtkUInt(addon, 300 + index * 18, out var itemId))
                    break;

                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                if (!NativeArrayAccess.TrySetAtkUInt(addon, 301 + index * 18, itemRow.Icon))
                    break;

                replacementCount++;
            }

            RecordReplacement("InclusionShop", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing InclusionShop.");
        }
    }

    private void OnShop(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValuesCount <= 441 ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 0, out var currentTab) ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 2, out var itemCount))
                return;

            var atkValuesCount = (int)addon->AtkValuesCount;
            // The read at 441+index is the binding AtkValues range. The write at
            // 197+index has more room but is constrained by the same safe count.
            var safeItemCount = ClampItemCount(itemCount, atkValuesCount, 441);
            var replacementCount = 0;

            for (var index = 0; index < safeItemCount; index++)
            {
                var itemId = 0u;
                var isItemHq = false;
                switch (currentTab)
                {
                    case 0:
                        if (!NativeArrayAccess.TryGetAtkUInt(addon, 441 + index, out itemId))
                            continue;
                        break;
                    case 1:
                    {
                        var proxy = ShopEventHandler.AgentProxy.Instance();
                        if (proxy == null || proxy->Handler == null)
                            continue;

                        var handler = proxy->Handler;
                        if (!NativeArrayAccess.TryGetBuyback(handler, index, out var buybackItem))
                            continue;

                        itemId = buybackItem.ItemId;
                        isItemHq = buybackItem.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                        break;
                    }
                }

                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                if (!NativeArrayAccess.TrySetAtkUInt(addon, 197 + index, itemRow.Icon + (isItemHq ? 1000000u : 0u)))
                    break;

                replacementCount++;
            }

            if (itemCount > (uint)safeItemCount)
                log.Warning($"[XASlave] Enable Item Icon In Shops clamped Shop item count from {itemCount} to {safeItemCount} for {atkValuesCount} AtkValues.");

            RecordReplacement("Shop", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing Shop.");
        }
    }

    internal static int ClampItemCount(uint requestedCount, int atkValuesCount, int highestBaseIndex, int stride = 1)
        => NativeArrayBounds.ClampElementCount(requestedCount, atkValuesCount, highestBaseIndex, stride);

    private bool TryGetItem(uint itemId, out Item itemRow)
    {
        return dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out itemRow);
    }

    private void RecordReplacement(string addonName, int replacementCount)
    {
        LastAddonName = addonName;
        LastReplacementCount = replacementCount;
        LastActionText = $"Last action: replaced {replacementCount} shop icon(s) in {addonName} at {DateTime.Now:HH:mm:ss}.";
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private readonly record struct CollectablesShopItemData(uint ItemId, uint IconId, string NormalizedName);
}
