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
/// Return Alts To Homeworlds task panel — partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // ── Return Alts To Homeworlds state ──
    private readonly HashSet<int> returnAltsSelectedIndices = new();
    private List<(string CharName, ReloggerCharacterData Info)> returnAltsCharList = new();
    private bool returnAltsShowLog;

    // Return Alts — per-character action config
    private bool raDoTextAdvance = true;
    private bool raDoRemoveSprout = true;
    private bool raDoOpenInventory = true;
    private bool raDoOpenArmoury = true;
    private bool raDoOpenSaddlebags = true;
    private bool raDoOpenJournal = true;
    private bool raDoReturnToHome = true;
    private bool raDoCollectPersonalPlotInfo = true;
    private bool raDoReturnToFc = true;
    private bool raDoParseForXaDatabase = true;
    private bool raDoLogoutOnComplete;
    private bool raDoKillGameOnComplete;
    private bool raDoEnableArMulti = true;

    // ───────────────────────────────────────────────
    //  Task: Return Alts To Homeworlds
    // ───────────────────────────────────────────────
    private void DrawReturnAltsToHomeworldsTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Return Alts To Homeworlds");
        ImGui.TextDisabled("Relog through selected characters and return any that are world-visiting back to their homeworld.");
        ImGui.TextDisabled("Useful after DC travel sessions to ensure all characters are on their homeworld for data collection.");
        ImGui.Spacing();

        // ── Data source buttons ──
        ImGui.TextDisabled("Press buttons in order: Import from AutoRetainer \u2192 Refresh AR Data \u2192 Pull XA Database Info \u2192 Refresh List.");
        ImGui.Spacing();

        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (ImGui.Button("Import from AutoRetainer##ra"))
        { ImportFromAutoRetainer(); RefreshReturnAltsList(); }
        if (!arConfigExists) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Refresh AR Data##ra"))
        { RefreshArCharacterCache(); RefreshReturnAltsList(); }

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        if (!xaDbAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Pull XA Database Info##ra"))
        { PullXaDatabaseInfo(); RefreshReturnAltsList(); }
        if (!xaDbAvailable) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Refresh List##ra"))
            RefreshReturnAltsList();

        // Import status message
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), arImportStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Plugin Status ──
        DrawTaskPluginStatus(raDoCollectPersonalPlotInfo || raDoParseForXaDatabase);

        ImGui.Separator();
        ImGui.Spacing();

        // ── Run controls ──
        if (runner.IsRunning && runner.CurrentTaskName == "Return Alts To Homeworlds")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {runner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);

            DrawProcessingList(runner);

            ImGui.Spacing();
            if (ImGui.Button("Cancel##ra"))
                runner.Cancel();
        }
        else
        {
            var selectedChars = returnAltsSelectedIndices
                .Where(i => i >= 0 && i < returnAltsCharList.Count)
                .Select(i => returnAltsCharList[i].CharName)
                .ToList();

            var canStart = selectedChars.Count > 0 && !runner.IsRunning
                && plugin.IpcClient.IsAutoRetainerAvailable()
                && plugin.IpcClient.IsLifestreamAvailable()
                && (!(raDoCollectPersonalPlotInfo || raDoParseForXaDatabase) || plugin.IpcClient.IsXaDatabaseAvailable());

            var started = DrawPriorityTaskActionButton(
                SlaveTask.ReturnAltsToHomeworlds,
                $"Start ({selectedChars.Count} chars)##ra",
                canStart,
                () => StartTaskWithConfig("Return Alts To Homeworlds", selectedChars, returnAltsSelectedIndices,
                    raDoTextAdvance, raDoRemoveSprout, raDoOpenInventory, raDoOpenArmoury,
                    raDoOpenSaddlebags, raDoOpenJournal, raDoReturnToHome, raDoCollectPersonalPlotInfo,
                    raDoReturnToFc, raDoParseForXaDatabase, raDoLogoutOnComplete, raDoKillGameOnComplete, raDoEnableArMulti),
                "Select at least one character and ensure required plugins are loaded.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref returnAltsShowLog);

            ImGui.SameLine();
            if (ImGui.Button("Check All##ra"))
            { for (int i = 0; i < returnAltsCharList.Count; i++) returnAltsSelectedIndices.Add(i); }
            ImGui.SameLine();
            if (ImGui.Button("Clear All##ra"))
                returnAltsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character table — only shows non-homeworld characters ──
        DrawCharacterListHeader("Characters Not On Homeworld", $"({returnAltsCharList.Count} shown)", "returnAltsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        if (ImGui.BeginTable("ReturnAltsTable", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(25f));
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Homeworld", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("Current World", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableHeadersRow();

            // Sort
            var sortedRa = Enumerable.Range(0, returnAltsCharList.Count).ToList();
            var raSortSpecs = ImGui.TableGetSortSpecs();
            if (raSortSpecs.SpecsDirty) raSortSpecs.SpecsDirty = false;
            if (raSortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = raSortSpecs.Specs;
                    var col = spec.ColumnIndex;
                    var asc = spec.SortDirection == ImGuiSortDirection.Ascending;
                    sortedRa.Sort((a, b) =>
                    {
                        var (na, ia) = returnAltsCharList[a];
                        var (nb, ib) = returnAltsCharList[b];
                        var hwA = GetWorldFromKey(na);
                        var hwB = GetWorldFromKey(nb);
                        var cwA = !string.IsNullOrEmpty(ia.CurrentWorld) ? ia.CurrentWorld : hwA;
                        var cwB = !string.IsNullOrEmpty(ib.CurrentWorld) ? ib.CurrentWorld : hwB;
                        int cmp = col switch
                        {
                            1 => a.CompareTo(b),
                            2 => string.Compare(na, nb, StringComparison.OrdinalIgnoreCase),
                            3 => string.Compare(hwA, hwB, StringComparison.OrdinalIgnoreCase),
                            4 => string.Compare(cwA, cwB, StringComparison.OrdinalIgnoreCase),
                            _ => a.CompareTo(b),
                        };
                        return asc ? cmp : -cmp;
                    });
                }
            }

            foreach (var i in sortedRa)
            {
                var (charName, info) = returnAltsCharList[i];
                var homeworld = GetWorldFromKey(charName);
                var currentWorld = !string.IsNullOrEmpty(info.CurrentWorld) ? info.CurrentWorld : homeworld;
                var displayName = GetDisplayCharacterName(charName, anonymizeCharacters);
                var displayHomeworld = GetDisplayWorld(homeworld, anonymizeCharacters);
                var displayCurrentWorld = GetDisplayWorld(currentWorld, anonymizeCharacters);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = returnAltsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##rasel{i}", ref selected))
                { if (selected) returnAltsSelectedIndices.Add(i); else returnAltsSelectedIndices.Remove(i); }
                ImGui.TableNextColumn(); ImGui.Text((i + 1).ToString());
                ImGui.TableNextColumn(); ImGui.Text(displayName);
                ImGui.TableNextColumn(); ImGui.TextDisabled(displayHomeworld);
                ImGui.TableNextColumn();
                if (currentWorld != homeworld)
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), displayCurrentWorld);
                else
                    ImGui.TextDisabled(displayCurrentWorld);
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
        ImGui.Checkbox("Enable TextAdvance (/at y)##ra", ref raDoTextAdvance);
        ImGui.Checkbox("Remove Sprout (/nastatus off)##ra", ref raDoRemoveSprout);
        ImGui.Checkbox("Open Inventory##ra", ref raDoOpenInventory);
        ImGui.Checkbox("Open Armoury Chest##ra", ref raDoOpenArmoury);
        ImGui.Checkbox("Open Saddlebags##ra", ref raDoOpenSaddlebags);
        ImGui.Checkbox("Open Journal##ra", ref raDoOpenJournal);
        ImGui.Checkbox("Teleport Home (Lifestream)##ra", ref raDoReturnToHome);
        ImGui.Checkbox("Collect Personal Plot Info##ra", ref raDoCollectPersonalPlotInfo);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens /housing, checks Apartment, Private Estate, and Shared Estate signboards when available,\nand triggers XA Database saves after each housing pass.");
        ImGui.Checkbox("Teleport FC (Lifestream)##ra", ref raDoReturnToFc);
        ImGui.Checkbox("Parse for XA Database (FC window + save)##ra", ref raDoParseForXaDatabase);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens FC window, navigates Members/Info/Housing tabs to collect\nall FC data, then saves to XA Database.");
        DrawSharedCompletionAndLogFooter("ra", "ralog", ref raDoLogoutOnComplete, ref raDoKillGameOnComplete, ref raDoEnableArMulti, ref returnAltsShowLog, runner);
    }

    private void RefreshReturnAltsList()
    {
        var charInfo = plugin.Configuration.ReloggerCharacterInfo;
        var chars = plugin.Configuration.ReloggerCharacters;

        // Only show characters where CurrentWorld != Homeworld (from AR import)
        returnAltsCharList = chars
            .Select(cn =>
            {
                charInfo.TryGetValue(cn, out var info);
                return (cn, info ?? new ReloggerCharacterData());
            })
            .Where(x =>
            {
                var nameParts = x.cn.Split('@');
                var homeworld = nameParts.Length > 1 ? nameParts[1] : "";
                var currentWorld = !string.IsNullOrEmpty(x.Item2.CurrentWorld) ? x.Item2.CurrentWorld : homeworld;
                return currentWorld != homeworld;
            })
            .OrderBy(x => x.cn)
            .ToList();
        returnAltsSelectedIndices.Clear();
    }
}
