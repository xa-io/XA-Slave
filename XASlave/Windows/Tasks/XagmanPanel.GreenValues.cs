using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int XagmanGreenValueProtocolRevision = 1;
    private static readonly TimeSpan XagmanGreenValueScanCacheDuration = TimeSpan.FromSeconds(5);

    // This is intentionally separate from XagmanMainInventoryTypes. Exact-item/W34 accounting
    // remains Inventory 1-4 only; green-value policies explicitly include the active Armoury bags.
    private static readonly InventoryType[] XagmanGreenValueInventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    };

    // The retired Waist container remains a valid Dropbox source on clients where it is still
    // available. Scan it opportunistically, but never fail a scan merely because it is absent.
    private static readonly InventoryType[] XagmanGreenValueOptionalInventoryTypes =
    {
        InventoryType.ArmoryWaist,
    };

    // Soul Crystals contribute to held-value totals but are not valid Dropbox trade sources.
    private static readonly HashSet<InventoryType> XagmanGreenValueDropboxInventoryTypes = new()
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryRings,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryWrist,
    };

    private XagmanGreenValueScanResult? xagmanGreenValueScanCache;
    private ulong xagmanGreenValueScanCacheContentId;

    private readonly record struct XagmanGreenValueItemKey(uint ItemId, bool IsHq);

    private sealed class XagmanGreenValueCandidate
    {
        public XagmanGreenValueItemKey Key { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public InventoryType InventoryType { get; init; }
        public short Slot { get; init; }
        public int Quantity { get; init; }
        public long GcSealsScaled2Each { get; init; }
        public long FcCreditsScaled2Each { get; init; }
        public bool DropboxInventoryEligible { get; init; }
        public bool DropboxQueueSafe { get; set; }
    }

    private sealed class XagmanGreenValueScanResult
    {
        public XagmanGreenValueSnapshot Snapshot { get; init; } = new();
        public List<XagmanGreenValueCandidate> Candidates { get; init; } = new();
        public global::XASlave.Localization.UiMessage ErrorMessage { get; init; } = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    }

    private sealed class XagmanGreenQueueEntry
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool IsHq { get; init; }
        public int Quantity { get; set; }
        public long GcSealsScaled2Each { get; init; }
        public long FcCreditsScaled2Each { get; init; }
    }

    private bool TryGetXagmanGreenValueScan(bool force, out XagmanGreenValueScanResult result)
    {
        var now = DateTime.UtcNow;
        if (!force
            && xagmanGreenValueScanCache != null
            && xagmanGreenValueScanCacheContentId != 0
            && xagmanGreenValueScanCacheContentId == Plugin.PlayerState.ContentId
            && now >= xagmanGreenValueScanCache.Snapshot.GeneratedAtUtc
            && now - xagmanGreenValueScanCache.Snapshot.GeneratedAtUtc <= XagmanGreenValueScanCacheDuration)
        {
            result = xagmanGreenValueScanCache;
            return result.Snapshot.Complete;
        }

        result = BuildXagmanGreenValueScan(now);
        xagmanGreenValueScanCache = result;
        xagmanGreenValueScanCacheContentId = Plugin.PlayerState.ContentId;
        return result.Snapshot.Complete;
    }

    private unsafe XagmanGreenValueScanResult BuildXagmanGreenValueScan(DateTime generatedAtUtc)
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
                return CreateIncompleteXagmanGreenValueScan(generatedAtUtc, global::XASlave.Localization.UiMessage.Key("S1F109AE84C0C5C97"));

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return CreateIncompleteXagmanGreenValueScan(generatedAtUtc, global::XASlave.Localization.UiMessage.Key("S7903E5E3D3ED0D17"));

            foreach (var inventoryType in XagmanGreenValueInventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded || container->Items == null)
                {
                    return CreateIncompleteXagmanGreenValueScan(
                        generatedAtUtc,
                        global::XASlave.Localization.UiMessage.Format("SBFFAC0441E83B256", GetXagmanGreenInventoryLabel(inventoryType)));
                }
            }

            var scannedInventoryTypes = new List<InventoryType>(XagmanGreenValueInventoryTypes);
            foreach (var inventoryType in XagmanGreenValueOptionalInventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container != null && container->IsLoaded && container->Items != null)
                    scannedInventoryTypes.Add(inventoryType);
            }

            if (!TryBuildXagmanGreenGearsetKeysMessage(out var gearsetKeys, out var gearsetError))
                return CreateIncompleteXagmanGreenValueScan(generatedAtUtc, gearsetError);

            if (!plugin.IpcClient.IsAutoRetainerAvailable())
            {
                return CreateIncompleteXagmanGreenValueScan(
                    generatedAtUtc,
                    global::XASlave.Localization.UiMessage.Key("SDC4C1F85A8B99FC8"));
            }

            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            var rewardSheet = Plugin.DataManager.GetExcelSheet<GCSupplyDutyReward>();
            if (itemSheet == null || rewardSheet == null)
                return CreateIncompleteXagmanGreenValueScan(generatedAtUtc, global::XASlave.Localization.UiMessage.Key("S12727C000078F31B"));

            var candidates = new List<XagmanGreenValueCandidate>();
            var blockedDropboxKeys = new HashSet<XagmanGreenValueItemKey>();
            var autoRetainerProtection = new Dictionary<uint, bool>();
            var excludedItemCount = 0;

            foreach (var inventoryType in scannedInventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded || container->Items == null)
                {
                    return CreateIncompleteXagmanGreenValueScan(
                        generatedAtUtc,
                        global::XASlave.Localization.UiMessage.Format("S9C99E98F652CFD90", GetXagmanGreenInventoryLabel(inventoryType)));
                }

                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                        continue;

                    if (slot->IsSymbolic)
                    {
                        excludedItemCount = AddXagmanGreenCount(excludedItemCount, slot->Quantity);
                        continue;
                    }

                    var itemId = slot->GetBaseItemId();
                    if (!itemSheet.TryGetRow(itemId, out var itemRow))
                    {
                        return CreateIncompleteXagmanGreenValueScan(
                            generatedAtUtc,
                            global::XASlave.Localization.UiMessage.Format("S2ECAD832365445E6", itemId));
                    }

                    if (itemRow.Rarity != 2)
                        continue;

                    var quantity = Math.Max(0, slot->Quantity);
                    if (!IsXagmanGreenStaticCandidate(itemRow)
                        || !rewardSheet.TryGetRow(itemRow.LevelItem.RowId, out var rewardRow)
                        || rewardRow.SealsExpertDelivery == 0)
                    {
                        excludedItemCount = AddXagmanGreenCount(excludedItemCount, quantity);
                        continue;
                    }

                    if (!autoRetainerProtection.TryGetValue(itemId, out var isAutoRetainerProtected))
                    {
                        if (!plugin.IpcClient.TryAutoRetainerPluginStateIsItemProtected(itemId, out isAutoRetainerProtected))
                        {
                            return CreateIncompleteXagmanGreenValueScan(
                                generatedAtUtc,
                                global::XASlave.Localization.UiMessage.Format("S2397FB1DCADEE284", itemId));
                        }

                        autoRetainerProtection[itemId] = isAutoRetainerProtected;
                    }

                    var isHq = slot->IsHighQuality();
                    var key = new XagmanGreenValueItemKey(itemId, isHq);
                    var isInGearset = gearsetKeys.Contains(key);
                    var hasMateria = HasXagmanGreenMateria(slot);
                    var isBoundOrCollectable = slot->SpiritbondOrCollectability != 0
                        || slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
                    var hasGlamour = slot->GlamourId != 0;
                    var isDropboxInventory = XagmanGreenValueDropboxInventoryTypes.Contains(inventoryType);
                    var isDropboxSelectableCopy = isDropboxInventory && !isBoundOrCollectable && !hasGlamour;

                    // Dropbox queues only (item ID, HQ, quantity), then resolves physical slots itself.
                    // A selectable materia, gearset, or AR-protected duplicate can therefore cause loss
                    // even when another copy is safe. Poison the entire concrete Dropbox key.
                    if (isDropboxSelectableCopy && (isInGearset || hasMateria || isAutoRetainerProtected))
                        blockedDropboxKeys.Add(key);

                    if (isBoundOrCollectable
                        || hasGlamour
                        || hasMateria
                        || isInGearset
                        || isAutoRetainerProtected)
                    {
                        excludedItemCount = AddXagmanGreenCount(excludedItemCount, quantity);
                        continue;
                    }

                    var itemLevel = itemRow.LevelItem.RowId;
                    candidates.Add(new XagmanGreenValueCandidate
                    {
                        Key = key,
                        ItemName = itemRow.Name.ToString(),
                        InventoryType = inventoryType,
                        Slot = slot->Slot,
                        Quantity = quantity,
                        GcSealsScaled2Each = checked((long)rewardRow.SealsExpertDelivery * 2L),
                        // WigglyMuffin's FC calculator uses iLvl * 1.5 for NQ and iLvl * 3
                        // for HQ without per-item rounding. Scaled-by-two integers preserve .5.
                        FcCreditsScaled2Each = checked((long)itemLevel * (isHq ? 6L : 3L)),
                        DropboxInventoryEligible = isDropboxInventory,
                    });
                }
            }

            var snapshot = CreateEmptyXagmanGreenValueSnapshot(generatedAtUtc);
            snapshot.Complete = true;
            snapshot.ExcludedItemCount = excludedItemCount;
            snapshot.BlockedKeyCount = blockedDropboxKeys.Count;

            foreach (var candidate in candidates)
            {
                snapshot.SafeItemCount = AddXagmanGreenCount(snapshot.SafeItemCount, candidate.Quantity);
                snapshot.GcSealsScaled2 = AddXagmanGreenMetric(
                    snapshot.GcSealsScaled2,
                    candidate.GcSealsScaled2Each,
                    candidate.Quantity);
                snapshot.FcCreditsScaled2 = AddXagmanGreenMetric(
                    snapshot.FcCreditsScaled2,
                    candidate.FcCreditsScaled2Each,
                    candidate.Quantity);

                candidate.DropboxQueueSafe = candidate.DropboxInventoryEligible
                    && !blockedDropboxKeys.Contains(candidate.Key);
                if (!candidate.DropboxQueueSafe)
                    continue;

                snapshot.DropboxSafeItemCount = AddXagmanGreenCount(snapshot.DropboxSafeItemCount, candidate.Quantity);
                snapshot.DropboxGcSealsScaled2 = AddXagmanGreenMetric(
                    snapshot.DropboxGcSealsScaled2,
                    candidate.GcSealsScaled2Each,
                    candidate.Quantity);
                snapshot.DropboxFcCreditsScaled2 = AddXagmanGreenMetric(
                    snapshot.DropboxFcCreditsScaled2,
                    candidate.FcCreditsScaled2Each,
                    candidate.Quantity);
            }

            return new XagmanGreenValueScanResult
            {
                Snapshot = snapshot,
                Candidates = candidates,
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Xagman] Green-item value scan failed closed.");
            return CreateIncompleteXagmanGreenValueScan(generatedAtUtc, global::XASlave.Localization.UiMessage.Format("S32E7189080719252", ex.Message));
        }
    }

    private static bool IsXagmanGreenStaticCandidate(Item item)
    {
        return item.Rarity == 2
            && item.PriceLow != 0
            && item.EquipSlotCategory.RowId != 0
            && !item.IsUntradable;
    }

    private static unsafe bool HasXagmanGreenMateria(InventoryItem* slot)
    {
        if (slot == null)
            return false;

        for (var materiaIndex = 0; materiaIndex < slot->Materia.Length; materiaIndex++)
        {
            if (slot->Materia[materiaIndex] != 0)
                return true;
        }

        return false;
    }

    private static unsafe bool TryBuildXagmanGreenGearsetKeys(
        out HashSet<XagmanGreenValueItemKey> keys,
        out string error)
    {
        var result = TryBuildXagmanGreenGearsetKeysMessage(out keys, out var presentation);
        error = presentation.Original;
        return result;
    }

    private static unsafe bool TryBuildXagmanGreenGearsetKeysMessage(
        out HashSet<XagmanGreenValueItemKey> keys,
        out global::XASlave.Localization.UiMessage error)
    {
        keys = new HashSet<XagmanGreenValueItemKey>();
        error = global::XASlave.Localization.UiMessage.Raw(string.Empty);

        var gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
        {
            error = global::XASlave.Localization.UiMessage.Key("SD7DEAF7E32ABC949");
            return false;
        }

        var lastItemIndex = (int)RaptureGearsetModule.GearsetItemIndex.SoulStone;
        for (var gearsetIndex = 0; gearsetIndex < gearsetModule->Entries.Length; gearsetIndex++)
        {
            var gearset = gearsetModule->Entries[gearsetIndex];
            if (!gearset.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            for (var itemIndex = 0; itemIndex <= lastItemIndex; itemIndex++)
            {
                // GetItem returns ref GearsetItem. A value copy is sufficient for the ID read.
                var gearsetItem = gearset.GetItem((RaptureGearsetModule.GearsetItemIndex)itemIndex);
                var encodedItemId = gearsetItem.ItemId;
                if (encodedItemId == 0)
                    continue;

                const uint hqOffset = 1_000_000;
                var isHq = encodedItemId >= hqOffset;
                var itemId = isHq ? encodedItemId - hqOffset : encodedItemId;
                keys.Add(new XagmanGreenValueItemKey(itemId, isHq));
            }
        }

        return true;
    }

    private bool TryGetXagmanGreenItemDisplayValues(
        uint itemId,
        bool isHq,
        out int seals,
        out long fcScaled2)
    {
        seals = 0;
        fcScaled2 = 0;
        if (itemId == 0)
            return false;

        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            var rewardSheet = Plugin.DataManager.GetExcelSheet<GCSupplyDutyReward>();
            if (itemSheet == null
                || rewardSheet == null
                || !itemSheet.TryGetRow(itemId, out var item)
                || !IsXagmanGreenStaticCandidate(item)
                || !rewardSheet.TryGetRow(item.LevelItem.RowId, out var reward)
                || reward.SealsExpertDelivery == 0)
            {
                return false;
            }

            seals = checked((int)reward.SealsExpertDelivery);
            fcScaled2 = checked((long)item.LevelItem.RowId * (isHq ? 6L : 3L));
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Xagman] Could not resolve green-item display values for {itemId}.");
            seals = 0;
            fcScaled2 = 0;
            return false;
        }
    }

    private static string FormatXagmanScaled2(long scaled2)
    {
        var format = (scaled2 & 1L) == 0 ? "N0" : "N1";
        return (scaled2 / 2m).ToString(format, CultureInfo.CurrentCulture);
    }

    private static long GetXagmanGreenMetricScaled2(
        XagmanGreenValueSnapshot snapshot,
        XagmanItemSelectorKind selector)
    {
        if (snapshot == null)
            return 0;

        return selector switch
        {
            XagmanItemSelectorKind.GreenItemGcSeals => Math.Max(0, snapshot.GcSealsScaled2),
            XagmanItemSelectorKind.GreenItemFcCreditsRankProgress => Math.Max(0, snapshot.FcCreditsScaled2),
            _ => 0,
        };
    }

    private bool TryPlanXagmanGreenSupply(
        IReadOnlyList<XagmanTradeRequestEntry> requests,
        IReadOnlyList<XagmanTradeRequestEntry> exactSupplyRequests,
        out List<XagmanGreenQueueEntry> queue,
        out string error)
    {
        var result = TryPlanXagmanGreenSupplyMessage(requests, exactSupplyRequests, out queue, out var presentation);
        error = presentation.Original;
        return result;
    }

    private bool TryPlanXagmanGreenSupplyMessage(
        IReadOnlyList<XagmanTradeRequestEntry> requests,
        IReadOnlyList<XagmanTradeRequestEntry> exactSupplyRequests,
        out List<XagmanGreenQueueEntry> queue,
        out global::XASlave.Localization.UiMessage error)
    {
        queue = new List<XagmanGreenQueueEntry>();
        error = global::XASlave.Localization.UiMessage.Raw(string.Empty);

        long gcSealsDeficitScaled2 = 0;
        long fcCreditsDeficitScaled2 = 0;
        foreach (var request in requests ?? Array.Empty<XagmanTradeRequestEntry>())
        {
            var deficit = Math.Max(0, request.ValueDeficitScaled2);
            switch (request.SelectorKind)
            {
                case XagmanItemSelectorKind.ExactItem:
                    continue;
                case XagmanItemSelectorKind.GreenItemGcSeals:
                    gcSealsDeficitScaled2 = AddXagmanGreenLong(gcSealsDeficitScaled2, deficit);
                    break;
                case XagmanItemSelectorKind.GreenItemFcCreditsRankProgress:
                    fcCreditsDeficitScaled2 = AddXagmanGreenLong(fcCreditsDeficitScaled2, deficit);
                    break;
                default:
                    error = global::XASlave.Localization.UiMessage.Format("S29EE2B7B6A635B65", request.SelectorKind);
                    return false;
            }
        }

        if (gcSealsDeficitScaled2 <= 0 && fcCreditsDeficitScaled2 <= 0)
            return true;

        if (!TryGetXagmanGreenValueScan(false, out var scan))
        {
            error = (string.IsNullOrWhiteSpace(scan.Snapshot.Error) ? global::XASlave.Localization.UiMessage.Key("S56058FD97B99F734") : scan.ErrorMessage);
            return false;
        }

        var pool = scan.Candidates
            .Where(candidate => candidate.DropboxQueueSafe && candidate.Quantity > 0)
            .Select(candidate => new XagmanGreenPlannerCandidate(candidate))
            .ToList();

        // Exact-item supply and aggregate green targets share the same physical transfer. Reserve
        // every exact queue quantity from the aggregate candidate pool and apply the intrinsic value
        // of those exact green items before selecting any additional aggregate supply.
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var hasExactSupplyProgress = false;
        foreach (var exactRequest in exactSupplyRequests
                     .Where(entry => entry.SelectorKind == XagmanItemSelectorKind.ExactItem))
        {
            var exactQuantity = GetXagmanRequestedExactSupplyQuantity(
                exactRequest,
                localCharacter,
                out _,
                out _);
            if (exactQuantity <= 0)
                continue;
            hasExactSupplyProgress = true;

            if (TryGetXagmanGreenItemDisplayValues(
                    exactRequest.ItemId,
                    exactRequest.IsHq,
                    out var seals,
                    out var fcCreditsScaled2))
            {
                var sealsContributionScaled2 = AddXagmanGreenMetric(
                    0,
                    checked((long)seals * 2L),
                    exactQuantity);
                var fcContributionScaled2 = AddXagmanGreenMetric(
                    0,
                    fcCreditsScaled2,
                    exactQuantity);
                gcSealsDeficitScaled2 = Math.Max(
                    0,
                    gcSealsDeficitScaled2 - Math.Min(
                        gcSealsDeficitScaled2,
                        sealsContributionScaled2));
                fcCreditsDeficitScaled2 = Math.Max(
                    0,
                    fcCreditsDeficitScaled2 - Math.Min(
                        fcCreditsDeficitScaled2,
                        fcContributionScaled2));
            }

            var remainingReservation = exactQuantity;
            foreach (var candidate in pool.Where(candidate =>
                         candidate.Candidate.Key.ItemId == exactRequest.ItemId
                         && candidate.Candidate.Key.IsHq == exactRequest.IsHq))
            {
                if (remainingReservation <= 0)
                    break;
                var reserved = Math.Min(remainingReservation, candidate.RemainingQuantity);
                candidate.RemainingQuantity -= reserved;
                remainingReservation -= reserved;
            }
        }

        if (gcSealsDeficitScaled2 <= 0 && fcCreditsDeficitScaled2 <= 0)
            return true;

        var selected = new List<XagmanGreenValueCandidate>();

        // Select one physical item at a time. This prevents a grouped queue quantity from
        // overshooting a completed target by more than the final indivisible item.
        while (gcSealsDeficitScaled2 > 0 || fcCreditsDeficitScaled2 > 0)
        {
            XagmanGreenPlannerCandidate? best = null;
            long bestUsefulContribution = -1;
            long bestOvershoot = long.MaxValue;

            foreach (var candidate in pool)
            {
                if (candidate.RemainingQuantity <= 0)
                    continue;

                var usefulContribution = AddXagmanGreenLong(
                    Math.Min(gcSealsDeficitScaled2, candidate.Candidate.GcSealsScaled2Each),
                    Math.Min(fcCreditsDeficitScaled2, candidate.Candidate.FcCreditsScaled2Each));
                if (usefulContribution <= 0)
                    continue;

                var overshoot = AddXagmanGreenLong(
                    gcSealsDeficitScaled2 > 0
                        ? Math.Max(0, candidate.Candidate.GcSealsScaled2Each - gcSealsDeficitScaled2)
                        : 0,
                    fcCreditsDeficitScaled2 > 0
                        ? Math.Max(0, candidate.Candidate.FcCreditsScaled2Each - fcCreditsDeficitScaled2)
                        : 0);

                if (best == null
                    || usefulContribution > bestUsefulContribution
                    || (usefulContribution == bestUsefulContribution && overshoot < bestOvershoot)
                    || (usefulContribution == bestUsefulContribution
                        && overshoot == bestOvershoot
                        && CompareXagmanGreenPlannerCandidates(candidate, best) < 0))
                {
                    best = candidate;
                    bestUsefulContribution = usefulContribution;
                    bestOvershoot = overshoot;
                }
            }

            if (best == null)
            {
                if (selected.Count == 0 && !hasExactSupplyProgress)
                {
                    error = global::XASlave.Localization.UiMessage.Key("S816422E1A57C8703");
                    return false;
                }

                // A Tony may contribute a partial safe pool, then let the established Tony
                // capacity rotation continue against the owner's freshly rescanned deficit.
                break;
            }

            best.RemainingQuantity--;
            selected.Add(best.Candidate);
            gcSealsDeficitScaled2 = Math.Max(0, gcSealsDeficitScaled2 - best.Candidate.GcSealsScaled2Each);
            fcCreditsDeficitScaled2 = Math.Max(0, fcCreditsDeficitScaled2 - best.Candidate.FcCreditsScaled2Each);
        }

        var groupedQueue = new Dictionary<XagmanGreenValueItemKey, XagmanGreenQueueEntry>();
        foreach (var candidate in selected)
        {
            if (groupedQueue.TryGetValue(candidate.Key, out var existing))
            {
                existing.Quantity++;
                continue;
            }

            var entry = new XagmanGreenQueueEntry
            {
                ItemId = candidate.Key.ItemId,
                ItemName = candidate.ItemName,
                IsHq = candidate.Key.IsHq,
                Quantity = 1,
                GcSealsScaled2Each = candidate.GcSealsScaled2Each,
                FcCreditsScaled2Each = candidate.FcCreditsScaled2Each,
            };
            groupedQueue.Add(candidate.Key, entry);
            queue.Add(entry);
        }

        return true;
    }

    private sealed class XagmanGreenPlannerCandidate
    {
        public XagmanGreenPlannerCandidate(XagmanGreenValueCandidate candidate)
        {
            Candidate = candidate;
            RemainingQuantity = candidate.Quantity;
        }

        public XagmanGreenValueCandidate Candidate { get; }
        public int RemainingQuantity { get; set; }
    }

    private static int CompareXagmanGreenPlannerCandidates(
        XagmanGreenPlannerCandidate left,
        XagmanGreenPlannerCandidate right)
    {
        var itemIdComparison = left.Candidate.Key.ItemId.CompareTo(right.Candidate.Key.ItemId);
        if (itemIdComparison != 0)
            return itemIdComparison;

        var hqComparison = left.Candidate.Key.IsHq.CompareTo(right.Candidate.Key.IsHq);
        if (hqComparison != 0)
            return hqComparison;

        var inventoryComparison = left.Candidate.InventoryType.CompareTo(right.Candidate.InventoryType);
        return inventoryComparison != 0
            ? inventoryComparison
            : left.Candidate.Slot.CompareTo(right.Candidate.Slot);
    }

    private static XagmanGreenValueScanResult CreateIncompleteXagmanGreenValueScan(
        DateTime generatedAtUtc,
        global::XASlave.Localization.UiMessage error)
    {
        var snapshot = CreateEmptyXagmanGreenValueSnapshot(generatedAtUtc);
        snapshot.Complete = false;
        snapshot.Error = error.Original;
        return new XagmanGreenValueScanResult
        {
            Snapshot = snapshot,
            ErrorMessage = error,
            Candidates = new List<XagmanGreenValueCandidate>(),
        };
    }

    private static XagmanGreenValueSnapshot CreateEmptyXagmanGreenValueSnapshot(DateTime generatedAtUtc)
    {
        return new XagmanGreenValueSnapshot
        {
            GeneratedAtUtc = generatedAtUtc,
            Revision = XagmanGreenValueProtocolRevision,
            Complete = false,
            Error = string.Empty,
            GcSealsScaled2 = 0,
            FcCreditsScaled2 = 0,
            GcSealsTargetScaled2 = 0,
            FcCreditsTargetScaled2 = 0,
            DropboxGcSealsScaled2 = 0,
            DropboxFcCreditsScaled2 = 0,
            SafeItemCount = 0,
            DropboxSafeItemCount = 0,
            ExcludedItemCount = 0,
            BlockedKeyCount = 0,
        };
    }

    private static global::XASlave.Localization.UiMessage GetXagmanGreenInventoryLabel(InventoryType inventoryType)
    {
        return inventoryType switch
        {
            InventoryType.Inventory1 => global::XASlave.Localization.UiMessage.Key("SDF27398427D31589"),
            InventoryType.Inventory2 => global::XASlave.Localization.UiMessage.Key("S3B3743EC6C83E553"),
            InventoryType.Inventory3 => global::XASlave.Localization.UiMessage.Key("S17ECC6C15565FAF4"),
            InventoryType.Inventory4 => global::XASlave.Localization.UiMessage.Key("S97E55078F6B87EFC"),
            InventoryType.ArmoryMainHand => global::XASlave.Localization.UiMessage.Key("S0CFE43338B3849EE"),
            InventoryType.ArmoryOffHand => global::XASlave.Localization.UiMessage.Key("SCF5E05CA0B2B391B"),
            InventoryType.ArmoryHead => global::XASlave.Localization.UiMessage.Key("S4B3D5FF344D6620A"),
            InventoryType.ArmoryBody => global::XASlave.Localization.UiMessage.Key("SA4A133CEDAE5399A"),
            InventoryType.ArmoryHands => global::XASlave.Localization.UiMessage.Key("SE6C6865F9E2EB132"),
            InventoryType.ArmoryLegs => global::XASlave.Localization.UiMessage.Key("S10BA25BA1B713E5D"),
            InventoryType.ArmoryWaist => global::XASlave.Localization.UiMessage.Key("SE33322CFD58B2ABF"),
            InventoryType.ArmoryFeets => global::XASlave.Localization.UiMessage.Key("SA62CA3BB1CBC7176"),
            InventoryType.ArmoryEar => global::XASlave.Localization.UiMessage.Key("SC8745458D3CBCFB3"),
            InventoryType.ArmoryNeck => global::XASlave.Localization.UiMessage.Key("SDC41FEA93EADB0E1"),
            InventoryType.ArmoryWrist => global::XASlave.Localization.UiMessage.Key("S178A89AC72294FD4"),
            InventoryType.ArmoryRings => global::XASlave.Localization.UiMessage.Key("S3B2136478C6289CD"),
            InventoryType.ArmorySoulCrystal => global::XASlave.Localization.UiMessage.Key("S4BCAF8FAB6523C94"),
            _ => global::XASlave.Localization.UiMessage.Raw(inventoryType.ToString()),
        };
    }

    private static int AddXagmanGreenCount(int current, int add)
    {
        if (add <= 0)
            return current;
        return current >= int.MaxValue - add ? int.MaxValue : current + add;
    }

    private static long AddXagmanGreenMetric(long current, long perUnit, int quantity)
    {
        if (perUnit <= 0 || quantity <= 0)
            return current;
        if (perUnit > long.MaxValue / quantity)
            return long.MaxValue;
        return AddXagmanGreenLong(current, perUnit * quantity);
    }

    private static long AddXagmanGreenLong(long left, long right)
    {
        if (right <= 0)
            return left;
        return left >= long.MaxValue - right ? long.MaxValue : left + right;
    }
}
