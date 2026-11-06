using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Data;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int XagmanCharacterListLimit = 200;

    private sealed class XagmanCharacterListUiState
    {
        public string Name = string.Empty;
        public UiMessage Status = UiMessage.Raw(string.Empty);
        public bool Error;
        public List<string> Unresolved = new();
    }

    private readonly XagmanCharacterListUiState xagmanTonyCharacterLists = new();
    private readonly XagmanCharacterListUiState xagmanOwnerCharacterLists = new();

    private bool CanEditXagmanCharacterLists()
        => !isDisposed && !xagmanRunning && !plugin.TaskRunner.IsRunning
            && !xagmanTradeSafetySessionActive && !IsXagmanNetworkRecoveryHolding
            && System.Threading.Volatile.Read(ref xagmanManualXaDatabasePullRunning) == 0;

    private static UiMessage XagmanCharacterListText(string text, params object?[] args)
        => args.Length == 0 ? UiMessage.Authored(text) : UiMessage.Format(UiLocale.Key(text), args);

    private static void SetXagmanCharacterListStatus(XagmanCharacterListUiState state, UiMessage message, bool error = false)
    {
        state.Status = message;
        state.Error = error;
        state.Unresolved.Clear();
    }

    private bool CheckXagmanCharacterListIdle(XagmanCharacterListUiState state)
    {
        if (CanEditXagmanCharacterLists()) return true;
        SetXagmanCharacterListStatus(state, UiMessage.Authored("Saved character lists are unavailable while automation or recovery is active."), true);
        return false;
    }

    private void DrawXagmanCharacterLists(XagmanRole role)
    {
        var state = role == XagmanRole.Tony ? xagmanTonyCharacterLists : xagmanOwnerCharacterLists;
        var id = role == XagmanRole.Tony ? "xagmanTonyCharacterLists" : "xagmanOwnerCharacterLists";
        if (UiNative.Button($"{UiLocale.Native(UiLocale.Key("Character Lists"))}##{id}"))
            ImGui.OpenPopup(id);
        using var popup = ImRaii.Popup(id);
        if (!popup) return;

        using var wrap = ImRaii.TextWrapPos(Scale(500f));
        ImGui.TextUnformatted(UiLocale.Get(role == XagmanRole.Tony ? "SC5A8D95238CD3EE8" : "SAD5B29877C321F10"));
        ImGui.TextWrapped(UiMessage.Authored("Save checked characters as a named list. Load replaces this role's selection; an empty list clears it.").Display);
        ImGui.TextWrapped(UiMessage.Authored("Export copies a saved list. Import from Clipboard saves a list without loading it. Existing roster entries only.").Display);
        if (!CanEditXagmanCharacterLists())
            ImGui.TextWrapped(UiMessage.Authored("Saved character lists are unavailable while automation or recovery is active.").Display);

        using (ImRaii.Disabled(!CanEditXagmanCharacterLists()))
        {
            ImGui.SetNextItemWidth(Scale(220f));
            ImGui.InputTextWithHint($"##{id}Name", UiLocale.Get("SA3C674C0BAE2100E"), ref state.Name, XagmanCharacterSelectionService.MaxNameLength + 1);
            ImGui.SameLine();
            if (UiNative.Button($"{UiLocale.Native(UiLocale.Key("Save / Replace"))}##{id}Save"))
                SaveXagmanCharacterSelection(role, state);
            if (UiNative.Button($"{UiLocale.Native("SE50BF6DD11D7F47E")}##{id}Import"))
                ImportXagmanCharacterSelection(role, state);
        }

        ImGui.Separator();
        var savedLists = plugin.Configuration.XagmanSavedCharacterLists;
        var visible = (savedLists ?? new List<XagmanCharacterSelection>())
            .Where(entry => entry != null && entry.Role == role)
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (visible.Count == 0) ImGui.TextDisabled(UiLocale.Get("SB908027C027C465D"));
        for (var i = 0; i < visible.Count; i++)
        {
            var saved = visible[i];
            ImGui.TextUnformatted(saved.Name ?? string.Empty);
            ImGui.SameLine();
            ImGui.TextDisabled(XagmanCharacterListText("{0} characters", saved.Characters?.Count ?? 0).Display);
            using (ImRaii.Disabled(!CanEditXagmanCharacterLists()))
            {
                if (UiNative.SmallButton($"{UiLocale.Native("S8A6BDB6B18DA586F")}##{id}Load{i}"))
                    LoadXagmanCharacterSelection(role, saved, state);
                ImGui.SameLine();
                if (UiNative.SmallButton($"{UiLocale.Native("S3664895579F0A7E6")}##{id}Export{i}"))
                    ExportXagmanCharacterSelection(saved, state);
                ImGui.SameLine();
                if (UiNative.SmallButton($"{UiLocale.Native("SE2D0A54968EAD24E")}##{id}Delete{i}"))
                {
                    DeleteXagmanCharacterSelection(saved, state);
                    break;
                }
            }
        }

        if (state.Status.Original.Length != 0)
            ImGui.TextColored(state.Error ? new Vector4(1f, 0.45f, 0.4f, 1f) : new Vector4(0.7f, 0.85f, 0.7f, 1f), state.Status.Display);
        if (state.Unresolved.Count > 0)
        {
            ImGui.TextUnformatted(UiMessage.Authored("Unresolved characters:").Display);
            foreach (var character in state.Unresolved.Take(8))
                ImGui.TextUnformatted(GetDisplayCharacterName(character, IsCharacterListAnonymizationEnabled()));
            if (state.Unresolved.Count > 8)
                ImGui.TextDisabled(XagmanCharacterListText("{0} more...", state.Unresolved.Count - 8).Display);
        }
    }

    private bool TryGetXagmanSelectionRoster(XagmanRole role, bool checkExclusions, out List<XagmanCharacterSelectionCandidate> roster, out UiMessage message)
    {
        roster = new();
        message = UiMessage.Raw(string.Empty);
        var cfg = plugin.Configuration;
        AutoRetainerExclusionSnapshot? exclusions = null;
        if (checkExclusions && cfg.XagmanHonorArExclusions)
        {
            // Read a detached snapshot. The general refresh helper can prune other selections on failure.
            try { exclusions = plugin.ArConfigReader.ReadExclusionSnapshot(); }
            catch (Exception ex)
            {
                message = UiMessage.Format("SBB2D86A75E1F07AD", UiMessage.FromException(ex));
                return false;
            }
        }
        // The cache uses case-sensitive keys. Reconcile case variants explicitly before binding a CID.
        var ids = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in cfg.ReloggerCharacterInfo)
        {
            if (pair.Value == null || pair.Value.CID == 0
                || !XagmanCharacterSelectionService.TryNormalizeCharacterKey(pair.Key, out var key)) continue;
            if (!ids.TryGetValue(key, out var values)) ids[key] = values = new();
            values.Add(unchecked((ulong)pair.Value.CID));
        }
        var names = role == XagmanRole.Tony
            ? cfg.XagmanTonyCharacters.Select(entry => entry?.CharacterNameWorld ?? string.Empty).ToList()
            : cfg.XagmanFranchiseCharacters.ToList();
        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index]?.Trim() ?? string.Empty;
            if (XagmanCharacterSelectionService.TryNormalizeCharacterKey(name, out var normalizedName))
                name = normalizedName;
            ids.TryGetValue(name, out var values);
            if (values?.Count > 1)
            {
                message = XagmanCharacterListText("Character data has conflicting IDs for {0}. Refresh character data before using saved lists.",
                    GetDisplayCharacterName(name, IsCharacterListAnonymizationEnabled()));
                return false;
            }
            var contentId = values?.FirstOrDefault() ?? 0;
            roster.Add(new XagmanCharacterSelectionCandidate(index, name, contentId,
                AutoRetainerExclusionPolicy.IsCharacterAllowed(name, unchecked((long)contentId),
                    checkExclusions && cfg.XagmanHonorArExclusions, exclusions)));
        }
        return true;
    }

    private bool TryStoreXagmanCharacterSelections(List<XagmanCharacterSelection> replacement, out UiMessage message)
    {
        var cfg = plugin.Configuration;
        var previous = cfg.XagmanSavedCharacterLists;
        message = UiMessage.Raw(string.Empty);
        if (!CanEditXagmanCharacterLists())
        {
            message = UiMessage.Authored("Saved character lists are unavailable while automation or recovery is active.");
            return false;
        }
        try
        {
            cfg.XagmanSavedCharacterLists = replacement;
            cfg.Save();
            return true;
        }
        catch (Exception ex)
        {
            cfg.XagmanSavedCharacterLists = previous;
            message = XagmanCharacterListText("Could not save character lists: {0}", UiMessage.FromException(ex));
            return false;
        }
    }

    private void SaveXagmanCharacterSelection(XagmanRole role, XagmanCharacterListUiState state)
    {
        if (!CheckXagmanCharacterListIdle(state)) return;
        var selected = role == XagmanRole.Tony ? xagmanTonySelectedIndices : xagmanFranchiseSelectedIndices;
        var roster = new List<XagmanCharacterSelectionCandidate>();
        if (selected.Count > 0 && !TryGetXagmanSelectionRoster(role, false, out roster, out var rosterMessage))
        {
            SetXagmanCharacterListStatus(state, rosterMessage, true);
            return;
        }
        if (!XagmanCharacterSelectionService.TryCreate(state.Name, role, roster, selected, out var saved, out var message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        var replacement = (plugin.Configuration.XagmanSavedCharacterLists ?? new()).ToList();
        replacement.RemoveAll(entry => entry != null && entry.Role == role && string.Equals(entry.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
        if (replacement.Count >= XagmanCharacterListLimit)
        {
            SetXagmanCharacterListStatus(state, XagmanCharacterListText("At most {0} saved character lists are allowed.", XagmanCharacterListLimit), true);
            return;
        }
        replacement.Add(saved);
        if (!TryStoreXagmanCharacterSelections(replacement, out message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        state.Name = saved.Name;
        SetXagmanCharacterListStatus(state, XagmanCharacterListText("Saved {0} characters in '{1}'.", saved.Characters.Count, saved.Name));
    }

    private void ImportXagmanCharacterSelection(XagmanRole role, XagmanCharacterListUiState state)
    {
        if (!CheckXagmanCharacterListIdle(state)) return;
        string json;
        try { json = ImGui.GetClipboardText(); }
        catch (Exception ex)
        {
            SetXagmanCharacterListStatus(state, XagmanCharacterListText("Could not access the clipboard: {0}", UiMessage.FromException(ex)), true);
            return;
        }
        if (!XagmanCharacterSelectionService.TryDeserialize(json, role, out var saved, out var message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        var replacement = (plugin.Configuration.XagmanSavedCharacterLists ?? new()).ToList();
        if (replacement.Any(entry => entry != null && entry.Role == role && string.Equals(entry.Name, saved.Name, StringComparison.OrdinalIgnoreCase)))
        {
            SetXagmanCharacterListStatus(state, XagmanCharacterListText("A saved {0} list named '{1}' already exists. Choose another name or delete that list first.",
                UiMessage.Key(role == XagmanRole.Tony ? "SC5A8D95238CD3EE8" : "SAD5B29877C321F10"), saved.Name), true);
            return;
        }
        if (replacement.Count >= XagmanCharacterListLimit)
        {
            SetXagmanCharacterListStatus(state, XagmanCharacterListText("At most {0} saved character lists are allowed.", XagmanCharacterListLimit), true);
            return;
        }
        replacement.Add(saved);
        if (!TryStoreXagmanCharacterSelections(replacement, out message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        state.Name = saved.Name;
        SetXagmanCharacterListStatus(state, XagmanCharacterListText("Imported '{0}' ({1} characters). Use Load to apply it.", saved.Name, saved.Characters.Count));
    }

    private void ExportXagmanCharacterSelection(XagmanCharacterSelection saved, XagmanCharacterListUiState state)
    {
        if (!CheckXagmanCharacterListIdle(state)) return;
        if (!XagmanCharacterSelectionService.TrySerialize(saved, out var json, out var message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        try { ImGui.SetClipboardText(json); }
        catch (Exception ex)
        {
            SetXagmanCharacterListStatus(state, XagmanCharacterListText("Could not access the clipboard: {0}", UiMessage.FromException(ex)), true);
            return;
        }
        SetXagmanCharacterListStatus(state, XagmanCharacterListText("Exported '{0}' to the clipboard.", saved.Name));
    }

    private void DeleteXagmanCharacterSelection(XagmanCharacterSelection saved, XagmanCharacterListUiState state)
    {
        if (!CheckXagmanCharacterListIdle(state)) return;
        var replacement = (plugin.Configuration.XagmanSavedCharacterLists ?? new()).ToList();
        if (!replacement.Remove(saved)) return;
        if (!TryStoreXagmanCharacterSelections(replacement, out var message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        SetXagmanCharacterListStatus(state, XagmanCharacterListText("Deleted '{0}'.", saved.Name));
    }

    private void LoadXagmanCharacterSelection(XagmanRole role, XagmanCharacterSelection saved, XagmanCharacterListUiState state)
    {
        if (!CheckXagmanCharacterListIdle(state)) return;
        // Clearing an explicitly empty preset needs no roster, cached identities or external exclusions.
        var roster = new List<XagmanCharacterSelectionCandidate>();
        if (saved.Characters?.Count > 0 && !TryGetXagmanSelectionRoster(role, true, out roster, out var rosterMessage))
        {
            SetXagmanCharacterListStatus(state, rosterMessage, true);
            return;
        }
        if (!XagmanCharacterSelectionService.TryMatch(saved, role, roster, out var match, out var message))
        {
            SetXagmanCharacterListStatus(state, message, true);
            return;
        }
        if (saved.Characters?.Count > 0 && match.Indices.Count == 0)
        {
            SetXagmanCharacterListStatus(state, XagmanCharacterListText("Current selection was kept: {0} missing, {1} excluded, {2} ambiguous, {3} conflicting.",
                match.MissingCount, match.ExcludedCount, match.AmbiguousCount, match.ConflictCount), true);
            state.Unresolved.AddRange(match.UnresolvedCharacters);
            return;
        }
        if (!CheckXagmanCharacterListIdle(state)) return;
        // Apply only after all validation and matching; imported/saved packages never retain row indices.
        ResetXagmanMatchingCharacterSelection();
        StopXagmanTonyAutoSelection(UiMessage.Key("S7886807C2BF23CA0"));
        var selected = role == XagmanRole.Tony ? xagmanTonySelectedIndices : xagmanFranchiseSelectedIndices;
        selected.Clear();
        selected.UnionWith(match.Indices);
        InvalidateXagmanTradeCapacityForecast();
        state.Name = saved.Name;
        SetXagmanCharacterListStatus(state, XagmanCharacterListText("Loaded '{0}': {1} selected, {2} missing, {3} excluded, {4} ambiguous, {5} conflicting.",
            saved.Name, match.Indices.Count, match.MissingCount, match.ExcludedCount, match.AmbiguousCount, match.ConflictCount));
        state.Unresolved.AddRange(match.UnresolvedCharacters);
    }
}
