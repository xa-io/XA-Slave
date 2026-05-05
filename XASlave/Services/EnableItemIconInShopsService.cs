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
            if (addon == null || addon->AtkValues == null)
                return;

            var atkValuesCount = (int)addon->AtkValuesCount;
            if (atkValuesCount <= FreeShopIconStartIndex)
                return;

            var itemCount = addon->AtkValues[FreeShopItemCountIndex].UInt;
            if (itemCount == 0)
            {
                RecordReplacement("FreeShop", 0);
                return;
            }

            var maxReadableItems = atkValuesCount - FreeShopItemIdStartIndex;
            var maxWritableItems = atkValuesCount - FreeShopIconStartIndex;
            var safeItemCount = (int)Math.Min(itemCount, (uint)Math.Min(maxReadableItems, maxWritableItems));

            var replacementCount = 0;
            for (var index = 0; index < safeItemCount; index++)
            {
                var itemId = addon->AtkValues[FreeShopItemIdStartIndex + index].UInt;
                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                addon->AtkValues[FreeShopIconStartIndex + index].SetUInt(itemRow.Icon);
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
                var itemCount = addon->AtkValues[20].UInt;
                for (var index = 0; index < itemCount; index++)
                {
                    var itemId = addon->AtkValues[34 + 11 * index].UInt % 500000;
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
            for (var index = 0; index < 15; index++)
            {
                var listItemComponent = (AtkComponentNode*)listComponent->Component->UldManager.NodeList[16 + index];
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
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 1064)
                return;

            var itemCount = addon->AtkValues[4].UInt;
            var replacementCount = 0;
            for (var index = 0; index < itemCount; index++)
            {
                var itemId = addon->AtkValues[1064 + index].UInt;
                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                addon->AtkValues[210 + index].SetUInt(itemRow.Icon);
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
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 317)
                return;

            var itemCount = addon->AtkValues[1].UInt;
            var replacementCount = 0;
            for (var index = 0; index < itemCount; index++)
            {
                var itemId = addon->AtkValues[317 + index].UInt;
                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                addon->AtkValues[167 + index].SetUInt(itemRow.Icon);
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
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 301)
                return;

            var itemCount = addon->AtkValues[298].UInt;
            var replacementCount = 0;
            for (var index = 0; index < itemCount; index++)
            {
                var itemId = addon->AtkValues[300 + index * 18].UInt;
                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                addon->AtkValues[301 + index * 18].SetUInt(itemRow.Icon);
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
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 441)
                return;

            var currentTab = addon->AtkValues[0].UInt;
            var itemCount = addon->AtkValues[2].UInt;
            var replacementCount = 0;

            for (var index = 0; index < itemCount; index++)
            {
                var itemId = 0u;
                var isItemHq = false;
                switch (currentTab)
                {
                    case 0:
                        itemId = addon->AtkValues[441 + index].UInt;
                        break;
                    case 1:
                    {
                        var buybackItem = ShopEventHandler.AgentProxy.Instance()->Handler->Buyback[index];
                        itemId = buybackItem.ItemId;
                        isItemHq = buybackItem.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                        break;
                    }
                }

                if (itemId == 0 || !TryGetItem(itemId, out var itemRow))
                    continue;

                addon->AtkValues[197 + index].SetUInt(itemRow.Icon + (isItemHq ? 1000000u : 0u));
                replacementCount++;
            }

            RecordReplacement("Shop", replacementCount);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Enable Item Icon In Shops failed while processing Shop.");
        }
    }

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
