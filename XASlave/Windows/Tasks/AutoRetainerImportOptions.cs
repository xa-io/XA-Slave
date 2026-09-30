using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private AutoRetainerExclusionSnapshot? arTaskExclusionSnapshot;
    private bool arTaskExclusionsInitialized;
    private string arTaskExclusionError = string.Empty;

    private bool CanChangeAutoRetainerExclusions()
        => !plugin.TaskRunner.IsRunning && !xagmanRunning && !xagmanTradeSafetySessionActive;

    private bool IsAutoRetainerCharacterAllowed(string characterKey, bool honorExclusions)
    {
        if (!honorExclusions)
            return true;
        if (!arTaskExclusionsInitialized && CanChangeAutoRetainerExclusions())
            RefreshAutoRetainerExclusions(pruneSelections: false);
        plugin.Configuration.ReloggerCharacterInfo.TryGetValue(characterKey, out var info);
        return AutoRetainerExclusionPolicy.IsCharacterAllowed(characterKey, info?.CID ?? 0, true, arTaskExclusionSnapshot);
    }

    private bool RefreshAutoRetainerExclusions(bool pruneSelections = true)
    {
        arTaskExclusionsInitialized = true;
        try
        {
            arTaskExclusionSnapshot = plugin.ArConfigReader.ReadExclusionSnapshot();
            arTaskExclusionError = string.Empty;
            if (pruneSelections)
                PruneAllAutoRetainerExcludedSelections();
            return true;
        }
        catch (Exception ex)
        {
            // Preserve saved rows and selections, but do not allow an unchecked roster to run.
            arTaskExclusionSnapshot = null;
            arTaskExclusionError = $"Exclusions unavailable: {ex.Message}";
            PruneAllAutoRetainerExcludedSelections();
            return false;
        }
    }

    private List<AutoRetainerConfigReader.ArCharacterInfo> ReadAutoRetainerCharactersForTask(bool honorExclusions)
    {
        if (honorExclusions && !CanChangeAutoRetainerExclusions())
            throw new InvalidOperationException("Stop active automation before refreshing honored AutoRetainer exclusions.");
        try
        {
            // Keep all imported rows so switching Honor Exclusions off restores them without another import.
            var characters = plugin.ArConfigReader.ReadCharacters(honorExclusions, retainExcludedCharacters: true);
            if (honorExclusions)
            {
                arTaskExclusionsInitialized = true;
                arTaskExclusionSnapshot = plugin.ArConfigReader.LastExclusionSnapshot
                    ?? throw new InvalidDataException("AutoRetainer exclusions could not be read; existing roster data was preserved.");
                arTaskExclusionError = string.Empty;
                PruneAllAutoRetainerExcludedSelections();
            }
            return characters;
        }
        catch (Exception ex) when (honorExclusions)
        {
            arTaskExclusionsInitialized = true;
            arTaskExclusionSnapshot = null;
            arTaskExclusionError = $"Exclusions unavailable: {ex.Message}";
            PruneAllAutoRetainerExcludedSelections();
            throw;
        }
    }

    private void DrawHonorAutoRetainerExclusions(string id, bool honorExclusions, Action<bool> setValue,
        Action pruneSelection, Action? afterChanged = null)
    {
        if (honorExclusions && !arTaskExclusionsInitialized && CanChangeAutoRetainerExclusions())
            RefreshAutoRetainerExclusions();
        ImGui.SameLine();
        using (ImRaii.Disabled(!CanChangeAutoRetainerExclusions()))
        {
            if (ImGui.Checkbox($"Honor Exclusions##{id}", ref honorExclusions))
            {
                setValue(honorExclusions);
                if (honorExclusions)
                    RefreshAutoRetainerExclusions();
                pruneSelection();
                afterChanged?.Invoke();
                plugin.Configuration.Save();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Saved separately for this task. Hide and uncheck AutoRetainer's globally excluded characters, including already imported rows. Turn off to show them again without selecting them. Saved rows are retained. Stop active automation before changing this option.");

        if (honorExclusions && !string.IsNullOrEmpty(arTaskExclusionError))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), "Exclusions unavailable");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(arTaskExclusionError + "\nThis task's characters cannot be selected until Retry succeeds or Honor Exclusions is turned off.");
            ImGui.SameLine();
            using (ImRaii.Disabled(!CanChangeAutoRetainerExclusions()))
            {
                if (ImGui.SmallButton($"Retry##arExclusions{id}"))
                {
                    RefreshAutoRetainerExclusions();
                    pruneSelection();
                    afterChanged?.Invoke();
                }
            }
        }
    }

    private void PruneAutoRetainerExcludedSelections(IReadOnlyList<string> characters, HashSet<int> selected, bool honorExclusions)
    {
        if (!honorExclusions || !CanChangeAutoRetainerExclusions())
            return;
        if (!arTaskExclusionsInitialized)
            RefreshAutoRetainerExclusions(pruneSelections: false);
        if (arTaskExclusionSnapshot == null)
            return;
        selected.RemoveWhere(index => index < 0 || index >= characters.Count
            || !IsAutoRetainerCharacterAllowed(characters[index], true));
    }

    private void PruneAllAutoRetainerExcludedSelections()
    {
        if (!CanChangeAutoRetainerExclusions())
            return;
        var cfg = plugin.Configuration;
        PruneAutoRetainerExcludedSelections(cfg.ReloggerCharacters, reloggerSelectedIndices, cfg.ReloggerHonorArExclusions);
        PruneAutoRetainerExcludedSelections(returnAltsCharList.Select(row => row.CharName).ToList(), returnAltsSelectedIndices, cfg.ReturnAltsHonorArExclusions);
        PruneAutoRetainerExcludedSelections(cfg.FcPermsCharacters, fcPermsSelectedIndices, cfg.FcPermsHonorArExclusions);
        PruneAutoRetainerExcludedSelections(cfg.PrepLogisticsCharacters, prepLogisticsSelectedIndices, cfg.PrepLogisticsHonorArExclusions);
        PruneAutoRetainerExcludedSelections(cfg.RefreshSubsCharacters, refreshSubsSelectedIndices, cfg.RefreshSubsHonorArExclusions);
        PruneAutoRetainerExcludedSelections(cfg.XagmanTonyCharacters.Select(row => row.CharacterNameWorld).ToList(), xagmanTonySelectedIndices, cfg.XagmanHonorArExclusions);
        PruneAutoRetainerExcludedSelections(cfg.XagmanFranchiseCharacters, xagmanFranchiseSelectedIndices, cfg.XagmanHonorArExclusions);
        if (cfg.XagmanHonorArExclusions)
        {
            ResetXagmanMatchingCharacterSelection();
            StopXagmanTonyAutoSelection("Auto: exclusions refreshed. Run Auto again to check eligible characters.");
            InvalidateXagmanTradeCapacityForecast();
        }
    }
}
