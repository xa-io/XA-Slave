using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class AutoUnlockExpertDeliveryService : IDisposable
{
    private const string SupplyListAddonName = "GrandCompanySupplyList";
    private const string RewardAddonName = "GrandCompanySupplyReward";
    private const string SelectYesNoAddonName = "SelectYesno";
    private const string TextErrorAddonName = "_TextError";
    private const string UnableToCompleteDeliveryText = "Unable to complete delivery";
    private const uint SupplyListLoadedState = 2;
    private const int ExpertDeliveryTab = 2;
    private const int RepeatedCandidateScanThreshold = 3;
    private const string HighQualityPromptFragment = "high-quality";
    private static readonly TimeSpan PendingSelectionTimeout = TimeSpan.FromSeconds(2);

    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private bool enabled;
    private bool frameworkSubscribed;
    private bool autoSwitchWhenOpen = true;
    private int defaultPage = ExpertDeliveryTab;
    private bool skipHqItems = true;
    private bool skipMateriaItems = true;
    private bool ignoreSealCap;
    private nint openAddonAddress;
    private nint blockedAddonAddress;
    private DateTime lastActionUtc = DateTime.MinValue;
    private string lastBlockingErrorText = string.Empty;
    private ExpertDeliveryItem? pendingItem;
    private DateTime pendingItemSelectedUtc = DateTime.MinValue;
    private readonly HashSet<ExpertDeliveryItemKey> sessionRejectedItems = new();
    private ExpertDeliveryItemKey? lastScannedItemKey;
    private int repeatedCandidateScanCount;

    public AutoUnlockExpertDeliveryService(IFramework framework, IDataManager dataManager, IPluginLog log)
    {
        this.framework = framework;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool IsEnabled => enabled;

    public string StatusText { get; private set; } = "Disabled";

    public void ApplyConfiguration(bool autoSwitchWhenOpen, int defaultPage, bool skipHqItems, bool skipMateriaItems, bool ignoreSealCap)
    {
        this.autoSwitchWhenOpen = autoSwitchWhenOpen;
        this.defaultPage = NormalizePage(defaultPage);
        this.skipHqItems = skipHqItems;
        this.skipMateriaItems = skipMateriaItems;
        this.ignoreSealCap = ignoreSealCap;

        if (enabled && openAddonAddress == nint.Zero)
            RefreshWaitingStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            Disable();
            StatusText = "Disabled";
            return false;
        }

        if (!frameworkSubscribed)
        {
            framework.Update += OnFrameworkUpdate;
            frameworkSubscribed = true;
        }

        enabled = true;
        RefreshWaitingStatusText();
        return true;
    }

    public void Dispose()
    {
        Disable();
    }

    private void Disable()
    {
        enabled = false;
        if (frameworkSubscribed)
        {
            framework.Update -= OnFrameworkUpdate;
            frameworkSubscribed = false;
        }

        ResetWindowState();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled)
            return;

        var addon = AddonHelper.GetAddon(SupplyListAddonName);

        if (IsAddonVisible(addon))
        {
            var addonAddress = (nint)addon;
            if (addonAddress != openAddonAddress)
            {
                openAddonAddress = addonAddress;
                blockedAddonAddress = nint.Zero;
                lastBlockingErrorText = string.Empty;
                lastActionUtc = DateTime.MinValue;
                pendingItem = null;
                pendingItemSelectedUtc = DateTime.MinValue;
                sessionRejectedItems.Clear();
                ResetRepeatedItemTracking();
            }

            if (TryBlockOnDeliveryError(addonAddress))
                return;

            if (blockedAddonAddress == addonAddress)
            {
                StatusText = $"Enabled - stopped after delivery error: {lastBlockingErrorText}";
                return;
            }
        }

        if (TryHandleSelectYesNoPrompt())
            return;

        if (TryClickRewardDeliver())
            return;

        if (pendingItem != null && !AddonHelper.IsAddonVisible(RewardAddonName) && !AddonHelper.IsAddonVisible(SelectYesNoAddonName))
        {
            if (DateTime.UtcNow - pendingItemSelectedUtc <= PendingSelectionTimeout)
            {
                StatusText = "Enabled - waiting for Expert Delivery confirmation window.";
                return;
            }

            log.Warning($"[XASlave] Auto Expert Delivery did not receive a reward or SelectYesno window within {PendingSelectionTimeout.TotalSeconds:F1}s after selecting item {(pendingItem?.ItemId ?? 0)}.");
            pendingItem = null;
            pendingItemSelectedUtc = DateTime.MinValue;
            ResetRepeatedItemTracking();
        }

        if (AddonHelper.IsAddonVisible(RewardAddonName))
        {
            ResetRepeatedItemTracking();
            StatusText = "Enabled - waiting for Expert Delivery reward window.";
            return;
        }

        if (!IsAddonVisible(addon) || !addon->IsReady || addon->AtkValues == null)
        {
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        if (addon->AtkValues[0].UInt != SupplyListLoadedState)
        {
            StatusText = "Enabled - waiting for Grand Company delivery data.";
            return;
        }

        var currentPage = NormalizePage((int)addon->AtkValues[5].UInt);
        if (autoSwitchWhenOpen && currentPage != defaultPage)
        {
            if (CanAct())
            {
                AddonHelper.FireCallback(SupplyListAddonName, 0, defaultPage);
                StatusText = $"Enabled - switching to {GetPageName(defaultPage)}.";
            }

            return;
        }

        if (currentPage != ExpertDeliveryTab)
        {
            pendingItem = null;
            pendingItemSelectedUtc = DateTime.MinValue;
            ResetRepeatedItemTracking();
            StatusText = autoSwitchWhenOpen && defaultPage != ExpertDeliveryTab
                ? $"Enabled - {GetPageName(defaultPage)} selected; Expert Delivery hand-ins are idle."
                : "Enabled - waiting for the Expert Delivery tab.";
            return;
        }

        var item = GetNextEligibleItem(addon);
        if (item == null)
        {
            pendingItem = null;
            pendingItemSelectedUtc = DateTime.MinValue;
            ResetRepeatedItemTracking();
            StatusText = "Enabled - no eligible Expert Delivery items found.";
            return;
        }

        if (!CanAct())
            return;

        var exhaustedAfterRepeatedSelection = false;
        item = ApplyRepeatedSelectionGuard(addon, item.Value, out exhaustedAfterRepeatedSelection);
        if (item == null)
        {
            pendingItem = null;
            pendingItemSelectedUtc = DateTime.MinValue;
            ResetRepeatedItemTracking();
            StatusText = exhaustedAfterRepeatedSelection
                ? "Enabled - reached the end of the Expert Delivery list after repeated selection attempts."
                : "Enabled - no eligible Expert Delivery items found.";
            return;
        }

        if (WouldReachSealCap(item.Value.SealReward))
        {
            StatusText = "Enabled - stopped before reaching the Company Seal cap.";
            return;
        }

        pendingItem = item.Value;
        pendingItemSelectedUtc = DateTime.UtcNow;
        AddonHelper.FireCallback(SupplyListAddonName, 1, item.Value.VisibleIndex);
        StatusText = $"Enabled - selecting item {item.Value.ItemId} for Expert Delivery.";
    }

    private void RefreshWaitingStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        StatusText = autoSwitchWhenOpen
            ? $"Enabled - waiting for Grand Company supply window (landing on {GetPageName(defaultPage)})."
            : "Enabled - waiting for Grand Company supply window.";
    }

    private void ResetWindowState()
    {
        openAddonAddress = nint.Zero;
        blockedAddonAddress = nint.Zero;
        lastActionUtc = DateTime.MinValue;
        lastBlockingErrorText = string.Empty;
        pendingItem = null;
        pendingItemSelectedUtc = DateTime.MinValue;
        sessionRejectedItems.Clear();
        ResetRepeatedItemTracking();
    }

    private static int NormalizePage(int page)
    {
        return Math.Clamp(page, 0, ExpertDeliveryTab);
    }

    private static string GetPageName(int page)
    {
        return NormalizePage(page) switch
        {
            0 => "Supply Missions",
            1 => "Provisioning Missions",
            _ => "Expert Delivery"
        };
    }

    private static bool IsAddonVisible(AtkUnitBase* addon)
    {
        return addon != null && addon->IsVisible;
    }

    private bool TryHandleSelectYesNoPrompt()
    {
        if (!AddonHelper.IsAddonReady(SelectYesNoAddonName) || !CanAct())
            return false;

        var rewardVisible = AddonHelper.IsAddonVisible(RewardAddonName);
        if (!rewardVisible && pendingItem == null)
            return false;

        var promptText = GetSelectYesNoPromptText();
        log.Information($"[XASlave] Auto Expert Delivery SelectYesno prompt: '{promptText}'");

        var pending = pendingItem;
        var isHighQualityPrompt = IsHighQualityPrompt(promptText);
        var isMateriaPrompt = IsMateriaPrompt(promptText);
        var isSealCapPrompt = IsSealCapPrompt(promptText);

        if (isSealCapPrompt)
        {
            if (ignoreSealCap)
            {
                AddonHelper.ClickYesNo(true, false);
                pendingItemSelectedUtc = DateTime.MinValue;
                ResetRepeatedItemTracking();
                StatusText = "Enabled - ignoring Company Seal cap warning and continuing deliveries.";
                pendingItem = null;
                return true;
            }

            if (pending is { } sealRejectedItem)
            {
                sessionRejectedItems.Add(sealRejectedItem.GetKey());
                log.Information($"[XASlave] Auto Expert Delivery rejected item {sealRejectedItem.ItemId} after Company Seal cap warning.");
            }

            AddonHelper.ClickYesNo(false, false);
            AddonHelper.CloseAddon(RewardAddonName);
            pendingItemSelectedUtc = DateTime.MinValue;
            ResetRepeatedItemTracking();
            StatusText = "Enabled - skipped item because the next turn-in would exceed the Company Seal cap.";
            pendingItem = null;
            return true;
        }

        if (pending is { } itemPrompt)
        {
            var shouldSkipForPrompt =
                (isHighQualityPrompt && skipHqItems) ||
                (isMateriaPrompt && skipMateriaItems) ||
                (!isHighQualityPrompt && !isMateriaPrompt &&
                 ((itemPrompt.IsHighQuality && skipHqItems) || (itemPrompt.HasMateria && skipMateriaItems)));

            var shouldConfirmForPrompt =
                (isHighQualityPrompt && !skipHqItems) ||
                (isMateriaPrompt && !skipMateriaItems) ||
                (!isHighQualityPrompt && !isMateriaPrompt &&
                 ((itemPrompt.IsHighQuality && !skipHqItems) || (itemPrompt.HasMateria && !skipMateriaItems)));

            if (shouldSkipForPrompt)
            {
                sessionRejectedItems.Add(itemPrompt.GetKey());
                log.Information($"[XASlave] Auto Expert Delivery rejected item {itemPrompt.ItemId} after SelectYesno prompt.");

                AddonHelper.ClickYesNo(false, false);
                AddonHelper.CloseAddon(RewardAddonName);
                pendingItemSelectedUtc = DateTime.MinValue;
                ResetRepeatedItemTracking();
                StatusText = $"Enabled - skipped item {itemPrompt.ItemId} after SelectYesno prompt.";
                pendingItem = null;
                return true;
            }

            if (shouldConfirmForPrompt)
            {
                AddonHelper.ClickYesNo(true, false);
                pendingItemSelectedUtc = DateTime.MinValue;
                ResetRepeatedItemTracking();
                StatusText = $"Enabled - confirming prompt for item {itemPrompt.ItemId}.";
                pendingItem = null;
                return true;
            }
        }

        log.Warning($"[XASlave] Auto Expert Delivery encountered an unclassified SelectYesno prompt and rejected it: '{promptText}'");
        AddonHelper.ClickYesNo(false, false);
        AddonHelper.CloseAddon(RewardAddonName);
        pendingItemSelectedUtc = DateTime.MinValue;
        ResetRepeatedItemTracking();
        StatusText = "Enabled - rejected an unclassified Expert Delivery confirmation prompt.";
        pendingItem = null;
        return true;
    }

    private bool TryClickRewardDeliver()
    {
        var addon = AddonHelper.GetAddon(RewardAddonName);
        if (!IsAddonVisible(addon) || !addon->IsReady || !CanAct())
            return false;

        ((AddonGrandCompanySupplyReward*)addon)->AtkUnitBase.FireCallbackInt(0);
        pendingItemSelectedUtc = DateTime.MinValue;
        ResetRepeatedItemTracking();
        StatusText = "Enabled - delivering selected item.";
        return true;
    }

    private bool TryBlockOnDeliveryError(nint supplyAddonAddress)
    {
        var errorText = AddonHelper.GetFirstAddonText(TextErrorAddonName, UnableToCompleteDeliveryText, true);
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        blockedAddonAddress = supplyAddonAddress;
        lastBlockingErrorText = errorText.Trim();
        StatusText = $"Enabled - stopped after delivery error: {lastBlockingErrorText}";
        return true;
    }

    private bool CanAct()
    {
        var now = DateTime.UtcNow;
        if ((now - lastActionUtc).TotalMilliseconds < 600)
            return false;

        lastActionUtc = now;
        return true;
    }

    private ExpertDeliveryItem? GetNextEligibleItem(AtkUnitBase* addon)
    {
        var agent = AgentGrandCompanySupply.Instance();
        if (agent == null || agent->ItemArray == null)
            return null;

        var items = new List<ExpertDeliveryItem>();
        for (var i = 0U; i < agent->NumItems; i++)
        {
            var item = agent->ItemArray[i];
            if (item.ItemId == 0 || item.IsBonusReward || item.ExpReward > 0 || item.SealReward <= 0)
                continue;

            var inventorySlot = InventoryManager.Instance()->GetInventorySlot(item.Inventory, item.Slot);
            if (inventorySlot == null)
                continue;

            var visibleIndex = ResolveVisibleIndex(addon, item.ItemId, item.Inventory, item.Slot, (uint)item.SealReward);
            if (visibleIndex < 0)
                continue;

            items.Add(new ExpertDeliveryItem(
                item.ItemId,
                item.Inventory,
                item.Slot,
                (uint)item.SealReward,
                visibleIndex,
                inventorySlot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                HasMateriaAttached(item.ItemId, inventorySlot)));
        }

        foreach (var item in items)
        {
            if (sessionRejectedItems.Contains(item.GetKey()) || ShouldSkipItem(item))
                continue;

            return item;
        }

        return null;
    }

    private ExpertDeliveryItem? ApplyRepeatedSelectionGuard(AtkUnitBase* addon, ExpertDeliveryItem candidate, out bool exhaustedAfterRepeatedSelection)
    {
        exhaustedAfterRepeatedSelection = false;

        var key = candidate.GetKey();
        if (lastScannedItemKey is { } lastKey && lastKey.Equals(key))
        {
            repeatedCandidateScanCount++;
        }
        else
        {
            lastScannedItemKey = key;
            repeatedCandidateScanCount = 1;
        }

        if (repeatedCandidateScanCount < RepeatedCandidateScanThreshold)
            return candidate;

        sessionRejectedItems.Add(key);
        log.Information($"[XASlave] Auto Expert Delivery tried item {candidate.ItemId} {repeatedCandidateScanCount} times in a row and is treating it as end-of-list for this supply window.");
        ResetRepeatedItemTracking();

        var nextItem = GetNextEligibleItem(addon);
        exhaustedAfterRepeatedSelection = nextItem == null;
        return nextItem;
    }

    private void ResetRepeatedItemTracking()
    {
        lastScannedItemKey = null;
        repeatedCandidateScanCount = 0;
    }

    private int ResolveVisibleIndex(AtkUnitBase* addon, uint itemId, InventoryType container, ushort slot, uint sealReward)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValues[5].UInt != ExpertDeliveryTab)
            return -1;

        var itemCount = addon->AtkValues[6].UInt;
        if (itemCount == 0)
            return -1;

        for (var i = 0; i < Math.Min(40, itemCount); i++)
        {
            var visibleSealReward = addon->AtkValues[265 + i].UInt;
            var visibleContainer = (InventoryType)addon->AtkValues[345 + i].UInt;
            var visibleSlot = (ushort)addon->AtkValues[385 + i].UInt;
            var visibleItemId = addon->AtkValues[425 + i].UInt;
            if (visibleItemId == itemId && visibleContainer == container && visibleSlot == slot && visibleSealReward == sealReward)
                return i;
        }

        return -1;
    }

    private bool ShouldSkipItem(ExpertDeliveryItem item)
    {
        if (skipHqItems && item.IsHighQuality)
            return true;

        return skipMateriaItems && item.HasMateria;
    }

    private bool WouldReachSealCap(uint sealReward)
    {
        if (ignoreSealCap)
            return false;

        var playerState = PlayerState.Instance();
        if (playerState == null || playerState->GrandCompany == 0)
            return true;

        var realRank = GetRealGrandCompanyRank(playerState);
        if (realRank == 0)
            return true;

        var rankSheet = dataManager.GetExcelSheet<GrandCompanyRank>();
        if (!rankSheet.TryGetRow(realRank, out var rank))
            return true;

        var currentSeals = InventoryManager.Instance()->GetCompanySeals(playerState->GrandCompany);
        return currentSeals + sealReward > rank.MaxSeals;
    }

    private bool HasMateriaAttached(uint itemId, InventoryItem* slot)
    {
        if (slot == null)
            return false;

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var itemRow) || itemRow.MateriaSlotCount <= 0)
            return false;

        for (var i = 0; i < Math.Min(itemRow.MateriaSlotCount, slot->Materia.Length); i++)
        {
            if (slot->Materia[i] != 0)
                return true;
        }

        return false;
    }

    private static string GetSelectYesNoPromptText()
    {
        var addon = (AddonSelectYesno*)AddonHelper.GetAddon(SelectYesNoAddonName);
        if (addon == null || addon->PromptText == null)
            return string.Empty;

        return addon->PromptText->NodeText.ToString().ReplaceLineEndings("");
    }

    private static bool IsHighQualityPrompt(string promptText)
    {
        return !string.IsNullOrWhiteSpace(promptText) &&
               promptText.Contains(HighQualityPromptFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMateriaPrompt(string promptText)
    {
        return !string.IsNullOrWhiteSpace(promptText) &&
               promptText.Contains("materia", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSealCapPrompt(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            return false;

        var hasSealWord = promptText.Contains("company seal", StringComparison.OrdinalIgnoreCase) ||
                          promptText.Contains("company seals", StringComparison.OrdinalIgnoreCase) ||
                          promptText.Contains("seals", StringComparison.OrdinalIgnoreCase);
        if (!hasSealWord)
            return false;

        return promptText.Contains("carry", StringComparison.OrdinalIgnoreCase) ||
               promptText.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
               promptText.Contains("max", StringComparison.OrdinalIgnoreCase) ||
               promptText.Contains("lose", StringComparison.OrdinalIgnoreCase) ||
               promptText.Contains("lost", StringComparison.OrdinalIgnoreCase) ||
               promptText.Contains("discard", StringComparison.OrdinalIgnoreCase) ||
               promptText.Contains("more", StringComparison.OrdinalIgnoreCase);
    }

    private static byte GetRealGrandCompanyRank(PlayerState* playerState)
    {
        return playerState->GrandCompany switch
        {
            1 => playerState->GCRanks[0],
            2 => playerState->GCRanks[1],
            3 => playerState->GCRanks[2],
            _ => 0,
        };
    }

    private readonly record struct ExpertDeliveryItemKey(uint ItemId, InventoryType Container, ushort Slot);

    private readonly record struct ExpertDeliveryItem(uint ItemId, InventoryType Container, ushort Slot, uint SealReward, int VisibleIndex, bool IsHighQuality, bool HasMateria)
    {
        public ExpertDeliveryItemKey GetKey()
        {
            return new ExpertDeliveryItemKey(ItemId, Container, Slot);
        }
    }
}
