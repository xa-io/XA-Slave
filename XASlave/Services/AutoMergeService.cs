using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class AutoMergeService : IDisposable
{
    private const int MergeThrottleMilliseconds = 300;

    private static readonly InventoryType[] MainInventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    private readonly record struct StackCandidate(
        InventoryType Container,
        ushort Slot,
        uint ItemId,
        bool IsHighQuality,
        int Quantity,
        int StackSize);

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private bool enabled;
    private bool listenerRegistered;
    private bool subscribed;
    private bool pendingMerge;
    private long lastMergeAttemptTick;

    public AutoMergeService(
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.dataManager = dataManager;
        this.log = log;
        RefreshStatusText();
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            RefreshStatusText();
            return enabled;
        }

        enabled = value;
        pendingMerge = false;
        lastMergeAttemptTick = 0;
        UpdateRegistrations(enabled);
        RefreshStatusText();
        return enabled;
    }

    public void Dispose()
    {
        enabled = false;
        pendingMerge = false;
        lastMergeAttemptTick = 0;
        UpdateRegistrations(false);
        RefreshStatusText();
    }

    private void UpdateRegistrations(bool targetEnabled)
    {
        if (listenerRegistered != targetEnabled)
        {
            if (targetEnabled)
            {
                addonLifecycle.RegisterListener(AddonEvent.PostShow, "Inventory", OnInventoryShown);
                addonLifecycle.RegisterListener(AddonEvent.PostShow, "InventoryExpansion", OnInventoryShown);
                addonLifecycle.RegisterListener(AddonEvent.PostShow, "InventoryLarge", OnInventoryShown);
            }
            else
            {
                addonLifecycle.UnregisterListener(OnInventoryShown);
            }

            listenerRegistered = targetEnabled;
        }

        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
            framework.Update += OnFrameworkUpdate;
        else
            framework.Update -= OnFrameworkUpdate;

        subscribed = targetEnabled;
    }

    private void OnInventoryShown(AddonEvent _, AddonArgs __)
    {
        if (!enabled)
            return;

        pendingMerge = true;
        lastMergeAttemptTick = 0;
        RefreshStatusText();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled || !pendingMerge)
            return;

        if (!clientState.IsLoggedIn)
        {
            pendingMerge = false;
            RefreshStatusText();
            return;
        }

        if (!CanMoveItemsNow())
        {
            RefreshStatusText();
            return;
        }

        var now = Environment.TickCount64;
        if (now - lastMergeAttemptTick < MergeThrottleMilliseconds)
            return;

        lastMergeAttemptTick = now;
        if (TryMergeNextStack())
        {
            RefreshStatusText();
            return;
        }

        pendingMerge = false;
        RefreshStatusText();
    }

    private bool CanMoveItemsNow()
    {
        return !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51]
            && !condition[ConditionFlag.Casting]
            && !condition[ConditionFlag.Occupied]
            && !condition[ConditionFlag.Occupied30]
            && !condition[ConditionFlag.Occupied33]
            && !condition[ConditionFlag.Occupied38]
            && !condition[ConditionFlag.Occupied39]
            && !condition[ConditionFlag.OccupiedInEvent]
            && !condition[ConditionFlag.OccupiedInQuestEvent]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent];
    }

    private bool TryMergeNextStack()
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return false;

            foreach (var group in GetMergeableStacks(inventoryManager))
            {
                var ordered = group
                    .OrderByDescending(entry => entry.Quantity)
                    .ThenBy(entry => entry.Container)
                    .ThenBy(entry => entry.Slot)
                    .ToList();
                if (ordered.Count < 2)
                    continue;

                var destination = ordered[0];
                if (destination.Quantity >= destination.StackSize)
                    continue;

                foreach (var source in ordered.Skip(1))
                {
                    if (source.Container == destination.Container && source.Slot == destination.Slot)
                        continue;

                    inventoryManager->MoveItemSlot(
                        source.Container,
                        source.Slot,
                        destination.Container,
                        destination.Slot,
                        true);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Merge failed while attempting to merge inventory stacks.");
        }

        return false;
    }

    private IEnumerable<IGrouping<(uint ItemId, bool IsHighQuality), StackCandidate>> GetMergeableStacks(InventoryManager* inventoryManager)
    {
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var candidates = new List<StackCandidate>();

        foreach (var containerType in MainInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = inventoryManager->GetInventorySlot(containerType, slotIndex);
                item = ResolveInventoryItem(item);
                if (item == null || item->ItemId == 0 || item->IsCollectable())
                    continue;

                var baseItemId = item->GetBaseItemId();
                if (baseItemId == 0 || !itemSheet.TryGetRow(baseItemId, out var itemRow))
                    continue;

                var stackSize = Math.Max(1, (int)itemRow.StackSize);
                if (stackSize <= 1 || item->Quantity >= stackSize)
                    continue;

                candidates.Add(new StackCandidate(
                    containerType,
                    (ushort)slotIndex,
                    baseItemId,
                    item->IsHighQuality(),
                    item->Quantity,
                    stackSize));
            }
        }

        return candidates
            .GroupBy(entry => (entry.ItemId, entry.IsHighQuality))
            .Where(group => group.Count() > 1);
    }

    private static InventoryItem* ResolveInventoryItem(InventoryItem* item)
    {
        if (item == null)
            return null;

        var resolved = item;
        while (resolved->IsSymbolic)
        {
            var linked = resolved->GetLinkedItem();
            if (linked == null || linked == resolved)
                break;

            resolved = linked;
        }

        return resolved;
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        StatusText = pendingMerge
            ? "Enabled - inventory opened; merging incomplete main-bag stacks."
            : "Enabled - merges incomplete main-bag stacks when inventory opens.";
    }
}
