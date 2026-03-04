using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Check Duplicate Plots task panel — partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // ── Check Duplicate Plots state ──
    private readonly HashSet<int> dupPlotsSelectedIndices = new();
    private List<(string CharName, ReloggerCharacterData Info)> dupPlotsCharList = new();
    private bool dupPlotsShowLog;

    // Check Duplicate Plots — per-character action config
    private bool dupDoTextAdvance = true;
    private bool dupDoRemoveSprout = true;
    private bool dupDoOpenInventory = true;
    private bool dupDoOpenArmoury = true;
    private bool dupDoOpenSaddlebags = true;
    private bool dupDoReturnToHome = true;
    private bool dupDoReturnToFc = true;
    private bool dupDoParseForXaDatabase = true;
    private bool dupDoEnableArMulti = true;

    // ───────────────────────────────────────────────
    //  Task: Check Duplicate Plots
    // ───────────────────────────────────────────────
    private void DrawCheckDuplicatePlotsTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var charInfo = cfg.ReloggerCharacterInfo;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Check Duplicate Plots");
        ImGui.TextDisabled("Detects characters sharing the same personal estate or apartment in the XA Database.");
        ImGui.TextDisabled("Process selected characters to refresh housing data and clear stale duplicates.");
        ImGui.Spacing();

        // ── Data source buttons ──
        ImGui.TextDisabled("Press the buttons in order to manually refresh the character list: Pull XA Database Info \u2192 Refresh List.");
        ImGui.Spacing();

        if (ImGui.Button("Pull XA Database Info##dup"))
        {
            PullXaDatabaseInfo();
            RefreshDupPlotsList();
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh List##dup"))
            RefreshDupPlotsList();
        ImGui.SameLine();
        ImGui.TextDisabled($"({charInfo.Count} characters in DB)");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Plugin Status ──
        DrawTaskPluginStatus(dupDoParseForXaDatabase);

        ImGui.Separator();
        ImGui.Spacing();

        // ── Duplicate summary (always visible) ──
        // Duplicates = same World + same Address (different worlds can have same plot number)
        var allWithHousing = charInfo
            .Where(kv => !string.IsNullOrEmpty(kv.Value.PersonalEstate)
                      || !string.IsNullOrEmpty(kv.Value.Apartment)
                      || !string.IsNullOrEmpty(kv.Value.FcEstate))
            .ToList();

        // Group by World + Address for each housing type
        var estateGroups = allWithHousing
            .Where(kv => !string.IsNullOrEmpty(kv.Value.PersonalEstate))
            .GroupBy(kv => GetWorldFromKey(kv.Key) + "|" + kv.Value.PersonalEstate)
            .Where(g => g.Count() > 1)
            .ToList();

        var aptGroups = allWithHousing
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Apartment))
            .GroupBy(kv => GetWorldFromKey(kv.Key) + "|" + kv.Value.Apartment)
            .Where(g => g.Count() > 1)
            .ToList();

        var fcEstateGroups = allWithHousing
            .Where(kv => !string.IsNullOrEmpty(kv.Value.FcEstate))
            .GroupBy(kv => GetWorldFromKey(kv.Key) + "|" + kv.Value.FcEstate)
            .Where(g => g.Count() > 1)
            .ToList();

        if (estateGroups.Count > 0 || aptGroups.Count > 0 || fcEstateGroups.Count > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Duplicates detected:");
            foreach (var g in estateGroups)
            {
                var world = g.First().Key.Split('@').LastOrDefault() ?? "";
                var addr = g.First().Value.PersonalEstate;
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    $"  Estate ({world}): {addr} \u2192 {string.Join(", ", g.Select(kv => kv.Key.Split('@')[0]))}");
            }
            foreach (var g in aptGroups)
            {
                var world = g.First().Key.Split('@').LastOrDefault() ?? "";
                var addr = g.First().Value.Apartment;
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    $"  Apartment ({world}): {addr} \u2192 {string.Join(", ", g.Select(kv => kv.Key.Split('@')[0]))}");
            }
            foreach (var g in fcEstateGroups)
            {
                var world = g.First().Key.Split('@').LastOrDefault() ?? "";
                var addr = g.First().Value.FcEstate;
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    $"  FC Estate ({world}): {addr} \u2192 {string.Join(", ", g.Select(kv => kv.Key.Split('@')[0]))}");
            }
        }
        else if (allWithHousing.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "No duplicates detected.");
        }
        else
        {
            ImGui.TextDisabled("No housing data found. Click 'Pull XA Database Info' first.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Run controls ──
        if (runner.IsRunning && runner.CurrentTaskName == "Check Duplicate Plots")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {runner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);

            DrawProcessingList(runner);

            ImGui.Spacing();
            if (ImGui.Button("Cancel##dup"))
                runner.Cancel();
        }
        else
        {
            var selectedChars = dupPlotsSelectedIndices
                .Where(i => i >= 0 && i < dupPlotsCharList.Count)
                .Select(i => dupPlotsCharList[i].CharName)
                .ToList();

            var canStart = selectedChars.Count > 0 && !runner.IsRunning
                && plugin.IpcClient.IsAutoRetainerAvailable()
                && plugin.IpcClient.IsLifestreamAvailable();

            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button($"Start ({selectedChars.Count} chars)##dup"))
            {
                StartTaskWithConfig("Check Duplicate Plots", selectedChars, dupPlotsSelectedIndices,
                    dupDoTextAdvance, dupDoRemoveSprout, dupDoOpenInventory, dupDoOpenArmoury,
                    dupDoOpenSaddlebags, dupDoReturnToHome, dupDoReturnToFc, dupDoParseForXaDatabase, dupDoEnableArMulti);
                dupPlotsShowLog = true;
            }
            if (!canStart) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Check All##dup"))
            {
                for (int i = 0; i < dupPlotsCharList.Count; i++)
                    dupPlotsSelectedIndices.Add(i);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear All##dup"))
                dupPlotsSelectedIndices.Clear();
            ImGui.SameLine();
            if (ImGui.Button("Select Duplicates Only"))
            {
                dupPlotsSelectedIndices.Clear();
                var dupNames = new HashSet<string>();
                foreach (var g in estateGroups)
                    foreach (var kv in g) dupNames.Add(kv.Key);
                foreach (var g in aptGroups)
                    foreach (var kv in g) dupNames.Add(kv.Key);
                foreach (var g in fcEstateGroups)
                    foreach (var kv in g) dupNames.Add(kv.Key);
                for (int i = 0; i < dupPlotsCharList.Count; i++)
                    if (dupNames.Contains(dupPlotsCharList[i].CharName))
                        dupPlotsSelectedIndices.Add(i);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Select only characters that have duplicate housing entries.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character table with housing columns ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Characters with Housing Data");
        ImGui.Spacing();

        if (ImGui.BeginTable("DupPlotsTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable,
            new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 30);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 25);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("Personal Estate", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Apartment", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("FC Estate", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            // Sort
            var sortedDup = Enumerable.Range(0, dupPlotsCharList.Count).ToList();
            var dupSortSpecs = ImGui.TableGetSortSpecs();
            if (dupSortSpecs.SpecsDirty) dupSortSpecs.SpecsDirty = false;
            if (dupSortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = dupSortSpecs.Specs;
                    var col = spec.ColumnIndex;
                    var asc = spec.SortDirection == ImGuiSortDirection.Ascending;
                    sortedDup.Sort((a, b) =>
                    {
                        var (na, ia) = dupPlotsCharList[a];
                        var (nb, ib) = dupPlotsCharList[b];
                        int cmp = col switch
                        {
                            1 => a.CompareTo(b),
                            2 => string.Compare(na, nb, StringComparison.OrdinalIgnoreCase),
                            3 => string.Compare(GetWorldFromKey(na), GetWorldFromKey(nb), StringComparison.OrdinalIgnoreCase),
                            4 => string.Compare(ia.PersonalEstate, ib.PersonalEstate, StringComparison.OrdinalIgnoreCase),
                            5 => string.Compare(ia.Apartment, ib.Apartment, StringComparison.OrdinalIgnoreCase),
                            6 => string.Compare(ia.FcEstate, ib.FcEstate, StringComparison.OrdinalIgnoreCase),
                            _ => a.CompareTo(b),
                        };
                        return asc ? cmp : -cmp;
                    });
                }
            }

            foreach (var i in sortedDup)
            {
                var (charName, info) = dupPlotsCharList[i];
                var nameParts = charName.Split('@');
                var displayName = nameParts[0];
                var world = nameParts.Length > 1 ? nameParts[1] : "";

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = dupPlotsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##dupsel{i}", ref selected))
                { if (selected) dupPlotsSelectedIndices.Add(i); else dupPlotsSelectedIndices.Remove(i); }
                ImGui.TableNextColumn(); ImGui.Text((i + 1).ToString());
                ImGui.TableNextColumn(); ImGui.Text(displayName);
                ImGui.TableNextColumn(); ImGui.TextDisabled(world);
                ImGui.TableNextColumn(); ImGui.Text(!string.IsNullOrEmpty(info.PersonalEstate) ? info.PersonalEstate : "-");
                ImGui.TableNextColumn(); ImGui.Text(!string.IsNullOrEmpty(info.Apartment) ? info.Apartment : "-");
                ImGui.TableNextColumn(); ImGui.Text(!string.IsNullOrEmpty(info.FcEstate) ? info.FcEstate : "-");
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Actions Per Character ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Actions Per Character");
        ImGui.Spacing();
        ImGui.TextDisabled("Check if on homeworld \u2192 return via Lifestream if not (always-on)");
        ImGui.Checkbox("Enable TextAdvance (/at y)##dup", ref dupDoTextAdvance);
        ImGui.Checkbox("Remove Sprout (/nastatus off)##dup", ref dupDoRemoveSprout);
        ImGui.Checkbox("Open Inventory##dup", ref dupDoOpenInventory);
        ImGui.Checkbox("Open Armoury Chest##dup", ref dupDoOpenArmoury);
        ImGui.Checkbox("Open Saddlebags##dup", ref dupDoOpenSaddlebags);
        ImGui.Checkbox("Teleport Home (Lifestream)##dup", ref dupDoReturnToHome);
        ImGui.Checkbox("Teleport FC (Lifestream)##dup", ref dupDoReturnToFc);
        ImGui.Checkbox("Parse for XA Database (FC window + save)##dup", ref dupDoParseForXaDatabase);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens FC window, navigates Members/Info/Housing tabs to collect\nall FC data, then saves to XA Database.");
        ImGui.Separator();
        ImGui.Checkbox("Enable AR Multi Mode on completion##dup", ref dupDoEnableArMulti);

        // ── Log with Copy/Clear ──
        DrawTaskLog("duplog", ref dupPlotsShowLog, runner);
    }

    private void RefreshDupPlotsList()
    {
        var charInfo = plugin.Configuration.ReloggerCharacterInfo;

        // Find all duplicate groups across personal estate, apartment, and FC estate
        // Duplicates = same WORLD + same address (different worlds can have the same plot number)
        var allWithHousing = charInfo
            .Where(kv => !string.IsNullOrEmpty(kv.Value.PersonalEstate)
                      || !string.IsNullOrEmpty(kv.Value.Apartment)
                      || !string.IsNullOrEmpty(kv.Value.FcEstate))
            .ToList();

        var dupCharNames = new HashSet<string>();
        foreach (var g in allWithHousing.Where(kv => !string.IsNullOrEmpty(kv.Value.PersonalEstate)).GroupBy(kv => GetWorldFromKey(kv.Key) + "|" + kv.Value.PersonalEstate).Where(g => g.Count() > 1))
            foreach (var kv in g) dupCharNames.Add(kv.Key);
        foreach (var g in allWithHousing.Where(kv => !string.IsNullOrEmpty(kv.Value.Apartment)).GroupBy(kv => GetWorldFromKey(kv.Key) + "|" + kv.Value.Apartment).Where(g => g.Count() > 1))
            foreach (var kv in g) dupCharNames.Add(kv.Key);
        foreach (var g in allWithHousing.Where(kv => !string.IsNullOrEmpty(kv.Value.FcEstate)).GroupBy(kv => GetWorldFromKey(kv.Key) + "|" + kv.Value.FcEstate).Where(g => g.Count() > 1))
            foreach (var kv in g) dupCharNames.Add(kv.Key);

        // Preserve selections for characters still in the new list
        var oldSelected = new HashSet<string>();
        foreach (var idx in dupPlotsSelectedIndices)
            if (idx >= 0 && idx < dupPlotsCharList.Count)
                oldSelected.Add(dupPlotsCharList[idx].CharName);

        // Only show characters that are part of a duplicate group
        dupPlotsCharList = allWithHousing
            .Where(kv => dupCharNames.Contains(kv.Key))
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(x => x.Key)
            .ToList();

        // Restore selections
        dupPlotsSelectedIndices.Clear();
        for (int i = 0; i < dupPlotsCharList.Count; i++)
            if (oldSelected.Contains(dupPlotsCharList[i].CharName))
                dupPlotsSelectedIndices.Add(i);
    }
}
