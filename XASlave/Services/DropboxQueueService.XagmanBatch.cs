using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public sealed partial class DropboxQueueService
{
    internal bool TryGetLoadedDropboxVersion(out string version, out string failure)
    {
        version = string.Empty;
        failure = string.Empty;
        try
        {
            Plugin.AssertGameThread();
            var instance = TryGetDropboxPluginInstance();
            if (instance == null || !ipcClient.IsDropboxAvailable())
            {
                failure = "The loaded Dropbox provider is unavailable.";
                return false;
            }
            if (!XagmanTradeVersionPolicy.TryNormalize(instance.GetType().Assembly.GetName().Version?.ToString(), out version))
            {
                failure = "The loaded Dropbox assembly version is unavailable.";
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            failure = "Could not read the loaded Dropbox assembly version.";
            return false;
        }
    }

    internal bool TryCaptureXagmanProviderIdentity(out object identity, out string failure)
    {
        identity = null!;
        failure = string.Empty;
        try
        {
            Plugin.AssertGameThread();
            return TryGetXagmanDropboxBatchContract(out identity, out _, out _, out failure);
        }
        catch (Exception ex)
        {
            failure = "Could not capture the loaded Dropbox provider identity.";
            log.Warning(ex, "[Xagman] {Failure}", failure);
            return false;
        }
    }

    internal bool IsXagmanProviderIdentityCurrent(object identity, out string failure)
    {
        if (!TryCaptureXagmanProviderIdentity(out var current, out failure))
            return false;
        if (identity != null && ReferenceEquals(identity, current))
            return true;
        failure = "The loaded Dropbox provider changed during the bounded transfer.";
        return false;
    }

    internal bool TryCaptureXagmanQueue(out IReadOnlyList<XagmanTradeBatchItem> selection, out string failure)
    {
        selection = Array.Empty<XagmanTradeBatchItem>();
        failure = string.Empty;
        try
        {
            Plugin.AssertGameThread();
            if (!TryGetXagmanDropboxBatchContract(out _, out _, out var quantities, out failure))
                return false;
            return TryReadXagmanQueue(quantities, out selection, out failure);
        }
        catch (Exception ex)
        {
            failure = "Could not capture the Dropbox selection.";
            log.Warning(ex, "[Xagman] {Failure}", failure);
            return false;
        }
    }

    internal bool TryReplaceXagmanQueue(IReadOnlyList<XagmanTradeBatchItem> items, out string failure)
    {
        failure = string.Empty;
        IDictionary? quantities = null;
        var cleared = false;
        try
        {
            Plugin.AssertGameThread();
            if (items == null || items.Any(item => item.ItemId is 0 or > int.MaxValue || item.Quantity <= 0
                    || item.ItemId <= 19 && item.IsHq)
                || items.Select(item => (item.ItemId, item.IsHq)).Distinct().Count() != items.Count)
            {
                failure = "Invalid replacement Dropbox selection.";
                return false;
            }
            if (!TryGetXagmanDropboxBatchContract(out var instance, out var queueType, out quantities, out failure)
                || !TryRequireXagmanDropboxIdle(instance, queueType, out failure))
                return false;

            // Clearing only ItemQuantities cannot cancel work already captured by
            // BeginTrading. A positively readable idle manager and closed Trade are
            // mandatory; the caller retains its managed remaining quantities.
            quantities.Clear();
            cleared = true;
            foreach (var item in items)
            {
                if (!TryPrepareQueueEntry(item.ItemId, item.IsHq)
                    || !ipcClient.DropboxSetItemQuantity(item.ItemId, item.IsHq, item.Quantity)
                    || !ipcClient.DropboxTryGetItemQuantity(item.ItemId, item.IsHq, out var readback)
                    || readback != item.Quantity)
                {
                    failure = "Dropbox did not retain the exact bounded item quantity.";
                    return false;
                }
            }

            if (!TryGetXagmanDropboxBatchContract(out var current, out var currentType, out var currentQuantities, out failure)
                || !ReferenceEquals(current, instance) || !ReferenceEquals(currentQuantities, quantities)
                || !TryRequireXagmanDropboxIdle(current, currentType, out failure)
                || !TryReadXagmanQueue(quantities, out var captured, out failure)
                || captured.Count != items.Count
                || !captured.OrderBy(item => item.ItemId).ThenBy(item => item.IsHq)
                    .SequenceEqual(items.OrderBy(item => item.ItemId).ThenBy(item => item.IsHq)))
            {
                if (failure.Length == 0)
                    failure = "Dropbox changed while replacing the bounded selection.";
                return false;
            }
            cleared = false;
            return true;
        }
        catch (Exception ex)
        {
            failure = "Could not replace the Dropbox selection.";
            log.Warning(ex, "[Xagman] {Failure}", failure);
            return false;
        }
        finally
        {
            // A partial rewrite must never be started. Preserve pending goals in
            // the caller, but leave this failed selection empty whenever possible.
            if (cleared && quantities != null)
            {
                try { quantities.Clear(); }
                catch (Exception ex) { log.Warning(ex, "[Xagman] Could not clear a failed bounded Dropbox selection."); }
            }
        }
    }

    internal unsafe bool TryCaptureXagmanTradeStacks(out IReadOnlyList<XagmanTradeBatchStack> stacks, out string failure)
    {
        stacks = Array.Empty<XagmanTradeBatchStack>();
        failure = string.Empty;
        try
        {
            Plugin.AssertGameThread();
            if (!TryGetXagmanDropboxBatchContract(out _, out var queueType, out _, out failure))
                return false;
            var inventory = InventoryManager.Instance();
            if (inventory == null || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            {
                failure = "The sender inventory is unavailable.";
                return false;
            }
            if (queueType.GetField("ValidInventories", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not InventoryType[] valid
                || queueType.GetField("CrystalInventories", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not InventoryType[] crystals
                || valid.Length == 0 || crystals.Length == 0 || valid.Length + crystals.Length > 32
                || MainInventoryTypes.Any(type => !valid.Contains(type)) || !crystals.Contains(InventoryType.Crystals))
            {
                failure = "Dropbox's complete physical inventory list is unavailable.";
                return false;
            }

            var result = new List<XagmanTradeBatchStack>();
            foreach (var type in valid.Union(crystals))
            {
                // Do not omit armoury or unloaded containers: a missing small
                // stack would invalidate the conservative per-offer bound.
                if (!NativeArrayAccess.TryGetInventoryContainer(inventory, type, out var container)
                    || container->Size <= 0 || container->Size > 256)
                {
                    failure = $"The sender inventory container {type} is not ready.";
                    return false;
                }
                for (var index = 0; index < container->Size; index++)
                {
                    if (!NativeArrayAccess.TryGetInventorySlot(container, index, out var slot))
                    {
                        failure = "A sender physical inventory slot is unavailable.";
                        return false;
                    }
                    if (slot->ItemId == 0 || slot->SpiritbondOrCollectability != 0 || slot->GlamourId != 0)
                        continue;
                    var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    if (slot->ItemId is 1 or > int.MaxValue || slot->Quantity > int.MaxValue || slot->ItemId <= 19 && isHq)
                    {
                        failure = "A sender physical inventory stack is invalid.";
                        return false;
                    }
                    // Empty crystal slots can retain their fixed item ID. Preserve
                    // zero-valued entries too: BeginTrading can enqueue a zero entry
                    // before reaching another matching physical slot.
                    result.Add(new XagmanTradeBatchStack(slot->ItemId, isHq, (int)slot->Quantity));
                }
            }
            var gil = inventory->GetGil();
            if (gil > int.MaxValue)
            {
                failure = "The sender gil balance is invalid.";
                return false;
            }
            if (gil > 0)
                result.Add(new XagmanTradeBatchStack(1, false, (int)gil));
            stacks = result.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            failure = "Could not capture all sender physical inventory stacks.";
            log.Warning(ex, "[Xagman] {Failure}", failure);
            return false;
        }
    }

    internal bool TryGetXagmanTradeMaxGil(out int maximum, out string failure)
    {
        maximum = 0;
        failure = string.Empty;
        try
        {
            Plugin.AssertGameThread();
            if (!TryGetXagmanDropboxBatchContract(out var instance, out _, out _, out failure))
                return false;
            if (instance.GetType().Assembly.GetType("Dropbox.TradeTask")
                ?.GetField("MaxGil", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not int value || value <= 0)
            {
                failure = "Dropbox's per-trade gil limit is unavailable.";
                return false;
            }
            maximum = Math.Min(value, XagmanTradeBatchPlanner.MaximumGilPerTrade);
            return true;
        }
        catch (Exception ex)
        {
            failure = "Could not read Dropbox's per-trade gil limit.";
            log.Warning(ex, "[Xagman] {Failure}", failure);
            return false;
        }
    }

    private bool TryGetXagmanDropboxBatchContract(out object instance, out Type queueType, out IDictionary quantities, out string failure)
    {
        instance = TryGetDropboxPluginInstance()!;
        queueType = null!;
        quantities = null!;
        failure = string.Empty;
        if (instance == null || !ipcClient.IsDropboxAvailable())
        {
            failure = "The loaded Dropbox provider is unavailable.";
            return false;
        }
        // The active Tony's loaded versions gate the exchange. Keep checking the
        // actual provider capabilities and identity before touching its queue.
        queueType = instance.GetType().Assembly.GetType("Dropbox.ItemQueueUI")!;
        if (queueType?.GetField("ItemQuantities", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not IDictionary storage)
        {
            failure = "Dropbox's selection storage is unavailable.";
            return false;
        }
        quantities = storage;
        return true;
    }

    private static bool TryReadXagmanQueue(IDictionary quantities, out IReadOnlyList<XagmanTradeBatchItem> selection, out string failure)
    {
        selection = Array.Empty<XagmanTradeBatchItem>();
        failure = string.Empty;
        var result = new List<XagmanTradeBatchItem>();
        var seen = new HashSet<(uint, bool)>();
        foreach (DictionaryEntry entry in quantities)
        {
            if (entry.Key?.GetType().GetField("Id")?.GetValue(entry.Key) is not int itemId || itemId <= 0
                || entry.Key.GetType().GetField("HQ")?.GetValue(entry.Key) is not bool isHq
                || entry.Value?.GetType().GetField("Value")?.GetValue(entry.Value) is not int quantity
                || quantity < 0 || itemId <= 19 && isHq || !seen.Add(((uint)itemId, isHq)))
            {
                failure = "Dropbox contains an unreadable or invalid selected quantity.";
                return false;
            }
            if (quantity > 0)
                result.Add(new XagmanTradeBatchItem((uint)itemId, isHq, quantity));
        }
        selection = result.ToArray();
        return true;
    }

    internal bool TryClearStoppedXagmanQueue(object providerIdentity, bool stopConfirmed, out string failure)
    {
        failure = string.Empty;
        try
        {
            Plugin.AssertGameThread();
            if (!stopConfirmed)
            {
                failure = "Dropbox must be explicitly stopped before clearing captured physical entries.";
                return false;
            }
            if (!TryGetXagmanDropboxBatchContract(out var instance, out var queueType, out var quantities, out failure))
                return false;
            if (providerIdentity == null || !ReferenceEquals(providerIdentity, instance))
            {
                failure = "The stopped Dropbox provider was replaced; its successor's queue was not changed.";
                return false;
            }
            if (!TryRequireXagmanDropboxIdle(instance, queueType, out failure, allowPendingPhysicalEntries: true))
                return false;

            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var tradeQueueField = queueType.GetField("TradeQueue", staticFlags);
            var confirmField = instance.GetType().Assembly.GetType("Dropbox.TradeTask")?.GetField("ConfirmAllowed", staticFlags);
            var config = instance.GetType().GetField("C", staticFlags)?.GetValue(null);
            if (tradeQueueField?.GetValue(null) is not IList pending || pending.IsReadOnly || pending.IsFixedSize
                || pending.GetType().GetGenericArguments() is not { Length: 1 } arguments
                || arguments[0].FullName != "Dropbox.QueueEntry"
                || confirmField?.FieldType != typeof(bool) || confirmField.IsInitOnly
                || config?.GetType().GetField("Active", BindingFlags.Public | BindingFlags.Instance)?.GetValue(config) is not false)
            {
                failure = "Stopped Dropbox cleanup requires its verified mutable queue and disabled auto-accept.";
                return false;
            }

            // Abort only clears TaskManager. After an explicitly stopped, settled
            // trade, discard any abandoned BeginTrading entries and its confirm flag.
            confirmField.SetValue(null, false);
            pending.Clear();
            quantities.Clear();
            if (!TryGetXagmanDropboxBatchContract(out var current, out var currentType, out var currentQuantities, out failure)
                || !ReferenceEquals(current, instance) || !ReferenceEquals(currentQuantities, quantities)
                || !ReferenceEquals(tradeQueueField.GetValue(null), pending)
                || quantities.Count != 0 || pending.Count != 0 || confirmField.GetValue(null) is not false
                || !TryRequireXagmanDropboxIdle(current, currentType, out failure))
            {
                if (failure.Length == 0)
                    failure = "Dropbox changed during stopped-queue cleanup.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            failure = "Could not clean the stopped Dropbox physical queue.";
            log.Warning(ex, "[Xagman] {Failure}", failure);
            return false;
        }
    }

    private static unsafe bool TryRequireXagmanDropboxIdle(object instance, Type queueType, out string failure, bool allowPendingPhysicalEntries = false)
    {
        failure = string.Empty;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = instance.GetType().GetField("TaskManager", flags)?.GetValue(instance)
            ?? instance.GetType().GetProperty("TaskManager", flags)?.GetValue(instance);
        if (manager?.GetType().GetProperty("IsBusy", flags)?.GetValue(manager) is not bool busy || busy)
        {
            failure = "Dropbox has not been positively confirmed idle.";
            return false;
        }
        if (queueType.GetField("TradeQueue", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not ICollection pending
            || !allowPendingPhysicalEntries && pending.Count != 0)
        {
            failure = "Dropbox still contains pending physical trade entries.";
            return false;
        }
        var stage = AtkStage.Instance();
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded
            || stage == null || stage->RaptureAtkUnitManager == null)
        {
            failure = "The current Trade window state is unavailable.";
            return false;
        }
        var trade = stage->RaptureAtkUnitManager->GetAddonByName("Trade");
        if (Plugin.Condition[ConditionFlag.TradeOpen] || trade != null && trade->IsVisible)
        {
            failure = "The current Trade window must close before rebuilding its selection.";
            return false;
        }
        return true;
    }
}
