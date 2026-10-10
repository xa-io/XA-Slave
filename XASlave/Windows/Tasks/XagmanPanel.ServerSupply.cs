using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool xagmanSupplyPassClosing;
    private readonly Dictionary<string, XagmanLegacyTonySupplySnapshot> xagmanServerSupplyAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanServerSupplyFinished = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XagmanLegacyTonySupplySnapshot> xagmanServerSupplyStock = new(StringComparer.OrdinalIgnoreCase);

    private bool IsXagmanServerSupplySweep() => IsXagmanSupplyCycleActive() && xagmanServerMatchingActive
        && !IsXagmanCollectionFirstRunActive() && !IsXagmanCapacityDrainActive();

    private bool IsXagmanOwnerSupplyPassClosing(XagmanPeerPresence? tony) => tony != null
        && IsXagmanSupplyCycleActive() && !IsXagmanCollectionFirstRunActive()
        && tony.SupplyCycleRevision == XagmanSupplyCycleRevision
        && tony.InstanceId == xagmanOwnerSupplyTonyInstance
        && tony.SupplyPassId == xagmanOwnerSupplyPassId && tony.SupplyPassClosing;

    private bool IsXagmanOwnerInSupplyServer(string owner, XagmanPeerPresence tony) =>
        !tony.ServerMatchingEnabled || IsXagmanCollectionFirstRunActive()
        || string.Equals(GetXagmanDataCenterOfChar(owner), tony.ServerMatchingActiveDataCenter, StringComparison.OrdinalIgnoreCase);

    // Forecasts are scheduling hints only. Missing/stale forecasts cannot prove depletion.
    // Include incoming work so a Balance/Give donor can still replenish this supplier.
    private void UpdateXagmanDepletedSupplyPass()
    {
        if (!IsXagmanSupplyCycleActive() || IsXagmanCollectionFirstRunActive() || xagmanSupplyPassClosing
            || plugin.TaskRunner.IsRunning || plugin.IpcClient.DropboxIsBusy() || AddonHelper.IsAddonVisible("Trade")
            || !string.IsNullOrWhiteSpace(xagmanActiveTradePartner) || !string.IsNullOrWhiteSpace(xagmanDeferredOwnerCharacter)) return;
        var owners = plugin.XagmanPeers.Peers.Where(peer => xagmanSupplyOwnerCohort.Contains(peer.InstanceId)
            && !IsXagmanOwnerClientCompleted(peer.InstanceId)).ToList();
        if (owners.Count == 0 || owners.Any(peer => peer.SupplyPassId != xagmanSupplyPassId)
            || !TryCaptureXagmanSupplyInventory(out var inventory)
            || !TryGetXagmanLiveLocalMainInventoryFreeSlots(out var slots) || !TryGetXagmanLiveGil(out var gil)) return;
        if (GetXagmanForecastTonyUse(owners, CaptureXagmanSupplyStock(inventory), GetXagmanRegionOfChar(xagmanActiveCharacter), slots, gil) != false) return;
        xagmanSupplyPassClosing = true;
        plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} has no useful remaining supply or receiving work in this region; closing this pass at safe owner boundaries before rotating. Unvisited and partial owners remain pending.");
        PublishXagmanPresence();
    }

    // Called only after every fresh cohort member acknowledged this exact pass at the menu,
    // and after useful same-Tony partial recalls were checked. Keep stocked suppliers eligible
    // on later servers; completing one server must not consume the entire regional roster.
    private bool AdvanceXagmanServerSupply(IReadOnlyList<XagmanPeerPresence> owners, IReadOnlyList<XagmanTradeRequestEntry> inventory)
    {
        if (!IsXagmanServerSupplySweep()) return false;
        if (!TryGetXagmanLiveLocalMainInventoryFreeSlots(out var slots) || !TryGetXagmanLiveGil(out var gil)) return true;
        xagmanServerSupplyStock[xagmanActiveCharacter] = new XagmanLegacyTonySupplySnapshot
        {
            Stock = CaptureXagmanSupplyStock(inventory), FreeSlots = slots, Gil = gil,
        };
        xagmanServerSupplyAttempts[$"{xagmanSweepDataCenter}|{xagmanActiveCharacter}"] = new XagmanLegacyTonySupplySnapshot
        {
            Stock = CaptureXagmanSupplyStock(inventory),
            ObservedDemand = CaptureXagmanLegacyDemand(owners),
        };
        var pending = owners.SelectMany(peer => peer.SupplyPendingDataCenters)
            .Where(dc => string.Equals(WorldData.GetRegionOfDataCenter(dc), xagmanSweepRegion, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(WorldData.GetSweepOrdinal).ToList();
        var currentPending = pending.Contains(xagmanSweepDataCenter, StringComparer.OrdinalIgnoreCase);
        string? NextSupplier(string dc) => xagmanTonyRunList.FirstOrDefault(key =>
            !plugin.TaskRunner.FailedCharacters.Contains(key)
            && GetXagmanRegionOfChar(key).Equals(xagmanSweepRegion, StringComparison.OrdinalIgnoreCase)
            && CanRevisitXagmanSupplyServer(key, dc, owners)
            && (!xagmanServerSupplyStock.TryGetValue(key, out var saved)
                || GetXagmanForecastTonyUse(owners, saved.Stock, xagmanSweepRegion, saved.FreeSlots, saved.Gil) != false));

        var next = currentPending ? NextSupplier(xagmanSweepDataCenter) : null;
        if (next != null)
        {
            plugin.TaskRunner.AddLog($"Xagman: rechecking all partial and unvisited owners on {xagmanSweepDataCenter} with next Tony {next} before leaving this server.");
            StartXagmanServerSupplyTony(next);
            return true;
        }
        xagmanServerSupplyFinished.Add(xagmanSweepDataCenter);
        plugin.TaskRunner.AddLog(currentPending
            ? $"Xagman: server {xagmanSweepDataCenter} has unresolved owners after all useful selected suppliers; retaining shortages while checking the remaining servers."
            : $"Xagman: server {xagmanSweepDataCenter} fully covered; all owner acknowledgements verified.");
        foreach (var dc in pending.Where(dc => !xagmanServerSupplyFinished.Contains(dc)
            || NextSupplier(dc) != null))
        {
            // Prefer the current supplier when it still has useful regional stock/capacity.
            var saved = xagmanServerSupplyStock[xagmanActiveCharacter];
            next = CanRevisitXagmanSupplyServer(xagmanActiveCharacter, dc, owners)
                && GetXagmanForecastTonyUse(owners, saved.Stock, xagmanSweepRegion, saved.FreeSlots, saved.Gil) != false
                ? xagmanActiveCharacter : NextSupplier(dc);
            if (next == null) continue;
            xagmanSweepDataCenter = dc;
            SetXagmanActiveMeetDestination(GetXagmanServerMeetWorld(dc), GetXagmanSharedMeetLocation());
            plugin.TaskRunner.AddLog($"Xagman: continuing regional supply coverage on {dc} with Tony {next}; earlier partial results remain unresolved.");
            if (next.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase))
            {
                BeginXagmanTonySupplyPass();
                ResetXagmanTonyMeetRetryState();
                PublishXagmanPresence();
            }
            else StartXagmanServerSupplyTony(next);
            return true;
        }
        // All regional servers have either completed or exhausted useful suppliers. The
        // existing transition retains warning results and owns final cohort cleanup.
        foreach (var partialState in xagmanPartialOwners.Values.Where(state =>
            GetXagmanRegionOfChar(state.CharacterNameWorld).Equals(xagmanSweepRegion, StringComparison.OrdinalIgnoreCase)))
            partialState.Reason = "selected regional suppliers exhausted; remaining items unavailable in this run";
        if (!TryAdvanceXagmanSweepToNextNeededRegion(pending.Count > 0
            ? "exhausted useful regional supplies with unresolved owner needs" : "fully served"))
        {
            FinalizeXagmanOpenEndedSupplyRequests();
            var warnings = owners.Any(peer => GetXagmanFinalUnresolvedOwnerCount(peer) > 0);
            StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: !warnings,
                completedWithWarnings: warnings, broadcastPeerCompletion: true);
        }
        return true;
    }

    private bool CanRevisitXagmanSupplyServer(string tony, string dc, IReadOnlyList<XagmanPeerPresence> owners)
    {
        if (!xagmanServerSupplyAttempts.TryGetValue($"{dc}|{tony}", out var attempt)) return true;
        if (!xagmanServerSupplyStock.TryGetValue(tony, out var stock)) return false;
        // Reopen an earlier server only for a newly useful opportunity, never an unchanged
        // failed attempt. A later Balance donor may replenish a previously exhausted Tony.
        return owners.Any(peer => peer.PartialOwners.Any(owner =>
            GetXagmanDataCenterOfChar(owner.CharacterNameWorld).Equals(dc, StringComparison.OrdinalIgnoreCase)
            && owner.RequestedItems.Any(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                && stock.Stock.GetValueOrDefault((item.ItemId, item.IsHq)) > 0
                && (stock.Stock.GetValueOrDefault((item.ItemId, item.IsHq)) > attempt.Stock.GetValueOrDefault((item.ItemId, item.IsHq))
                    || item.Quantity > attempt.ObservedDemand.GetValueOrDefault(GetXagmanLegacyDemandKey(peer, owner, item, false))))));
    }

    private void StartXagmanServerSupplyTony(string character)
    {
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, $"server supply rotation to {character}")) return;
        xagmanActiveTradePartner = xagmanActiveTradePartnerInstanceId = string.Empty;
        ResetXagmanTonyApproachWait();
        xagmanObservedDropboxBusy = false;
        var entry = plugin.Configuration.XagmanTonyCharacters.FirstOrDefault(item =>
            item.CharacterNameWorld.Equals(character, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry { CharacterNameWorld = character, Mode = xagmanTonyMode };
        StartXagmanTonyStartup(entry, true);
    }
}
