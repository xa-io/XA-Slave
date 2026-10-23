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
    private global::XASlave.Localization.UiMessage? lastBlockingErrorMessage;
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
    public string ListSummary { get; private set; } = global::XASlave.Localization.UiMessage.Raw(string.Empty).Original;
    private global::XASlave.Localization.UiMessage? uiListSummary = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    internal global::XASlave.Localization.UiMessage ListSummaryMessage => global::XASlave.Localization.UiMessage.Capture(ref uiListSummary, ListSummary);
    internal string LocalizedListSummary => global::XASlave.Localization.UiMessage.Resolve(ref uiListSummary, ListSummary);
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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public bool RequestNewRun()
    {
        if (!enabled || pendingItem != null || pendingCompletionCommand.Length > 0 ||
            AddonHelper.IsAddonVisible(RewardAddonName) || AddonHelper.IsAddonVisible(SelectYesNoAddonName)) return false;
        if (openAddonAddress != 0 && !terminal) PublishOutcome(ExpertDeliveryOutcome.Cancelled, global::XASlave.Localization.UiMessage.Key("SC985356E685BF3EB"));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
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
            if (openAddonAddress != 0 && !terminal) PublishOutcome(ExpertDeliveryOutcome.Cancelled, global::XASlave.Localization.UiMessage.Key("S1B473C623F1499F7"));
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
            StopForWindow(global::XASlave.Localization.UiMessage.Key("SE83567551F4E3C71"));
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
            PublishOutcome(ExpertDeliveryOutcome.Cancelled, global::XASlave.Localization.UiMessage.Key("SDC55C800957BD45B"));
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        var addon = AddonHelper.GetAddon(SupplyListAddonName);
        var rewardVisible = AddonHelper.IsAddonVisible(RewardAddonName);
        var promptVisible = AddonHelper.IsAddonVisible(SelectYesNoAddonName);
        if (addon == null || (!IsAddonVisible(addon) && !(pendingItem != null && (rewardVisible || promptVisible))))
        {
            PublishOutcome(ExpertDeliveryOutcome.Cancelled, global::XASlave.Localization.UiMessage.Key("S63DBE5E244EA89B3"));
            ResetWindowState();
            RefreshWaitingStatusText();
            return;
        }

        var addonAddress = (nint)addon;
        if (addonAddress != openAddonAddress)
        {
            PublishOutcome(ExpertDeliveryOutcome.Cancelled, global::XASlave.Localization.UiMessage.Key("S4A7681C0A1EAFFC5"));
            ResetWindowState();
            openAddonAddress = addonAddress;
            runId++;
            runStarted = now;
        }
        if (terminal) return;
        if (blockedAddonAddress == addonAddress)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S338FF5603347D2BB", global::XASlave.Localization.UiMessage.Capture(ref lastBlockingErrorMessage, lastBlockingErrorText)));
            return;
        }
        if (TryBlockOnDeliveryError(addonAddress))
            return;

        if (pendingItem != null)
        {
            if (progress.TransactionTimedOut(now))
            {
                StopForWindow(global::XASlave.Localization.UiMessage.Key("SF35A4202A48833D3"));
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
                    StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S8625B5E57F36223A"));
                    return;
                }
                if (progress.Rejected || (itemRemoved && (progress.DeliverySent || progress.ConfirmationSent)))
                {
                    progress.MarkRefreshing();
                    nextScanTick = 0;
                }
                else if (itemRemoved)
                {
                    StopForWindow(global::XASlave.Localization.UiMessage.Key("SD13739496148B277"));
                    return;
                }
                else if (progress.SelectionTimedOut(now))
                {
                    var timedOut = pendingItem.Value;
                    if (selectionAttempts.GetValueOrDefault(timedOut.GetKey()) >= MaximumSelectionAttempts)
                        failedItems.Add(timedOut.GetKey());
                    log.Warning($"[XASlave] Expert Delivery selection of item {timedOut.ItemId} did not open a dialog within 2.0s.");
                    FinishPendingItem(false);
                    StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SA44A99322E9DADA1"));
                    return;
                }
                else
                {
                    StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (progress.DeliverySent || progress.ConfirmationSent ? global::XASlave.Localization.UiMessage.Key("SC05147137785E663") : global::XASlave.Localization.UiMessage.Key("S12C97D7933BDBC15")));
                    return;
                }
            }
        }
        else if (rewardVisible || promptVisible)
        {
            // A manually opened or unrelated dialog is not owned by this transaction.
            ownsDeliverySession = false;
            progress.ResetEmptyObservation();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SA923E4892C669009"));
            return;
        }

        if (!addon->IsReady ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 0, out var loadedState) ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 5, out var rawCurrentPage) ||
            loadedState != SupplyListLoadedState)
        {
            progress.ResetEmptyObservation();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SA25BD8481A19835E"));
            return;
        }
        if (rawCurrentPage > ExpertDeliveryTab)
        {
            progress.ResetEmptyObservation();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S319AD2D5D43B6F58"));
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
                    StopForWindow(global::XASlave.Localization.UiMessage.Key("SDFC04183E708A6A6"));
                else
                    StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF04CC1B12765F7D6"));
                return;
            }
            progress.ClearCallbackFailures(ExpertDeliveryAction.SwitchPage);
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S7F8325FE80E6D1D5", GetPageName(defaultPage)));
            return;
        }
        if (currentPage != ExpertDeliveryTab)
        {
            progress.ResetEmptyObservation();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (autoSwitchWhenOpen && defaultPage != ExpertDeliveryTab ? global::XASlave.Localization.UiMessage.Format("SAE311E5DD72BE99A", GetPageName(defaultPage)) : global::XASlave.Localization.UiMessage.Key("S4CA9365E63B67111")));
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
            if (now - unavailableSince >= 5000) StopForWindow(global::XASlave.Localization.UiMessage.Format("S1717B0E4FC391D6E", global::XASlave.Localization.UiMessage.Authored(scan.WaitReason)));
            else StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S1B347EA259AE507B", global::XASlave.Localization.UiMessage.Authored(scan.WaitReason)));
            return;
        }

        unavailableSince = 0;
        if (pendingItem != null)
            FinishPendingItem(!progress.Rejected);

        if (scan.Item is not { } item)
        {
            if (!progress.ObserveEmpty(scan.Fingerprint, now))
            {
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S5BD01119678CCA7B"));
                return;
            }
            if (scan.HasFailedItems)
                StopForWindow(global::XASlave.Localization.UiMessage.Key("S72185BFA893A780E"));
            else if (capBlockedCount > 0 || sealCapRejected)
                PublishOutcome(ExpertDeliveryOutcome.SealCapBlocked, global::XASlave.Localization.UiMessage.Key("S32F42D19E76FAF3C"));
            else if (protectedCount > 0)
                PublishOutcome(ExpertDeliveryOutcome.ProtectedOnly, global::XASlave.Localization.UiMessage.Key("S2B5F2CCA8F399718"));
            else
                PublishOutcome(ExpertDeliveryOutcome.Completed, global::XASlave.Localization.UiMessage.Key("SF5458CA0A5C4EB9B"));
            return;
        }

        progress.ResetEmptyObservation();
        if (WouldReachSealCap(item.SealReward))
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S094DB5393BD6ECC0"));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("SEAE2B74371ABEA79", item.ItemId));
            return;
        }
        pendingItem = item;
        progress.BeginSelection(now);
        rewardAddress = 0;
        handledPromptText = null;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("SD99A8BBE40CCC62D", item.ItemId));
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

    private void StopForWindow(global::XASlave.Localization.UiMessage reason)
    {
        blockedAddonAddress = openAddonAddress;
        lastBlockingErrorText = global::XASlave.Localization.UiMessage.Store(ref lastBlockingErrorMessage, reason);
        progress.ResetEmptyObservation();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S338FF5603347D2BB", reason));
        PublishOutcome(ExpertDeliveryOutcome.Failed, reason);
    }

    private void RefreshWaitingStatusText()
    {
        if (!enabled)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (autoSwitchWhenOpen ? global::XASlave.Localization.UiMessage.Format("SF99A076D0F969233", GetPageName(defaultPage)) : global::XASlave.Localization.UiMessage.Key("S2E53E0EE4F91837B")));
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
        ListSummary = global::XASlave.Localization.UiMessage.Store(ref uiListSummary, global::XASlave.Localization.UiMessage.Raw(string.Empty));
    }

    private static int NormalizePage(int page)
    {
        return Math.Clamp(page, 0, ExpertDeliveryTab);
    }

    private static global::XASlave.Localization.UiMessage GetPageName(int page)
    {
        return NormalizePage(page) switch
        {
            0 => global::XASlave.Localization.UiMessage.Key("S06F6143A39676057"),
            1 => global::XASlave.Localization.UiMessage.Key("S2FCF7E6BBAEC6128"),
            _ => global::XASlave.Localization.UiMessage.Key("S13887C7442815B2B")
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S9EEAE98C39DED73E"));
            return;
        }

        if (!TryRefreshPendingProtection(out item))
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF2C51C3DFB5B9A71"));
            return;
        }
        var promptText = GetSelectYesNoPromptText();
        if (string.IsNullOrWhiteSpace(promptText) || handledPromptText == promptText)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S3F315B600F9CAF9A"));
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
                StopForWindow(global::XASlave.Localization.UiMessage.Key("SD58D8BD3959AACCA"));
                return;
            }
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S18F34BCA1BDD2DB9"));
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
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (accept ? global::XASlave.Localization.UiMessage.Format("S05AEA9FC19009C13", item.ItemId) : global::XASlave.Localization.UiMessage.Format("S100EA866B28EC4DE", item.ItemId)));
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
            StopForWindow(global::XASlave.Localization.UiMessage.Key("SD6E34340E6CCC397"));
            return;
        }
        if (!TryReadPendingInventory(out var itemRemoved))
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S8625B5E57F36223A"));
            return;
        }
        if (itemRemoved)
        {
            if (!progress.DeliverySent && !progress.ConfirmationSent) StopForWindow(global::XASlave.Localization.UiMessage.Key("S321269C72127EFAF"));
            else StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S6577BD74B31D8EB6"));
            return;
        }
        if (!TryRefreshPendingProtection(out item))
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S3D1FDB120E0A9EE8"));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SB2617308E4F1EDE9"));
            return;
        }
        // Retry only the same ready reward, for the same still-present item, before
        // any confirmation. A dispatched callback alone is never completion proof.
        if (progress.DeliverySent && (!progress.CanRetryDelivery(now) ||
            !TryGetSealCapacity(out var currentSeals, out _) || currentSeals != sealsAtSelection))
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S868F91035FF85175"));
            return;
        }
        if (!progress.TryBeginAction(ExpertDeliveryAction.Deliver, now)) return;
        if (!HasDeliveryOwnership()) return;
        if (!ExpertDeliveryNative.Deliver(addon))
        {
            if (progress.RecordCallbackFailure(ExpertDeliveryAction.Deliver)) StopForWindow(global::XASlave.Localization.UiMessage.Key("SC4BF8886762E4316"));
            return;
        }
        rewardAddress = (nint)addon;
        progress.ClearCallbackFailures(ExpertDeliveryAction.Deliver);
        progress.MarkDeliverySent(now);
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("SF7E7BF134BC4C8DB", item.ItemId));
    }

    private bool TryBlockOnDeliveryError(nint supplyAddonAddress)
    {
        if (pendingItem == null) return false;
        var errorText = AddonHelper.GetFirstAddonText(TextErrorAddonName, UnableToCompleteDeliveryText, true);
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        StopForWindow(global::XASlave.Localization.UiMessage.Raw(errorText.Trim()));
        return true;
    }

    private ExpertDeliveryScan GetNextEligibleItem(AtkUnitBase* addon)
    {
        var agent = AgentGrandCompanySupply.Instance();
        var inventory = InventoryManager.Instance();
        if (agent == null || inventory == null || agent->NumItems is < 0 or > MaximumNativeItems ||
            !NativeArrayAccess.TryGetAtkUInt(addon, 6, out var itemCount) || itemCount > MaximumNativeItems)
            return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S192DEBD9C6CE5775"));
        if (!protectedIdsValid) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SF72F327C27FBD3C7"));
        if (!TryBuildGearsetProtection()) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SE3A97D3592D139E1"));
        if (!TryGetSealCapacity(out var currentSeals, out var maximumSeals)) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SA289F7148FF0F9DF"));
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
            return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S59B3D0D6FB167C5F"));
        for (var i = 0; i < agent->NumItems; i++)
        {
            var native = agent->ItemArray[i];
            if (native.ItemId == 0 || native.IsBonusReward || native.ExpReward > 0 || native.SealReward <= 0) continue;
            var key = new ExpertDeliveryItemKey(native.ItemId, native.Inventory, native.Slot);
            if (!nativeRewards.TryAdd(key, (uint)native.SealReward)) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S543795FEE9CDCFA7"));
        }
        var visibleCount = (int)Math.Min((uint)MaximumVisibleItems, itemCount);
        if (NativeArrayBounds.ClampElementCount((uint)visibleCount, addon->AtkValuesCount, 425) != visibleCount)
            return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SB44C3A6B57487115"));
        GrandCompanyItem* ordered = null;
        if (itemCount > MaximumVisibleItems && !ExpertDeliveryNative.TryGetOrderedRows(addon, (int)itemCount, out ordered))
            return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S1AA6F8F57CAD1B54"));
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
                    return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SE7C018763D82F7F5"));
                // Anchor the entire backing-list layout/order to every exposed row.
                if (ordered != null && (ordered[i].ItemId != itemId || ordered[i].Slot != slotValue ||
                    (uint)ordered[i].Inventory != containerValue || ordered[i].SealReward != reward))
                    return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S94C1CDDA57E399D9"));
            }
            else
            {
                var row = ordered[i];
                itemId = row.ItemId;
                slotValue = row.Slot;
                containerValue = (uint)row.Inventory;
                if (row.SealReward <= 0) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SDC81255E2F7666E0"));
                reward = (uint)row.SealReward;
            }
            if (itemId == 0 || slotValue > ushort.MaxValue) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SE7C018763D82F7F5"));
            var container = (InventoryType)containerValue;
            var key = new ExpertDeliveryItemKey(itemId, container, (ushort)slotValue);
            if (!scannedKeys.Add(key)) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S5E48006F2B6FB505"));
            if (!nativeRewards.TryGetValue(key, out var nativeReward) || reward != nativeReward)
                return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SB5D702F085BFCD24"));
            if (!NativeArrayAccess.TryGetInventorySlot(inventory, container, (int)slotValue, out var slot) ||
                slot->ItemId != itemId || slot->Quantity == 0)
                return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("S46ABC32EBA57D54B"));
            var item = new ExpertDeliveryItem(itemId, container, (ushort)slotValue, reward, i,
                slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), HasMateriaAttached(itemId, slot), slot->Quantity);
            if (!TryReadExternalProtection(itemId)) return ExpertDeliveryScan.Wait(global::XASlave.Localization.UiLocale.Native("SF3A32D6C0D2826A4"));
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
        ListSummary = global::XASlave.Localization.UiMessage.Store(ref uiListSummary, global::XASlave.Localization.UiMessage.Format("S39E719974394981D", eligible, protectedItems, capItems));
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
        if (pendingItem != null) StopForWindow(global::XASlave.Localization.UiMessage.Key("SA57556FB73C56D45"));
        else StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (externalRunning == true ? global::XASlave.Localization.UiMessage.Key("SC855CDC62BED4B38") : global::XASlave.Localization.UiMessage.Key("S811B099AF87C3031")));
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


    internal static global::XASlave.Localization.UiMessage OutcomeMessage(ExpertDeliveryOutcome outcome) => outcome switch
    {
        ExpertDeliveryOutcome.Completed => global::XASlave.Localization.UiMessage.Key("S22A970D2E5B1CC23"),
        ExpertDeliveryOutcome.ProtectedOnly => global::XASlave.Localization.UiMessage.Key("S8A754B44B66725FB"),
        ExpertDeliveryOutcome.SealCapBlocked => global::XASlave.Localization.UiMessage.Key("S596A68C70633E04E"),
        ExpertDeliveryOutcome.Cancelled => global::XASlave.Localization.UiMessage.Key("SD353A99EB4556847"),
        ExpertDeliveryOutcome.Failed => global::XASlave.Localization.UiMessage.Key("S031A8F0F659DF890"),
        _ => global::XASlave.Localization.UiMessage.Value(outcome),
    };
    private void PublishOutcome(ExpertDeliveryOutcome outcome, global::XASlave.Localization.UiMessage message)
    {
        if (terminal || openAddonAddress == 0) return;
        terminal = true; // Latch BEFORE callbacks, which may re-enter configuration/disable.
        var result = new ExpertDeliveryResult(runId, outcome, delivered, protectedCount, capBlockedCount,
            Math.Max(0, Environment.TickCount64 - runStarted), message.Original);
        LastResult = result;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("SC9EBB3069F761B52", OutcomeMessage(outcome), message, delivered));
        log.Information($"[XASlave] Expert Delivery run {runId}: {outcome}; {delivered} delivered in {result.ElapsedMilliseconds} ms.");
        if (notifyOutcome)
            try { Plugin.ChatGui.Print(global::XASlave.Localization.UiMessage.Format("S4EE7914332AC3105", OutcomeMessage(outcome), message, ListSummaryMessage).Original); }
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
