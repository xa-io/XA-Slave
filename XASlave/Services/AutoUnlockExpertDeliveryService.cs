using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
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
    private const uint HighQualityPromptRow = 102434;
    private const uint MateriaPromptRow = 102433;
    private const uint SealCapPromptRow = 4605;

    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Func<bool?> getExternalDeliveryOperation;
    private readonly Func<uint, bool?> getExternalItemProtection;

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

    private readonly HashSet<ExpertDeliveryItemKey> scannedKeys = new();
    private readonly HashSet<uint> gearsetItems = new();
    private HashSet<uint> protectedIds = new();
    private readonly Dictionary<uint, bool> externalProtection = new();
    private int itemScope = 1;
    private int scanInterval = 100;
    private bool useExternalProtection, closeOnCompletion, notifyOutcome, runCompletionCommand;
    private bool protectedIdsValid = true;
    private string completionCommand = string.Empty;
    private nint rewardAddress;
    private uint sealsAtSelection;
    private long runId, runStarted;
    private long unavailableSince;
    private long externalProtectionRetryAfter;
    private bool terminal;
    private bool ownsDeliverySession;
    private int delivered, protectedCount, capBlockedCount;
    private string pendingCompletionCommand = string.Empty;
    private long completionCloseDeadline;
    public ExpertDeliveryResult? LastResult { get; private set; }
    public bool IsProcessing => enabled && ownsDeliverySession && !terminal;
    public long RunId => runId;
    public int DeliveredCount => delivered;
    public string ListSummary { get; private set; } = string.Empty;
    public event Action<ExpertDeliveryResult>? RunFinished;

    public AutoUnlockExpertDeliveryService(IFramework framework, IDataManager dataManager, IPluginLog log, Func<bool?> getExternalDeliveryOperation, Func<uint, bool?> getExternalItemProtection)
    {
        this.framework = framework;
        this.dataManager = dataManager;
        this.log = log;
        this.getExternalDeliveryOperation = getExternalDeliveryOperation;
        this.getExternalItemProtection = getExternalItemProtection;
    }

    public bool IsEnabled => enabled;

    public string StatusText { get; private set; } = "Disabled";

    public bool RequestNewRun()
    {
        if (!enabled || pendingItem != null || pendingCompletionCommand.Length > 0 ||
            AddonHelper.IsAddonVisible(RewardAddonName) || AddonHelper.IsAddonVisible(SelectYesNoAddonName)) return false;
        if (openAddonAddress != 0 && !terminal) PublishOutcome(ExpertDeliveryOutcome.Cancelled, "restarted by the operator.");
        ResetWindowState();
        RefreshWaitingStatusText();
        return true;
    }

    public void ApplyConfiguration(bool autoSwitchWhenOpen, int defaultPage, bool skipHqItems, bool skipMateriaItems, bool ignoreSealCap,
        int speedProfile = 0, int itemScope = 1, bool useExternalProtection = false, string? protectedItemIds = "",
        bool closeOnCompletion = false, bool notifyOutcome = false, bool runCompletionCommand = false, string? completionCommand = "")
    {
        this.autoSwitchWhenOpen = autoSwitchWhenOpen;
        this.defaultPage = NormalizePage(defaultPage);
        this.skipHqItems = skipHqItems;
        this.skipMateriaItems = skipMateriaItems;
        this.ignoreSealCap = ignoreSealCap;
        this.itemScope = Math.Clamp(itemScope, 0, 2);
        this.useExternalProtection = useExternalProtection;
        this.closeOnCompletion = closeOnCompletion;
        this.notifyOutcome = notifyOutcome;
        this.runCompletionCommand = runCompletionCommand;
        if (!runCompletionCommand) pendingCompletionCommand = string.Empty;
        this.completionCommand = completionCommand ?? string.Empty;
        protectedIdsValid = ExpertDeliveryPolicy.TryParseProtectedIds(protectedItemIds, out protectedIds);
        var timing = ExpertDeliveryPolicy.Timing(speedProfile);
        progress.ActionInterval = timing.Action;
        scanInterval = timing.Scan;
        externalProtection.Clear();
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
        pendingCompletionCommand = string.Empty;
        enabled = false;
        if (frameworkSubscribed)
        {
            framework.Update -= OnFrameworkUpdate;
            frameworkSubscribed = false;
        }

        try
        {
            if (openAddonAddress != 0 && !terminal) PublishOutcome(ExpertDeliveryOutcome.Cancelled, "cancelled by the operator.");
        }
        finally { ResetWindowState(); }
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
        if (pendingCompletionCommand.Length > 0)
        {
            if (!Plugin.ClientState.IsLoggedIn || now > completionCloseDeadline)
            {
                pendingCompletionCommand = string.Empty;
                log.Warning("[XASlave] Expert Delivery follow-up cancelled: delivery windows did not close or the character logged out.");
            }
            else if (!AddonHelper.IsAddonVisible(SupplyListAddonName) && !AddonHelper.IsAddonVisible(RewardAddonName) && !AddonHelper.IsAddonVisible(SelectYesNoAddonName))
            {
                var command = pendingCompletionCommand;
                pendingCompletionCommand = string.Empty;
                ChatHelper.TrySend(command);
            }
            return;
        }
        if (!Plugin.ClientState.IsLoggedIn)
        {
            PublishOutcome(ExpertDeliveryOutcome.Cancelled, "character logged out.");
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        var addon = AddonHelper.GetAddon(SupplyListAddonName);
        var rewardVisible = AddonHelper.IsAddonVisible(RewardAddonName);
        var promptVisible = AddonHelper.IsAddonVisible(SelectYesNoAddonName);
        if (addon == null || (!IsAddonVisible(addon) && !(pendingItem != null && (rewardVisible || promptVisible))))
        {
            PublishOutcome(ExpertDeliveryOutcome.Cancelled, "delivery window closed before completion.");
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        var addonAddress = (nint)addon;
        if (addonAddress != openAddonAddress)
        {
            PublishOutcome(ExpertDeliveryOutcome.Cancelled, "delivery window was replaced.");
            ResetWindowState();
            openAddonAddress = addonAddress;
            runId++;
            runStarted = now;
        }
        if (terminal) return;
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
            ownsDeliverySession = false;
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
        if (currentPage != ExpertDeliveryTab) ownsDeliverySession = false;
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
            if (!HasDeliveryOwnership()) return;
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
        nextScanTick = now + scanInterval;
        if (!HasDeliveryOwnership()) return;
        ownsDeliverySession = true;
        var scan = GetNextEligibleItem(addon);
        if (!scan.Ready)
        {
            progress.ResetEmptyObservation();
            if (unavailableSince == 0) unavailableSince = now;
            if (now - unavailableSince >= 5000) StopForWindow($"delivery data did not become coherent: {scan.WaitReason}.");
            else StatusText = $"Enabled - waiting for delivery list refresh ({scan.WaitReason}).";
            return;
        }

        unavailableSince = 0;
        if (pendingItem != null)
            FinishPendingItem(!progress.Rejected);

        if (scan.Item is not { } item)
        {
            if (!progress.ObserveEmpty(scan.Fingerprint, now))
            {
                StatusText = "Enabled - checking the settled Expert Delivery list.";
                return;
            }
            if (scan.HasFailedItems)
                StopForWindow("remaining items could not be selected after repeated attempts.");
            else if (capBlockedCount > 0 || sealCapRejected)
                PublishOutcome(ExpertDeliveryOutcome.SealCapBlocked, "no remaining eligible item fits the Company Seal cap.");
            else if (protectedCount > 0)
                PublishOutcome(ExpertDeliveryOutcome.ProtectedOnly, "remaining items are protected or were rejected.");
            else
                PublishOutcome(ExpertDeliveryOutcome.Completed, "Expert Delivery completed.");
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
        if (!HasDeliveryOwnership()) return;
        if (!TryGetSealCapacity(out sealsAtSelection, out _)) return;
        if (!ExpertDeliveryNative.Select(addon, item.VisibleIndex))
        {
            if (attempts >= MaximumSelectionAttempts)
                failedItems.Add(key);
            StatusText = $"Enabled - could not select item {item.ItemId}; waiting to rescan.";
            return;
        }
        pendingItem = item;
        progress.BeginSelection(now);
        rewardAddress = 0;
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
            delivered++;
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
        PublishOutcome(ExpertDeliveryOutcome.Failed, reason);
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
        unavailableSince = 0;
        externalProtectionRetryAfter = 0;
        lastBlockingErrorText = string.Empty;
        pendingItem = null;
        handledPromptText = null;
        progress.Reset();
        sealCapRejected = false;
        sessionRejectedItems.Clear();
        failedItems.Clear();
        selectionAttempts.Clear();
        nativeRewards.Clear();
        externalProtection.Clear();
        terminal = false;
        ownsDeliverySession = false;
        delivered = protectedCount = capBlockedCount = 0;
        rewardAddress = 0;
        ListSummary = string.Empty;
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

        if (!TryRefreshPendingProtection(out item))
        {
            StatusText = "Enabled - waiting for current confirmation item protection data.";
            return;
        }
        var promptText = GetSelectYesNoPromptText();
        if (string.IsNullOrWhiteSpace(promptText) || handledPromptText == promptText)
        {
            StatusText = "Enabled - waiting for the confirmation dialog to settle.";
            return;
        }

        var isHq = MatchesPrompt(promptText, HighQualityPromptRow);
        var isMateria = MatchesPrompt(promptText, MateriaPromptRow);
        var isCap = MatchesPrompt(promptText, SealCapPromptRow);
        // Only recognized transaction prompts may be accepted.
        var accept = !progress.Rejected && !ShouldSkipItem(item) && (isCap ? ignoreSealCap
            : (isHq || isMateria) && !(isHq && skipHqItems) && !(isMateria && skipMateriaItems));
        var button = accept ? addon->YesButton : addon->NoButton;
        if (button == null || !button->IsEnabled ||
            !progress.TryBeginAction(ExpertDeliveryAction.Confirm, now))
            return;
        if (!HasDeliveryOwnership()) return;
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
        if (pendingItem is not { } item) return;
        var addon = (AddonGrandCompanySupplyReward*)AddonHelper.GetAddon(RewardAddonName);
        if (addon == null || !addon->AtkUnitBase.IsReady) return;
        if (progress.Rejected)
        {
            if (progress.TryBeginAction(ExpertDeliveryAction.Deliver, now)) AddonHelper.CloseAddon(RewardAddonName);
            return;
        }
        if (rewardAddress != 0 && rewardAddress != (nint)addon)
        {
            StopForWindow("the owned reward dialog was replaced.");
            return;
        }
        if (!TryReadPendingInventory(out var itemRemoved))
        {
            StatusText = "Enabled - waiting for selected item inventory data.";
            return;
        }
        if (itemRemoved)
        {
            if (!progress.DeliverySent && !progress.ConfirmationSent) StopForWindow("selected inventory changed before delivery.");
            else StatusText = "Enabled - waiting for the completed reward window to close.";
            return;
        }
        if (!TryRefreshPendingProtection(out item))
        {
            StatusText = "Enabled - waiting for current item protection data.";
            return;
        }
        var wouldReachCap = WouldReachSealCap(item.SealReward);
        if (ShouldSkipItem(item) || wouldReachCap)
        {
            sealCapRejected |= wouldReachCap;
            sessionRejectedItems.Add(item.GetKey());
            progress.MarkConfirmationSent(true);
            AddonHelper.CloseAddon(RewardAddonName);
            return;
        }
        var name = dataManager.GetExcelSheet<Item>().GetRow(item.ItemId).Name.ToString();
        if (string.IsNullOrWhiteSpace(name) || !AddonHelper.AddonHasText(RewardAddonName, name, false))
        {
            StatusText = "Enabled - waiting for reward item identity to match.";
            return;
        }
        // Retry only the same ready reward, for the same still-present item, before
        // any confirmation. A dispatched callback alone is never completion proof.
        if (progress.DeliverySent && (!progress.CanRetryDelivery(now) ||
            !TryGetSealCapacity(out var currentSeals, out _) || currentSeals != sealsAtSelection))
        {
            StatusText = "Enabled - waiting for delivery acknowledgement.";
            return;
        }
        if (!progress.TryBeginAction(ExpertDeliveryAction.Deliver, now)) return;
        if (!HasDeliveryOwnership()) return;
        if (!ExpertDeliveryNative.Deliver(addon))
        {
            if (progress.RecordCallbackFailure(ExpertDeliveryAction.Deliver)) StopForWindow("the Deliver button could not be dispatched three times.");
            return;
        }
        rewardAddress = (nint)addon;
        progress.ClearCallbackFailures(ExpertDeliveryAction.Deliver);
        progress.MarkDeliverySent(now);
        StatusText = $"Enabled - delivering item {item.ItemId}.";
    }

    private bool TryBlockOnDeliveryError(nint supplyAddonAddress)
    {
        if (pendingItem == null) return false;
        var errorText = AddonHelper.GetFirstAddonText(TextErrorAddonName, UnableToCompleteDeliveryText, true);
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        StopForWindow(errorText.Trim());
        return true;
    }

    private ExpertDeliveryScan GetNextEligibleItem(AtkUnitBase* addon)
    {
        var agent = AgentGrandCompanySupply.Instance();
        var inventory = InventoryManager.Instance();
        if (agent == null || inventory == null || agent->NumItems is < 0 or > MaximumNativeItems ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 6, out var itemCount) || itemCount > MaximumNativeItems)
            return ExpertDeliveryScan.Wait("native list unavailable");
        if (!protectedIdsValid) return ExpertDeliveryScan.Wait("protected item IDs are invalid");
        if (!TryBuildGearsetProtection()) return ExpertDeliveryScan.Wait("gear-set protection unavailable");
        if (!TryGetSealCapacity(out var currentSeals, out var maximumSeals)) return ExpertDeliveryScan.Wait("seal capacity unavailable");
        var fingerprint = 14695981039346656037UL;
        void Observe(uint value) => fingerprint = unchecked((fingerprint ^ value) * 1099511628211UL);
        Observe((uint)agent->NumItems);
        Observe(itemCount);
        Observe(currentSeals);
        Observe(maximumSeals);
        nativeRewards.Clear();
        externalProtection.Clear();
        scannedKeys.Clear();
        if (agent->NumItems > 0 && !ExpertDeliveryNative.IsReadable((nint)agent->ItemArray, checked(agent->NumItems * sizeof(GrandCompanyItem))))
            return ExpertDeliveryScan.Wait("native rows unreadable");
        for (var i = 0; i < agent->NumItems; i++)
        {
            var native = agent->ItemArray[i];
            if (native.ItemId == 0 || native.IsBonusReward || native.ExpReward > 0 || native.SealReward <= 0) continue;
            var key = new ExpertDeliveryItemKey(native.ItemId, native.Inventory, native.Slot);
            if (!nativeRewards.TryAdd(key, (uint)native.SealReward)) return ExpertDeliveryScan.Wait("duplicate native slots");
        }
        var visibleCount = (int)Math.Min((uint)MaximumVisibleItems, itemCount);
        if (NativeArrayBounds.ClampElementCount((uint)visibleCount, addon->AtkValuesCount, 425) != visibleCount)
            return ExpertDeliveryScan.Wait("visible rows unavailable");
        GrandCompanyItem* ordered = null;
        if (itemCount > MaximumVisibleItems && !ExpertDeliveryNative.TryGetOrderedRows(addon, (int)itemCount, out ordered))
            return ExpertDeliveryScan.Wait("complete ordered list unavailable");
        ExpertDeliveryItem? candidate = null;
        var failed = false;
        var protectedItems = 0;
        var capItems = 0;
        var eligible = 0;
        for (var i = 0; i < (int)itemCount; i++)
        {
            uint reward, containerValue, slotValue, itemId;
            if (i < visibleCount)
            {
                if (!NativeArrayAccess.TryGetAtkUInt(addon, 265 + i, out reward) ||
                    !NativeArrayAccess.TryGetAtkUInt(addon, 345 + i, out containerValue) ||
                    !NativeArrayAccess.TryGetAtkUInt(addon, 385 + i, out slotValue) ||
                    !NativeArrayAccess.TryGetAtkUInt(addon, 425 + i, out itemId))
                    return ExpertDeliveryScan.Wait("visible row incomplete");
                // Anchor the entire backing-list layout/order to every exposed row.
                if (ordered != null && (ordered[i].ItemId != itemId || ordered[i].Slot != slotValue ||
                    (uint)ordered[i].Inventory != containerValue || ordered[i].SealReward != reward))
                    return ExpertDeliveryScan.Wait("ordered and visible rows differ");
            }
            else
            {
                var row = ordered[i];
                itemId = row.ItemId;
                slotValue = row.Slot;
                containerValue = (uint)row.Inventory;
                if (row.SealReward <= 0) return ExpertDeliveryScan.Wait("ordered row incomplete");
                reward = (uint)row.SealReward;
            }
            if (itemId == 0 || slotValue > ushort.MaxValue) return ExpertDeliveryScan.Wait("visible row incomplete");
            var container = (InventoryType)containerValue;
            var key = new ExpertDeliveryItemKey(itemId, container, (ushort)slotValue);
            if (!scannedKeys.Add(key)) return ExpertDeliveryScan.Wait("duplicate ordered slots");
            if (!nativeRewards.TryGetValue(key, out var nativeReward) || reward != nativeReward)
                return ExpertDeliveryScan.Wait("native and visible rows differ");
            if (!NativeArrayAccess.TryGetInventorySlot(inventory, container, (int)slotValue, out var slot) ||
                slot->ItemId != itemId || slot->Quantity == 0)
                return ExpertDeliveryScan.Wait("inventory and visible rows differ");
            var item = new ExpertDeliveryItem(itemId, container, (ushort)slotValue, reward, i,
                slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), HasMateriaAttached(itemId, slot), slot->Quantity);
            if (!TryReadExternalProtection(itemId)) return ExpertDeliveryScan.Wait("external protected list unavailable");
            Observe(itemId); Observe(containerValue); Observe(slotValue); Observe(reward);
            Observe(unchecked((uint)slot->Quantity)); Observe((uint)slot->Flags); Observe(item.HasMateria ? 1u : 0u);
            var isProtected = ShouldSkipItem(item) || sessionRejectedItems.Contains(key);
            Observe(isProtected ? 1u : 0u);
            if (failedItems.Contains(key)) { failed = true; continue; }
            if (isProtected) { protectedItems++; continue; }
            if (!ignoreSealCap && !ExpertDeliveryPolicy.Fits(currentSeals, maximumSeals, reward)) { capItems++; continue; }
            eligible++;
            candidate ??= item;
        }
        protectedCount = protectedItems;
        capBlockedCount = capItems;
        ListSummary = $"{eligible} eligible | {protectedItems} protected/rejected | {capItems} over cap";
        // Return only after the FULL mapping has passed validation, including later rows.
        return new ExpertDeliveryScan(true, candidate, fingerprint, failed, string.Empty);
    }

    private bool ShouldSkipItem(ExpertDeliveryItem item)
    {
        if (skipHqItems && item.IsHighQuality || skipMateriaItems && item.HasMateria) return true;
        if (protectedIds.Contains(item.ItemId)) return true;
        if (itemScope == 0 && item.Container is not (InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4)) return true;
        if (itemScope < 2 && gearsetItems.Contains(item.ItemId)) return true;
        return useExternalProtection && (!externalProtection.TryGetValue(item.ItemId, out var isProtected) || isProtected);
    }

    private bool WouldReachSealCap(uint sealReward)
    {
        if (ignoreSealCap) return false;
        return !TryGetSealCapacity(out var current, out var maximum) || !ExpertDeliveryPolicy.Fits(current, maximum, sealReward);
    }

    private bool TryGetSealCapacity(out uint current, out uint maximum)
    {
        current = maximum = 0;
        var playerState = PlayerState.Instance();
        var inventory = InventoryManager.Instance();
        if (playerState == null || playerState->GrandCompany == 0 || inventory == null) return false;
        var rankSheet = dataManager.GetExcelSheet<GrandCompanyRank>();
        if (!rankSheet.TryGetRow(GetRealGrandCompanyRank(playerState), out var rank) || rank.MaxSeals == 0) return false;
        current = inventory->GetCompanySeals(playerState->GrandCompany);
        maximum = rank.MaxSeals;
        return true;
    }

    private static bool HasMateriaAttached(uint itemId, InventoryItem* slot)
    {
        if (slot == null) return true;
        // Include overmeld slots. Protection must not depend on normal slot count.
        for (var i = 0; i < slot->Materia.Length; i++)
            if (slot->Materia[i] != 0) return true;
        return false;
    }

    private static string GetSelectYesNoPromptText()
    {
        var addon = (AddonSelectYesno*)AddonHelper.GetAddon(SelectYesNoAddonName);
        if (addon == null || addon->PromptText == null)
            return string.Empty;

        return addon->PromptText->NodeText.ToString().ReplaceLineEndings("");
    }

    private bool HasDeliveryOwnership()
    {
        var externalRunning = getExternalDeliveryOperation();
        if (externalRunning == false) return true;
        ownsDeliverySession = false;
        if (pendingItem != null) StopForWindow("delivery ownership changed; stop the other GC engine and reopen this window.");
        else StatusText = externalRunning == true ? "Enabled - waiting for external GC delivery to stop." : "Enabled - unable to verify external GC ownership.";
        return false;
    }

    private bool MatchesPrompt(string promptText, uint rowId)
    {
        var sheet = dataManager.GetExcelSheet<Addon>();
        return sheet.TryGetRow(rowId, out var row) && ExpertDeliveryPolicy.PromptMatches(promptText, row.Text.ToString());
    }

    private bool TryBuildGearsetProtection()
    {
        gearsetItems.Clear();
        if (itemScope == 2) return true;
        var module = RaptureGearsetModule.Instance();
        if (module == null) return false;
        foreach (var entry in module->Entries)
        {
            if (!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            foreach (var gear in entry.Items)
                if (gear.ItemId != 0) gearsetItems.Add(gear.ItemId % 1_000_000);
        }
        return true;
    }

    private bool TryReadExternalProtection(uint itemId)
    {
        if (!useExternalProtection || externalProtection.ContainsKey(itemId)) return true;
        if (Environment.TickCount64 < externalProtectionRetryAfter) return false;
        var protection = getExternalItemProtection(itemId);
        if (protection is not { } value)
        {
            externalProtectionRetryAfter = Environment.TickCount64 + 2000;
            return false;
        }
        externalProtectionRetryAfter = 0;
        externalProtection[itemId] = value;
        return true;
    }

    private bool TryRefreshPendingProtection(out ExpertDeliveryItem item)
    {
        item = default;
        if (pendingItem is not { } selected || !protectedIdsValid || !TryBuildGearsetProtection() ||
            !NativeArrayAccess.TryGetInventorySlot(InventoryManager.Instance(), selected.Container, selected.Slot, out var slot) ||
            slot->ItemId != selected.ItemId || slot->Quantity != selected.Quantity) return false;
        externalProtection.Remove(selected.ItemId);
        if (!TryReadExternalProtection(selected.ItemId)) return false;
        item = selected with { IsHighQuality = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), HasMateria = HasMateriaAttached(selected.ItemId, slot) };
        return true;
    }

    private void PublishOutcome(ExpertDeliveryOutcome outcome, string message)
    {
        if (terminal || openAddonAddress == 0) return;
        terminal = true; // Latch BEFORE callbacks, which may re-enter configuration/disable.
        var result = new ExpertDeliveryResult(runId, outcome, delivered, protectedCount, capBlockedCount,
            Math.Max(0, Environment.TickCount64 - runStarted), message);
        LastResult = result;
        StatusText = $"Enabled - {outcome}: {message} ({delivered} delivered)";
        log.Information($"[XASlave] Expert Delivery run {runId}: {outcome}; {delivered} delivered in {result.ElapsedMilliseconds} ms.");
        if (notifyOutcome)
            try { Plugin.ChatGui.Print($"[XASlave] Expert Delivery: {outcome}. {message} {ListSummary}"); }
            catch (Exception ex) { log.Warning(ex, "[XASlave] Expert Delivery local notification failed."); }
        if (outcome == ExpertDeliveryOutcome.Completed)
        {
            if (closeOnCompletion) AddonHelper.CloseAddon(SupplyListAddonName);
            if (runCompletionCommand && ExpertDeliveryPolicy.IsValidCommand(completionCommand))
            {
                if (closeOnCompletion)
                {
                    pendingCompletionCommand = completionCommand;
                    completionCloseDeadline = Environment.TickCount64 + 5000;
                }
                else ChatHelper.TrySend(completionCommand);
            }
        }
        if (RunFinished is { } handlers)
            foreach (Action<ExpertDeliveryResult> handler in handlers.GetInvocationList())
                try { handler(result); } catch (Exception ex) { log.Warning(ex, "[XASlave] Expert Delivery result subscriber failed."); }
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
