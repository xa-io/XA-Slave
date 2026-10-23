using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class OnhGoal
    {
        public uint Id;
        public bool Hq;
        public int GiveFloor;
        public int ReceiveTarget;
        public bool TakeAll;
        public bool Collect;
        public uint WireId => Id + (Hq ? 1_000_000u : 0);
    }

    private readonly List<OnhGoal> onhGoals = new();
    private readonly List<string> onhWaitingOwners = new();
    private readonly HashSet<string> onhRetiredTonys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> onhFailedOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (uint Id, int Quantity)> onhRequestParts = new();
    private readonly Dictionary<uint, int> onhSupplyAnnounced = new();
    private readonly Queue<int> onhReceivedSignals = new();
    private readonly Queue<string> onhTellOutbox = new();
    private string onhSession = string.Empty;
    private int onhRequestCount = -1;
    private int onhSignalSequence;
    private int onhAcknowledgedSequence;
    private DateTime onhLastTellSent;
    private int onhLastReceivedSequence;
    private int onhArmedSequence;
    private int onhArmedAmount;
    private DateTime onhArmedAt;
    private DateTime onhPhaseSince;
    private DateTime onhLastQueueScan;
    private DateTime onhLastActivity;
    private bool onhTransferFailed;
    private bool onhTransferPartial;
    private bool onhGiveBlocked;
    private bool onhCollectionDone;
    private bool onhRemoteTransferStop;
    private bool onhPartnerSelling;
    private DateTime onhPartnerSellSince;
    private bool onhSellHasOwner;
    private bool onhSaleCompleted;
    private bool onhSaleSucceeded;
    private bool onhSaleReturnFailed;
    private bool onhReturningFailed;
    private bool onhReturningIncomplete;
    private bool onhReturningRotation;
    private bool onhReturnFailed;
    private Dictionary<uint, int>? onhTradeBefore;
    private bool onhTradeOpened;
    private DateTime onhTradeStarted;
    private DateTime onhTradeClosed;
    private string onhTradePartner = string.Empty;
    private int onhOutgoingSignalAmount;
    private bool onhOutgoingSignalConfirmed;
    private int onhSignalGilAdjustment;
    private const int OnhMaxRequestItems = 64;

    private int OnhGilFloor => Math.Max(5000, plugin.Configuration.XagmanTonyGilMinimum);
    private bool OnhTradeOpen => AddonHelper.IsAddonVisible("Trade");
    private int OnhHeld(uint wireId) => GetXagmanLiveLocalItemQuantity(wireId % 1_000_000, wireId >= 1_000_000, false);
    private int OnhOwnerHeld(uint wireId) => wireId == 1 ? Math.Max(0, OnhHeld(1) - onhSignalGilAdjustment) : OnhHeld(wireId);
    private bool OnhRequestsComplete => onhRequestCount >= 0 && onhRequestParts.Count == onhRequestCount;

    private void ResetXagmanOnhProtocol()
    {
        onhRetiredTonys.Clear();
        onhFailedOwners.Clear();
        onhWaitingOwners.Clear();
        ResetXagmanOnhCharacterProtocol();
    }

    private void ResetXagmanOnhCharacterProtocol()
    {
        onhGoals.Clear();
        onhSignalGilAdjustment = 0;
        onhTradeBefore = null;
        onhTradeOpened = false;
        onhGiveBlocked = false;
        onhCollectionDone = false;
        ResetXagmanOnhExchange();
    }

    private void ResetXagmanOnhExchange()
    {
        onhSession = string.Empty;
        onhRequestParts.Clear();
        onhSupplyAnnounced.Clear();
        onhRequestCount = -1;
        onhReceivedSignals.Clear();
        onhTellOutbox.Clear();
        onhAcknowledgedSequence = 0;
        onhSignalSequence = onhLastReceivedSequence = onhArmedSequence = onhArmedAmount = 0;
        onhTransferFailed = onhTransferPartial = false;
        onhRemoteTransferStop = false;
        onhPartnerSelling = false;
        onhSaleCompleted = onhSaleSucceeded = onhSaleReturnFailed = false;
        onhSellHasOwner = false;
        onhOutgoingSignalAmount = 0;
        onhOutgoingSignalConfirmed = false;
        onhLastActivity = onhPhaseSince = DateTime.UtcNow;
    }

    private IPlayerCharacter? FindXagmanOnhPlayer(string key)
    {
        var name = GetCharacterNameFromKey(key);
        return Plugin.ObjectTable.OfType<IPlayerCharacter>().FirstOrDefault(p => p.IsTargetable
            && p.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private bool AdmitXagmanOnhTrade(uint entityId)
    {
        if (!xagmanRunning || !plugin.Configuration.XagmanOutsideNetworkHelper
            || string.IsNullOrWhiteSpace(xagmanOnhEngagedPartner)
            || xagmanOnhPhase is XagmanOnhPhase.ReturningHome or XagmanOnhPhase.StartCharacter or XagmanOnhPhase.AwaitStartup
                or XagmanOnhPhase.TonySelling or XagmanOnhPhase.TonyReturnFromSale)
            return false;
        var actor = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        return actor != null && actor.EntityId == entityId;
    }

    private unsafe Dictionary<uint, int>? SnapshotXagmanOnhInventory()
    {
        var manager = InventoryManager.Instance();
        if (manager == null || Plugin.ObjectTable.LocalPlayer == null) return null;
        var result = new Dictionary<uint, int> { [1] = GetXagmanOwnGil() };
        foreach (var type in XagmanMainInventoryTypes.Concat(XagmanCrystalInventoryTypes))
        {
            if (!NativeArrayAccess.TryGetInventoryContainer(manager, type, out var bag)) return null;
            for (var i = 0; i < bag->Size; i++)
            {
                if (!NativeArrayAccess.TryGetInventorySlot(bag, i, out var item)) return null;
                if (item->ItemId == 0) continue;
                var key = item->GetBaseItemId() + (item->IsHighQuality() ? 1_000_000u : 0);
                result[key] = result.GetValueOrDefault(key) + (int)item->Quantity;
            }
        }
        return result;
    }

    private void ObserveXagmanOnhTradeStart(uint entityId)
    {
        if (!AdmitXagmanOnhTrade(entityId)) return;
        // SendTradeRequest and the incoming-status hook may observe the same trade.
        if (onhTradeBefore != null)
        {
            if (OnhTradeOpen || (!onhTradeOpened && (DateTime.UtcNow - onhTradeStarted).TotalSeconds < 0.5)) return;
            FinishXagmanOnhTradeObservation();
        }
        onhTradeBefore = SnapshotXagmanOnhInventory();
        onhTradePartner = xagmanOnhEngagedPartner;
        onhTradeStarted = DateTime.UtcNow;
        onhTradeClosed = DateTime.MinValue;
        onhTradeOpened = false;
    }

    private void ObserveXagmanOnhTrade()
    {
        if (onhRemoteTransferStop)
        {
            onhRemoteTransferStop = false;
            onhTransferFailed = true;
            CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S68D386B1258AD49E"));
            if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("SDDFB5DEBCC9EB1E1"))) return;
        }
        if (onhTellOutbox.Count > 0 && (DateTime.UtcNow - onhLastTellSent).TotalSeconds >= 1)
        {
            onhLastTellSent = DateTime.UtcNow;
            if (!SendXagmanOnhTell(onhTellOutbox.Dequeue()))
            { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S663273F993F1ED0B")); return; }
        }
        if (onhTradeBefore == null) return;
        if (OnhTradeOpen)
        {
            onhTradeOpened = true;
            onhTradeClosed = DateTime.MinValue;
            onhLastActivity = DateTime.UtcNow;
            return;
        }
        if (onhTradeClosed == DateTime.MinValue) onhTradeClosed = DateTime.UtcNow;
        if ((onhTradeOpened && (DateTime.UtcNow - onhTradeClosed).TotalSeconds >= 0.5)
            || (!onhTradeOpened && (DateTime.UtcNow - onhTradeStarted).TotalSeconds >= 3))
            FinishXagmanOnhTradeObservation();
    }

    private void FinishXagmanOnhTradeObservation()
    {
        var before = onhTradeBefore;
        var after = SnapshotXagmanOnhInventory();
        onhTradeBefore = null;
        if (before == null || after == null
            || !onhTradePartner.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)) return;
        var gilDelta = after[1] - before[1];
        var itemsChanged = before.Keys.Union(after.Keys).Any(k => k != 1 && before.GetValueOrDefault(k) != after.GetValueOrDefault(k));
        if (gilDelta != 0 || itemsChanged) onhLastActivity = DateTime.UtcNow;
        if (itemsChanged) return;
        if (onhOutgoingSignalAmount > 0 && gilDelta == -onhOutgoingSignalAmount)
        {
            onhOutgoingSignalConfirmed = true;
            if (xagmanActiveRole == XagmanRole.FranchiseOwner) onhSignalGilAdjustment += gilDelta;
        }
        if (gilDelta == onhArmedAmount && gilDelta is 1 or 2 && onhArmedSequence > onhLastReceivedSequence
            && (DateTime.UtcNow - onhArmedAt).TotalSeconds <= 90)
        {
            onhReceivedSignals.Enqueue(gilDelta);
            onhLastReceivedSequence = onhArmedSequence;
            onhArmedAmount = 0;
            if (xagmanActiveRole == XagmanRole.FranchiseOwner) onhSignalGilAdjustment += gilDelta;
            plugin.TaskRunner.AddLog($"Xagman ONH: verified {gilDelta}-gil signal from {onhTradePartner}.");
        }
    }

    private bool SendXagmanOnhTell(string text)
    {
        var player = FindXagmanOnhPlayer(xagmanOnhEngagedPartner);
        if (player == null) return false;
        return ChatHelper.TrySend($"/tell {player.Name.TextValue}@{player.HomeWorld.Value.Name} {text}");
    }

    private void ReceiveXagmanOnhTell(IHandleableChatMessage message)
    {
        // TellIncoming is base log kind 13; ignore links/commands in every other channel.
        if (!xagmanRunning || ((int)message.LogKind & 0x7f) != 13) return;
        var sender = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault()?.PlayerName;
        if (string.IsNullOrWhiteSpace(sender)) return;
        var roster = xagmanActiveRole == XagmanRole.Tony ? plugin.Configuration.XagmanOnhFriendFoCharacters : plugin.Configuration.XagmanOnhFriendTonyCharacters;
        if (!roster.Any(k => GetCharacterNameFromKey(k).Equals(sender, StringComparison.OrdinalIgnoreCase))
            || !sender.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)) return;
        var text = message.Message.TextValue.Trim();
        if (text.Length > 400) return;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5 && parts[0] == "/xa" && parts[1] == "onh" && parts[2] == onhSession
            && parts[3] == "selling" && int.TryParse(parts[4], out var sellSeq) && sellSeq > onhLastReceivedSequence
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && xagmanOnhPhase is XagmanOnhPhase.FoGiving or XagmanOnhPhase.FoSettle or XagmanOnhPhase.FoSendDoneGil or XagmanOnhPhase.FoAwaitSale)
        {
            onhPartnerSelling = true;
            onhPartnerSellSince = DateTime.UtcNow;
            onhLastReceivedSequence = sellSeq;
            onhRemoteTransferStop = true;
            xagmanOnhGilSendAborted = true;
            onhTellOutbox.Enqueue($"/xa onh {onhSession} armed {sellSeq}");
            plugin.TaskRunner.AddLog($"Xagman ONH: {sender} is selling; pausing collection without retiring this Tony.");
            return;
        }
        if (parts.Length == 6 && parts[0] == "/xa" && parts[1] == "onh" && parts[3] == "signal"
            && Guid.TryParseExact(parts[2], "N", out _)
            && int.TryParse(parts[4], out var seq) && seq > onhLastReceivedSequence
            && int.TryParse(parts[5], out var amount) && amount is 1 or 2)
        {
            if (xagmanOnhPhase == XagmanOnhPhase.FoAwaitStartGil && amount == 1
                && onhReceivedSignals.Count == 0 && !OnhTradeOpen && onhTradeBefore == null && onhSession != parts[2])
            {
                onhSession = parts[2];
                onhLastReceivedSequence = 0;
            }
            if (onhSession != parts[2]) return;
            var allowedPhase = xagmanActiveRole == XagmanRole.Tony
                ? (amount == 1 ? xagmanOnhPhase == XagmanOnhPhase.TonyReceiving && OnhRequestsComplete
                    : xagmanOnhPhase is XagmanOnhPhase.TonySupplying or XagmanOnhPhase.TonyReceiving)
                : (amount == 1 ? xagmanOnhPhase is XagmanOnhPhase.FoAwaitStartGil or XagmanOnhPhase.FoReceiving or XagmanOnhPhase.FoSendDoneGil or XagmanOnhPhase.FoAwaitSale
                    : xagmanOnhPhase is XagmanOnhPhase.FoGiving or XagmanOnhPhase.FoSettle or XagmanOnhPhase.FoReceiving or XagmanOnhPhase.FoAwaitSale);
            if (!allowedPhase) return;
            onhArmedSequence = seq;
            onhArmedAmount = amount;
            onhArmedAt = DateTime.UtcNow;
            if (amount == 2 && xagmanOnhPhase is XagmanOnhPhase.FoGiving or XagmanOnhPhase.TonySupplying)
                onhRemoteTransferStop = true;
            var ack = $"/xa onh {onhSession} armed {seq}";
            if (onhTellOutbox.Count < 8 && !onhTellOutbox.Contains(ack)) onhTellOutbox.Enqueue(ack);
            return;
        }
        if (parts.Length == 5 && parts[0] == "/xa" && parts[1] == "onh" && parts[2] == onhSession && parts[3] == "armed"
            && int.TryParse(parts[4], out var ackSeq) && ackSeq == onhSignalSequence)
        { onhAcknowledgedSequence = ackSeq; return; }
        if (parts.Length == 6 && parts[0] == "/xa" && parts[1] == "onh" && parts[2] == onhSession && parts[3] == "supply"
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && uint.TryParse(parts[4], out var suppliedId) && int.TryParse(parts[5], out var suppliedQty) && suppliedQty >= 0)
        {
            var goal = onhGoals.FirstOrDefault(g => g.WireId == suppliedId);
            if (goal != null && !onhSupplyAnnounced.ContainsKey(suppliedId))
            {
                onhSupplyAnnounced[suppliedId] = suppliedQty;
                if (goal.TakeAll) goal.ReceiveTarget = (int)Math.Min(int.MaxValue, (long)OnhOwnerHeld(suppliedId) + suppliedQty);
            }
            return;
        }
        if (xagmanActiveRole != XagmanRole.Tony || xagmanOnhPhase != XagmanOnhPhase.TonyReceiving
            || parts.Length != 4 || parts[0] != "/xa" || parts[1] != "db") return;
        var envelope = parts[3].Split(':');
        var item = parts[2].Split(':');
        if (envelope.Length != 4 || envelope[0] != "ONH" || envelope[1] != onhSession
            || !int.TryParse(envelope[2], out var index) || !int.TryParse(envelope[3], out var count)
            || count < 0 || count > OnhMaxRequestItems || index < 0 || index >= Math.Max(1, count)
            || item.Length != 2 || !uint.TryParse(item[0], out var id) || !int.TryParse(item[1], out var qty) || qty < 0) return;
        if (onhRequestCount >= 0 && onhRequestCount != count) return;
        if (count == 0 && (id != 0 || qty != 0)) return;
        if (count > 0 && (id % 1_000_000 == 0 || id >= 2_000_000 || id == 1_000_001)) return;
        onhRequestCount = count;
        if (count > 0 && !onhRequestParts.ContainsKey(index)) onhRequestParts[index] = (id, qty);
        onhLastActivity = DateTime.UtcNow;
    }

    private void RefreshXagmanOnhQueue()
    {
        if ((DateTime.UtcNow - onhLastQueueScan).TotalSeconds < 1) return;
        onhLastQueueScan = DateTime.UtcNow;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null) return;
        onhWaitingOwners.RemoveAll(name => FindXagmanOnhPlayer(name) is not { } p
            || Vector3.Distance(p.Position, local.Position) > XagmanOnhScanRadius
            || xagmanOnhCompletedPartners.Contains(name));
        foreach (var key in plugin.Configuration.XagmanOnhFriendFoCharacters)
        {
            var name = GetCharacterNameFromKey(key);
            var p = FindXagmanOnhPlayer(name);
            if (p == null || Vector3.Distance(p.Position, local.Position) > XagmanOnhScanRadius
                || xagmanOnhCompletedPartners.Contains(name) || onhWaitingOwners.Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase)) continue;
            onhWaitingOwners.Add(name);
            plugin.TaskRunner.AddLog($"Xagman ONH: queued {name} (position {onhWaitingOwners.Count}).");
        }
    }

    private void InitializeXagmanOnhOwnerGoals()
    {
        onhGoals.Clear();
        foreach (var item in ResolveXagmanItemsForOwner(plugin.Configuration.XagmanItems, xagmanActiveCharacter))
        {
            if (item.SelectorKind != XagmanItemSelectorKind.ExactItem) continue;
            var held = GetXagmanLiveLocalItemQuantity(item.ItemId, item.IsHq, false);
            var goal = new OnhGoal { Id = item.ItemId, Hq = item.IsHq, GiveFloor = held, ReceiveTarget = 0,
                Collect = item.Mode is XagmanItemMode.Give or XagmanItemMode.Balance };
            if (item.Mode == XagmanItemMode.Give) goal.GiveFloor = item.Quantity <= 0 ? 0 : Math.Max(0, held - item.Quantity);
            if (item.Mode == XagmanItemMode.Balance) goal.GiveFloor = Math.Min(held, Math.Max(0, item.Quantity));
            if (item.Mode is XagmanItemMode.TopUp or XagmanItemMode.Balance) goal.ReceiveTarget = Math.Max(0, item.Quantity);
            if (item.Mode == XagmanItemMode.Take)
            {
                goal.TakeAll = item.Quantity <= 0;
                goal.ReceiveTarget = (int)Math.Min(int.MaxValue, (long)held + Math.Max(0, item.Quantity));
            }
            if (item.ItemId == 1) goal.GiveFloor = Math.Max(2, goal.GiveFloor);
            onhGoals.Add(goal);
        }
    }

    private List<(uint Id, int Quantity)> XagmanOnhGiveRemaining()
        => onhGoals.Where(g => g.Collect && !onhCollectionDone).Select(g => (Id: g.WireId, Quantity: Math.Max(0, OnhOwnerHeld(g.WireId) - g.GiveFloor)))
            .Where(x => x.Quantity > 0).ToList();

    private List<(uint Id, int Quantity)> XagmanOnhReceiveRemaining()
        => onhGoals.Where(g => g.TakeAll || g.ReceiveTarget > OnhOwnerHeld(g.WireId))
            .Select(g => (g.WireId, g.TakeAll ? 0 : g.ReceiveTarget - OnhOwnerHeld(g.WireId))).ToList();

    private void DriveXagmanOnhFoFindTony()
    {
        var tony = FindNearbyXagmanOnhRosterMember(plugin.Configuration.XagmanOnhFriendTonyCharacters, onhRetiredTonys, out _);
        if (tony.Length == 0)
        {
            xagmanStatus = XagmanStatus.Standby;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("SFBE80885519A4264"));
            MaybeLogOnhWait("no eligible Tony nearby; waiting for rotation or arrival.");
            return;
        }
        if (!tony.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase))
        {
            ResetXagmanOnhExchange();
            xagmanOnhEngagedPartner = xagmanActiveTradePartner = tony;
        }
        if (!IsCurrentTargetWithinStopDistanceAndStopped(tony, XagmanOnhTradeStopDistance)) return;
        BeginXagmanOnhFoAwaitStart(global::XASlave.Localization.UiMessage.Key("SF78476E73B354613"));
    }

    private void BeginXagmanOnhFoAwaitStart(global::XASlave.Localization.UiMessage reason)
    {
        if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("SB899E6BCA20868ED"))) return;
        xagmanStatus = XagmanStatus.Queued;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SED7CBBA203C6B932", reason, xagmanOnhEngagedPartner));
        SetXagmanOnhPhase(XagmanOnhPhase.FoAwaitStartGil);
    }

    private void DriveXagmanOnhFoAwaitStartGil()
    {
        if (onhReceivedSignals.TryDequeue(out var signal) && signal == 1)
        {
            var rows = XagmanOnhGiveRemaining();
            var steps = BuildXagmanOnhTransfer(rows, false);
            if (TryStartXagmanOnhSubTask(steps)) SetXagmanOnhPhase(XagmanOnhPhase.FoGiving);
            return;
        }
        if (!IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner))
        {
            ReleaseXagmanOnhOwnerPartner(false);
            return;
        }
        // Waiting behind other FOs is not a failed invitation and has no short timeout.
        MaybeLogOnhWait($"{xagmanActiveCharacter} queued for {xagmanOnhEngagedPartner}'s 1-gil invitation.");
    }

    private void DriveXagmanOnhFoAfterGive()
    {
        onhGiveBlocked = XagmanOnhGiveRemaining().Count > 0;
        if (!onhGiveBlocked) onhCollectionDone = true;
        if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("S8AE8498B0D5B49A9"))) return;
        xagmanOnhFoSettleSinceUtc = DateTime.UtcNow;
        SetXagmanOnhPhase(XagmanOnhPhase.FoSettle);
    }

    private void DriveXagmanOnhFoSettle()
    {
        if (onhReceivedSignals.TryDequeue(out var signal) && signal == 2) { ReleaseXagmanOnhOwnerPartner(true); return; }
        if (onhGiveBlocked)
        {
            if (!IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner)) { ReleaseXagmanOnhOwnerPartner(true); return; }
            if ((DateTime.UtcNow - xagmanOnhFoSettleSinceUtc).TotalSeconds > 120)
                FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SA748FEC12F29D282"));
            else MaybeLogOnhWait("collection paused; waiting for Tony's 2-gil rotation signal.");
            return;
        }
        if ((DateTime.UtcNow - xagmanOnhFoSettleSinceUtc).TotalSeconds < 3 || OnhTradeOpen) return;
        var rows = XagmanOnhReceiveRemaining();
        if (rows.Count > OnhMaxRequestItems) { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S9917761E4E406EB7")); return; }
        var steps = new List<TaskStep>();
        onhSupplyAnnounced.Clear();
        for (var i = 0; i < Math.Max(1, rows.Count); i++)
        {
            var index = i;
            var row = rows.Count == 0 ? (Id: 0u, Quantity: 0) : rows[i];
            AppendXagmanOnhTell(steps, $"/xa db {row.Id}:{row.Quantity} ONH:{onhSession}:{index}:{rows.Count}");
        }
        steps.AddRange(BuildXagmanOnhSendGilSteps(xagmanOnhEngagedPartner, global::XASlave.Localization.UiMessage.Key("SC640B70A9E161E38"), 1));
        if (TryStartXagmanOnhSubTask(steps)) SetXagmanOnhPhase(XagmanOnhPhase.FoSendDoneGil);
    }

    private void DriveXagmanOnhFoAfterDoneGil()
    {
        if (xagmanOnhGilSendAborted) { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SFB9F8F69E27E772D")); return; }
        if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("S09DAE4D64B1A7CF7"))) return;
        onhPhaseSince = onhLastActivity = DateTime.UtcNow;
        SetXagmanOnhPhase(XagmanOnhPhase.FoReceiving);
    }

    private void ReleaseXagmanOnhOwnerPartner(bool retire)
    {
        if (retire) onhRetiredTonys.Add(xagmanOnhEngagedPartner);
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S98C1BB0E85998E9F"))) return;
        plugin.TaskRunner.AddLog($"Xagman ONH: {(retire ? "retired" : "released")} Tony {xagmanOnhEngagedPartner}; preserving outstanding inventory goals.");
        xagmanOnhEngagedPartner = xagmanActiveTradePartner = string.Empty;
        onhGiveBlocked = false;
        ResetXagmanOnhExchange();
        SetXagmanOnhPhase(XagmanOnhPhase.FoFindTony);
    }

    private void DriveXagmanOnhTonySearch()
    {
        if (GetXagmanLiveLocalMainInventoryFreeSlots() <= XagmanOnhTonyMinFreeSlots)
        { StartXagmanOnhFullInventoryRecovery(false); return; }
        if (onhWaitingOwners.Count == 0)
        {
            MaybeLogOnhWait("Tony waiting for a nearby imported FO.");
            if ((DateTime.UtcNow - xagmanOnhSearchSinceUtc).TotalSeconds >= XagmanOnhTonyIdleAdvanceSeconds)
                AdvanceXagmanOnhCharacter(false);
            return;
        }
        var candidate = onhWaitingOwners[0];
        if (xagmanOnhEngagedPartner != candidate)
        {
            ResetXagmanOnhExchange();
            xagmanOnhEngagedPartner = xagmanActiveTradePartner = candidate;
            onhSession = Guid.NewGuid().ToString("N");
        }
        if (GetXagmanOwnGil() < OnhGilFloor + 3)
        { AdvanceXagmanOnhCharacter(false, rotateForFull: true); return; }
        if (!IsCurrentTargetWithinStopDistanceAndStopped(candidate, XagmanOnhTradeStopDistance))
        {
            if ((DateTime.UtcNow - onhPhaseSince).TotalSeconds > 30) FinishXagmanOnhTonyEngagement(false);
            return;
        }
        if ((DateTime.UtcNow - onhPhaseSince).TotalSeconds < 2) return;
        if (TryStartXagmanOnhSubTask(BuildXagmanOnhSendGilSteps(candidate, global::XASlave.Localization.UiMessage.Key("SA3013C082C75EDC9"), 1)))
            SetXagmanOnhPhase(XagmanOnhPhase.TonySendStartGil);
    }

    private void DriveXagmanOnhTonyAfterStartGil()
    {
        if (xagmanOnhGilSendAborted) { FinishXagmanOnhTonyEngagement(false); return; }
        if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("SAEB459D349FF1B72"))) return;
        onhLastActivity = DateTime.UtcNow;
        SetXagmanOnhPhase(XagmanOnhPhase.TonyReceiving);
        plugin.TaskRunner.AddLog($"Xagman ONH: invitation delivered to {xagmanOnhEngagedPartner}; receiving collection.");
    }

    private void DriveXagmanOnhTonyReceiving()
    {
        if (onhReceivedSignals.TryPeek(out var signal))
        {
            if (signal == 2) { onhReceivedSignals.Dequeue(); FailXagmanOnhRemoteOwner(); return; }
            if (signal == 1 && OnhRequestsComplete)
            {
                onhReceivedSignals.Dequeue();
                var rows = onhRequestParts.OrderBy(x => x.Key).Select(x => x.Value).ToList();
                if (rows.Select(x => x.Id).Distinct().Count() != rows.Count) { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S743BC65723D58AE5")); return; }
                if (TryStartXagmanOnhSubTask(BuildXagmanOnhTransfer(rows, true))) SetXagmanOnhPhase(XagmanOnhPhase.TonySupplying);
                return;
            }
        }
        if (GetXagmanLiveLocalMainInventoryFreeSlots() <= XagmanOnhTonyMinFreeSlots
            || (OnhTradeOpen && GetXagmanTradeFailureKind(out _) == XagmanTradeFailureKind.TradeNotComplete))
        { StartXagmanOnhFullInventoryRecovery(true); return; }
        if (!IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner)
            || (DateTime.UtcNow - onhLastActivity).TotalSeconds > 120)
        {
            plugin.TaskRunner.AddLog("Xagman ONH: active owner disappeared or timed out; exchange incomplete, releasing queue slot.");
            FinishXagmanOnhTonyEngagement(false);
        }
    }

    private void FinishXagmanOnhTonyEngagement(bool completed = true)
    {
        var name = xagmanOnhEngagedPartner;
        if (completed) xagmanOnhCompletedPartners.Add(name);
        onhWaitingOwners.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("SC4697A92FC94FC0D"));
        xagmanOnhEngagedPartner = xagmanActiveTradePartner = string.Empty;
        ResetXagmanOnhExchange();
        xagmanOnhSearchSinceUtc = DateTime.UtcNow;
        SetXagmanOnhPhase(XagmanOnhPhase.TonySearch);
    }

    private void FailXagmanOnhRemoteOwner()
    {
        onhFailedOwners.Add(xagmanOnhEngagedPartner);
        plugin.TaskRunner.AddLog($"Xagman ONH: owner {xagmanOnhEngagedPartner} sent 2 gil: resupply FAILED because owner cannot accept more; releasing owner and serving the next queue entry.");
        FinishXagmanOnhTonyEngagement(true);
    }

    private void StartXagmanOnhTonyRotationSignal()
    {
        CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S51F35A8468A36665"));
        if (TryStartXagmanOnhSubTask(BuildXagmanOnhSendGilSteps(xagmanOnhEngagedPartner, global::XASlave.Localization.UiMessage.Key("S2FEED9732E92C0C1"), 2)))
            SetXagmanOnhPhase(XagmanOnhPhase.TonySendRotate);
    }

    private void StartXagmanOnhFullInventoryRecovery(bool hasOwner)
    {
        if (!plugin.Configuration.XagmanSellWhenInventoryFull)
        {
            if (hasOwner) StartXagmanOnhTonyRotationSignal();
            else AdvanceXagmanOnhCharacter(false, rotateForFull: true);
            return;
        }
        CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S92BDF2B8B06C0DCC"));
        if (!xagmanRunning) return;
        if (plugin.IpcClient.DropboxIsBusy()) { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SEF4021FA692408FE")); return; }
        onhSellHasOwner = hasOwner;
        onhSaleCompleted = onhSaleSucceeded = onhSaleReturnFailed = false;
        if (!hasOwner)
        {
            xagmanOnhEngagedPartner = xagmanActiveTradePartner = string.Empty;
            StartXagmanOnhSeller();
            return;
        }
        var seq = ++onhSignalSequence;
        var steps = new List<TaskStep>();
        AppendXagmanOnhTell(steps, $"/xa onh {onhSession} selling {seq}");
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SFC2033409E9B060D"),
            IsComplete = () => onhAcknowledgedSequence == seq,
            TimeoutSec = 15,
            OnTimeout = () => FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S990A243C38B842D2")),
        });
        if (TryStartXagmanOnhSubTask(steps)) SetXagmanOnhPhase(XagmanOnhPhase.TonyPauseForSale);
    }

    private void StartXagmanOnhSeller()
    {
        SetXagmanOnhPhase(XagmanOnhPhase.TonySelling);
        xagmanStatus = XagmanStatus.Paused;
        // Reuse the normal seller, including direct-XA versus AR selection, gil cap,
        // vendor path, capacity verification and uncertain-cleanup safeguards.
        if (!TryStartXagmanTonySellWhenInventoryFull(xagmanOnhEngagedPartner, (succeeded, uncertain) =>
            {
                if (!xagmanRunning || xagmanOnhPhase != XagmanOnhPhase.TonySelling) return;
                if (uncertain) { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SE6C93B1BF70DBC6D")); return; }
                onhSaleSucceeded = succeeded;
                onhSaleCompleted = true;
                xagmanStatus = XagmanStatus.Traveling;
            }))
        {
            onhSaleSucceeded = false;
            onhSaleCompleted = true;
            xagmanStatus = XagmanStatus.Traveling;
        }
    }

    private void ReturnXagmanOnhFromSale()
    {
        var steps = new List<TaskStep>();
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S4766BBF3442D4076"),
            IsComplete = () =>
            {
                if (AddonHelper.IsAddonVisible("Shop"))
                { AddonHelper.FireCallbackAndClose("Shop", -1); return false; }
                return CharacterSafetyHelper.IsCharacterSafeWaitReady();
            },
            TimeoutSec = 15,
            OnTimeout = () => FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S48DB4408676787D5")),
        });
        AddXagmanTeleportSteps(steps, global::XASlave.Localization.UiMessage.Key("S6604736E1D66E93A"), GetXagmanActiveMeetAetheryte, plugin.TaskRunner, null, true,
            () => { xagmanStatus = XagmanStatus.Traveling; xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("SC4490041E92783F2")); },
            () =>
            {
                if (!IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte())) onhSaleReturnFailed = true;
            },
            () => onhSaleReturnFailed = true, expectCrossDataCenterLogout: true);
        AddXagmanCustomMeetingStep(steps, xagmanActiveCharacter, () => onhSaleReturnFailed);
        if (TryStartXagmanOnhSubTask(steps)) SetXagmanOnhPhase(XagmanOnhPhase.TonyReturnFromSale);
    }

    private bool DriveXagmanOnhExtendedPhase()
    {
        switch (xagmanOnhPhase)
        {
            case XagmanOnhPhase.FoAwaitSale:
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SAD03D2DA370A07B0", xagmanOnhEngagedPartner));
                if (onhReceivedSignals.TryPeek(out var saleSignal))
                {
                    onhPartnerSelling = false;
                    if (saleSignal == 2) { onhReceivedSignals.Dequeue(); ReleaseXagmanOnhOwnerPartner(true); return true; }
                    onhGiveBlocked = false;
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S16B3E80875EB0181", xagmanOnhEngagedPartner));
                    SetXagmanOnhPhase(XagmanOnhPhase.FoAwaitStartGil);
                    DriveXagmanOnhFoAwaitStartGil();
                }
                else if ((DateTime.UtcNow - onhPartnerSellSince).TotalSeconds > 2400)
                    FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S4FDDDF89575C5614"));
                else MaybeLogOnhWait($"waiting for {xagmanOnhEngagedPartner} to sell and return; collection progress retained.");
                return true;
            case XagmanOnhPhase.TonyPauseForSale:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                StartXagmanOnhSeller();
                return true;
            case XagmanOnhPhase.TonySelling:
                if (!onhSaleCompleted)
                {
                    if (plugin.TaskRunner.StatusText is "Cancelled" or "Halted") FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S85CFDE754AC94C85"));
                    return true;
                }
                ReturnXagmanOnhFromSale();
                return true;
            case XagmanOnhPhase.TonyReturnFromSale:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                if (onhSaleReturnFailed) { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SF01C0DB40735EFBD")); return true; }
                if (!onhSaleSucceeded)
                {
                    if (onhSellHasOwner && IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner)) StartXagmanOnhTonyRotationSignal();
                    else AdvanceXagmanOnhCharacter(false, rotateForFull: true);
                    return true;
                }
                if (!onhSellHasOwner || !IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner))
                { FinishXagmanOnhTonyEngagement(false); return true; }
                onhRequestParts.Clear();
                onhRequestCount = -1;
                onhReceivedSignals.Clear();
                if (TryStartXagmanOnhSubTask(BuildXagmanOnhSendGilSteps(xagmanOnhEngagedPartner, global::XASlave.Localization.UiMessage.Key("SA790D1AC3D7AFEC6"), 1)))
                    SetXagmanOnhPhase(XagmanOnhPhase.TonySendStartGil);
                return true;
            case XagmanOnhPhase.FoReceiving:
                if (onhReceivedSignals.TryDequeue(out var signal))
                {
                    if (signal == 2) { ReleaseXagmanOnhOwnerPartner(true); return true; }
                    var satisfied = onhGoals.All(g => (!g.TakeAll || onhSupplyAnnounced.ContainsKey(g.WireId))
                        && OnhOwnerHeld(g.WireId) >= g.ReceiveTarget);
                    if (satisfied) AdvanceXagmanOnhCharacter(false);
                    else FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S91C2BD61954E3FD1"));
                    return true;
                }
                if (XagmanOnhOwnerCapacityFailure())
                {
                    CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("SF87E3F1CB0689F10"));
                    if (TryStartXagmanOnhSubTask(BuildXagmanOnhSendGilSteps(xagmanOnhEngagedPartner, global::XASlave.Localization.UiMessage.Key("S4498B971C279B0E7"), 2)))
                        SetXagmanOnhPhase(XagmanOnhPhase.FoSendFailed);
                }
                else if ((DateTime.UtcNow - onhLastActivity).TotalSeconds > 180)
                    FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SE1E5E0649AE02395"));
                return true;
            case XagmanOnhPhase.FoSendFailed:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                if (xagmanOnhGilSendAborted) FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S694AD62038A6A27A"));
                else AdvanceXagmanOnhCharacter(true);
                return true;
            case XagmanOnhPhase.TonySupplying:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                if (onhReceivedSignals.TryPeek(out var failedSignal) && failedSignal == 2)
                { onhReceivedSignals.Dequeue(); FailXagmanOnhRemoteOwner(); return true; }
                if (onhTransferFailed)
                {
                    if (!IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner))
                    { FinishXagmanOnhTonyEngagement(false); return true; }
                    if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("S074D3795AF9A26FC"))) return true;
                    if ((DateTime.UtcNow - onhLastActivity).TotalSeconds > 30)
                        FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SE63EBAD5E06A7CB0"));
                    else xagmanOnhSubTaskCompleted = true;
                    return true;
                }
                if (onhTransferPartial) { StartXagmanOnhTonyRotationSignal(); return true; }
                if (TryStartXagmanOnhSubTask(BuildXagmanOnhSendGilSteps(xagmanOnhEngagedPartner, global::XASlave.Localization.UiMessage.Key("SA03DD2065F2985B0"), 1)))
                    SetXagmanOnhPhase(XagmanOnhPhase.TonySendFinish);
                return true;
            case XagmanOnhPhase.TonySendFinish:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                if (xagmanOnhGilSendAborted) FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S5EA4394FF3E98275"));
                else FinishXagmanOnhTonyEngagement();
                return true;
            case XagmanOnhPhase.TonySendRotate:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                if (xagmanOnhGilSendAborted) FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S3B3120E7984606E7"));
                else AdvanceXagmanOnhCharacter(false, rotateForFull: true);
                return true;
            case XagmanOnhPhase.ReturningHome:
                if (!TryConsumeXagmanOnhSubTaskCompletion()) return true;
                if (onhReturnFailed) FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SF99490EDDEE7F06D"));
                else FinishXagmanOnhCharacter(onhReturningFailed, onhReturningIncomplete, onhReturningRotation);
                return true;
            default: return false;
        }
    }

    private void AppendXagmanOnhTell(List<TaskStep> steps, string text)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S13E3A5D142ADFD9C"),
            ShouldSkip = () => onhPartnerSelling,
            OnEnter = () => { if (!SendXagmanOnhTell(text)) FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("SFDA9E7A2FF7636E4")); },
            IsComplete = () => true, TimeoutSec = 1,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SD6265CA12DBACA5A"), 1.0f));
    }

    private bool XagmanOnhOwnerCapacityFailure()
    {
        if (!AddonHelper.IsAddonVisible("_TextError")) return false;
        var text = string.Join(" ", AddonHelper.GetAddonTextEntries("_TextError"));
        return text.Contains("inventory full", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot carry", StringComparison.OrdinalIgnoreCase)
            || text.Contains("insufficient inventory", StringComparison.OrdinalIgnoreCase)
            || (GetXagmanTradeFailureKind(out _) != XagmanTradeFailureKind.None && GetXagmanLiveLocalMainInventoryFreeSlots() == 0);
    }

    private List<TaskStep> BuildXagmanOnhTransfer(List<(uint Id, int Quantity)> rows, bool supplying)
    {
        onhTransferFailed = onhTransferPartial = false;
        var steps = new List<TaskStep>();
        var targets = new Dictionary<uint, int>();
        var planned = new List<(uint Id, int Quantity)>();
        var queuedRows = 0;
        foreach (var row in rows)
        {
            var available = GetXagmanLiveLocalItemQuantity(row.Id % 1_000_000, row.Id >= 1_000_000);
            if (row.Id == 1) available = Math.Max(0, available - (supplying ? OnhGilFloor + 2 : 2));
            var amount = row.Quantity == 0 && supplying ? available : Math.Min(available, row.Quantity);
            if (row.Quantity > amount) onhTransferPartial = true;
            if (supplying) AppendXagmanOnhTell(steps, $"/xa onh {onhSession} supply {row.Id} {amount}");
            if (amount <= 0) continue;
            planned.Add((row.Id, amount));
            targets[row.Id] = OnhHeld(row.Id) - amount;
        }
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S1BF168ACED3A423A"), 2f));
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S9D2C2F40FA4BC84E"),
            OnEnter = () =>
            {
                OpenXagmanDropboxWindow();
                OpenXagmanDropboxTradeTab();
                if (!TryClearXagmanDropbox(out _) || !TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S4E84B6471C8CD19D"))
                    || !FocusXagmanCurrentTarget(xagmanOnhEngagedPartner)) onhTransferFailed = true;
            }, IsComplete = () => true, TimeoutSec = 2,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S9DDE364F2BE9A65F"), 1f));
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S896438A1D17D63B3"),
            OnEnter = () =>
            {
                if (onhTransferFailed) return;
                foreach (var row in planned)
                {
                    var amount = row.Quantity;
                    if (row.Id == 1)
                    {
                        amount = Math.Min(amount, Math.Max(0, GetXagmanOwnGil() - (supplying ? OnhGilFloor + 2 : 2)));
                        if (amount < row.Quantity) onhTransferPartial = true;
                    }
                    targets[row.Id] = OnhHeld(row.Id) - amount;
                    if (amount == 0) continue;
                    queuedRows++;
                    if (!plugin.IpcClient.DropboxSetItemQuantity(row.Id % 1_000_000, row.Id >= 1_000_000, amount)
                        || !plugin.IpcClient.DropboxTryGetItemQuantity(row.Id % 1_000_000, row.Id >= 1_000_000, out var queued)
                        || queued != amount) onhTransferFailed = true;
                }
                if (!FocusXagmanCurrentTarget(xagmanOnhEngagedPartner)) onhTransferFailed = true;
                if (onhTransferFailed) ClearXagmanDropbox();
                else if (queuedRows > 0 && !StartXagmanDropboxTrade("ONH exact item transfer")) onhTransferFailed = true;
                onhPhaseSince = DateTime.UtcNow;
            }, IsComplete = () => true, TimeoutSec = 2,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S11DD8C95E768089F"),
            IsComplete = () =>
            {
                if (onhTransferFailed || queuedRows == 0) return true;
                if (!IsXagmanOnhPartnerPresent(xagmanOnhEngagedPartner))
                { onhTransferFailed = true; CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S89E0F08137FA8605")); return true; }
                var failure = GetXagmanTradeFailureKind(out _);
                if ((DateTime.UtcNow - onhPhaseSince).TotalSeconds > 2 && failure != XagmanTradeFailureKind.None)
                { onhTransferFailed = true; CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S3B1C5D0D954822B5")); return true; }
                if (plugin.IpcClient.DropboxIsBusy() || OnhTradeOpen || onhTradeBefore != null) return false;
                if (targets.All(x => OnhHeld(x.Key) <= x.Value)) return true;
                if ((DateTime.UtcNow - onhPhaseSince).TotalSeconds < 10) return false;
                onhTransferFailed = true;
                return true;
            }, TimeoutSec = 600,
            OnTimeout = () => { onhTransferFailed = true; CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S89826927007964E4")); },
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SB7A2305A9354AC9E"),
            OnEnter = () =>
            {
                ClearXagmanDropbox();
                TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("SD1DBFC3ACE79748B"));
                onhLastActivity = DateTime.UtcNow;
            }, IsComplete = () => true, TimeoutSec = 2,
        });
        return steps;
    }

    private List<TaskStep> BuildXagmanOnhSendGilSteps(string targetKey, global::XASlave.Localization.UiMessage contextLabel, int amount = 1)
    {
        xagmanOnhGilSendAborted = false;
        var steps = new List<TaskStep>();
        var seq = ++onhSignalSequence;
        AppendXagmanOnhTell(steps, $"/xa onh {onhSession} signal {seq} {amount}");
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SFFB247E0AB2906DD"),
            IsComplete = () => onhAcknowledgedSequence == seq,
            TimeoutSec = 15, OnTimeout = () => xagmanOnhGilSendAborted = true,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SCA00E6D061DCEBE7", contextLabel),
            ShouldSkip = () => xagmanOnhGilSendAborted,
            IsComplete = () => IsCurrentTargetWithinStopDistanceAndStopped(targetKey, XagmanOnhTradeStopDistance),
            TimeoutSec = 15, OnTimeout = () => xagmanOnhGilSendAborted = true,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S5C7D543804EE4C30", amount),
            ShouldSkip = () => xagmanOnhGilSendAborted,
            OnEnter = () =>
            {
                OpenXagmanDropboxWindow();
                OpenXagmanDropboxTradeTab();
                if (!TryClearXagmanDropbox(out _) || !TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("SEE3984428C9C76EC"))
                    || !FocusXagmanCurrentTarget(targetKey)) xagmanOnhGilSendAborted = true;
            }, IsComplete = () => true, TimeoutSec = 2,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S8CF90E20F5BB2374"), 1f));
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S6ED935E234DF4B92", amount, contextLabel),
            ShouldSkip = () => xagmanOnhGilSendAborted,
            OnEnter = () =>
            {
                var floor = xagmanActiveRole == XagmanRole.Tony ? OnhGilFloor : 0;
                onhOutgoingSignalAmount = amount;
                onhOutgoingSignalConfirmed = false;
                if (!FocusXagmanCurrentTarget(targetKey) || GetXagmanOwnGil() - amount < floor
                    || !plugin.IpcClient.DropboxSetItemQuantity(1, false, amount)
                    || !plugin.IpcClient.DropboxTryGetItemQuantity(1, false, out var queued) || queued != amount
                    || !StartXagmanDropboxTrade($"ONH {amount}-gil {contextLabel}")) xagmanOnhGilSendAborted = true;
            }, IsComplete = () => true, TimeoutSec = 2,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S1DD5E8F196C819B5", amount),
            ShouldSkip = () => xagmanOnhGilSendAborted,
            IsComplete = () => onhOutgoingSignalConfirmed && !OnhTradeOpen && !plugin.IpcClient.DropboxIsBusy(),
            TimeoutSec = 60,
            OnTimeout = () => { xagmanOnhGilSendAborted = true; CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S06C891AAF3DF4024")); },
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SE57203BA47146381"),
            OnEnter = () =>
            {
                onhOutgoingSignalAmount = 0;
                ClearXagmanDropbox();
                TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Key("S77F870DBB2ACA6FE"));
                plugin.TaskRunner.AddLog($"Xagman ONH: {contextLabel}: {amount}-gil delivery {(xagmanOnhGilSendAborted ? "FAILED" : "verified")}.");
            }, IsComplete = () => true, TimeoutSec = 2,
        });
        return steps;
    }

    private void BeginXagmanOnhReturnHome(bool failed, bool incomplete, bool rotate)
    {
        onhReturningFailed = failed;
        onhReturningIncomplete = incomplete;
        onhReturningRotation = rotate;
        onhReturnFailed = false;
        CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Key("S0628F92194C44786"));
        if (plugin.IpcClient.DropboxIsBusy())
        { FailXagmanOnhRun(global::XASlave.Localization.UiMessage.Key("S246F51001770EE35")); return; }
        var steps = new List<TaskStep>();
        AddXagmanTeleportSteps(steps, global::XASlave.Localization.UiMessage.Key("S64EE74CBF4FDAAF0"), () => "fc", plugin.TaskRunner, null, true,
            () => { xagmanStatus = XagmanStatus.ReturningHome; xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA9F0BA7C25CD7E82", xagmanActiveCharacter)); },
            () => RememberXagmanRecentFcReturn(xagmanActiveCharacter), () => onhReturnFailed = true, expectCrossDataCenterLogout: true);
        if (TryStartXagmanOnhSubTask(steps)) SetXagmanOnhPhase(XagmanOnhPhase.ReturningHome);
    }
}
