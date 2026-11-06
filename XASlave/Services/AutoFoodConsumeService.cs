using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Localization;

namespace XASlave.Services;

internal sealed record AutoFoodChoice(uint ItemId, string Name, bool CanBeHq, uint FoodRowId);
internal readonly record struct AutoFoodBuff(bool Present, ushort Param, float RemainingSeconds);

internal static class AutoFoodConsumePolicy
{
    internal const int MaxAttempts = 3;
    internal const long ObserveMilliseconds = 15_000;
    internal const long RetryMilliseconds = 5_000;
    internal const long PollMilliseconds = 500;

    internal static bool IsLocationAllowed(bool onlySanctuary, bool onlyDuty, bool inSanctuary, bool inDuty)
        => (!onlySanctuary && !onlyDuty) || (onlySanctuary && inSanctuary) || (onlyDuty && inDuty);

    internal static uint ExpectedParam(uint foodRowId, bool highQuality)
        => foodRowId + (highQuality ? 10_000u : 0u);

    internal static bool IsOtherActiveFood(AutoFoodBuff buff, uint expectedParam)
        => buff.Present && buff.RemainingSeconds > 0 && buff.Param != expectedParam;

    internal static bool NeedsRefresh(AutoFoodBuff buff, uint expectedParam, int threshold)
        => !buff.Present || (buff.RemainingSeconds <= threshold
            && (buff.RemainingSeconds <= 0 || buff.Param == expectedParam));

    internal static bool ConfirmsRefresh(AutoFoodBuff buff, uint expectedParam, float beforeRemaining, int threshold)
        => buff.Present && buff.Param == expectedParam && float.IsFinite(buff.RemainingSeconds)
            && buff.RemainingSeconds > Math.Max(threshold, beforeRemaining + 5f);

    internal static bool SameSettings(AutoFoodConsumeSettings left, AutoFoodConsumeSettings right)
        => left.ItemId == right.ItemId && left.HighQuality == right.HighQuality
            && left.OnlyInSanctuary == right.OnlyInSanctuary && left.OnlyInDuty == right.OnlyInDuty
            && left.RefreshThresholdSeconds == right.RefreshThresholdSeconds;
}

/// <summary>Opt-in food maintenance; all item actions occur on a later framework update with bounded observation.</summary>
public unsafe sealed class AutoFoodConsumeService : IDisposable
{
    private static readonly InventoryType[] MainBags =
        { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Func<bool> isOwnerDisposed;
    private readonly object sync = new();
    private AutoFoodConsumeSettings settings = new();
    private IReadOnlyList<AutoFoodChoice>? foods;
    private UiMessage status = UiMessage.Authored("Auto Food Consume is disabled.");
    private FoodAttempt? pending;
    private bool enabled, disposed, suspended, catalogAttempted;
    private long generation, nextPoll, earliestNextAction;
    private int attempts;
    private ulong observedContentId;
    private uint observedTerritory;

    private sealed record FoodAttempt(long Generation, ulong ContentId, uint Territory, uint ExpectedParam,
        int BeforeCount, float BeforeRemaining, int Threshold, long Deadline, bool Accepted, bool ObservationUncertain);

    public AutoFoodConsumeService(IFramework framework, IClientState clientState, IPlayerState playerState,
        IObjectTable objectTable, ICondition condition, IDataManager dataManager, IPluginLog log, Func<bool> isOwnerDisposed)
    {
        this.framework = framework; this.clientState = clientState; this.playerState = playerState;
        this.objectTable = objectTable; this.condition = condition; this.dataManager = dataManager;
        this.log = log; this.isOwnerDisposed = isOwnerDisposed;
    }

    public string StatusText { get { lock (sync) return status.Original; } }
    public string LocalizedStatusText { get { lock (sync) return status.Display; } }
    internal UiMessage StatusTextMessage { get { lock (sync) return status; } }

    public void ApplyConfiguration(AutoFoodConsumeSettings value)
    {
        var normalized = (value ?? new()).Normalized();
        lock (sync)
        {
            if (disposed || AutoFoodConsumePolicy.SameSettings(settings, normalized)) return;
            var priorDeadline = pending?.Deadline ?? 0;
            settings = normalized;
            ResetSession();
            // A changed selection cannot immediately overlap a previously dispatched item action.
            nextPoll = Math.Max(Environment.TickCount64 + AutoFoodConsumePolicy.PollMilliseconds, Math.Max(priorDeadline, earliestNextAction));
            status = enabled ? UiMessage.Authored("Select a food item to maintain Well Fed.") : UiMessage.Authored("Auto Food Consume is disabled.");
        }
    }

    public bool SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return false;
            if (enabled == value) return enabled;
            var priorDeadline = pending?.Deadline ?? 0;
            enabled = value;
            ResetSession();
            nextPoll = Math.Max(Environment.TickCount64 + AutoFoodConsumePolicy.PollMilliseconds, Math.Max(priorDeadline, earliestNextAction));
            if (value)
            {
                framework.Update += Update;
                clientState.Logout += OnLogout;
                clientState.TerritoryChanged += OnTerritoryChanged;
            }
            else Unsubscribe();
            status = value ? UiMessage.Authored("Select a food item to maintain Well Fed.") : UiMessage.Authored("Auto Food Consume is disabled.");
            return enabled;
        }
    }

    private void ResetSession()
    {
        generation++; pending = null; suspended = false; attempts = 0;
        observedContentId = 0; observedTerritory = 0;
    }

    internal IReadOnlyList<AutoFoodChoice> GetFoods()
    {
        lock (sync)
        {
            if (catalogAttempted) return foods ?? Array.Empty<AutoFoodChoice>();
            catalogAttempted = true;
            try
            {
                var foodSheet = dataManager.GetExcelSheet<ItemFood>();
                var choices = new List<AutoFoodChoice>();
                foreach (var item in dataManager.GetExcelSheet<Item>())
                {
                    if (item.RowId == 0 || item.RowId >= 1_000_000 || item.ItemUICategory.RowId != 46 || !item.ItemAction.IsValid) continue;
                    var action = item.ItemAction.Value;
                    if (action.Data[0] != 48 || action.Data[1] == 0 || action.Data[1] >= 10_000
                        || !foodSheet.TryGetRow(action.Data[1], out var food)) continue;
                    choices.Add(new(item.RowId, item.Name.ExtractText(), item.CanBeHq, food.RowId));
                }
                foods = choices.OrderBy(food => food.Name, StringComparer.OrdinalIgnoreCase).ThenBy(food => food.ItemId).ToArray();
            }
            catch (Exception error)
            {
                foods = Array.Empty<AutoFoodChoice>();
                status = UiMessage.Format(UiLocale.Key("Food data could not be loaded: {0}"), UiMessage.FromException(error));
                log.Warning(error, "[XASlave] Auto Food Consume could not load food data.");
            }
            return foods;
        }
    }

    internal bool TryGetFood(uint itemId, out AutoFoodChoice food)
    {
        food = GetFoods().FirstOrDefault(candidate => candidate.ItemId == itemId)!;
        return food != null;
    }

    private void OnLogout(int type, int code) { lock (sync) InvalidateIdentity(); }
    private void OnTerritoryChanged(uint territory) { lock (sync) InvalidateIdentity(); }

    private void InvalidateIdentity()
    {
        generation++;
        if (pending != null)
            Suspend(UiMessage.Authored("Character or territory changed during food use. Disable and re-enable Auto Food Consume to retry."));
        observedContentId = 0; observedTerritory = 0;
        nextPoll = Math.Max(nextPoll, Environment.TickCount64 + AutoFoodConsumePolicy.PollMilliseconds);
    }

    private bool IsCurrent(long owner)
    {
        if (disposed || !enabled || suspended || generation != owner || framework.IsFrameworkUnloading) return false;
        if (isOwnerDisposed()) return false;
        return !disposed && enabled && !suspended && generation == owner && !framework.IsFrameworkUnloading;
    }

    private void Update(IFramework _)
    {
        lock (sync)
        {
            if (!framework.IsInFrameworkUpdateThread || disposed || !enabled || suspended || framework.IsFrameworkUnloading) return;
            var now = Environment.TickCount64;
            if (now < nextPoll) return;
            nextPoll = now + AutoFoodConsumePolicy.PollMilliseconds;
            var owner = generation;
            try
            {
                if (!IsCurrent(owner)) return;
                Tick(owner, now);
            }
            catch (Exception error)
            {
                if (generation != owner || disposed) return;
                Suspend(UiMessage.Format(UiLocale.Key("Auto Food Consume failed: {0}"), UiMessage.FromException(error)));
                log.Warning(error, "[XASlave] Auto Food Consume suspended after an error.");
            }
        }
    }

    private void Tick(long owner, long now)
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || playerState.ContentId == 0 || objectTable.LocalPlayer == null || clientState.TerritoryType == 0)
        {
            if (pending != null) InvalidateIdentity();
            else status = UiMessage.Authored("Waiting for a logged-in character.");
            return;
        }
        var contentId = playerState.ContentId;
        var territory = clientState.TerritoryType;
        if ((observedContentId != 0 && observedContentId != contentId) || (observedTerritory != 0 && observedTerritory != territory))
        {
            InvalidateIdentity();
            return;
        }
        observedContentId = contentId; observedTerritory = territory;
        if (settings.ItemId == 0) { status = UiMessage.Authored("Select a food item to maintain Well Fed."); return; }
        if (!TryGetFood(settings.ItemId, out var food) || (settings.HighQuality && !food.CanBeHq))
        {
            status = UiMessage.Authored("The selected food or quality is unavailable.");
            return;
        }
        var buffKnown = TryReadBuff(out var buff);
        var countKnown = TryCountSelectedFood(settings.ItemId, settings.HighQuality, out var count);
        if (!IsCurrent(owner)) return;
        if (pending != null)
        {
            if (!buffKnown || !countKnown)
                pending = pending with { ObservationUncertain = true };
            if (buffKnown)
                ObservePending(buff, countKnown, count, now);
            else if (now >= pending.Deadline)
                Suspend(UiMessage.Authored("Food use could not be confirmed. Disable and re-enable Auto Food Consume to retry."));
            else status = UiMessage.Authored("Waiting for food use confirmation.");
            return;
        }
        if (!buffKnown || !countKnown)
        {
            status = UiMessage.Authored("The main inventory or food status is unavailable.");
            return;
        }
        var expectedParam = AutoFoodConsumePolicy.ExpectedParam(food.FoodRowId, settings.HighQuality);
        if (AutoFoodConsumePolicy.IsOtherActiveFood(buff, expectedParam))
        {
            status = UiMessage.Authored("A different or unknown food buff is active; waiting for it to expire.");
            return;
        }
        if (!AutoFoodConsumePolicy.NeedsRefresh(buff, expectedParam, settings.RefreshThresholdSeconds))
        {
            attempts = 0;
            status = UiMessage.Authored("Well Fed does not need refreshing.");
            return;
        }
        if (!CanUseFood()) { status = UiMessage.Authored("Waiting until the character can safely use food."); return; }
        var territoryInfo = TerritoryInfo.Instance();
        if (territoryInfo == null) { status = UiMessage.Authored("Waiting until the character can safely use food."); return; }
        var inDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
        if (!AutoFoodConsumePolicy.IsLocationAllowed(settings.OnlyInSanctuary, settings.OnlyInDuty, territoryInfo->InSanctuary, inDuty))
        {
            status = UiMessage.Authored("Waiting for a selected sanctuary or duty.");
            return;
        }
        if (count <= 0) { status = UiMessage.Authored("The selected food quality is missing from the main inventory."); return; }
        var manager = ActionManager.Instance();
        var useId = food.ItemId + (settings.HighQuality ? 1_000_000u : 0u);
        if (manager == null || manager->ActionQueued || !float.IsFinite(manager->AnimationLock) || manager->AnimationLock > 0
            || manager->GetActionStatus(ActionType.Item, useId) != 0)
        {
            status = UiMessage.Authored("Waiting for the selected food to become usable.");
            return;
        }
        if (!IsCurrent(owner) || !CanUseFood() || playerState.ContentId != contentId || clientState.TerritoryType != territory
            || objectTable.LocalPlayer == null || ActionManager.Instance() != manager) return;
        if (now < earliestNextAction || !TryReadBuff(out var freshBuff)
            || !TryCountSelectedFood(settings.ItemId, settings.HighQuality, out var freshCount) || freshCount != count
            || AutoFoodConsumePolicy.IsOtherActiveFood(freshBuff, expectedParam)
            || !AutoFoodConsumePolicy.NeedsRefresh(freshBuff, expectedParam, settings.RefreshThresholdSeconds)) return;
        territoryInfo = TerritoryInfo.Instance();
        inDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
        if (territoryInfo == null || !AutoFoodConsumePolicy.IsLocationAllowed(settings.OnlyInSanctuary, settings.OnlyInDuty, territoryInfo->InSanctuary, inDuty)
            || !IsCurrent(owner) || !CanUseFood() || playerState.ContentId != contentId || clientState.TerritoryType != territory
            || ActionManager.Instance() != manager || manager->ActionQueued || !float.IsFinite(manager->AnimationLock) || manager->AnimationLock > 0)
            return;
        // Publish uncertainty before calling native code. Reentrant disable/settings/identity changes cannot dispatch again.
        attempts++;
        earliestNextAction = now + AutoFoodConsumePolicy.ObserveMilliseconds;
        var sent = new FoodAttempt(owner, contentId, territory, expectedParam, count,
            freshBuff.Present ? Math.Max(0, freshBuff.RemainingSeconds) : 0, settings.RefreshThresholdSeconds,
            earliestNextAction, true, false);
        pending = sent;
        status = UiMessage.Authored("Waiting for food use confirmation.");
        var accepted = manager->UseAction(ActionType.Item, useId, extraParam: 0xFFFF);
        if (!IsCurrent(owner) || !ReferenceEquals(pending, sent)) return;
        pending = sent with { Accepted = accepted };
    }

    private void ObservePending(AutoFoodBuff buff, bool countKnown, int count, long now)
    {
        var sent = pending!;
        if (sent.Generation != generation || sent.ContentId != playerState.ContentId || sent.Territory != clientState.TerritoryType)
        {
            InvalidateIdentity();
            return;
        }
        if (AutoFoodConsumePolicy.ConfirmsRefresh(buff, sent.ExpectedParam, sent.BeforeRemaining, sent.Threshold))
        {
            pending = null; attempts = 0;
            status = UiMessage.Authored("Well Fed was refreshed.");
            return;
        }
        if (!countKnown || count != sent.BeforeCount)
        {
            sent = sent with { ObservationUncertain = true };
            pending = sent;
        }
        if (now < sent.Deadline) { status = UiMessage.Authored("Waiting for food use confirmation."); return; }
        if (sent.ObservationUncertain)
        {
            Suspend(UiMessage.Authored("Food use could not be confirmed. Disable and re-enable Auto Food Consume to retry."));
            return;
        }
        if (sent.Accepted || AutoFoodConsumePolicy.IsOtherActiveFood(buff, sent.ExpectedParam))
        {
            Suspend(UiMessage.Authored("Food use could not be confirmed. Disable and re-enable Auto Food Consume to retry."));
            return;
        }
        if (attempts >= AutoFoodConsumePolicy.MaxAttempts)
        {
            Suspend(UiMessage.Authored("Food use failed three times. Disable and re-enable Auto Food Consume to retry."));
            return;
        }
        pending = null;
        nextPoll = now + AutoFoodConsumePolicy.RetryMilliseconds;
        status = UiMessage.Format(UiLocale.Key("Food use was rejected; retrying ({0}/{1})."), attempts, AutoFoodConsumePolicy.MaxAttempts);
    }

    private bool CanUseFood()
        => clientState.IsLoggedIn && playerState.IsLoaded && objectTable.LocalPlayer != null && !clientState.IsPvP
            && CharacterSafetyHelper.IsDutySafeCondition(condition)
            && !condition[ConditionFlag.Casting] && !condition[ConditionFlag.Unconscious]
            && !condition[ConditionFlag.Mounted] && !condition[ConditionFlag.RidingPillion]
            && !condition[ConditionFlag.Crafting] && !condition[ConditionFlag.ExecutingCraftingAction]
            && !condition[ConditionFlag.Gathering] && !condition[ConditionFlag.ExecutingGatheringAction]
            && !condition[ConditionFlag.Fishing] && !condition[ConditionFlag.Performing]
            && !condition[ConditionFlag.TradeOpen] && !condition[ConditionFlag.LoggingOut];

    private bool TryReadBuff(out AutoFoodBuff buff)
    {
        buff = default;
        var player = objectTable.LocalPlayer;
        if (player == null) return false;
        foreach (var candidate in player.StatusList)
        {
            if (candidate.StatusId != 48) continue;
            if (buff.Present || !float.IsFinite(candidate.RemainingTime) || candidate.RemainingTime < 0) return false;
            buff = new(true, candidate.Param, candidate.RemainingTime);
        }
        return true;
    }

    private static bool TryCountSelectedFood(uint itemId, bool highQuality, out int count)
    {
        count = 0;
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;
        long total = 0;
        foreach (var type in MainBags)
        {
            if (!NativeArrayAccess.TryGetInventoryContainer(inventory, type, out var bag) || bag->Size != 35) return false;
            for (var index = 0; index < bag->Size; index++)
            {
                if (!NativeArrayAccess.TryGetInventorySlot(bag, index, out var slot)) return false;
                if (slot->ItemId != itemId || slot->IsCollectable()
                    || ((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != highQuality) continue;
                total += slot->Quantity;
                if (total > int.MaxValue) return false;
            }
        }
        count = (int)total;
        return true;
    }

    private void Suspend(UiMessage message)
    {
        suspended = true; pending = null; status = message;
    }

    private void Unsubscribe()
    {
        framework.Update -= Update;
        clientState.Logout -= OnLogout;
        clientState.TerritoryChanged -= OnTerritoryChanged;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; enabled = false; generation++; pending = null;
            Unsubscribe();
        }
    }
}
