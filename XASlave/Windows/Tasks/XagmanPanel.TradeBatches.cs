using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private enum XagmanBatchProgress { None, Pending, Completed, CapacityBlocked, Failed, LegacyFailure }

    private sealed class XagmanTradeBatchRun
    {
        internal required string Character;
        internal required string Partner;
        internal required string PartnerInstance;
        internal required ulong ContentId;
        internal required long TaskRunId;
        internal required bool OutsideNetwork;
        internal required string OnhSession;
        internal required string Context;
        internal required object ProviderIdentity;
        internal required string SlaveVersion;
        internal required string DropboxVersion;
        internal required List<XagmanTradeBatchItem> Remaining;
        internal List<XagmanTradeBatchItem> Offered = new();
        internal Dictionary<(uint, bool), int> Before = new();
        internal string RequestId = string.Empty;
        internal string LastCapacityError = string.Empty;
        internal string FailureText = string.Empty;
        internal XagmanTradeFailureKind FailureKind;
        internal DateTime StartedAt;
        internal DateTime QuietSince;
        internal bool Trading;
        internal bool SawBusy;
        internal bool Stopping;
        internal bool LegacyFailure;
        internal int NoProgress;
        internal int OfferLimit = 5;
    }

    private XagmanTradeBatchRun? xagmanTradeBatch;
    private XagmanTradeFailureKind xagmanBatchLegacyFailureKind;

    private void CancelXagmanTradeBatch()
    {
        xagmanTradeBatch = null;
        ResetXagmanTradeCapacityExchange();
    }

    private void GuardXagmanTradeBatchLifecycle()
    {
        if (xagmanTradeBatch is not { } run
            || (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentRunId == run.TaskRunId)) return;
        FailXagmanTradeBatch("The task owning the bounded trade ended; remaining goals were not completed.", out var failure);
        HaltXagmanCapacityTrade(failure, haltOwningTask: plugin.TaskRunner.CurrentRunId == run.TaskRunId);
    }

    private bool TryStartXagmanCapacityTrade(string context, out bool handled)
    {
        handled = true;
        try { return StartXagmanCapacityTradeCore(context, out handled); }
        catch (Exception error)
        {
            FailXagmanTradeBatch("Could not establish a capacity-limited transfer: " + error.Message, out _);
            Plugin.Log.Error(error, "[Xagman] Could not establish a capacity-limited transfer.");
            return false;
        }
    }

    private bool StartXagmanCapacityTradeCore(string context, out bool handled)
    {
        handled = true;
        if (xagmanTradeBatch != null)
        {
            plugin.TaskRunner.AddLog("Xagman: another capacity-limited transfer is still active.");
            return false;
        }
        if (!TryRequireXagmanTradeVersions(out var failure)
            || !TryGetXagmanTradeVersions(out var slaveVersion, out var dropboxVersion, out failure))
        {
            plugin.TaskRunner.AddLog($"Xagman: cannot prepare the outgoing trade: {failure}");
            return false;
        }
        if (!plugin.DropboxQueue.TryCaptureXagmanProviderIdentity(out var providerIdentity, out failure)
            || !plugin.DropboxQueue.TryCaptureXagmanQueue(out var selected, out failure))
        {
            plugin.TaskRunner.AddLog($"Xagman: cannot capture the outgoing selection: {failure}");
            return false;
        }
        // Control gil retains its acknowledged signal path. Policy-driven ONH collection gil
        // uses the same settlement/accounting as other collection, including cancellation.
        if (selected.All(item => item.ItemId == 1) && !IsXagmanOnhCollectionTransfer) { handled = false; return true; }
        xagmanBatchLegacyFailureKind = XagmanTradeFailureKind.None;
        if (!plugin.DropboxQueue.TryCaptureXagmanTradeStacks(out var stacks, out failure))
        {
            plugin.TaskRunner.AddLog($"Xagman: cannot measure outgoing inventory: {failure}");
            return false;
        }
        var held = SumXagmanTradeStacks(stacks);
        var remaining = selected.Select(item => item with
            { Quantity = Math.Min(item.Quantity, held.GetValueOrDefault((item.ItemId, item.IsHq))) })
            .Where(item => item.Quantity > 0).ToList();
        if (remaining.Count == 0)
        {
            plugin.TaskRunner.AddLog("Xagman: the selected outgoing items are no longer available.");
            return false;
        }
        if (!Plugin.PlayerState.IsLoaded || !IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)
            || string.IsNullOrWhiteSpace(xagmanActiveTradePartner)) return false;
        var run = new XagmanTradeBatchRun
        {
            Character = xagmanActiveCharacter, Partner = xagmanActiveTradePartner,
            PartnerInstance = xagmanActiveTradePartnerInstanceId, ContentId = Plugin.PlayerState.ContentId,
            TaskRunId = plugin.TaskRunner.CurrentRunId, OutsideNetwork = plugin.Configuration.XagmanOutsideNetworkHelper,
            OnhSession = onhSession, Context = context, Remaining = remaining, ProviderIdentity = providerIdentity,
            SlaveVersion = slaveVersion, DropboxVersion = dropboxVersion,
        };
        GetXagmanBoundedTradeFailureKind(out run.LastCapacityError);
        // Checkpoint before replacing the provider selection. The rest stays in Xagman's
        // existing live-inventory goals even if cancellation later abandons this runner.
        if (!TryValidateXagmanBatchVersions(run, out failure)
            || !plugin.DropboxQueue.TryReplaceXagmanQueue(Array.Empty<XagmanTradeBatchItem>(), out failure))
        {
            plugin.TaskRunner.AddLog($"Xagman: cannot establish a bounded outgoing selection: {failure}");
            return false;
        }
        xagmanTradeBatch = run;
        if (RequestNextXagmanTradeCapacity(run, out failure)) return true;
        CancelXagmanTradeBatch();
        plugin.TaskRunner.AddLog($"Xagman: {failure}");
        return false;
    }

    private bool RequestNextXagmanTradeCapacity(XagmanTradeBatchRun run, out string failure)
    {
        if (!TryValidateXagmanBatchVersions(run, out failure))
            return false;
        ResetXagmanTradeCapacityExchange(notifyRemote: false);
        run.RequestId = Guid.NewGuid().ToString("N");
        run.StartedAt = DateTime.UtcNow;
        run.QuietSince = DateTime.MinValue;
        run.Trading = run.SawBusy = run.Stopping = run.LegacyFailure = false;
        run.Offered.Clear();
        if (run.OutsideNetwork && IsXagmanOnhCollectionTransfer)
        {
            onhCollectionLocalCleanup = false;
            onhCollectionTradeAdmitted = false;
        }
        return RequestXagmanTradeCapacity(run.RequestId, out failure);
    }

    private bool TryValidateXagmanBatchVersions(XagmanTradeBatchRun run, out string failure)
    {
        if (!TryRequireXagmanTradeVersions(out failure)
            || !TryGetXagmanTradeVersions(out var slaveVersion, out var dropboxVersion, out failure))
            return false;
        if (slaveVersion != run.SlaveVersion || dropboxVersion != run.DropboxVersion)
        {
            failure = "The loaded XA Slave or Dropbox version changed during the trade; remaining items were not dispatched.";
            return false;
        }
        return true;
    }

    private static Dictionary<(uint, bool), int> SumXagmanTradeStacks(IReadOnlyList<XagmanTradeBatchStack> stacks)
        => stacks.GroupBy(stack => (stack.ItemId, stack.IsHq))
            .ToDictionary(group => group.Key, group => checked((int)group.Sum(stack => (long)stack.Quantity)));

    private XagmanBatchProgress PollXagmanCapacityTrade(out string failure)
    {
        failure = string.Empty;
        var run = xagmanTradeBatch;
        if (run == null) return XagmanBatchProgress.None;
        try
        {
            var now = DateTime.UtcNow;
            if (!TryValidateXagmanBatchVersions(run, out failure)
                || !plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(run.ProviderIdentity, out failure))
                return FailXagmanTradeBatch(failure, out failure);
            if (!xagmanRunning || !Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != run.ContentId
                || !IsXagmanCurrentLocalCharacter(run.Character) || plugin.TaskRunner.CurrentRunId != run.TaskRunId
                || !string.Equals(run.Partner, xagmanActiveTradePartner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(run.PartnerInstance, xagmanActiveTradePartnerInstanceId, StringComparison.Ordinal)
                || run.OutsideNetwork != plugin.Configuration.XagmanOutsideNetworkHelper
                || (run.OutsideNetwork && run.OnhSession != onhSession))
                return FailXagmanTradeBatch("Trade ownership changed; remaining items were not dispatched.", out failure);
            if (!plugin.IpcClient.DropboxTryGetBusy(out var busy))
                return FailXagmanTradeBatch("Dropbox activity is unreadable; remaining items were not dispatched.", out failure);
            var tradeOpen = AddonHelper.IsAddonVisible("Trade");
            if (run.Trading && !IsXagmanTradeCapacityBindingCurrent(run.RequestId, out failure))
                return FailXagmanTradeBatch(failure, out failure);
            if (!run.Trading)
            {
                if (busy || tradeOpen)
                    return FailXagmanTradeBatch("An unexpected trade started while receiver capacity was being checked.", out failure);
                var capacity = PollXagmanTradeCapacity(run.RequestId, out var slots, out failure);
                if (capacity == XagmanTradeCapacityState.Pending) return XagmanBatchProgress.Pending;
                if (capacity == XagmanTradeCapacityState.Invalid)
                    return FailXagmanTradeBatch(failure, out failure);
                if (!plugin.DropboxQueue.TryCaptureXagmanTradeStacks(out var stacks, out failure)
                    || !plugin.DropboxQueue.TryGetXagmanTradeMaxGil(out var maxGil, out failure))
                    return FailXagmanTradeBatch(failure, out failure);
                var plan = XagmanTradeBatchPlanner.Plan(run.Remaining, stacks, slots, run.OfferLimit, maxGil);
                if (!plan.IsValid) return FailXagmanTradeBatch(plan.FailureReason, out failure);
                if (plan.Selected.Count == 0)
                {
                    if (slots != 0)
                        return FailXagmanTradeBatch("Pending trade items became unavailable; no delivery was credited.", out failure);
                    plugin.TaskRunner.AddLog($"Xagman: {run.Partner} has no empty bag slots; remaining quantities are retained.");
                    CancelXagmanTradeBatch();
                    return XagmanBatchProgress.CapacityBlocked;
                }
                run.Before = SumXagmanTradeStacks(stacks);
                run.Offered = plan.Selected.ToList();
                if (!TryValidateXagmanBatchVersions(run, out failure)
                    || !plugin.DropboxQueue.TryReplaceXagmanQueue(run.Offered, out failure))
                    return FailXagmanTradeBatch(failure, out failure);
                if (!TryFocusXagmanTradeCapacityPartner(run.RequestId, out failure))
                    return FailXagmanTradeBatch(failure, out failure);
                ResetXagmanTradeCamera(run.Context);
                if (!TryValidateXagmanBatchVersions(run, out failure))
                    return FailXagmanTradeBatch(failure, out failure);
                if (!plugin.IpcClient.DropboxBeginTrading())
                    return FailXagmanTradeBatch("Dropbox rejected the bounded trade start.", out failure);
                run.Trading = true;
                run.StartedAt = now;
                run.QuietSince = DateTime.MinValue;
                xagmanObservedDropboxBusy = true;
                plugin.TaskRunner.AddLog($"Xagman: sending {plan.PhysicalOfferSlots} physical stack(s), using at most {plan.BagSlots} of {slots} empty receiver slots.");
                return XagmanBatchProgress.Pending;
            }

            var kind = GetXagmanBoundedTradeFailureKind(out var text);
            if (run.OutsideNetwork && IsXagmanOnhCollectionTransfer)
            {
                if (onhCollectionCancellationObserved)
                {
                    kind = XagmanTradeFailureKind.TradeCanceled;
                    text = "ONH admitted collection cancellation";
                }
                else if (kind == XagmanTradeFailureKind.TradeCanceled)
                {
                    // Visible English error text is neither a fresh event nor a scoped cancellation.
                    kind = XagmanTradeFailureKind.None;
                    text = string.Empty;
                }
            }
            var capacityError = IsXagmanInventoryCapacityFailure(text);
            if (kind == XagmanTradeFailureKind.None) run.LastCapacityError = string.Empty;
            if (!run.Stopping && kind != XagmanTradeFailureKind.None
                && text != run.LastCapacityError)
            {
                run.LastCapacityError = text;
                run.LegacyFailure = !capacityError;
                run.FailureKind = kind;
                run.FailureText = text;
                run.Stopping = true;
                run.StartedAt = now;
                run.OfferLimit = capacityError ? 1 : run.OfferLimit;
                if (!TryStopXagmanDropboxTradeQueue(preserveCapacityBatch: true))
                    return FailXagmanTradeBatch("Dropbox could not be stopped after trade rejection.", out failure);
                if (!TrySetXagmanDropboxAutoAccept(false))
                    return FailXagmanTradeBatch("Dropbox passive acceptance could not be disabled after trade rejection.", out failure);
                if (tradeOpen) TryAbortXagmanTradeWindow();
            }
            run.SawBusy |= busy || tradeOpen;
            if (busy || tradeOpen)
            {
                run.QuietSince = DateTime.MinValue;
                if ((now - run.StartedAt).TotalSeconds > (run.Stopping ? 15 : 60))
                    return FailXagmanTradeBatch("The bounded trade did not settle before its deadline.", out failure);
                return XagmanBatchProgress.Pending;
            }
            if (!run.SawBusy && !run.Stopping && (now - run.StartedAt).TotalSeconds < 10)
                return XagmanBatchProgress.Pending;
            if (run.QuietSince == DateTime.MinValue) run.QuietSince = now;
            if ((now - run.QuietSince).TotalSeconds < 0.75) return XagmanBatchProgress.Pending;
            if (!plugin.DropboxQueue.TryCaptureXagmanTradeStacks(out var afterStacks, out failure))
                return FailXagmanTradeBatch(failure, out failure);
            var after = SumXagmanTradeStacks(afterStacks);
            var reconciled = XagmanTradeBatchAccounting.Reconcile(run.Remaining, run.Offered, run.Before, after);
            if (!reconciled.IsValid)
                return FailXagmanTradeBatch(reconciled.FailureReason, out failure);
            run.Remaining = reconciled.Remaining.ToList();
            var delivered = reconciled.DeliveredQuantity;
            var cleared = run.Stopping
                ? plugin.DropboxQueue.TryClearStoppedXagmanQueue(run.ProviderIdentity, stopConfirmed: true, out failure)
                : plugin.DropboxQueue.TryReplaceXagmanQueue(Array.Empty<XagmanTradeBatchItem>(), out failure);
            if (!cleared)
                return FailXagmanTradeBatch(failure, out failure);
            if (run.LegacyFailure)
            {
                xagmanBatchLegacyFailureKind = run.FailureKind;
                failure = run.FailureText;
                CancelXagmanTradeBatch();
                return XagmanBatchProgress.LegacyFailure;
            }
            if (run.Remaining.Count == 0)
            {
                CancelXagmanTradeBatch();
                return XagmanBatchProgress.Completed;
            }
            run.NoProgress = delivered > 0 ? 0 : run.NoProgress + 1;
            if (run.NoProgress >= 3)
                return FailXagmanTradeBatch("Three bounded trade attempts made no inventory progress; remaining items are retained.", out failure);
            return RequestNextXagmanTradeCapacity(run, out failure)
                ? XagmanBatchProgress.Pending : FailXagmanTradeBatch(failure, out failure);
        }
        catch (Exception error)
        {
            Plugin.Log.Error(error, "[Xagman] Capacity-limited trade failed.");
            return FailXagmanTradeBatch("Capacity-limited trade failed: " + error.Message, out failure);
        }
    }

    private XagmanBatchProgress FailXagmanTradeBatch(string reason, out string failure)
    {
        // Never clear a possibly live provider queue. Ordinary halt/recovery retains the
        // original live-inventory goals and requires a safe, confirmed cleanup before reuse.
        if (xagmanTradeBatch is { } run)
        {
            if (plugin.DropboxQueue.IsXagmanProviderIdentityCurrent(run.ProviderIdentity, out _))
            {
                if (!TryStopXagmanDropboxTradeQueue(preserveCapacityBatch: true))
                    reason += " Dropbox stop is unconfirmed; captured work may still be active.";
                if (!TrySetXagmanDropboxAutoAccept(false))
                    reason += " Dropbox passive acceptance could not be disabled.";
                if (AddonHelper.IsAddonVisible("Trade")) TryAbortXagmanTradeWindow();
            }
            else reason += " Dropbox was replaced; the new provider was left untouched.";
        }
        failure = reason;
        CancelXagmanTradeBatch();
        plugin.TaskRunner.AddLog("Xagman: " + reason);
        return XagmanBatchProgress.Failed;
    }

    private void HaltXagmanCapacityTrade(string reason, bool haltOwningTask = true)
    {
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage,
            global::XASlave.Localization.UiMessage.Authored("Trade capacity could not be confirmed. See the task log."));
        plugin.TaskRunner.AddLog("Xagman: " + reason);
        if (haltOwningTask) plugin.TaskRunner.RequestHalt(reason);
        PublishXagmanPresence();
    }

    private static bool IsXagmanInventoryCapacityFailure(string text)
        => text.Contains("insufficient inventory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sufficient inventory space", StringComparison.OrdinalIgnoreCase)
            || text.Contains("inventory full", StringComparison.OrdinalIgnoreCase);
}
