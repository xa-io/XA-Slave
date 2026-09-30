using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class XagmanFiniteGiveGoal
    {
        public int RemainingQuantity;
        public int LastObservedQuantity;
        public int OriginalTargetQuantity;
    }

    private readonly Dictionary<string, XagmanFiniteGiveGoal> xagmanFiniteGiveGoals = new(StringComparer.OrdinalIgnoreCase);
    private sealed class XagmanLegacyTonySupplySnapshot
    {
        public Dictionary<(uint ItemId, bool IsHq), int> Stock = new();
        public Dictionary<string, int> ObservedDemand = new(StringComparer.OrdinalIgnoreCase);
        public int FreeSlots;
        public int Gil;
    }

    private readonly Dictionary<string, XagmanLegacyTonySupplySnapshot> xagmanLegacyTonySupplySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private bool xagmanSupplyDrainOnly;
    private bool xagmanSupplyDrainCleanupPending;
    private bool xagmanSupplyCapacityRotationPending;
    private DateTime xagmanSupplyReceivingPolicyWaitSinceUtc = DateTime.MinValue;
    private bool xagmanSupplyDrainBlockedOnItems;
    private bool xagmanSupplyDrainBlockedOnGil;
    private Dictionary<(uint ItemId, bool IsHq), int> xagmanSupplyDrainStartingStock = new();
    private string xagmanSupplyDrainCharacter = string.Empty;
    private DateTime xagmanSupplyDrainStartedAtUtc = DateTime.MinValue;
    private DateTime xagmanSupplyDrainNextPollAtUtc = DateTime.MinValue;
    private DateTime xagmanSupplyDrainQuietSinceUtc = DateTime.MinValue;

    // A new Tony resets the capacity latch, but must not reset any owner's Give goal.
    private void ResetXagmanTonySupplyDrain()
    {
        xagmanSupplyDrainOnly = xagmanSupplyDrainCleanupPending = false;
        xagmanSupplyCapacityRotationPending = false;
        xagmanSupplyReceivingPolicyWaitSinceUtc = DateTime.MinValue;
        xagmanSupplyDrainBlockedOnItems = xagmanSupplyDrainBlockedOnGil = false;
        xagmanSupplyDrainStartingStock.Clear();
        xagmanSupplyDrainCharacter = string.Empty;
        xagmanSupplyDrainStartedAtUtc = xagmanSupplyDrainNextPollAtUtc = xagmanSupplyDrainQuietSinceUtc = DateTime.MinValue;
    }

    // Call only with the other run-state resets, never at a Tony/owner/pass transition.
    private void ResetXagmanSupplyDrain()
    {
        xagmanFiniteGiveGoals.Clear();
        xagmanLegacyTonySupplySnapshots.Clear();
        ResetXagmanTonySupplyDrain();
    }

    private int GetXagmanPendingGiveQuantity(string owner, XagmanItemEntry item, int current)
    {
        current = Math.Max(0, current);
        if (item.SelectorKind != XagmanItemSelectorKind.ExactItem || item.ItemId == 0)
            return 0;
        if (item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
            return 0;
        var liveOwner = IsXagmanCurrentLocalCharacter(owner);
        if (xagmanRunning && !plugin.Configuration.XagmanOutsideNetworkHelper && liveOwner
            && !TryReadXagmanGiveQuantity(item, out current))
        {
            FailXagmanSupplyCycle($"Cannot reconcile Give/Balance work for {owner}: live inventory is not readable.");
            return 0;
        }
        if (item.Mode == XagmanItemMode.Balance)
            return Math.Max(0, current - Math.Max(0, item.Quantity));
        if (item.Quantity <= 0)
            return current;

        // Preserve the existing non-connected path. Connected collection-first collection
        // also needs the ledger before a later capacity-drain pass is activated.
        if (!xagmanRunning || plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            return xagmanTradeQuantitySnapshot.TryGetValue(GetXagmanTradeSnapshotKey(item.ItemId, item.IsHq), out var captured)
                ? Math.Max(0, current - Math.Max(0, captured - item.Quantity))
                : Math.Min(current, item.Quantity);
        }

        var key = GetXagmanFiniteTakeGoalKey(owner, item.ItemId, item.IsHq);
        if (!xagmanFiniteGiveGoals.TryGetValue(key, out var goal))
        {
            // A database estimate must never pin the original run goal before this owner logs in.
            if (!liveOwner)
                return Math.Min(current, item.Quantity);
            goal = new XagmanFiniteGiveGoal
            {
                RemainingQuantity = Math.Min(current, item.Quantity),
                LastObservedQuantity = current,
                OriginalTargetQuantity = Math.Max(0, current - item.Quantity),
            };
            xagmanFiniteGiveGoals[key] = goal;
        }
        else if (liveOwner)
        {
            // Observe inventory decreases once. Retrying, relogging, or receiving new stock
            // cannot create another Give N batch after the original obligation is satisfied.
            var given = Math.Max(0, goal.LastObservedQuantity - current);
            goal.RemainingQuantity = Math.Max(0, goal.RemainingQuantity - given);
            goal.LastObservedQuantity = current;
        }
        return Math.Min(current, goal.RemainingQuantity);
    }

    private unsafe bool TryGetXagmanLiveGil(out int gil)
    {
        gil = 0;
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null) return false;
            gil = (int)Math.Min((ulong)int.MaxValue, inventory->GetGil());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool TryReadXagmanGiveQuantity(XagmanItemEntry item, out int quantity)
    {
        quantity = 0;
        try
        {
            if (IsXagmanGilItem(item.ItemId))
                return TryGetXagmanLiveGil(out quantity);
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return false;
            long total = 0;
            var types = IsXagmanElementalCrystalItem(item.ItemId) ? XagmanCrystalInventoryTypes : XagmanMainInventoryTypes;
            foreach (var type in types)
            {
                if (!NativeArrayAccess.TryGetInventoryContainer(inventory, type, out var container) || !container->IsLoaded)
                    return false;
                for (var index = 0; index < container->Size; index++)
                {
                    if (!NativeArrayAccess.TryGetInventorySlot(container, index, out var slot))
                        return false;
                    if (slot->ItemId != 0 && slot->GetBaseItemId() == item.ItemId && slot->IsHighQuality() == item.IsHq
                        && !slot->IsSymbolic && slot->SpiritbondOrCollectability == 0)
                        total += slot->Quantity;
                }
            }
            quantity = (int)Math.Min(int.MaxValue, total);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<XagmanTradeRequestEntry> BuildXagmanPendingGiveItems(string owner)
    {
        var pending = new List<XagmanTradeRequestEntry>();
        if (string.IsNullOrWhiteSpace(owner) || xagmanOwnerCollectionCompletedKeys.Contains(owner))
            return pending;
        foreach (var item in ResolveXagmanItemsForOwner(plugin.Configuration.XagmanItems, owner))
        {
            if (item.SelectorKind != XagmanItemSelectorKind.ExactItem
                || item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
                continue;
            var current = GetXagmanCharacterItemQuantity(owner, item.ItemId, item.IsHq, item.ItemName);
            if (xagmanRunning && !plugin.Configuration.XagmanOutsideNetworkHelper && IsXagmanCurrentLocalCharacter(owner)
                && !TryReadXagmanGiveQuantity(item, out current))
            {
                FailXagmanSupplyCycle($"Cannot preserve pending Give/Balance work for {owner}: live inventory is not readable.");
                return pending;
            }
            var quantity = GetXagmanPendingGiveQuantity(owner, item, current);
            if (quantity <= 0)
                continue;
            var target = item.Mode == XagmanItemMode.Balance ? Math.Max(0, item.Quantity) : 0;
            if (item.Mode == XagmanItemMode.Give && item.Quantity > 0)
            {
                var key = GetXagmanFiniteTakeGoalKey(owner, item.ItemId, item.IsHq);
                target = xagmanFiniteGiveGoals.TryGetValue(key, out var goal)
                    ? goal.OriginalTargetQuantity
                    : Math.Max(0, current - quantity);
            }
            pending.Add(new XagmanTradeRequestEntry
            {
                SelectorKind = XagmanItemSelectorKind.ExactItem,
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                IsHq = item.IsHq,
                Mode = item.Mode,
                Quantity = quantity,
                TargetQuantity = target,
                CurrentQuantity = current,
            });
        }
        return pending;
    }

    private bool IsXagmanOwnerSupplyDraining()
    {
        if (!xagmanRunning || plugin.Configuration.XagmanOutsideNetworkHelper
            || xagmanActiveRole != XagmanRole.FranchiseOwner)
            return false;
        var tony = GetXagmanSupplyTonyPeer();
        return tony != null && IsXagmanPeerFresh(tony)
            && tony.SupplyCycleRevision == XagmanSupplyCycleRevision
            && !string.IsNullOrWhiteSpace(xagmanOwnerSupplyPassId)
            && tony.SupplyPassId == xagmanOwnerSupplyPassId
            && tony.InstanceId == xagmanOwnerSupplyTonyInstance
            && IsXagmanSupplyTonyDraining(tony);
    }

    private bool IsXagmanSupplyTonyDraining(XagmanPeerPresence tony)
        => tony.SupplyDrainOnly || (IsXagmanCapacityDrainActive()
            && tony.CapacityDrainId == xagmanCapacityDrainId
            && tony.CapacityRecoveryEpoch == xagmanCapacityRecoveryEpoch
            && tony.CapacityRecoveryCoordinatorInstanceId == tony.InstanceId);

    // Policy capability covers unvisited owners too: an empty current request queue during
    // Give, travel or relog is not evidence that no later owner needs supplies.
    private List<string>? GetXagmanSupplyReceivingPolicyRegions()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner
            || (HasXagmanConditionalItemPolicies(plugin.Configuration.XagmanItems)
                && !xagmanOwnerPolicyRunCapabilitiesPinned))
            return null;
        var unresolved = xagmanOwnerRunPlan.Concat(xagmanPartialOwners.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(IsXagmanSupplyOwnerUnresolved).ToList();
        var receivers = BuildXagmanCollectionFirstOwnerPhasePlan(unresolved, XagmanRunPhase.Restock)
            .Concat(xagmanPartialOwners.Values.Where(state => state.RequestedItems.Count > 0)
                .Select(state => state.CharacterNameWorld)).Distinct(StringComparer.OrdinalIgnoreCase);
        var regions = receivers.Select(GetXagmanRegionOfChar).ToList();
        if (regions.Any(string.IsNullOrWhiteSpace)) return null;
        return regions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool TryGetXagmanReceivingPolicyDemand(out bool hasReceivingPolicies, out bool hasRemainingWork)
    {
        hasReceivingPolicies = false;
        hasRemainingWork = false;
        if (xagmanSupplyOwnerCohort.Count == 0) return false;
        var region = GetXagmanRegionOfChar(xagmanActiveCharacter);
        if (string.IsNullOrWhiteSpace(region)) return false;
        var peers = plugin.XagmanPeers.Peers;
        foreach (var instanceId in xagmanSupplyOwnerCohort)
        {
            if (IsXagmanOwnerClientCompleted(instanceId)) continue;
            var peer = peers.FirstOrDefault(candidate => candidate.InstanceId == instanceId);
            // A prefilled replacement must decide before calling standby owners. Their
            // run-wide policy can come from a prior pass issued by this same run, but
            // never from an unrelated run or coordinator.
            if (peer == null || !IsXagmanPeerFresh(peer) || !peer.XagmanEnabled
                || peer.Role != XagmanRole.FranchiseOwner || !IsXagmanPeerInCurrentRunPhase(peer)
                || peer.SupplyCycleRevision != XagmanSupplyCycleRevision
                || peer.SupplyCoordinatorInstanceId != plugin.InstanceId || !xagmanSupplyRunPassIds.Contains(peer.SupplyPassId)
                || peer.SupplyReceivingPolicyRegions == null || peer.Status == XagmanStatus.Error)
                return false;
            hasReceivingPolicies |= peer.SupplyReceivingPolicyRegions.Contains(region, StringComparer.OrdinalIgnoreCase);
            hasRemainingWork |= xagmanServerMatchingActive
                ? peer.SupplyPendingDataCenters.Any(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), region, StringComparison.OrdinalIgnoreCase))
                    || peer.PartialOwners.Any(state => GetXagmanRegionOfChar(state.CharacterNameWorld).Equals(region, StringComparison.OrdinalIgnoreCase))
                : peer.CompletedCharacters < peer.TotalCharacters || peer.PartialOwners.Count > 0;
        }
        return true;
    }

    private bool IsXagmanCapacityRotationOwnerReady()
    {
        if (string.IsNullOrWhiteSpace(xagmanActiveTradePartner)) return true;
        var owner = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.InstanceId == xagmanActiveTradePartnerInstanceId
            && peer.Role == XagmanRole.FranchiseOwner && peer.XagmanEnabled && IsXagmanPeerFresh(peer)
            && IsXagmanPeerInCurrentRunPhase(peer) && peer.SupplyCoordinatorInstanceId == plugin.InstanceId
            && peer.SupplyPassId == xagmanSupplyPassId);
        if (owner == null) return false;
        // The last transfer can fill Tony exactly as the owner completes verification and
        // begins sendoff. Such an owner has already left trading and need not be cancelled.
        if (!owner.ActiveCharacter.Equals(xagmanActiveTradePartner, StringComparison.OrdinalIgnoreCase)
            || owner.Status is XagmanStatus.ReturningHome or XagmanStatus.Completed)
            return true;
        return xagmanTonyRotationRequestedByOwnerStandby || TryObserveXagmanOwnerStandbyRotationRequest() == true;
    }

    // Forecasts choose scheduling eligibility, never successful owner completion. Null
    // retains uncertainty (including selectors that exact-item forecasts cannot model).
    private bool? GetXagmanForecastTonyUse(IReadOnlyList<XagmanPeerPresence> owners,
        IReadOnlyDictionary<(uint ItemId, bool IsHq), int> stock, string region,
        int freeSlots, int gil, bool receivingBlocked = false)
    {
        if (owners.Count == 0 || string.IsNullOrWhiteSpace(region)) return null;
        var useful = false;
        foreach (var peer in owners)
        {
            var forecast = peer.TradeCapacityForecast;
            if (!IsXagmanPeerFresh(peer) || !peer.XagmanEnabled || peer.Role != XagmanRole.FranchiseOwner
                || peer.SupplyCycleRevision != XagmanSupplyCycleRevision || !IsXagmanPeerInCurrentRunPhase(peer)
                || peer.SupplyCoordinatorInstanceId != plugin.InstanceId || !xagmanSupplyRunPassIds.Contains(peer.SupplyPassId)
                || forecast == null || !IsXagmanTradeCapacityForecastFresh(forecast, DateTime.UtcNow)
                || !forecast.SchedulingPoliciesComplete || forecast.IsTruncated || forecast.UnknownOwnerCount > 0
                || forecast.KnownOwnerCount != forecast.SelectedOwnerCount
                || forecast.SelectedOwnerKeys.Count != forecast.SelectedOwnerCount
                || forecast.Items.Any(item => item.UnknownOwnerCount > 0 || item.GroupKey == "Unknown"))
                return null;
            foreach (var row in forecast.Items.Where(item => item.GroupKey.Equals(region, StringComparison.OrdinalIgnoreCase)))
            {
                useful |= (row.NeededFromTonyQuantity > 0 || row.AllAvailableRequestCount > 0)
                    && stock.GetValueOrDefault((row.ItemId, row.IsHq)) > 0;
                if (!receivingBlocked && row.IncomingToTonyQuantity > 0)
                    useful |= row.ItemId == 1 ? gil < XagmanTonySellGilLimit : freeSlots > 0;
            }
            // Live reconciled requests outrank a forecast captured before the last trade.
            if (!receivingBlocked)
                useful |= peer.PartialOwners
                    .Where(owner => GetXagmanRegionOfChar(owner.CharacterNameWorld).Equals(region, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(owner => owner.PendingGiveItems)
                    .Any(item => item.Quantity > 0 && (item.ItemId == 1 ? gil < XagmanTonySellGilLimit : freeSlots > 0));
            var explicitRequests = peer.PartialOwners
                .Where(owner => GetXagmanRegionOfChar(owner.CharacterNameWorld).Equals(region, StringComparison.OrdinalIgnoreCase))
                .SelectMany(owner => owner.RequestedItems)
                .Concat(GetXagmanRegionOfChar(peer.ActiveCharacter).Equals(region, StringComparison.OrdinalIgnoreCase)
                    ? peer.RequestedItems : Enumerable.Empty<XagmanTradeRequestEntry>());
            foreach (var request in explicitRequests)
            {
                if (request.SelectorKind != XagmanItemSelectorKind.ExactItem) return null;
                useful |= stock.GetValueOrDefault((request.ItemId, request.IsHq)) > 0;
            }
        }
        return useful;
    }

    // True means this capacity fallback was handled, including a fail-closed outcome.
    // A missing requested item alone is never evidence of receiver capacity exhaustion.
    private bool TryBeginXagmanTonySupplyDrain()
    {
        if (!IsXagmanSupplyCycleActive() || xagmanActiveRole != XagmanRole.Tony)
            return false;
        if (xagmanSupplyDrainOnly || xagmanSupplyDrainCleanupPending)
        {
            if (!xagmanSupplyCapacityRotationPending) xagmanTonyRotationRequestedByOwnerStandby = false;
            return true;
        }
        if (!IsXagmanCurrentLocalCharacter(xagmanActiveCharacter))
            return false;
        var fullInventory = TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots) && freeSlots == 0;
        var gilReadable = TryGetXagmanLiveGil(out var currentGil);
        var gilAtLimit = gilReadable && currentGil >= XagmanTonySellGilLimit;
        if (!fullInventory && !gilAtLimit)
            return false;
        if (!gilReadable)
        {
            FailXagmanSupplyCycle("Tony gil could not be read at the capacity-drain boundary; unfinished owner work was preserved.");
            return true;
        }

        if (!TryGetXagmanReceivingPolicyDemand(out var hasReceivingPolicies, out var hasRemainingWork))
        {
            if (xagmanSupplyReceivingPolicyWaitSinceUtc == DateTime.MinValue)
                xagmanSupplyReceivingPolicyWaitSinceUtc = DateTime.UtcNow;
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = "Tony is at receiving capacity; waiting for every owner client's receiving-policy information before choosing distribution or rotation.";
            TrySetXagmanDropboxAutoAcceptOrStop(false, "Tony receiving-policy check");
            if ((DateTime.UtcNow - xagmanSupplyReceivingPolicyWaitSinceUtc).TotalSeconds >= 60)
                FailXagmanSupplyCycle("Owner receiving-policy information remained unknown at Tony capacity; unfinished owners were preserved.");
            PublishXagmanPresence();
            return true;
        }
        xagmanSupplyReceivingPolicyWaitSinceUtc = DateTime.MinValue;
        if (!hasRemainingWork) return false;

        var hasUsefulSupply = hasReceivingPolicies;
        if (hasReceivingPolicies && TryCaptureXagmanSupplyInventory(out var liveStock))
        {
            var owners = plugin.XagmanPeers.Peers.Where(peer => xagmanSupplyOwnerCohort.Contains(peer.InstanceId)
                && !IsXagmanOwnerClientCompleted(peer.InstanceId)).ToList();
            hasUsefulSupply = GetXagmanForecastTonyUse(owners, CaptureXagmanSupplyStock(liveStock),
                GetXagmanRegionOfChar(xagmanActiveCharacter), freeSlots, currentGil, receivingBlocked: true) != false;
        }

        xagmanSupplyCapacityRotationPending = !hasUsefulSupply;
        xagmanSupplyDrainOnly = hasUsefulSupply;
        xagmanSupplyDrainCleanupPending = true;
        xagmanSupplyDrainBlockedOnItems = fullInventory;
        xagmanSupplyDrainBlockedOnGil = gilAtLimit;
        xagmanSupplyDrainStartingStock.Clear();
        xagmanSupplyDrainCharacter = xagmanActiveCharacter;
        xagmanSupplyDrainStartedAtUtc = DateTime.UtcNow;
        xagmanSupplyDrainNextPollAtUtc = xagmanSupplyDrainQuietSinceUtc = DateTime.MinValue;
        if (hasUsefulSupply) xagmanTonyRotationRequestedByOwnerStandby = false;
        if (xagmanSupplyCapacityRotationPending) xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = hasUsefulSupply
            ? $"Tony {xagmanActiveCharacter} reached {(fullInventory ? "inventory capacity" : "the selling gil limit")}; closing the receiving trade before distributing remaining useful stock."
            : $"Tony {xagmanActiveCharacter} reached receiving capacity with no useful future supply {(hasReceivingPolicies ? "in the remaining-owner forecast" : "policies in this region")}; closing the trade before completing this Tony and rotating. Unfinished owners will wait on their current character.";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        // Keep the active owner lock while the receiver queue and native Trade window settle.
        // The presence flag lets that owner preserve its unfinished Give work immediately.
        PublishXagmanPresence();
        UpdateXagmanTonySupplyDrain();
        return true;
    }

    // Poll before normal Tony queue/trade dispatch. True holds all ordinary work this frame.
    private bool UpdateXagmanTonySupplyDrain()
    {
        if (!xagmanSupplyDrainCleanupPending)
            return false;
        if (!xagmanRunning || xagmanStatus == XagmanStatus.Error)
            return true;
        var now = DateTime.UtcNow;
        if (!IsXagmanCurrentLocalCharacter(xagmanSupplyDrainCharacter)
            || !xagmanActiveCharacter.Equals(xagmanSupplyDrainCharacter, StringComparison.OrdinalIgnoreCase))
        {
            FailXagmanSupplyCycle("Tony changed before the receiving trade was safely closed; partial owner work remains preserved.");
            return true;
        }
        if ((now - xagmanSupplyDrainStartedAtUtc).TotalSeconds >= 15)
        {
            FailXagmanSupplyCycle("Tony's capacity handoff did not reach a closed trade and safe owner acknowledgement within 15 seconds; unfinished owner work remains preserved.");
            return true;
        }
        if (now < xagmanSupplyDrainNextPollAtUtc)
            return true;
        xagmanSupplyDrainNextPollAtUtc = now.AddMilliseconds(500);
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "Tony capacity supply drain"))
            return true;
        if (!TryStopXagmanDropboxTradeQueue())
        {
            FailXagmanSupplyCycle("Could not confirm Dropbox's receiving queue was stopped before Tony supply drain.");
            return true;
        }
        if (!TryClearXagmanDropbox(out var failure))
        {
            FailXagmanSupplyCycle($"Could not clear Dropbox's receiving queue before Tony supply drain: {failure}");
            return true;
        }
        if (AddonHelper.IsAddonVisible("Trade"))
        {
            TryAbortXagmanTradeWindow();
            xagmanSupplyDrainQuietSinceUtc = DateTime.MinValue;
            return true;
        }
        if (plugin.IpcClient.DropboxIsBusy())
        {
            xagmanSupplyDrainQuietSinceUtc = DateTime.MinValue;
            return true;
        }
        if (xagmanSupplyDrainQuietSinceUtc == DateTime.MinValue)
            xagmanSupplyDrainQuietSinceUtc = now;
        if ((now - xagmanSupplyDrainQuietSinceUtc).TotalSeconds < 1)
            return true;
        if (xagmanSupplyCapacityRotationPending)
        {
            // Paused makes the active FO cancel into its existing standby path, retaining
            // its exact character cursor and finite Give ledger. Do not rotate before ack.
            if (!IsXagmanCapacityRotationOwnerReady())
                return true;
            if (!TryGetXagmanReceivingPolicyDemand(out var hasReceivingPolicies, out var hasRemainingWork)) return true;
            if (!hasRemainingWork)
            {
                // The last owner may have completed during cleanup. Let the ordinary
                // exact-pass/menu completion or next-region barrier run without a relog.
                ResetXagmanTonySupplyDrain();
                xagmanTonyRotationRequestedByOwnerStandby = false;
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = "Tony is waiting for owner completion acknowledgements.";
                PublishXagmanPresence();
                return true;
            }
            if (hasReceivingPolicies)
            {
                // A final incoming trade can add useful stock while shutdown settles.
                // Re-evaluate after closure before permanently discarding revisit eligibility.
                xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
                if (!TryCaptureXagmanSupplyInventory(out var settledStock) || !TryGetXagmanLiveGil(out var settledGil)) return true;
                var owners = plugin.XagmanPeers.Peers.Where(peer => xagmanSupplyOwnerCohort.Contains(peer.InstanceId)
                    && !IsXagmanOwnerClientCompleted(peer.InstanceId)).ToList();
                if (GetXagmanForecastTonyUse(owners, CaptureXagmanSupplyStock(settledStock),
                        GetXagmanRegionOfChar(xagmanActiveCharacter), 0, settledGil, receivingBlocked: true) != false)
                {
                    xagmanSupplyCapacityRotationPending = false;
                    xagmanSupplyDrainOnly = true;
                }
            }
            if (xagmanSupplyCapacityRotationPending)
            {
                xagmanSupplyDrainCleanupPending = false;
                xagmanLegacyTonySupplySnapshots.Remove(xagmanActiveCharacter);
                // Keep the rotation latch through fallback dispatch to prevent re-entering drain.
                StartXagmanTonyFullInventoryFallback(xagmanActiveTradePartner);
                return true;
            }
        }
        xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
        if (!TryCaptureXagmanSupplyInventory(out var startingStock) || !TryGetXagmanLiveGil(out var startingGil))
            return true;
        // Capture after receiver cleanup and before permitting any outgoing supply. Unknown
        // native inventory never becomes evidence that receiving capacity was recovered.
        xagmanSupplyDrainStartingStock = CaptureXagmanSupplyStock(startingStock);
        xagmanSupplyDrainStartingStock[(1, false)] = Math.Max(0, startingGil - GetXagmanTonyGilMinimum());

        xagmanSupplyDrainCleanupPending = false;
        xagmanObservedDropboxBusy = false;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        // Retain the current owner's explicit call through the direction change. Releasing it
        // here would make the owner's normal lock-loss guard enter rotation standby before
        // it can advertise remaining supply requests or acknowledge unfinished Give work.
        xagmanLastTonyActionAtUtc = now;
        xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
        ClearXagmanFocusTarget();
        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} is distributing useful stock; unfinished owner Give work remains pending until receiving capacity is available.";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
        return true;
    }

    // The caller has already observed every owner's exact pass acknowledgement at the menu.
    // Only proven outgoing progress that clears the original capacity limits opens another
    // receiving pass on this same logged-in Tony; unchanged inventory cannot cause a loop.
    private bool TryResumeXagmanLegacyTonyReceiving(
        IReadOnlyList<XagmanPeerPresence> owners,
        IReadOnlyList<XagmanTradeRequestEntry> availableStock)
    {
        if (IsXagmanCollectionFirstRunActive() || !xagmanSupplyDrainOnly || xagmanSupplyDrainCleanupPending)
            return false;
        var region = GetXagmanRegionOfChar(xagmanActiveCharacter);
        if (!owners.Any(peer => peer.PartialOwners.Any(state => state.PendingGiveItems.Count > 0
            && GetXagmanRegionOfChar(state.CharacterNameWorld).Equals(region, StringComparison.OrdinalIgnoreCase))))
            return false;
        if (!IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)
            || !TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots)
            || !TryGetXagmanLiveGil(out var currentGil))
        {
            FailXagmanSupplyCycle("Tony receiving capacity could not be verified after supply drain; pending Give work was preserved.");
            return true;
        }
        var currentStock = CaptureXagmanSupplyStock(availableStock);
        currentStock[(1, false)] = Math.Max(0, currentGil - GetXagmanTonyGilMinimum());
        var deliveredSupply = xagmanSupplyDrainStartingStock.Any(item => currentStock.GetValueOrDefault(item.Key) < item.Value);
        var recoveredCapacity = (!xagmanSupplyDrainBlockedOnItems || freeSlots > 0)
            && (!xagmanSupplyDrainBlockedOnGil || currentGil < XagmanTonySellGilLimit);
        if (!deliveredSupply || !recoveredCapacity)
            return false;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, "resume Tony receiving after useful supply drain"))
            return true;
        ResetXagmanTonySupplyDrain();
        if (xagmanServerMatchingActive)
        {
            var firstPendingDataCenter = owners.SelectMany(peer => peer.SupplyPendingDataCenters)
                .Where(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), region, StringComparison.OrdinalIgnoreCase))
                .OrderBy(WorldData.GetSweepOrdinal).FirstOrDefault();
            if (firstPendingDataCenter != null)
            {
                xagmanSweepDataCenter = firstPendingDataCenter;
                SetXagmanActiveMeetDestination(GetXagmanServerMeetWorld(firstPendingDataCenter), GetXagmanSharedMeetLocation());
                xagmanSweepServerDrainedSinceUtc = DateTime.MinValue;
                ResetXagmanTonyMeetRetryState();
            }
        }
        BeginXagmanTonySupplyPass();
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        xagmanStatus = XagmanStatus.AtMeetSpot;
        xagmanStatusText = $"Tony {xagmanActiveCharacter} recovered receiving capacity through supply deliveries; starting another pass for unfinished owner Give work.";
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
        return true;
    }

    private static string GetXagmanLegacyDemandKey(XagmanPeerPresence peer, XagmanPartialOwnerState owner,
        XagmanTradeRequestEntry item, bool giving)
        => $"{peer.InstanceId}|{owner.CharacterNameWorld}|{(giving ? "give" : "take")}|{item.ItemId}|{item.IsHq}|{item.Mode}|{item.TargetQuantity}";

    private static Dictionary<string, int> CaptureXagmanLegacyDemand(IReadOnlyList<XagmanPeerPresence> owners)
    {
        var demand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in owners)
        foreach (var owner in peer.PartialOwners)
        {
            foreach (var item in owner.RequestedItems.Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem))
                demand[GetXagmanLegacyDemandKey(peer, owner, item, false)] = IsXagmanOpenEndedSupplyRequest(item) ? int.MaxValue : Math.Max(0, item.Quantity);
            foreach (var item in owner.PendingGiveItems)
                demand[GetXagmanLegacyDemandKey(peer, owner, item, true)] = Math.Max(0, item.Quantity);
        }
        return demand;
    }

    // Keep departed Tonys' verified stock/capacity as scheduling hints, never as completion
    // evidence. Revisit only for newly reported exact-item work that was absent or smaller at
    // that Tony's last completed pass. Repeating unchanged partial states cannot reopen it.
    private void RetainXagmanLegacyTonyEligibility(
        IReadOnlyList<XagmanPeerPresence> owners,
        IReadOnlyList<XagmanTradeRequestEntry> availableStock)
    {
        if (IsXagmanCollectionFirstRunActive()) return;
        var demand = CaptureXagmanLegacyDemand(owners);
        var region = GetXagmanRegionOfChar(xagmanActiveCharacter);
        if (IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)
            && TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots)
            && TryGetXagmanLiveGil(out var currentGil))
        {
            var verifiedStock = CaptureXagmanSupplyStock(availableStock);
            verifiedStock[(1, false)] = Math.Max(0, currentGil - GetXagmanTonyGilMinimum());
            if (GetXagmanForecastTonyUse(owners, verifiedStock, region, freeSlots, currentGil) == false)
            {
                xagmanLegacyTonySupplySnapshots.Remove(xagmanActiveCharacter);
                plugin.TaskRunner.AddLog($"Xagman: the remaining-owner forecast has no useful future trade for Tony {xagmanActiveCharacter}; completing this Tony without retaining it for another relog.");
            }
            else
                xagmanLegacyTonySupplySnapshots[xagmanActiveCharacter] = new XagmanLegacyTonySupplySnapshot
                {
                    Stock = verifiedStock,
                    ObservedDemand = new Dictionary<string, int>(demand, StringComparer.OrdinalIgnoreCase),
                    FreeSlots = freeSlots,
                    Gil = currentGil,
                };
        }
        else xagmanLegacyTonySupplySnapshots.Remove(xagmanActiveCharacter);
        foreach (var tony in xagmanTonyRunPlan)
        {
            if (tony.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
                || xagmanTonyRunList.Contains(tony, StringComparer.OrdinalIgnoreCase)
                || plugin.TaskRunner.FailedCharacters.Contains(tony)
                || !GetXagmanRegionOfChar(tony).Equals(region, StringComparison.OrdinalIgnoreCase)
                || !xagmanLegacyTonySupplySnapshots.TryGetValue(tony, out var previous))
                continue;
            if (GetXagmanForecastTonyUse(owners, previous.Stock, region, previous.FreeSlots, previous.Gil) == false)
            {
                xagmanLegacyTonySupplySnapshots.Remove(tony);
                continue;
            }
            var newlyUseful = owners.Any(peer => peer.PartialOwners.Any(owner =>
                GetXagmanRegionOfChar(owner.CharacterNameWorld).Equals(region, StringComparison.OrdinalIgnoreCase)
                && (owner.RequestedItems.Any(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                    && previous.Stock.GetValueOrDefault((item.ItemId, item.IsHq)) > 0
                    && demand.GetValueOrDefault(GetXagmanLegacyDemandKey(peer, owner, item, false))
                        > previous.ObservedDemand.GetValueOrDefault(GetXagmanLegacyDemandKey(peer, owner, item, false)))
                    || owner.PendingGiveItems.Any(item => previous.FreeSlots > 0 && previous.Gil < XagmanTonySellGilLimit
                        && demand.GetValueOrDefault(GetXagmanLegacyDemandKey(peer, owner, item, true))
                            > previous.ObservedDemand.GetValueOrDefault(GetXagmanLegacyDemandKey(peer, owner, item, true))))));
            if (!newlyUseful) continue;
            // Claim this new-demand opportunity before relogging; a later live no-stock
            // result cannot schedule it again from the same saved inventory hint.
            previous.ObservedDemand = new Dictionary<string, int>(demand, StringComparer.OrdinalIgnoreCase);
            xagmanTonyRunList.Add(tony);
            xagmanTonyCompletedCharacters = Math.Max(0, xagmanTonyCompletedCharacters - 1);
            for (var index = 0; index < plugin.Configuration.XagmanTonyCharacters.Count; index++)
                if (plugin.Configuration.XagmanTonyCharacters[index].CharacterNameWorld.Equals(tony, StringComparison.OrdinalIgnoreCase))
                { xagmanTonySelectedIndices.Add(index); break; }
            plugin.TaskRunner.AddLog($"Xagman: earlier Tony {tony} has saved stock or capacity for newly reported owner work; queued one live revisit after the remaining Tonys.");
        }
    }
}
