using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class MsqProgressDisplayService : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private List<Quest>? allMainScenarioQuests;
    private bool enabled;
    private bool subscribed;
    private DateTime lastRefreshUtc = DateTime.MinValue;

    public MsqProgressDisplayService(
        IAddonLifecycle addonLifecycle,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.dataManager = dataManager;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        if (!EnsureQuestCache())
        {
            StatusText = "Unavailable - quest sheet cache could not be prepared.";
            return false;
        }

        Subscribe();
        enabled = true;
        StatusText = "Enabled - Scenario Tree shows remaining MSQ count and completion percentage.";
        TryRefreshScenarioTree();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
    }

    private bool EnsureQuestCache()
    {
        if (allMainScenarioQuests != null)
            return allMainScenarioQuests.Count > 0;

        try
        {
            var questSheet = dataManager.GetExcelSheet<Quest>();
            if (questSheet == null)
            {
                allMainScenarioQuests = [];
                return false;
            }

            allMainScenarioQuests = questSheet
                .Where(quest => quest.RowId > 0
                    && !string.IsNullOrWhiteSpace(quest.Name.ToString())
                    && quest.JournalGenre.Value.RowId > 0
                    && quest.JournalGenre.Value.Icon == 61412)
                .OrderBy(quest => quest.RowId)
                .ToList();

            return allMainScenarioQuests.Count > 0;
        }
        catch (Exception ex)
        {
            allMainScenarioQuests = [];
            log.Warning(ex, "[XASlave] Failed to build the MSQ quest cache.");
            return false;
        }
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "ScenarioTree", OnScenarioTreeAddon);
        subscribed = true;

        if (TryGetScenarioTreeAddon(out var scenarioTree) && IsScenarioTreeReady(scenarioTree))
            TryRefreshScenarioTree(scenarioTree);
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnScenarioTreeAddon);
        subscribed = false;
    }

    private void OnScenarioTreeAddon(AddonEvent _, AddonArgs __)
    {
        if (!enabled)
            return;

        TryRefreshScenarioTree();
    }

    private void TryRefreshScenarioTree()
    {
        if (!TryGetScenarioTreeAddon(out var scenarioTree))
            return;

        TryRefreshScenarioTree(scenarioTree);
    }

    private void TryRefreshScenarioTree(AtkUnitBase* scenarioTree)
    {
        if ((DateTime.UtcNow - lastRefreshUtc).TotalMilliseconds < 1000)
            return;

        lastRefreshUtc = DateTime.UtcNow;

        try
        {
            if (!IsScenarioTreeReady(scenarioTree) || scenarioTree->AtkValues == null || scenarioTree->AtkValuesCount <= 7)
                return;

            if (!TryGetCurrentExpansionProgress(out var result) || result.Remaining <= 0)
                return;

            var questSheet = dataManager.GetExcelSheet<Quest>();
            if (questSheet == null || !questSheet.TryGetRow(result.FirstIncompleteQuest, out var quest))
                return;

            var text = $"{quest.Name.ToString()} ({result.Remaining} / {result.PercentComplete:F1}%)";
            scenarioTree->AtkValues[7].SetManagedString(text);
            scenarioTree->OnRefresh(scenarioTree->AtkValuesCount, scenarioTree->AtkValues);

            var button = scenarioTree->GetComponentButtonById(13);
            if (button == null)
                return;

            var textNode = (AtkTextNode*)button->UldManager.SearchNodeById(6);
            if (textNode != null)
                textNode->SetText(text);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to refresh Scenario Tree MSQ progress.");
        }
    }

    private static bool TryGetScenarioTreeAddon(out AtkUnitBase* scenarioTree)
    {
        scenarioTree = null;
        var addonAddress = Plugin.GameGui.GetAddonByName("ScenarioTree", 1);
        if (addonAddress.IsNull)
            return false;

        scenarioTree = (AtkUnitBase*)addonAddress.Address;
        return scenarioTree != null;
    }

    private static bool IsScenarioTreeReady(AtkUnitBase* scenarioTree)
    {
        return scenarioTree != null
            && scenarioTree->IsReady
            && scenarioTree->IsVisible
            && scenarioTree->RootNode != null;
    }

    private bool TryGetCurrentExpansionProgress(out MsqProgressResult result)
    {
        result = default;
        if (!EnsureQuestCache() || allMainScenarioQuests == null || allMainScenarioQuests.Count == 0)
            return false;

        var scenarioTree = AgentScenarioTree.Instance();
        var uiState = UIState.Instance();
        if (scenarioTree == null || uiState == null || scenarioTree->Data == null)
            return false;

        var firstIncompleteQuest = (uint)scenarioTree->Data->MainScenarioQuestIds[0] + 65536;
        if (firstIncompleteQuest == 65536)
            return false;

        var questSheet = dataManager.GetExcelSheet<Quest>();
        if (questSheet == null || !questSheet.TryGetRow(firstIncompleteQuest, out var firstIncompleteQuestData))
            return false;

        var currentJournalGenreId = firstIncompleteQuestData.JournalGenre.Value.RowId;
        if (currentJournalGenreId == 0)
            return false;

        var currentExpansionQuests = allMainScenarioQuests
            .Where(quest => quest.JournalGenre.Value.RowId == currentJournalGenreId)
            .ToList();
        if (currentExpansionQuests.Count == 0)
            return false;

        var completedCount = 0;
        foreach (var quest in currentExpansionQuests)
        {
            var maxSequence = 0u;
            for (var i = 0; i < quest.TodoParams.Count; i++)
                maxSequence = Math.Max(maxSequence, quest.TodoParams[i].ToDoCompleteSeq);

            if (uiState->IsUnlockLinkUnlockedOrQuestCompleted(quest.RowId, (byte)Math.Min(maxSequence, byte.MaxValue)))
                completedCount++;
        }

        var totalCount = currentJournalGenreId == 1
            ? AdjustArrQuestCount(currentExpansionQuests.Count)
            : currentExpansionQuests.Count;
        if (totalCount <= 0)
            return false;

        var remaining = Math.Max(0, totalCount - completedCount);
        var percentComplete = completedCount * 100f / totalCount;
        result = new MsqProgressResult(remaining, percentComplete, firstIncompleteQuest);
        return true;
    }

    private static int AdjustArrQuestCount(int baseCount)
    {
        var adjustedCount = baseCount;
        var playerState = PlayerState.Instance();
        if (playerState == null)
            return baseCount;

        if (playerState->StartTown != 1)
            adjustedCount -= 23;
        if (playerState->StartTown != 2)
            adjustedCount -= 23;
        if (playerState->StartTown != 3)
            adjustedCount -= 24;

        return adjustedCount - 8;
    }

    private readonly record struct MsqProgressResult(int Remaining, float PercentComplete, uint FirstIncompleteQuest);
}
