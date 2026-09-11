using System;
using System.Collections.Generic;
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
    private const int MaximumSelectionAttempts = 3;
    private const int MaximumVisibleItems = 40;
    private const int MaximumNativeItems = 1024;
    private const string HighQualityPromptFragment = "high-quality";

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
    private bool sealCapRejected;
    private nint openAddonAddress;
    private nint blockedAddonAddress;
    private long nextScanTick;
    private readonly ExpertDeliveryProgress progress = new();
    private string lastBlockingErrorText = string.Empty;
    private ExpertDeliveryItem? pendingItem;
    private string? handledPromptText;
    private readonly HashSet<ExpertDeliveryItemKey> sessionRejectedItems = new();
    private readonly HashSet<ExpertDeliveryItemKey> failedItems = new();
    private readonly Dictionary<ExpertDeliveryItemKey, int> selectionAttempts = new();
    private readonly Dictionary<ExpertDeliveryItemKey, uint> nativeRewards = new();

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
        if (ignoreSealCap)
            sealCapRejected = false;
        progress.ResetEmptyObservation();
        sessionRejectedItems.Clear();

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
        try
        {
            UpdateDelivery(Environment.TickCount64);
        }
        catch (Exception ex)
        {
            StopForWindow("could not safely read or update the delivery window.");
            log.Warning(ex, "[XASlave] Expert Delivery update failed.");
        }
    }

    private void UpdateDelivery(long now)
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        var addon = AddonHelper.GetAddon(SupplyListAddonName);
        var rewardVisible = AddonHelper.IsAddonVisible(RewardAddonName);
        var promptVisible = AddonHelper.IsAddonVisible(SelectYesNoAddonName);
        if (addon == null || (!IsAddonVisible(addon) && !(pendingItem != null && (rewardVisible || promptVisible))))
        {
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        var addonAddress = (nint)addon;
        if (addonAddress != openAddonAddress)
        {
            ResetWindowState();
            openAddonAddress = addonAddress;
        }
        if (blockedAddonAddress == addonAddress)
        {
            StatusText = $"Enabled - stopped after delivery error: {lastBlockingErrorText}";
            return;
        }
        if (TryBlockOnDeliveryError(addonAddress))
            return;

        if (pendingItem != null)
        {
            if (progress.TransactionTimedOut(now))
            {
                StopForWindow("the selected delivery did not settle; reopen the supply window to retry.");
                return;
            }

            if (promptVisible)
            {
                TryHandleSelectYesNoPrompt(now);
                return;
            }
            handledPromptText = null;
            if (rewardVisible)
            {
                TryClickRewardDeliver(now);
                return;
            }

            if (progress.Phase != ExpertDeliveryPhase.Refreshing)
            {
                if (!TryReadPendingInventory(out var itemRemoved))
                {
                    progress.ResetEmptyObservation();
                    StatusText = "Enabled - waiting for selected item inventory data.";
                    return;
                }
                if (progress.Rejected || (itemRemoved && (progress.DeliverySent || progress.ConfirmationSent)))
                {
                    progress.MarkRefreshing();
                    nextScanTick = 0;
                }
                else if (itemRemoved)
                {
                    StopForWindow("the selected inventory item changed before delivery.");
                    return;
                }
                else if (progress.SelectionTimedOut(now))
                {
                    var timedOut = pendingItem.Value;
                    if (selectionAttempts.GetValueOrDefault(timedOut.GetKey()) >= MaximumSelectionAttempts)
                        failedItems.Add(timedOut.GetKey());
                    log.Warning($"[XASlave] Expert Delivery selection of item {timedOut.ItemId} did not open a dialog within 2.0s.");
                    FinishPendingItem(false);
                    StatusText = "Enabled - waiting to retry an unconfirmed item selection.";
                    return;
                }
                else
                {
                    StatusText = progress.DeliverySent || progress.ConfirmationSent
                        ? "Enabled - waiting for the selected delivery to finish."
                        : "Enabled - waiting for Expert Delivery confirmation window.";
                    return;
                }
            }
        }
        else if (rewardVisible || promptVisible)
        {
            // A manually opened or unrelated dialog is not owned by this transaction.
            progress.ResetEmptyObservation();
            StatusText = "Enabled - waiting for the open dialog to close.";
            return;
        }

        if (!addon->IsReady ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 0, out var loadedState) ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 5, out var rawCurrentPage) ||
            loadedState != SupplyListLoadedState)
        {
            progress.ResetEmptyObservation();
            StatusText = "Enabled - waiting for Grand Company delivery data.";
            return;
        }
        if (rawCurrentPage > ExpertDeliveryTab)
        {
            progress.ResetEmptyObservation();
            StatusText = "Enabled - waiting for a valid Grand Company delivery page.";
            return;
        }

        var currentPage = (int)rawCurrentPage;
        if (pendingItem != null && currentPage != ExpertDeliveryTab)
        {
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }
        if (autoSwitchWhenOpen && currentPage != defaultPage)
        {
            progress.ResetEmptyObservation();
            if (!progress.TryBeginAction(ExpertDeliveryAction.SwitchPage, now))
                return;
            if (!AddonHelper.FireCallback(SupplyListAddonName, 0, defaultPage))
            {
                if (progress.RecordCallbackFailure(ExpertDeliveryAction.SwitchPage))
                    StopForWindow("the delivery page callback failed three times.");
                else
                    StatusText = "Enabled - waiting to switch the delivery page.";
                return;
            }
            progress.ClearCallbackFailures(ExpertDeliveryAction.SwitchPage);
            StatusText = $"Enabled - switching to {GetPageName(defaultPage)}.";
            return;
        }
        if (currentPage != ExpertDeliveryTab)
        {
            progress.ResetEmptyObservation();
            StatusText = autoSwitchWhenOpen && defaultPage != ExpertDeliveryTab
                ? $"Enabled - {GetPageName(defaultPage)} selected; Expert Delivery hand-ins are idle."
                : "Enabled - waiting for the Expert Delivery tab.";
            return;
        }

        if (now < nextScanTick)
            return;
        nextScanTick = now + ExpertDeliveryProgress.ScanIntervalMilliseconds;
        var scan = GetNextEligibleItem(addon);
        if (!scan.Ready)
        {
            progress.ResetEmptyObservation();
            StatusText = $"Enabled - waiting for delivery list refresh ({scan.WaitReason}).";
            return;
        }

        if (pendingItem != null)
            FinishPendingItem(!progress.Rejected);

        if (sealCapRejected && !ignoreSealCap)
        {
            StatusText = "Enabled - stopped before reaching the Company Seal cap.";
            return;
        }

        if (scan.Item is not { } item)
        {
            if (!progress.ObserveEmpty(scan.Fingerprint, now))
            {
                StatusText = "Enabled - checking the settled Expert Delivery list.";
                return;
            }
            if (scan.HasFailedItems)
                StopForWindow("remaining items could not be selected after repeated attempts.");
            else
                StatusText = "Enabled - no eligible Expert Delivery items found.";
            return;
        }

        progress.ResetEmptyObservation();
        if (WouldReachSealCap(item.SealReward))
        {
            StatusText = "Enabled - stopped before reaching the Company Seal cap.";
            return;
        }
        if (!progress.TryBeginAction(ExpertDeliveryAction.Select, now))
            return;

        var key = item.GetKey();
        var attempts = selectionAttempts.GetValueOrDefault(key) + 1;
        selectionAttempts[key] = attempts;
        if (!AddonHelper.FireCallback(SupplyListAddonName, 1, item.VisibleIndex))
        {
            if (attempts >= MaximumSelectionAttempts)
                failedItems.Add(key);
            StatusText = $"Enabled - could not select item {item.ItemId}; waiting to rescan.";
            return;
        }
        pendingItem = item;
        progress.BeginSelection(now);
        handledPromptText = null;
        StatusText = $"Enabled - selecting item {item.ItemId} for Expert Delivery.";
    }

    private bool TryReadPendingInventory(out bool itemRemoved)
    {
        itemRemoved = false;
        if (pendingItem is not { } item ||
            !NativeArrayAccess.TryGetInventorySlot(InventoryManager.Instance(), item.Container, item.Slot, out var slot))
            return false;
        itemRemoved = ExpertDeliveryProgress.ItemWasRemoved(item.ItemId, item.Quantity, slot->ItemId, slot->Quantity);
        return true;
    }

    private void FinishPendingItem(bool completed)
    {
        if (completed && pendingItem is { } item)
        {
            selectionAttempts.Remove(item.GetKey());
            failedItems.Remove(item.GetKey());
        }
        pendingItem = null;
        handledPromptText = null;
        progress.FinishTransaction();
    }

    private void StopForWindow(string reason)
    {
        blockedAddonAddress = openAddonAddress;
        lastBlockingErrorText = reason;
        progress.ResetEmptyObservation();
        StatusText = $"Enabled - stopped after delivery error: {reason}";
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
        nextScanTick = 0;
        lastBlockingErrorText = string.Empty;
        pendingItem = null;
        handledPromptText = null;
        progress.Reset();
        sealCapRejected = false;
        sessionRejectedItems.Clear();
        failedItems.Clear();
        selectionAttempts.Clear();
        nativeRewards.Clear();
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

    private void TryHandleSelectYesNoPrompt(long now)
    {
        var addon = (AddonSelectYesno*)AddonHelper.GetAddon(SelectYesNoAddonName);
        if (pendingItem is not { } item || addon == null || !addon->AtkUnitBase.IsReady ||
            addon->PromptText == null)
        {
            StatusText = "Enabled - waiting for the confirmation dialog to become ready.";
            return;
        }

        var promptText = GetSelectYesNoPromptText();
        if (string.IsNullOrWhiteSpace(promptText) || handledPromptText == promptText)
        {
            StatusText = "Enabled - waiting for the confirmation dialog to settle.";
            return;
        }

        var isHq = IsHighQualityPrompt(promptText);
        var isMateria = IsMateriaPrompt(promptText);
        var isCap = IsSealCapPrompt(promptText);
        // Only recognized transaction prompts may be accepted.
        var accept = !progress.Rejected && !ShouldSkipItem(item) && (isCap ? ignoreSealCap
            : (isHq || isMateria) && !(isHq && skipHqItems) && !(isMateria && skipMateriaItems));
        var button = accept ? addon->YesButton : addon->NoButton;
        if (button == null || !button->IsEnabled ||
            !progress.TryBeginAction(ExpertDeliveryAction.Confirm, now))
            return;
        if (!AddonHelper.ClickYesNo(accept, false))
        {
            if (progress.RecordCallbackFailure(ExpertDeliveryAction.Confirm))
            {
                StopForWindow("the confirmation callback failed three times.");
                return;
            }
            StatusText = "Enabled - waiting to retry the confirmation callback.";
            return;
        }

        progress.ClearCallbackFailures(ExpertDeliveryAction.Confirm);
        handledPromptText = promptText;
        progress.MarkConfirmationSent(!accept);
        if (!accept)
        {
            if (isCap && !ignoreSealCap)
                sealCapRejected = true;
            sessionRejectedItems.Add(item.GetKey());
            if (!isHq && !isMateria && !isCap)
                failedItems.Add(item.GetKey());
            AddonHelper.CloseAddon(RewardAddonName);
        }
        log.Information($"[XASlave] Expert Delivery {(accept ? "confirmed" : "rejected")} a prompt for item {item.ItemId}.");
        StatusText = accept
            ? $"Enabled - confirming prompt for item {item.ItemId}."
            : $"Enabled - rejected a confirmation for item {item.ItemId}; waiting for the list.";
    }

    private void TryClickRewardDeliver(long now)
    {
        if (pendingItem is not { } item)
            return;
        var addon = (AddonGrandCompanySupplyReward*)AddonHelper.GetAddon(RewardAddonName);
        if (addon == null || !addon->AtkUnitBase.IsReady)
        {
            StatusText = "Enabled - waiting for Expert Delivery reward window.";
            return;
        }
        if (progress.Rejected)
        {
            if (progress.TryBeginAction(ExpertDeliveryAction.Deliver, now))
                AddonHelper.CloseAddon(RewardAddonName);
            return;
        }
        if (progress.DeliverySent)
        {
            StatusText = "Enabled - waiting for the delivery reward window to close.";
            return;
        }
        if (!TryReadPendingInventory(out var itemRemoved))
        {
            StatusText = "Enabled - waiting for selected item inventory data.";
            return;
        }
        if (itemRemoved)
        {
            StopForWindow("the selected inventory item changed before the Deliver callback.");
            return;
        }
        var wouldReachCap = WouldReachSealCap(item.SealReward);
        if (ShouldSkipItem(item) || wouldReachCap)
        {
            sealCapRejected |= wouldReachCap;
            sessionRejectedItems.Add(item.GetKey());
            progress.MarkConfirmationSent(true);
            AddonHelper.CloseAddon(RewardAddonName);
            StatusText = "Enabled - skipped the selected item because its protection settings changed.";
            return;
        }
        if (addon->DeliverButton == null || !addon->DeliverButton->IsEnabled ||
            !progress.TryBeginAction(ExpertDeliveryAction.Deliver, now))
            return;
        if (!AddonHelper.FireCallback(RewardAddonName, 0))
        {
            if (progress.RecordCallbackFailure(ExpertDeliveryAction.Deliver))
            {
                StopForWindow("the Deliver callback failed three times.");
                return;
            }
            StatusText = "Enabled - waiting to retry the Deliver callback.";
            return;
        }
        progress.ClearCallbackFailures(ExpertDeliveryAction.Deliver);
        progress.MarkDeliverySent();
        StatusText = "Enabled - delivering selected item.";
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

    private ExpertDeliveryScan GetNextEligibleItem(AtkUnitBase* addon)
    {
        var agent = AgentGrandCompanySupply.Instance();
        var inventory = InventoryManager.Instance();
        if (agent == null || agent->ItemArray == null || inventory == null ||
            agent->NumItems is < 0 or > MaximumNativeItems ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 6, out var itemCount))
            return ExpertDeliveryScan.Wait("native list unavailable");

        var fingerprint = 14695981039346656037UL;
        void Observe(uint value) => fingerprint = unchecked((fingerprint ^ value) * 1099511628211UL);
        Observe((uint)agent->NumItems);
        Observe(itemCount);
        var player = PlayerState.Instance();
        if (player == null || player->GrandCompany == 0)
            return ExpertDeliveryScan.Wait("player data unavailable");
        Observe(inventory->GetCompanySeals(player->GrandCompany));

        // Build one native lookup, then read each visible row at most once.
        nativeRewards.Clear();
        for (var i = 0; i < agent->NumItems; i++)
        {
            var native = agent->ItemArray[i];
            if (native.ItemId == 0 || native.IsBonusReward || native.ExpReward > 0 || native.SealReward <= 0)
                continue;
            var key = new ExpertDeliveryItemKey(native.ItemId, native.Inventory, native.Slot);
            if (!nativeRewards.TryAdd(key, (uint)native.SealReward))
                return ExpertDeliveryScan.Wait("duplicate native slots");
            Observe(native.ItemId);
            Observe((uint)native.Inventory);
            Observe(native.Slot);
            Observe((uint)native.SealReward);
        }

        var visibleCount = (int)Math.Min((uint)MaximumVisibleItems, itemCount);
        if (NativeArrayBounds.ClampElementCount((uint)visibleCount, addon->AtkValuesCount, 425) != visibleCount)
            return ExpertDeliveryScan.Wait("visible rows unavailable");

        var hasFailedItems = false;
        for (var i = 0; i < visibleCount; i++)
        {
            if (!NativeArrayAccess.TryGetAtkUInt(addon, 265 + i, out var reward) ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 345 + i, out var containerValue) ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 385 + i, out var slotValue) ||
                !NativeArrayAccess.TryGetAtkUInt(addon, 425 + i, out var itemId) ||
                itemId == 0 || slotValue > ushort.MaxValue)
                return ExpertDeliveryScan.Wait("visible row incomplete");

            var container = (InventoryType)containerValue;
            var key = new ExpertDeliveryItemKey(itemId, container, (ushort)slotValue);
            if (!nativeRewards.TryGetValue(key, out var nativeReward) || reward != nativeReward)
                return ExpertDeliveryScan.Wait("native and visible rows differ");
            if (!NativeArrayAccess.TryGetInventorySlot(inventory, container, (int)slotValue, out var slot) ||
                slot->ItemId != itemId || slot->Quantity == 0)
                return ExpertDeliveryScan.Wait("inventory and visible rows differ");

            var item = new ExpertDeliveryItem(itemId, container, (ushort)slotValue, reward, i,
                slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), HasMateriaAttached(itemId, slot), slot->Quantity);
            Observe(itemId);
            Observe(containerValue);
            Observe(slotValue);
            Observe(reward);
            Observe(unchecked((uint)slot->Quantity));
            Observe((uint)slot->Flags);
            Observe(item.HasMateria ? 1u : 0u);
            if (failedItems.Contains(key))
            {
                hasFailedItems = true;
                continue;
            }
            if (sessionRejectedItems.Contains(key) || ShouldSkipItem(item))
                continue;
            return new ExpertDeliveryScan(true, item, fingerprint, false, string.Empty);
        }

        // Never infer exhaustion from a partial view whose remaining rows cannot be read.
        if (itemCount > MaximumVisibleItems)
            return ExpertDeliveryScan.Wait("additional rows are not available in the current view");
        return new ExpertDeliveryScan(true, null, fingerprint, hasFailedItems, string.Empty);
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

        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return true;
        var currentSeals = inventory->GetCompanySeals(playerState->GrandCompany);
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

    private readonly record struct ExpertDeliveryScan(bool Ready, ExpertDeliveryItem? Item, ulong Fingerprint, bool HasFailedItems, string WaitReason)
    {
        public static ExpertDeliveryScan Wait(string reason) => new(false, null, 0, false, reason);
    }

    private readonly record struct ExpertDeliveryItemKey(uint ItemId, InventoryType Container, ushort Slot);

    private readonly record struct ExpertDeliveryItem(uint ItemId, InventoryType Container, ushort Slot, uint SealReward, int VisibleIndex, bool IsHighQuality, bool HasMateria, int Quantity)
    {
        public ExpertDeliveryItemKey GetKey()
        {
            return new ExpertDeliveryItemKey(ItemId, Container, Slot);
        }
    }
}
