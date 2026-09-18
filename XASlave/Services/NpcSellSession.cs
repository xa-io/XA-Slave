using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

// Independent explicit-ID seller. Native entry-point contract researched from PunishXIV/AutoRetainer
// AutoRetainer/Internal/Memory.cs (AGPL-3.0), checked 2026-09-12:
// https://github.com/PunishXIV/AutoRetainer/blob/master/AutoRetainer/Internal/Memory.cs
// No AutoRetainer runtime, sell plan, task manager, or hooks are used.
internal unsafe sealed class NpcSellSession
{
    public const string TreasureItemIds = "22500,22501,22502,22503,22504,22505,22506,22507";
    private delegate void SellSlotDelegate(uint slot, InventoryType inventory, uint unknown);
    private readonly HashSet<uint> itemIds;
    private readonly Action<string> log;
    private readonly string character;
    private readonly uint territory;
    private readonly DateTime started = DateTime.UtcNow;
    private DateTime nextAction;
    private DateTime lastProgress = DateTime.UtcNow;
    private SellSlotDelegate? sellSlot;
    private static HashSet<uint>? vendorIds;
    private static HashSet<string>? purchaseNames;
    private (InventoryType Bag, int Slot, uint Id, int Quantity, long Gil)? pending;
    private string previousError;
    private bool shopOpened;
    private bool ownsVendorInteraction;
    private bool closing;
    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    public bool Finished { get; private set; }
    public bool Succeeded { get; private set; }
    public string Result { get; private set; } = "NPC selling has not finished.";
    public long SoldUnits { get; private set; }

    public NpcSellSession(HashSet<uint> ids, Action<string> logger)
    {
        if (ids.Count == 0 || ids.Any(id => id < 22500 || id > 22507))
            throw new ArgumentException("XA NPC selling accepts only treasure IDs 22500-22507.", nameof(ids));
        itemIds = new HashSet<uint>(ids);
        log = logger;
        character = Tasks.MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        territory = Plugin.ClientState.TerritoryType;
        previousError = ReadError();
    }

    public static bool TryParseIds(string text, out HashSet<uint> ids, out string error)
    {
        ids = new HashSet<uint>();
        error = "Use /xa npcsell subloot or /xa npcsell <itemId,itemId,...>; only treasure IDs 22500-22507 are permitted. All matching quantities are sold.";
        if (string.Equals(text?.Trim(), "subloot", StringComparison.OrdinalIgnoreCase))
            text = TreasureItemIds;
        var parts = (text ?? string.Empty).Split(',');
        if (parts.Length is < 1 or > 256) return false;
        foreach (var part in parts)
        {
            if (!uint.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 22500 || id > 22507)
                return false;
            var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
            if (row == null || row.Value.PriceLow == 0)
            {
                error = $"Item ID {id} is unknown or has no NPC sell price; nothing was started.";
                return false;
            }
            ids.Add(id);
        }
        error = string.Empty;
        return ids.Count > 0;
    }

    private static string ReadError() => AddonHelper.IsAddonVisible(AddonHelper.TextErrorAddonName)
        ? string.Join(" | ", AddonHelper.GetAddonTextEntries(AddonHelper.TextErrorAddonName)) : string.Empty;

    public void Abort(string reason)
    {
        if (Finished) return;
        Result = $"NPC selling failed after {SoldUnits:N0} units: {reason}";
        Succeeded = false;
        Finished = true;
        CloseOwnedShop();
        log(Result);
    }

    private bool CloseOwnedShop()
    {
        if (!shopOpened && !ownsVendorInteraction) return true;
        foreach (var addon in new[] { "Shop", "SelectString", "SelectIconString" })
        {
            if (!AddonHelper.IsAddonVisible(addon)) continue;
            AddonHelper.FireCallbackAndClose(addon, -1);
            return false;
        }
        return true;
    }

    // Called only by the owning TaskRunner step on the game thread. Cancellation stops all calls.
    public bool Tick()
    {
        if (Finished) return true;
        try
        {
            if (!Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null || Plugin.ClientState.TerritoryType != territory
                || Tasks.MonthlyReloggerTask.GetCurrentCharacterNameWorld() != character)
            { Abort("character or territory changed"); return true; }
            if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BetweenAreas]
                || Plugin.Condition[ConditionFlag.BetweenAreas51])
            { Abort("combat or area transition interrupted selling"); return true; }
            if ((DateTime.UtcNow - started).TotalSeconds >= 900) { Abort("900-second time limit reached"); return true; }
            if (AddonHelper.IsAddonVisible("Trade")) { Abort("a trade window is open"); return true; }
            if (DateTime.UtcNow < nextAction) return false;
            nextAction = DateTime.UtcNow.AddMilliseconds(600);
            var ipc = Plugin.Instance?.IpcClient;
            if (ipc != null && ipc.IsAutoRetainerAvailable()
                && (!ipc.TryGetAutoRetainerBusy(out var arBusy) || arBusy))
            { Abort("AutoRetainer became busy or its state is unknown; refusing concurrent automation"); return true; }
            var error = ReadError();
            if (error.Length > 0 && error != previousError) { Abort($"game reported '{error}'"); return true; }
            previousError = error;
            if ((DateTime.UtcNow - lastProgress).TotalSeconds > 30) { Abort("no sale or shop progress for 30 seconds"); return true; }
            if (closing)
            {
                if (!CloseOwnedShop()) return false;
                if (!CharacterSafetyHelper.IsCharacterSafeWaitReady()) return false;
                Succeeded = true;
                Finished = true;
                Result = $"NPC selling finished: {SoldUnits:N0} units sold; no listed items remain in the four main bags.";
                log(Result);
                return true;
            }
            var manager = InventoryManager.Instance();
            if (manager == null) { Abort("inventory unavailable"); return true; }
            foreach (var bag in Bags)
            {
                var container = manager->GetInventoryContainer(bag);
                if (container == null || !container->IsLoaded) { Abort("main inventory not fully loaded"); return true; }
            }
            if (pending is { } sale)
            {
                var slot = manager->GetInventoryContainer(sale.Bag)->GetInventorySlot(sale.Slot);
                if (slot == null) { Abort("pending inventory slot unavailable"); return true; }
                if (slot->ItemId == sale.Id && slot->Quantity >= sale.Quantity) return false;
                if (manager->GetInventoryItemCount(1) <= sale.Gil) return false;
                SoldUnits += slot->ItemId == sale.Id ? sale.Quantity - slot->Quantity : sale.Quantity;
                pending = null;
                lastProgress = DateTime.UtcNow;
            }
            if (!AddonHelper.IsAddonReady("Shop"))
            {
                if (shopOpened) { Abort("shop closed before selling finished"); return true; }
                OpenNearbyVendor();
                return false;
            }
            if (!shopOpened) { shopOpened = true; lastProgress = DateTime.UtcNow; }
            foreach (var bag in Bags)
            {
                var container = manager->GetInventoryContainer(bag);
                for (var index = 0; index < container->Size; index++)
                {
                    var slot = container->GetInventorySlot(index);
                    if (slot == null || !itemIds.Contains(slot->ItemId) || slot->Quantity == 0) continue;
                    var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(slot->ItemId);
                    if (row == null || row.Value.PriceLow == 0) { Abort($"item {slot->ItemId} cannot be sold"); return true; }
                    var gil = (long)manager->GetInventoryItemCount(1);
                    if (gil < 0) { Abort("gil could not be read"); return true; }
                    // Round HQ upward for a conservative upper bound before selling an entire stack.
                    var price = (long)row.Value.PriceLow;
                    if ((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0) price = (price * 11 + 9) / 10;
                    if (gil >= 990_000_000 || gil + price * slot->Quantity > 999_999_999)
                    { Abort("gil limit reached or the next stack could exceed maximum gil"); return true; }
                    sellSlot ??= Marshal.GetDelegateForFunctionPointer<SellSlotDelegate>(Plugin.SigScanner.ScanText(Sigs.NpcSellSlotSig));
                    pending = (bag, index, slot->ItemId, slot->Quantity, gil);
                    sellSlot((uint)index, bag, 0);
                    return false;
                }
            }
            closing = true;
            lastProgress = DateTime.UtcNow;
            return false;
        }
        catch (Exception ex) { Abort($"{ex.GetType().Name}: {ex.Message}"); return true; }
    }

    private void OpenNearbyVendor()
    {
        if (vendorIds == null)
        {
            var foundVendorIds = new HashSet<uint>();
            foreach (var npc in Plugin.DataManager.GetExcelSheet<ENpcBase>())
                if (npc.ENpcData.Any(data => data.Is<GilShop>()
                    || (data.Is<PreHandler>() && data.TryGetValue(out PreHandler pre) && pre.Target.Is<GilShop>())
                    || (data.Is<TopicSelect>() && data.TryGetValue(out TopicSelect topic) && topic.Shop.Any(s => s.Is<GilShop>()))))
                    foundVendorIds.Add(npc.RowId);
            var foundPurchaseNames = Plugin.DataManager.GetExcelSheet<GilShop>().Select(s => s.Name.ToString()).Where(s => s.Length > 0).ToHashSet();
            foreach (var topic in Plugin.DataManager.GetExcelSheet<TopicSelect>().Where(t => t.Shop.Any(s => s.Is<GilShop>())))
                foundPurchaseNames.Add(topic.Name.ToString());
            purchaseNames = foundPurchaseNames;
            vendorIds = foundVendorIds;
        }
        foreach (var addon in new[] { "SelectIconString", "SelectString" })
        {
            if (!AddonHelper.IsAddonReady(addon)) continue;
            foreach (var name in purchaseNames!)
                if (AddonHelper.GetAddonListTextCallbackIndex(addon, name) >= 0)
                { AddonHelper.SelectAddonListText(addon, name); return; }
            return; // Do not click unrelated dialogue choices.
        }
        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady()) return;
        var player = Plugin.ObjectTable.LocalPlayer!;
        var vendor = Plugin.ObjectTable.Where(o => o.ObjectKind == ObjectKind.EventNpc && vendorIds.Contains(o.BaseId)
            && Vector3.Distance(player.Position, o.Position) < 7f).OrderBy(o => Vector3.Distance(player.Position, o.Position)).FirstOrDefault();
        if (vendor == null) { Abort("no supported NPC vendor within 7 yalms; open a shop or move closer"); return; }
        Plugin.TargetManager.Target = vendor;
        TargetSystem.Instance()->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)vendor.Address, false);
        ownsVendorInteraction = true;
    }
}
