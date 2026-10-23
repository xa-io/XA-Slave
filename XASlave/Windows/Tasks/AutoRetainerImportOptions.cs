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
    private global::XASlave.Localization.UiMessage? arTaskExclusionErrorUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);

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
            arTaskExclusionError = global::XASlave.Localization.UiMessage.Store(ref arTaskExclusionErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
            if (pruneSelections)
                PruneAllAutoRetainerExcludedSelections();
            return true;
        }
        catch (Exception ex)
        {
            // Preserve saved rows and selections, but do not allow an unchecked roster to run.
            arTaskExclusionSnapshot = null;
            arTaskExclusionError = global::XASlave.Localization.UiMessage.Store(ref arTaskExclusionErrorUiMessage, global::XASlave.Localization.UiMessage.Format("SBB2D86A75E1F07AD", global::XASlave.Localization.UiMessage.FromException(ex)));
            PruneAllAutoRetainerExcludedSelections();
            return false;
        }
    }

    private List<AutoRetainerConfigReader.ArCharacterInfo> ReadAutoRetainerCharactersForTask(bool honorExclusions)
    {
        if (honorExclusions && !CanChangeAutoRetainerExclusions())
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S0F59484A76C0487B"), original => new InvalidOperationException(original));
        try
        {
            // Keep all imported rows so switching Honor Exclusions off restores them without another import.
            var characters = plugin.ArConfigReader.ReadCharacters(honorExclusions, retainExcludedCharacters: true);
            if (honorExclusions)
            {
                arTaskExclusionsInitialized = true;
                arTaskExclusionSnapshot = plugin.ArConfigReader.LastExclusionSnapshot
                    ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5E0D70BB12102D87"), original => new InvalidDataException(original));
                arTaskExclusionError = global::XASlave.Localization.UiMessage.Store(ref arTaskExclusionErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
                PruneAllAutoRetainerExcludedSelections();
            }
            return characters;
        }
        catch (Exception ex) when (honorExclusions)
        {
            arTaskExclusionsInitialized = true;
            arTaskExclusionSnapshot = null;
            arTaskExclusionError = global::XASlave.Localization.UiMessage.Store(ref arTaskExclusionErrorUiMessage, global::XASlave.Localization.UiMessage.Format("SBB2D86A75E1F07AD", global::XASlave.Localization.UiMessage.FromException(ex)));
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
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S9CF500B411EB366C") + $"##{id}"), ref honorExclusions))
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
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S3A8AF9CC1690654E"));

        if (honorExclusions && !string.IsNullOrEmpty(arTaskExclusionError))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), global::XASlave.Localization.UiLocale.Get("S3C7A854F8BAC7B5D"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S6146A7ADFE5103AB", global::XASlave.Localization.UiMessage.Resolve(ref arTaskExclusionErrorUiMessage, arTaskExclusionError)));
            ImGui.SameLine();
            using (ImRaii.Disabled(!CanChangeAutoRetainerExclusions()))
            {
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S942087CC2D41E013") + $"##arExclusions{id}")))
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
            StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("S51F9A4C091CC0626"));
            InvalidateXagmanTradeCapacityForecast();
        }
    }
}
