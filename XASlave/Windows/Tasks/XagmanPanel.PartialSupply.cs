using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int XagmanSupplyCycleRevision = 3;
    private const int XagmanInitialSupplyBindTimeoutSeconds = 600;
    private const int XagmanSupplyRebindTimeoutSeconds = 120;
    private readonly Dictionary<string, XagmanPartialOwnerState> xagmanPartialOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanOwnerCollectionCompletedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanSupplyVisitedOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanSupplyOwnerCohort = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanSupplyRunPassIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> xagmanSupplyPartialOwnerInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<(uint ItemId, bool IsHq), int>> xagmanSupplyOwnerStockAtVisit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<(uint ItemId, bool IsHq), int>> xagmanSupplyDeferredStockByOwner = new(StringComparer.OrdinalIgnoreCase);
    private string xagmanSupplyPassId = string.Empty;
    private string xagmanOwnerSupplyPassId = string.Empty;
    private string xagmanOwnerSupplyTonyInstance = string.Empty;
    private string xagmanOwnerSupplyStartTonyInstance = string.Empty;
    private string xagmanOwnerSupplyRegion = string.Empty;
    private bool xagmanOwnerSupplyPassComplete;
    private bool xagmanOwnerSupplyWaiting;
    private bool xagmanOwnerSupplyAtMenu;
    private bool xagmanOwnerSupplyContinuation;
    private string xagmanDeferredOwnerCharacter = string.Empty;
    private string xagmanDeferredOwnerInstanceId = string.Empty;
    private DateTime xagmanDeferredOwnerSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyBarrierSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyRecallSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyInitialCohortSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyUnboundCohortSinceUtc = DateTime.MinValue;
    private DateTime xagmanOwnerSupplyBindSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyMissingPeerSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyInventoryUnreadableSinceUtc = DateTime.MinValue;
    private DateTime xagmanOwnerSupplyWaitSinceUtc = DateTime.MinValue;
    private DateTime xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
    private string xagmanSupplyInventoryCachedCharacter = string.Empty;
    private List<XagmanTradeRequestEntry> xagmanSupplyInventoryCache = new();

    private bool IsXagmanSupplyCycleActive() => xagmanRunning
        && !plugin.Configuration.XagmanOutsideNetworkHelper
        && !IsXagmanCollectionFirstCollectionPhase();

    private void ResetXagmanSupplyCycle()
    {
        xagmanSupplyPassClosing = false;
        xagmanServerSupplyAttempts.Clear();
        xagmanServerSupplyFinished.Clear();
        xagmanServerSupplyStock.Clear();
        ResetXagmanOwnerCompletion();
        ResetXagmanSupplyDrain();
        ResetXagmanCapacityRecovery();
        xagmanPartialOwners.Clear();
        xagmanOwnerCollectionCompletedKeys.Clear();
        xagmanSupplyVisitedOwners.Clear();
        xagmanSupplyOwnerCohort.Clear();
        xagmanSupplyRunPassIds.Clear();
        xagmanSupplyPartialOwnerInstances.Clear();
        xagmanSupplyOwnerStockAtVisit.Clear();
        xagmanSupplyDeferredStockByOwner.Clear();
        xagmanSupplyPassId = xagmanOwnerSupplyPassId = xagmanOwnerSupplyTonyInstance = xagmanOwnerSupplyRegion = string.Empty;
        xagmanOwnerSupplyStartTonyInstance = string.Empty;
        xagmanOwnerSupplyPassComplete = xagmanOwnerSupplyWaiting = xagmanOwnerSupplyAtMenu = false;
        xagmanOwnerSupplyContinuation = false;
        xagmanDeferredOwnerCharacter = xagmanDeferredOwnerInstanceId = string.Empty;
        xagmanDeferredOwnerSinceUtc = xagmanSupplyBarrierSinceUtc = xagmanSupplyMissingPeerSinceUtc = DateTime.MinValue;
        xagmanSupplyRecallSinceUtc = DateTime.MinValue;
        xagmanSupplyInitialCohortSinceUtc = xagmanSupplyUnboundCohortSinceUtc = xagmanOwnerSupplyBindSinceUtc = DateTime.MinValue;
        xagmanSupplyInventoryUnreadableSinceUtc = xagmanOwnerSupplyWaitSinceUtc = DateTime.MinValue;
        xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
        xagmanSupplyInventoryCachedCharacter = string.Empty;
        xagmanSupplyInventoryCache.Clear();
    }

    private void BeginXagmanTonySupplyPass()
    {
        if (!IsXagmanSupplyCycleActive()) return;
        xagmanSupplyPassClosing = false;
        xagmanSupplyPassId = Guid.NewGuid().ToString("N");
        xagmanSupplyRunPassIds.Add(xagmanSupplyPassId);
        xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
        xagmanSupplyInventoryCachedCharacter = string.Empty;
        xagmanSupplyInventoryCache.Clear();
        xagmanSupplyDeferredStockByOwner.Clear();
        xagmanSupplyRecallSinceUtc = DateTime.MinValue;
        xagmanDeferredOwnerCharacter = xagmanDeferredOwnerInstanceId = string.Empty;
        xagmanDeferredOwnerSinceUtc = xagmanSupplyBarrierSinceUtc = DateTime.MinValue;
        xagmanSupplyInventoryUnreadableSinceUtc = DateTime.MinValue;
    }

    private void FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage reasonPresentation)
    {
        var reason = reasonPresentation.Original;
        if (xagmanStatus == XagmanStatus.Error)
        { plugin.TaskRunner.RequestHalt(reason); return; }
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, reasonPresentation);
        plugin.TaskRunner.AddLog($"Xagman: {reason}");
        plugin.TaskRunner.RequestHalt(reason);
        PublishXagmanPresence();
    }

    private bool IsXagmanSupplyOwnerUnresolved(string owner) => !xagmanOwnerCompletedKeys.Contains(owner)
        && !IsXagmanCapacityDrainOwnerFilled(owner)
        && !plugin.TaskRunner.FailedCharacters.Contains(owner) && !xagmanSkippedCharacters.Contains(owner);

    // Fixed-world legacy runs share one meetup across home regions. Travel dispatch still
    // validates each character's route; server sweeps and phased recovery stay regional.
    private bool IsXagmanSupplyRegionScoped(bool serverMatching)
        => serverMatching || IsXagmanCollectionFirstRunActive() || IsXagmanCapacityDrainActive();

    private List<string> GetXagmanSupplyPendingDataCenters()
    {
        if (!IsXagmanSupplyCycleActive() || xagmanActiveRole != XagmanRole.FranchiseOwner) return new();
        if (xagmanOwnerSupplyWaiting && !xagmanOwnerSupplyAtMenu && !xagmanOwnerSupplyPassComplete)
        {
            var drainingDataCenter = GetXagmanSupplyTonyPeer()?.ServerMatchingActiveDataCenter;
            if (!string.IsNullOrWhiteSpace(drainingDataCenter)) return new() { drainingDataCenter };
        }
        var continuationTony = xagmanOwnerSupplyWaiting && xagmanOwnerSupplyAtMenu && xagmanOwnerSupplyContinuation
            ? GetXagmanSupplyTonyPeer() : null;
        var frontier = continuationTony != null ? GetXagmanUsefulUnvisitedOwners(continuationTony)
            : xagmanOwnerSupplyPassComplete ? xagmanOwnerRunPlan
            : xagmanOwnerRunList.Skip(Math.Max(0, xagmanOwnerCurrentCharacterIndex));
        return frontier.Where(IsXagmanSupplyOwnerUnresolved)
            .Where(owner => continuationTony != null || xagmanOwnerSupplyPassComplete || !xagmanSupplyVisitedOwners.Contains(owner))
            .Where(owner => xagmanOwnerSupplyPassComplete || string.IsNullOrWhiteSpace(xagmanOwnerSupplyRegion)
                || GetXagmanRegionOfChar(owner).Equals(xagmanOwnerSupplyRegion, StringComparison.OrdinalIgnoreCase))
            .Select(GetXagmanDataCenterOfChar).Where(dc => !string.IsNullOrWhiteSpace(dc))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(WorldData.GetSweepOrdinal).ToList();
    }

    private bool TryBindXagmanOwnerSupplyPass()
    {
        if (!IsXagmanSupplyCycleActive()) return true;
        bool WaitForSupplyPass(global::XASlave.Localization.UiMessage reason)
        {
            // Initial Start is sent before Tony's FC return, relog and meetup travel.
            // Allow that handshake to survive delayed/missing readiness advertisements;
            // an established supply cycle retains the shorter missing-pass bound.
            var initialBinding = string.IsNullOrWhiteSpace(xagmanOwnerSupplyPassId);
            var timeoutSeconds = initialBinding ? XagmanInitialSupplyBindTimeoutSeconds : XagmanSupplyRebindTimeoutSeconds;
            var coordinator = GetXagmanOwnerSupplyCoordinatorInstance();
            var coordinatorLabel = string.IsNullOrWhiteSpace(coordinator) ? global::XASlave.Localization.UiMessage.Key("S291A6BA9BE4DA15A") : global::XASlave.Localization.UiMessage.Raw(coordinator);
            if (xagmanOwnerSupplyBindSinceUtc == DateTime.MinValue)
            {
                xagmanOwnerSupplyBindSinceUtc = DateTime.UtcNow;
                plugin.TaskRunner.AddLog($"Xagman: waiting up to {timeoutSeconds}s for a readable Tony supply pass (initial binding={initialBinding}, coordinator={coordinatorLabel}): {reason}.");
            }
            if ((DateTime.UtcNow - xagmanOwnerSupplyBindSinceUtc).TotalSeconds > timeoutSeconds)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Format("S9FB4E77652131E4C", timeoutSeconds, coordinatorLabel, reason));
            return false;
        }
        var tony = GetXagmanSupplyTonyPeer();
        if (tony == null || !IsXagmanPeerFresh(tony))
            return WaitForSupplyPass(global::XASlave.Localization.UiMessage.Key("S6FC2FDA70514F31A"));
        if (!string.IsNullOrWhiteSpace(xagmanOwnerSupplyTonyInstance) && tony.InstanceId != xagmanOwnerSupplyTonyInstance)
        {
            FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S84579FD587088D9F"));
            return false;
        }
        if (tony.SupplyCycleRevision != XagmanSupplyCycleRevision)
        {
            FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S8B47A89758F965B4"));
            return false;
        }
        if (string.IsNullOrWhiteSpace(tony.SupplyPassId) || !tony.SupplyInventoryComplete)
        {
            if (tony.Status is XagmanStatus.Preflight or XagmanStatus.Relogging or XagmanStatus.Traveling or XagmanStatus.ReturningHome)
            {
                // Supplier travel keeps its existing bounded retry policy. This timeout only
                // diagnoses a missing coordinator or unreadable stock after travel settles.
                xagmanOwnerSupplyBindSinceUtc = DateTime.MinValue;
                return false;
            }
            return WaitForSupplyPass(global::XASlave.Localization.UiMessage.Format("S3FC6ED618C9E121B", tony.ActiveCharacter, tony.Status, !string.IsNullOrWhiteSpace(tony.SupplyPassId), tony.SupplyInventoryComplete));
        }
        xagmanOwnerSupplyBindSinceUtc = DateTime.MinValue;
        if (xagmanOwnerSupplyPassId == tony.SupplyPassId) return true;
        xagmanOwnerSupplyPassId = tony.SupplyPassId;
        xagmanOwnerSupplyTonyInstance = tony.InstanceId;
        xagmanOwnerSupplyRegion = IsXagmanSupplyRegionScoped(tony.ServerMatchingEnabled)
            ? GetXagmanRegionOfChar(tony.ActiveCharacter) : string.Empty;
        xagmanPreferredTonyCharacter = tony.ActiveCharacter;
        xagmanOwnerSupplyPassComplete = xagmanOwnerSupplyWaiting = xagmanOwnerSupplyAtMenu = false;
        xagmanOwnerSupplyWaitSinceUtc = DateTime.MinValue;
        xagmanSupplyVisitedOwners.Clear();
        xagmanSupplyOwnerStockAtVisit.Clear();
        plugin.TaskRunner.AddLog($"Xagman: bound owner supply pass to Tony {tony.ActiveCharacter} (client {tony.InstanceId}, pass {tony.SupplyPassId}).");
        PublishXagmanPresence();
        return true;
    }

    private bool ShouldDeferXagmanOwnerForSupplyPass(string owner)
    {
        if (!IsXagmanSupplyCycleActive()) return false;
        if (!IsXagmanSupplyOwnerUnresolved(owner)) return true;
        var tony = GetXagmanSupplyTonyPeer();
        if (tony == null || tony.SupplyPassId != xagmanOwnerSupplyPassId)
        {
            // A capacity/error replacement is adopted only at a character boundary.
            if (!TryBindXagmanOwnerSupplyPass()) return true;
            tony = GetXagmanSupplyTonyPeer();
        }
        if (IsXagmanOwnerSupplyPassClosing(tony) || (tony != null && !IsXagmanOwnerInSupplyServer(owner, tony))) return true;
        if (tony == null || (IsXagmanSupplyRegionScoped(tony.ServerMatchingEnabled)
            && !GetXagmanRegionOfChar(owner).Equals(xagmanOwnerSupplyRegion, StringComparison.OrdinalIgnoreCase))) return true;
        if (xagmanPartialOwners.TryGetValue(owner, out var partialState)
            && tony != null && tony.SupplyInventoryComplete
            && (IsXagmanSupplyTonyDraining(tony) || partialState.PendingGiveItems.Count == 0)
            && !HasXagmanMatchingSupply(partialState.RequestedItems, tony))
        {
            xagmanSupplyOwnerStockAtVisit[owner] = CaptureXagmanSupplyStock(tony.SupplyInventory);
            xagmanSupplyVisitedOwners.Add(owner);
            return true;
        }
        return false;
    }

    private static bool HasXagmanMatchingSupply(IEnumerable<XagmanTradeRequestEntry> requests, XagmanPeerPresence tony)
        => requests.Any(request => request.SelectorKind != XagmanItemSelectorKind.ExactItem
            // Value selectors retain their live scan/selection checks when the owner is loaded.
            || tony.SupplyInventory.Any(stock => stock.ItemId == request.ItemId && stock.IsHq == request.IsHq && stock.Quantity > 0));

    private static Dictionary<(uint ItemId, bool IsHq), int> CaptureXagmanSupplyStock(IEnumerable<XagmanTradeRequestEntry> stock)
        => stock.Where(item => item.ItemId > 0 && item.Quantity > 0)
            .GroupBy(item => (item.ItemId, item.IsHq))
            .ToDictionary(group => group.Key, group => group.Max(item => item.Quantity));

    private static bool HasXagmanUsefulSupplyIncrease(
        IEnumerable<XagmanTradeRequestEntry> requests,
        IReadOnlyDictionary<(uint ItemId, bool IsHq), int> stock,
        IReadOnlyDictionary<(uint ItemId, bool IsHq), int>? previousStock)
    {
        foreach (var request in requests)
        {
            if (request.SelectorKind != XagmanItemSelectorKind.ExactItem)
            {
                // A value selector can reject an unchanged bag mix. Only newly received stock
                // permits another same-Tony live planning attempt after that rejection.
                if (previousStock != null && stock.Any(item => item.Value > previousStock.GetValueOrDefault(item.Key)))
                    return true;
                continue;
            }
            var key = (request.ItemId, request.IsHq);
            if (stock.GetValueOrDefault(key) > (previousStock?.GetValueOrDefault(key) ?? 0))
                return true;
        }
        return false;
    }

    private static global::XASlave.Localization.UiMessage GetXagmanPartialOwnerLabelMessage(XagmanPartialOwnerState state)
        => state.RequestedItems.Count > 0 ? global::XASlave.Localization.UiMessage.Key("SE1603E9C577554C9") : global::XASlave.Localization.UiMessage.Key("SF0A0EA0A50BB5384");

    private global::XASlave.Localization.UiMessage GetXagmanPartialOwnerDetailMessage(XagmanPartialOwnerState state)
        => global::XASlave.Localization.UiMessage.Concat(
            global::XASlave.Localization.UiMessage.Join(", ", state.RequestedItems.Select(request => global::XASlave.Localization.UiMessage.Format("SDB0CDEA78A42ECF2", GetXagmanTradeRequestLabelMessage(request), GetXagmanTradeRequestAmountLabelMessage(request)))
                .Concat(state.PendingGiveItems.Select(item => global::XASlave.Localization.UiMessage.Format("S18881952BDF8A8D7", GetXagmanTradeRequestLabelMessage(item), GetXagmanTradeRequestAmountLabelMessage(item))))),
            string.IsNullOrWhiteSpace(state.Reason) ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Format("S9E2A858BBBF7E6E4", state.ReasonMessage));

    private static string GetXagmanPartialOwnerLabel(XagmanPartialOwnerState state)
        => state.RequestedItems.Count > 0 ? "Partially filled" : "Collection pending";

    private string GetXagmanPartialOwnerDetail(XagmanPartialOwnerState state)
        => string.Join(", ", state.RequestedItems.Select(request =>
            $"need {GetXagmanTradeRequestLabel(request)}: {GetXagmanTradeRequestAmountLabel(request)}")
            .Concat(state.PendingGiveItems.Select(item => $"give {GetXagmanTradeRequestLabel(item)}: {GetXagmanTradeRequestAmountLabel(item)}")))
            + (string.IsNullOrWhiteSpace(state.Reason) ? string.Empty : $" ({state.Reason})");

    private bool TryAcceptXagmanPartialDeferral(string owner)
    {
        if (!IsXagmanSupplyCycleActive() || plugin.IpcClient.DropboxIsBusy() || AddonHelper.IsAddonVisible("Trade")) return false;
        var tony = GetXagmanSupplyTonyPeer();
        if (tony == null || !IsXagmanPeerFresh(tony) || tony.SupplyCycleRevision != XagmanSupplyCycleRevision
            || string.IsNullOrWhiteSpace(xagmanOwnerSupplyPassId)
            || tony.SupplyPassId != xagmanOwnerSupplyPassId || tony.InstanceId != xagmanOwnerSupplyTonyInstance || !tony.SupplyInventoryComplete
            || tony.Status is XagmanStatus.Error or XagmanStatus.Paused or XagmanStatus.Preflight or XagmanStatus.ReturningHome
                or XagmanStatus.Relogging or XagmanStatus.Traveling or XagmanStatus.Trading
            || !TryGetXagmanLiveLocalMainInventoryFreeSlots(out _) || !IsXagmanCurrentLocalCharacter(owner)) return false;
        var draining = IsXagmanOwnerSupplyDraining();
        if (!IsXagmanCollectionFirstRestockPhase() && !xagmanOwnerCollectionCompletedKeys.Contains(owner) && !draining) return false;
        var requests = BuildXagmanOwnerTradeRequests(plugin.Configuration.XagmanItems, owner, false);
        var pendingGive = BuildXagmanPendingGiveItems(owner);
        if (xagmanStatus == XagmanStatus.Error) return false;
        var explicitDeferral = tony.SupplyDeferredOwner.Equals(owner, StringComparison.OrdinalIgnoreCase)
            && tony.SupplyDeferredOwnerInstanceId == plugin.InstanceId;
        if (!explicitDeferral && !(draining && requests.Count == 0 && pendingGive.Count > 0)) return false;
        if (pendingGive.Count > 0 && !draining) return false;
        if (requests.Count == 0 && pendingGive.Count == 0)
        {
            SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
            return false;
        }
        if (requests.Any(request => IsXagmanGreenValueSelector(request.SelectorKind)
                && (!request.GreenScanComplete || request.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision))) return false;
        if (HasXagmanMatchingSupply(requests, tony) && requests.All(request => !IsXagmanGreenValueSelector(request.SelectorKind))) return false;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S2B8A187D252BE22F", owner))) return false;
        var state = new XagmanPartialOwnerState
        {
            CharacterNameWorld = owner,
            RequestedItems = CloneXagmanTradeRequests(requests),
            PendingGiveItems = CloneXagmanTradeRequests(pendingGive),
            ReasonMessage = (pendingGive.Count > 0 ? global::XASlave.Localization.UiMessage.Key("SC659856556310C98") : global::XASlave.Localization.UiMessage.Key("S6D40B519F2D21636")),
        };
        xagmanPartialOwners[owner] = state;
        xagmanSupplyOwnerStockAtVisit[owner] = CaptureXagmanSupplyStock(tony.SupplyInventory);
        if (!draining && pendingGive.Count == 0) xagmanOwnerCollectionCompletedKeys.Add(owner);
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
        xagmanStatus = XagmanStatus.ReturningHome;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S17A8798C1217C227", owner, GetXagmanPartialOwnerLabelMessage(state), GetXagmanPartialOwnerDetailMessage(state)));
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}. Continuing the remaining owner roster.");
        InvalidateXagmanTradeCapacityForecast();
        PublishXagmanPresence();
        return true;
    }

    private void CompleteXagmanOwnerSupplyVisit(string owner, bool partial)
    {
        var collectionFinished = !IsXagmanOwnerSupplyDraining() && BuildXagmanPendingGiveItems(owner).Count == 0;
        if (xagmanStatus == XagmanStatus.Error) return;
        xagmanSupplyVisitedOwners.Add(owner);
        if (!partial) xagmanPartialOwners.Remove(owner);
        if (collectionFinished) xagmanOwnerCollectionCompletedKeys.Add(owner);
        xagmanOwnerCompletedCharacters = xagmanOwnerCompletedKeys.Count;
        plugin.TaskRunner.CompletedItems = xagmanOwnerCompletedCharacters;
        xagmanOwnerSweepPendingDataCenter = GetXagmanSupplyPendingDataCenters().FirstOrDefault() ?? string.Empty;
    }

    private void FinishXagmanOwnerSupplyPass()
    {
        if (xagmanTravelRouteFatalError || xagmanStatus == XagmanStatus.Error)
        { plugin.TaskRunner.RequestHalt(xagmanStatusText); return; }
        if (TryFinishXagmanLocalOwnerRun()) return;
        xagmanOwnerSupplyWaiting = true;
        var tony = GetXagmanSupplyTonyPeer();
        xagmanOwnerSupplyContinuation = tony != null && tony.SupplyInventoryComplete
            && GetXagmanUsefulUnvisitedOwners(tony).Any();
        // Completion is advertised only after the final logged-in owner reaches the menu.
        xagmanOwnerSupplyPassComplete = false;
        xagmanOwnerSupplyAtMenu = false;
        xagmanOwnerSupplyWaitSinceUtc = DateTime.UtcNow;
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
        xagmanPhaseComplete = false;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (xagmanOwnerSupplyContinuation ? global::XASlave.Localization.UiMessage.Format("S5CED47FEE798A7A8", xagmanPartialOwners.Count) : global::XASlave.Localization.UiMessage.Format("SF39D9578CD8FBE1C", xagmanPartialOwners.Count)));
        PublishXagmanPresence();
    }

    private void UpdateXagmanOwnerSupplyCycle()
    {
        if (!IsXagmanSupplyCycleActive() || !xagmanOwnerSupplyWaiting || plugin.TaskRunner.IsRunning || xagmanStatus == XagmanStatus.Error) return;
        if (UpdateXagmanLocalOwnerCompletion()) return;
        if (TryFinishXagmanLocalOwnerRun()) return;
        if (!xagmanOwnerSupplyAtMenu) { StartXagmanSupplyMenuWait(); return; }
        var tony = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.Role == XagmanRole.Tony && peer.XagmanEnabled
            && peer.InstanceId == xagmanOwnerSupplyTonyInstance && IsXagmanPeerInCurrentRunPhase(peer) && IsXagmanPeerFresh(peer));
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, GetXagmanOwnerSupplyMenuStatus(tony));
        if (tony == null)
        {
            if ((DateTime.UtcNow - xagmanOwnerSupplyWaitSinceUtc).TotalSeconds > 120)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("SC5F54F20B36CE839"));
            return;
        }
        xagmanOwnerSupplyWaitSinceUtc = DateTime.UtcNow;
        if (!tony.SupplyInventoryComplete
            || tony.Status is XagmanStatus.Error or XagmanStatus.Paused or XagmanStatus.Preflight or XagmanStatus.ReturningHome
                or XagmanStatus.Relogging or XagmanStatus.Traveling) return;
        var continuing = tony.SupplyPassId == xagmanOwnerSupplyPassId;
        var remaining = (continuing ? GetXagmanUsefulUnvisitedOwners(tony) : xagmanOwnerRunPlan.Where(IsXagmanSupplyOwnerUnresolved))
            .OrderBy(owner => WorldData.GetSweepOrdinalForWorld(GetWorldFromKey(owner))).ThenBy(owner => owner, StringComparer.OrdinalIgnoreCase).ToList();
        if (continuing && remaining.Count == 0)
        {
            // Another owner can use the newly observed stock while this client logs out. Do not
            // restart an empty continuation or keep the shared pass barrier open indefinitely.
            if (xagmanOwnerSupplyContinuation || !xagmanOwnerSupplyPassComplete)
            {
                xagmanOwnerSupplyContinuation = false;
                xagmanOwnerSupplyPassComplete = true;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, GetXagmanOwnerSupplyMenuStatus(tony));
                PublishXagmanPresence();
            }
            return;
        }
        if (continuing)
        {
            xagmanOwnerSupplyContinuation = true;
            xagmanOwnerSupplyPassComplete = false;
            plugin.TaskRunner.AddLog($"Xagman: Tony {tony.ActiveCharacter} now has useful supply for {remaining.Count} pending owner(s); revisiting after the completed owner traversal.");
        }
        // Preserve immutable run plan, policy capabilities, finite Take goals and collected Give work.
        xagmanOwnerRunList = remaining;
        xagmanOwnerCurrentCharacterIndex = 0;
        xagmanPreferredTonyCharacter = tony.ActiveCharacter;
        SetXagmanActiveMeetDestination(tony.MeetWorld, tony.MeetAetheryte);
        xagmanOwnerStartRequested = true;
        if (!TryBindXagmanOwnerSupplyPass()) return;
        if (continuing)
            xagmanSupplyVisitedOwners.ExceptWith(remaining);
        xagmanOwnerSupplyWaiting = xagmanOwnerSupplyPassComplete = xagmanOwnerSupplyAtMenu = xagmanOwnerSupplyContinuation = false;
        PublishXagmanPresence();
        var steps = BuildXagmanFranchiseSteps(remaining, 0);
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onFinished: OnXagmanFranchiseTaskFinished,
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), preserveRunHistory: true);
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = xagmanOwnerCompletedKeys.Count;
    }

    private void StartXagmanSupplyMenuWait()
    {
        var steps = new List<TaskStep>();
        AddXagmanSupplyPassMenuWaitSteps(steps);
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true, preserveRunHistory: true);
    }

    private global::XASlave.Localization.UiMessage GetXagmanOwnerSupplyMenuStatus(XagmanPeerPresence? tony)
    {
        var partialCount = xagmanPartialOwners.Values.Count(state => state.RequestedItems.Count > 0);
        var collectionPendingCount = xagmanPartialOwners.Values.Count(state => state.RequestedItems.Count == 0 && state.PendingGiveItems.Count > 0);
        var summary = global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S751D4665BB35E24B", partialCount), ((collectionPendingCount > 0 ? global::XASlave.Localization.UiMessage.Format("SF4F055CB5C47EE05", collectionPendingCount) : global::XASlave.Localization.UiMessage.Value(string.Empty)))), global::XASlave.Localization.UiMessage.Key("SCDB4EE2AEA69CC6A"));
        if (tony == null || !IsXagmanPeerFresh(tony))
            return global::XASlave.Localization.UiMessage.Format("SC2F4FCB498506C0F", summary);

        string RouteLabel(string dataCenter)
        {
            var region = WorldData.GetRegionOfDataCenter(dataCenter);
            return string.IsNullOrWhiteSpace(region) ? dataCenter : $"{dataCenter} ({region})";
        }

        var activeDataCenter = tony.ServerMatchingEnabled ? tony.ServerMatchingActiveDataCenter : string.Empty;
        var location = (string.IsNullOrWhiteSpace(activeDataCenter) ? global::XASlave.Localization.UiMessage.Raw("") : global::XASlave.Localization.UiMessage.Format("S3E9FA1436342ADD6", RouteLabel(activeDataCenter)));
        if (!xagmanOwnerRunPlan.Any(IsXagmanSupplyOwnerUnresolved))
            return global::XASlave.Localization.UiMessage.Format("S0000B6D2F8E8FEB1", location, summary);

        var pendingDataCenter = tony.ServerMatchingEnabled ? GetXagmanSupplyPendingDataCenters().FirstOrDefault() : null;
        global::XASlave.Localization.UiMessage waiting;
        if (!string.IsNullOrWhiteSpace(pendingDataCenter)
            && !pendingDataCenter.Equals(activeDataCenter, StringComparison.OrdinalIgnoreCase))
            waiting = global::XASlave.Localization.UiMessage.Format("S0A870FC4A9A7BC73", RouteLabel(pendingDataCenter));
        else if (xagmanOwnerSupplyContinuation)
            waiting = global::XASlave.Localization.UiMessage.Key("SF28EC30889DC4BD1");
        else if (!string.IsNullOrWhiteSpace(pendingDataCenter))
            waiting = global::XASlave.Localization.UiMessage.Format("SA0D6B8C874EF6F8A", RouteLabel(pendingDataCenter));
        else
            waiting = (tony.ServerMatchingEnabled ? global::XASlave.Localization.UiMessage.Key("S643E6A5689078F50") : global::XASlave.Localization.UiMessage.Key("S6D40B519F2D21636"));
        return global::XASlave.Localization.UiMessage.Format("S946D0462D7885D37", location, waiting, summary);
    }

    private string GetXagmanOwnerSupplyCoordinatorInstance() => !string.IsNullOrWhiteSpace(xagmanOwnerSupplyTonyInstance)
        ? xagmanOwnerSupplyTonyInstance : xagmanOwnerSupplyStartTonyInstance;

    private bool TryRememberXagmanOwnerSupplyStart(XagmanPeerMessage directive)
    {
        if (plugin.Configuration.XagmanOutsideNetworkHelper || string.IsNullOrWhiteSpace(directive.SenderInstanceId)) return true;
        var coordinator = GetXagmanOwnerSupplyCoordinatorInstance();
        if (!string.IsNullOrWhiteSpace(coordinator) && coordinator != directive.SenderInstanceId)
        {
            plugin.TaskRunner.AddLog($"Xagman: ignored start from Tony client {directive.SenderInstanceId}; owner run belongs to client {coordinator}.");
            return false;
        }
        // Remember the sender without acknowledging a supply pass before its stock is readable.
        xagmanOwnerSupplyStartTonyInstance = directive.SenderInstanceId;
        return true;
    }

    private XagmanPeerPresence? GetXagmanSupplyTonyPeer()
    {
        var coordinator = GetXagmanOwnerSupplyCoordinatorInstance();
        if (!string.IsNullOrWhiteSpace(coordinator))
            return plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.InstanceId == coordinator
                && peer.Role == XagmanRole.Tony && peer.XagmanEnabled && IsXagmanPeerFresh(peer) && IsXagmanPeerInCurrentRunPhase(peer));
        // Server Matching coordinates the entire roster, including owners on later servers.
        // A local preferred character must not hide the live coordinator during initial binding.
        return GetXagmanServerMatchingTonyPeer() ?? GetXagmanLiveTonyPeer();
    }

    private IEnumerable<string> GetXagmanUsefulUnvisitedOwners(XagmanPeerPresence tony)
    {
        if (IsXagmanOwnerSupplyPassClosing(tony)) return Enumerable.Empty<string>();
        var stock = CaptureXagmanSupplyStock(tony.SupplyInventory);
        return xagmanOwnerRunPlan.Where(IsXagmanSupplyOwnerUnresolved)
            .Where(owner => IsXagmanOwnerInSupplyServer(owner, tony))
            .Where(owner => !IsXagmanSupplyRegionScoped(tony.ServerMatchingEnabled)
                || GetXagmanRegionOfChar(owner).Equals(GetXagmanRegionOfChar(tony.ActiveCharacter), StringComparison.OrdinalIgnoreCase))
            .Where(owner =>
            {
                if (!xagmanPartialOwners.TryGetValue(owner, out var state))
                    return !xagmanSupplyVisitedOwners.Contains(owner);
                if (!IsXagmanSupplyTonyDraining(tony) && state.PendingGiveItems.Count > 0)
                    return !xagmanSupplyVisitedOwners.Contains(owner);
                if (!HasXagmanMatchingSupply(state.RequestedItems, tony)) return false;
                return !xagmanSupplyVisitedOwners.Contains(owner)
                    || HasXagmanUsefulSupplyIncrease(state.RequestedItems, stock, xagmanSupplyOwnerStockAtVisit.GetValueOrDefault(owner));
            });
    }

    private bool IsXagmanPeerInSupplyCoordinatorScope(XagmanPeerPresence peer)
    {
        if (!IsXagmanSupplyCycleActive()) return true;
        if (xagmanActiveRole == XagmanRole.Tony && peer.Role == XagmanRole.FranchiseOwner)
            return peer.SupplyCoordinatorInstanceId == plugin.InstanceId && !IsXagmanOwnerClientCompleted(peer.InstanceId);
        if (xagmanActiveRole == XagmanRole.FranchiseOwner && peer.Role == XagmanRole.Tony)
        {
            var coordinator = GetXagmanOwnerSupplyCoordinatorInstance();
            return string.IsNullOrWhiteSpace(coordinator) || peer.InstanceId == coordinator;
        }
        return true;
    }

    private unsafe bool TryCaptureXagmanSupplyInventory(out List<XagmanTradeRequestEntry> stock)
    {
        stock = new();
        if (!IsXagmanSupplyCycleActive() || xagmanActiveRole != XagmanRole.Tony
            || !IsXagmanCurrentLocalCharacter(xagmanActiveCharacter) || !CharacterSafetyHelper.IsCharacterSafeWaitReady()) return false;
        if (xagmanSupplyInventoryCachedCharacter == xagmanActiveCharacter
            && (DateTime.UtcNow - xagmanSupplyInventoryCachedAtUtc).TotalSeconds < 1)
        {
            stock = CloneXagmanTradeRequests(xagmanSupplyInventoryCache);
            return true;
        }
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null || !TryGetXagmanLiveGil(out var currentGil)) return false;
            var keys = new HashSet<(uint Id, bool Hq)> { (1, false) };
            foreach (var type in XagmanMainInventoryTypes.Concat(XagmanCrystalInventoryTypes).Distinct())
            {
                if (!NativeArrayAccess.TryGetInventoryContainer(manager, type, out var container)) return false;
                for (var i = 0; i < container->Size; i++)
                {
                    if (!NativeArrayAccess.TryGetInventorySlot(container, i, out var slot)) return false;
                    if (slot->ItemId != 0 && !slot->IsSymbolic && slot->SpiritbondOrCollectability == 0)
                        keys.Add((slot->GetBaseItemId(), slot->IsHighQuality()));
                }
            }
            foreach (var key in keys)
            {
                var quantity = IsXagmanGilItem(key.Id)
                    ? Math.Max(0, currentGil - GetXagmanTonyGilMinimum())
                    : GetXagmanCharacterTradableQuantity(xagmanActiveCharacter, key.Id, key.Hq, string.Empty);
                if (quantity > 0) stock.Add(new XagmanTradeRequestEntry { ItemId = key.Id, IsHq = key.Hq, Quantity = quantity, Mode = XagmanItemMode.Take });
            }
            xagmanSupplyInventoryCache = CloneXagmanTradeRequests(stock);
            xagmanSupplyInventoryCachedCharacter = xagmanActiveCharacter;
            xagmanSupplyInventoryCachedAtUtc = DateTime.UtcNow;
            return true;
        }
        catch { stock.Clear(); return false; }
    }

    private bool TryDeferXagmanTonySupplyOwner()
    {
        if (!IsXagmanSupplyCycleActive()) return false;
        if (plugin.IpcClient.DropboxIsBusy() || AddonHelper.IsAddonVisible("Trade")) return true;
        var owner = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.InstanceId == xagmanActiveTradePartnerInstanceId
            && peer.ActiveCharacter.Equals(xagmanActiveTradePartner, StringComparison.OrdinalIgnoreCase)
            && peer.Role == XagmanRole.FranchiseOwner && peer.XagmanEnabled && IsXagmanPeerFresh(peer) && IsXagmanPeerInCurrentRunPhase(peer));
        if (owner == null) return true;
        if (owner.SupplyCycleRevision != XagmanSupplyCycleRevision || owner.SupplyPassId != xagmanSupplyPassId
            || owner.SupplyCoordinatorInstanceId != plugin.InstanceId)
        {
            FailXagmanSupplyCycle((owner.SupplyCycleRevision != XagmanSupplyCycleRevision ? global::XASlave.Localization.UiMessage.Key("S8B47A89758F965B4") : global::XASlave.Localization.UiMessage.Key("S34B2227E117988A5")));
            return true;
        }
        if (!TryCaptureXagmanSupplyInventory(out var deferredStock))
        {
            if (xagmanSupplyInventoryUnreadableSinceUtc == DateTime.MinValue) xagmanSupplyInventoryUnreadableSinceUtc = DateTime.UtcNow;
            if ((DateTime.UtcNow - xagmanSupplyInventoryUnreadableSinceUtc).TotalSeconds > 60)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("SAF2950060709917D"));
            return true;
        }
        xagmanSupplyInventoryUnreadableSinceUtc = DateTime.MinValue;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S9EC689204D616678"))) return true;
        ClearXagmanDropbox();
        xagmanDeferredOwnerCharacter = owner.ActiveCharacter;
        xagmanDeferredOwnerInstanceId = owner.InstanceId;
        xagmanSupplyDeferredStockByOwner[$"{owner.InstanceId}|{owner.ActiveCharacter}"] = CaptureXagmanSupplyStock(deferredStock);
        xagmanDeferredOwnerSinceUtc = DateTime.UtcNow;
        xagmanStatus = XagmanStatus.Called;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S6343ADA727C9A3D0", xagmanActiveCharacter, owner.ActiveCharacter));
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
        return true;
    }

    private bool HoldXagmanSupplyDeferral()
    {
        if (string.IsNullOrWhiteSpace(xagmanDeferredOwnerCharacter)) return false;
        var owner = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.InstanceId == xagmanDeferredOwnerInstanceId && IsXagmanPeerFresh(peer));
        if (owner != null && owner.SupplyPassId == xagmanSupplyPassId && owner.SupplyCoordinatorInstanceId == plugin.InstanceId
            && (!owner.ActiveCharacter.Equals(xagmanDeferredOwnerCharacter, StringComparison.OrdinalIgnoreCase)
                || (owner.QueueRequestedAtUtc == DateTime.MinValue && owner.Status != XagmanStatus.Called && owner.Status != XagmanStatus.Trading)))
        {
            xagmanDeferredOwnerCharacter = xagmanDeferredOwnerInstanceId = string.Empty;
            xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
            xagmanLastTonyActionAtUtc = DateTime.UtcNow;
            return false;
        }
        if ((DateTime.UtcNow - xagmanDeferredOwnerSinceUtc).TotalSeconds > 60)
            FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("SC9A8DE310A74CB77"));
        return true;
    }

    private bool ObserveXagmanSupplyCohort()
    {
        if (!IsXagmanSupplyCycleActive()) return true;
        if (IsXagmanCollectionFirstRestockPhase() || IsXagmanCapacityDrainActive())
            xagmanSupplyOwnerCohort.UnionWith(xagmanExpectedFranchiseOwnerInstanceIds);
        if (!TryCaptureXagmanSupplyInventory(out _))
        {
            if (xagmanSupplyInventoryUnreadableSinceUtc == DateTime.MinValue) xagmanSupplyInventoryUnreadableSinceUtc = DateTime.UtcNow;
            if ((DateTime.UtcNow - xagmanSupplyInventoryUnreadableSinceUtc).TotalSeconds > 60)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S366C845F991919A1"));
            return false;
        }
        xagmanSupplyInventoryUnreadableSinceUtc = DateTime.MinValue;
        var peers = plugin.XagmanPeers.Peers.Where(peer => !IsXagmanOwnerClientCompleted(peer.InstanceId) && peer.Role == XagmanRole.FranchiseOwner
            && peer.XagmanEnabled && (peer.TotalCharacters > 0 || xagmanExpectedFranchiseOwnerInstanceIds.Contains(peer.InstanceId)
                || xagmanSupplyOwnerCohort.Contains(peer.InstanceId))
            && IsXagmanPeerInCurrentRunPhase(peer)
            && (peer.SupplyCoordinatorInstanceId == plugin.InstanceId || xagmanSupplyOwnerCohort.Contains(peer.InstanceId))).ToList();
        foreach (var peer in peers.Where(peer => IsXagmanPeerFresh(peer)))
        {
            if (!string.IsNullOrWhiteSpace(peer.SupplyCoordinatorInstanceId) && peer.SupplyCoordinatorInstanceId != plugin.InstanceId)
            {
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S739B2780D4714877"));
                return false;
            }
            if (peer.SupplyCycleRevision != XagmanSupplyCycleRevision)
            {
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S8B47A89758F965B4"));
                return false;
            }
            xagmanSupplyOwnerCohort.Add(peer.InstanceId);
            foreach (var ownerKey in xagmanSupplyPartialOwnerInstances.Where(entry => entry.Value == peer.InstanceId).Select(entry => entry.Key).ToList())
            {
                xagmanPartialOwners.Remove(ownerKey);
                xagmanSupplyPartialOwnerInstances.Remove(ownerKey);
            }
            foreach (var partialState in peer.PartialOwners)
            {
                xagmanPartialOwners[partialState.CharacterNameWorld] = new XagmanPartialOwnerState
                {
                    CharacterNameWorld = partialState.CharacterNameWorld,
                    RequestedItems = CloneXagmanTradeRequests(partialState.RequestedItems),
                    PendingGiveItems = CloneXagmanTradeRequests(partialState.PendingGiveItems),
                    ReasonMessage = partialState.ReasonMessage,
                };
                xagmanSupplyPartialOwnerInstances[partialState.CharacterNameWorld] = peer.InstanceId;
            }
        }
        if (xagmanSupplyOwnerCohort.Count == 0)
        {
            if (xagmanSupplyInitialCohortSinceUtc == DateTime.MinValue) xagmanSupplyInitialCohortSinceUtc = DateTime.UtcNow;
            if ((DateTime.UtcNow - xagmanSupplyInitialCohortSinceUtc).TotalSeconds > 120)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("SE13E4D2393506A4D"));
            return false;
        }
        xagmanSupplyInitialCohortSinceUtc = DateTime.MinValue;
        var unboundOwners = peers.Where(peer => IsXagmanPeerFresh(peer) && string.IsNullOrWhiteSpace(peer.SupplyCoordinatorInstanceId)).ToList();
        if (unboundOwners.Count > 0)
        {
            if (xagmanSupplyUnboundCohortSinceUtc == DateTime.MinValue) xagmanSupplyUnboundCohortSinceUtc = DateTime.UtcNow;
            if ((DateTime.UtcNow - xagmanSupplyUnboundCohortSinceUtc).TotalSeconds > 120)
            {
                var details = string.Join("; ", unboundOwners.Take(8).Select(peer =>
                    $"client {peer.InstanceId}, character '{peer.ActiveCharacter}', status {peer.Status}, preferred Tony '{peer.PreferredTonyCharacter}'"));
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Format("S8427F04F437A550A", unboundOwners.Count, xagmanActiveCharacter, details));
            }
            return false;
        }
        xagmanSupplyUnboundCohortSinceUtc = DateTime.MinValue;
        // Retain peer-loss uncertainty. A disconnected participant is never an empty owner list.
        if (xagmanSupplyOwnerCohort.Any(id => !IsXagmanOwnerClientCompleted(id) && !peers.Any(peer => peer.InstanceId == id && IsXagmanPeerFresh(peer))))
        {
            if (xagmanSupplyMissingPeerSinceUtc == DateTime.MinValue) xagmanSupplyMissingPeerSinceUtc = DateTime.UtcNow;
            if ((DateTime.UtcNow - xagmanSupplyMissingPeerSinceUtc).TotalSeconds > 60)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S1AC4C19A0A0C8238"));
            return false;
        }
        xagmanSupplyMissingPeerSinceUtc = DateTime.MinValue;
        return true;
    }

    private bool TryAdvanceXagmanSupplyCycle()
    {
        if (!IsXagmanSupplyCycleActive() || xagmanActiveRole != XagmanRole.Tony) return false;
        if (!ObserveXagmanSupplyCohort() || xagmanSupplyOwnerCohort.Count == 0) return true;
        if (plugin.TaskRunner.IsRunning || plugin.IpcClient.DropboxIsBusy() || AddonHelper.IsAddonVisible("Trade")
            || !string.IsNullOrWhiteSpace(xagmanActiveTradePartner)) return true;
        var owners = plugin.XagmanPeers.Peers.Where(peer => xagmanSupplyOwnerCohort.Contains(peer.InstanceId) && !IsXagmanOwnerClientCompleted(peer.InstanceId)
            && IsXagmanPeerFresh(peer) && peer.XagmanEnabled && IsXagmanPeerInCurrentRunPhase(peer)).ToList();
        if (owners.Any(peer => peer.Status == XagmanStatus.Error))
        {
            FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S2D5061C49093093C"));
            return true;
        }
        if (owners.Any(peer => peer.SupplyPassId != xagmanSupplyPassId || !peer.SupplyPassComplete))
        { xagmanSupplyBarrierSinceUtc = xagmanSupplyRecallSinceUtc = DateTime.MinValue; return true; }
        if (!TryCaptureXagmanSupplyInventory(out var availableStock)) return true;
        var stock = CaptureXagmanSupplyStock(availableStock);
        if (xagmanSupplyPassClosing && TryGetXagmanLiveLocalMainInventoryFreeSlots(out var closingSlots)
            && TryGetXagmanLiveGil(out var closingGil)
            && GetXagmanForecastTonyUse(owners, stock, GetXagmanRegionOfChar(xagmanActiveCharacter), closingSlots, closingGil) == true)
        {
            xagmanSupplyPassClosing = false;
            xagmanSupplyBarrierSinceUtc = DateTime.MinValue;
            plugin.TaskRunner.AddLog("Xagman: settled stock has useful owner work again; reopening this supply pass before rotation.");
            PublishXagmanPresence();
            return true;
        }
        var recallPending = owners.Any(peer => peer.PartialOwners.Any(state =>
            (!IsXagmanServerSupplySweep() || GetXagmanDataCenterOfChar(state.CharacterNameWorld).Equals(xagmanSweepDataCenter, StringComparison.OrdinalIgnoreCase))
            && (!IsXagmanSupplyRegionScoped(xagmanServerMatchingActive)
                || GetXagmanRegionOfChar(state.CharacterNameWorld).Equals(GetXagmanRegionOfChar(xagmanActiveCharacter), StringComparison.OrdinalIgnoreCase))
            && HasXagmanUsefulSupplyIncrease(state.RequestedItems, stock,
                xagmanSupplyDeferredStockByOwner.GetValueOrDefault($"{peer.InstanceId}|{state.CharacterNameWorld}"))));
        if (recallPending)
        {
            // An earlier owner may already be at the title screen when a later client gives
            // this Tony the missing item. Let that owner revoke its pass acknowledgement and
            // publish a new frontier before this Tony can be consumed or rotated.
            xagmanSupplyBarrierSinceUtc = DateTime.MinValue;
            if (xagmanSupplyRecallSinceUtc == DateTime.MinValue)
            {
                xagmanSupplyRecallSinceUtc = DateTime.UtcNow;
                plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} received useful stock for partially filled owners; waiting for their revisit acknowledgement before rotating.");
            }
            if ((DateTime.UtcNow - xagmanSupplyRecallSinceUtc).TotalSeconds > 120)
                FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Key("S88B8FC052DA83A40"));
            return true;
        }
        xagmanSupplyRecallSinceUtc = DateTime.MinValue;
        if (xagmanSupplyBarrierSinceUtc == DateTime.MinValue) xagmanSupplyBarrierSinceUtc = DateTime.UtcNow;
        if ((DateTime.UtcNow - xagmanSupplyBarrierSinceUtc).TotalSeconds < 2) return true;
        if (TryFinishXagmanCapacityRecoverySupplyPass(owners)) return true;
        if (TryResumeXagmanLegacyTonyReceiving(owners, availableStock)) return true;
        if (AdvanceXagmanServerSupply(owners, availableStock)) return true;
        RetainXagmanLegacyTonyEligibility(owners, availableStock);
        var pendingDcs = owners.SelectMany(peer => peer.SupplyPendingDataCenters).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sameScopePending = !xagmanServerMatchingActive
            ? owners.Any(peer => peer.PartialOwners.Count > 0 || peer.CompletedCharacters < peer.TotalCharacters)
            : pendingDcs.Any(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), xagmanSweepRegion, StringComparison.OrdinalIgnoreCase));
        var nextTony = xagmanTonyRunList.FirstOrDefault(key => !key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
            && (!xagmanServerMatchingActive || GetXagmanRegionOfChar(key).Equals(xagmanSweepRegion, StringComparison.OrdinalIgnoreCase)));
        if (sameScopePending && nextTony != null)
        {
            if (xagmanServerMatchingActive)
            {
                xagmanSweepDataCenter = pendingDcs.Where(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), xagmanSweepRegion, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(WorldData.GetSweepOrdinal).First();
                SetXagmanActiveMeetDestination(GetXagmanServerMeetWorld(xagmanSweepDataCenter), GetXagmanSharedMeetLocation());
            }
            plugin.TaskRunner.AddLog($"Xagman: every owner client finished Tony {xagmanActiveCharacter}'s useful supply pass; rotating to serve remaining needs.");
            RotateXagmanTony();
            return true;
        }
        if (xagmanServerMatchingActive)
        {
            if (sameScopePending)
            {
                foreach (var partialState in xagmanPartialOwners.Values.Where(state => GetXagmanRegionOfChar(state.CharacterNameWorld).Equals(xagmanSweepRegion, StringComparison.OrdinalIgnoreCase)))
                    partialState.ReasonMessage = global::XASlave.Localization.UiMessage.Key("S47EB5BE17B4AEB75");
            }
            // The ordinary region transition owns reachability and can now consume this region's roster.
            return false;
        }
        FinalizeXagmanOpenEndedSupplyRequests();
        var warnings = owners.Any(peer => GetXagmanFinalUnresolvedOwnerCount(peer) > 0);
        foreach (var partialState in xagmanPartialOwners.Values) partialState.ReasonMessage = global::XASlave.Localization.UiMessage.Key("S47EB5BE17B4AEB75");
        StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: !warnings, completedWithWarnings: warnings, broadcastPeerCompletion: true);
        return true;
    }

    private void AddXagmanSupplyPassMenuWaitSteps(List<TaskStep> steps)
    {
        var menuStepsStart = steps.Count;
        var runner = plugin.TaskRunner;
        var needsLogout = false;
        var failed = false;
        void Fail(global::XASlave.Localization.UiMessage reason) { failed = true; FailXagmanSupplyCycle(reason); }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SA4B7230824048DEE"),
            OnEnter = () => needsLogout = Plugin.ClientState.IsLoggedIn,
            IsComplete = () => !needsLogout || (!plugin.IpcClient.DropboxIsBusy() && !AddonHelper.IsAddonVisible("Trade")
                && !AddonHelper.IsAddonVisible("SelectYesno") && !plugin.IpcClient.LifestreamIsBusy() && CharacterSafetyHelper.IsCharacterSafeWaitReady()),
            TimeoutSec = 60, OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("S864873FACF0F0983")),
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S97FAE495ABF8474D"),
            ShouldSkip = () => failed || !needsLogout, OnEnter = () => xagmanExpectedLogout = true,
            IsComplete = () => true, TimeoutSec = 1,
        });
        var logout = new List<TaskStep>();
        MonthlyReloggerTask.AddLogoutOnCompleteSteps(logout, runner);
        foreach (var logoutStep in logout)
        {
            var step = logoutStep;
            if (step.Name != "Logout Confirm") { steps.Add(MonthlyReloggerTask.WithSkip(step, () => failed || !needsLogout)); continue; }
            steps.Add(new TaskStep
            {
                NameMessage = step.NameMessage, ShouldSkip = () => failed || !needsLogout, OnEnter = step.OnEnter, TimeoutSec = step.TimeoutSec,
                OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("SA83CE11732A52BCC")),
                IsComplete = () =>
                {
                    if (AddonHelper.IsAddonReady("SelectYesno") && (!AddonHelper.TryGetSelectYesnoText(out var prompt)
                        || (!prompt.Contains("log out", StringComparison.OrdinalIgnoreCase) && !prompt.Contains("logout", StringComparison.OrdinalIgnoreCase))))
                    { Fail(global::XASlave.Localization.UiMessage.Key("S4C6E1B6894D358D7")); return false; }
                    return step.IsComplete();
                },
            });
        }
        var nextNavigation = DateTime.MinValue;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SB67C664A2C42002F"), ShouldSkip = () => failed,
            IsComplete = () =>
            {
                if (Plugin.ClientState.IsLoggedIn || AddonHelper.IsAddonVisible("SelectYesno") || AddonHelper.IsAddonVisible("SelectOk")) return false;
                var characterSelect = AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu");
                var dcSelect = AddonHelper.IsAddonVisible("TitleDCWorldMap");
                if (!characterSelect && !dcSelect && AddonHelper.IsAddonReady("_TitleMenu") && AddonHelper.IsAddonVisible("_TitleMenu")) return true;
                if (DateTime.UtcNow >= nextNavigation)
                {
                    nextNavigation = DateTime.UtcNow.AddSeconds(2);
                    if (characterSelect) AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
                    else if (dcSelect) KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                }
                return false;
            },
            TimeoutSec = 90, OnTimeout = () => Fail(global::XASlave.Localization.UiMessage.Key("S9759DE4E5F0289CF")),
        });
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S146AF4C5FD0E9D76"),
            OnEnter = () =>
            {
                if (failed || !xagmanRunning || !xagmanOwnerSupplyWaiting || xagmanStatus == XagmanStatus.Error) return;
                xagmanOwnerSupplyAtMenu = true;
                var tony = GetXagmanSupplyTonyPeer();
                // Refresh after logout: later owners may have supplied this Tony, or consumed
                // the stock that originally made a continuation useful.
                xagmanOwnerSupplyContinuation = tony != null && tony.SupplyInventoryComplete
                    && tony.SupplyPassId == xagmanOwnerSupplyPassId && GetXagmanUsefulUnvisitedOwners(tony).Any();
                xagmanOwnerSupplyPassComplete = !xagmanOwnerSupplyContinuation;
                if (IsXagmanCollectionFirstRestockPhase() && xagmanOwnerRunPlan.Count == 0)
                {
                    xagmanPhaseComplete = true;
                    xagmanPhaseResolvedCharacters = xagmanPhaseTotalCharacters;
                }
                xagmanOwnerSweepPendingDataCenter = string.Empty;
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, GetXagmanOwnerSupplyMenuStatus(tony));
                runner.AddLog($"Xagman: {xagmanStatusText}");
                PublishXagmanPresence();
            }, IsComplete = () => true, TimeoutSec = 1,
        });
        for (var i = menuStepsStart; i < steps.Count; i++)
            steps[i] = MonthlyReloggerTask.WithSkip(steps[i], () => !xagmanOwnerSupplyWaiting || !string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken)
                || xagmanStatus == XagmanStatus.Error || xagmanTravelRouteFatalError);
    }
}
