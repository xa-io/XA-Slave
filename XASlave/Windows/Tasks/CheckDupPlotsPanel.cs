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
/// Check Duplicate Plots task panel - partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // -- Check Duplicate Plots state --
    private readonly HashSet<int> dupPlotsSelectedIndices = new();
    private List<(string CharName, ReloggerCharacterData Info)> dupPlotsCharList = new();
    private bool dupPlotsShowLog;

    // Check Duplicate Plots - per-character action config
    private bool dupDoTextAdvance = true;
    private bool dupDoRemoveSprout = true;
    private bool dupDoOpenInventory = true;
    private bool dupDoOpenArmoury = true;
    private bool dupDoOpenSaddlebags = true;
    private bool dupDoOpenJournal = true;
    private bool dupDoReturnToHome = true;
    private bool dupDoCollectPersonalPlotInfo = true;
    private bool dupDoReturnToFc = true;
    private bool dupDoParseForXaDatabase = true;
    private bool dupDoLogoutOnComplete;
    private bool dupDoKillGameOnComplete;
    private bool dupDoEnableArMulti = true;

    // -----------------------------------------------
    //  Task: Check Duplicate Plots
    // -----------------------------------------------
    private void DrawCheckDuplicatePlotsTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var charInfo = cfg.ReloggerCharacterInfo;
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SA455BD4F4CF7E92B"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S09C6672214880918"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S9DFF89B013BC2836"));
        ImGui.Spacing();

        // -- Data source buttons --
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SBCD5FE6ECC63445C"));
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S658431DD798692FF") + "##dup")))
        {
            PullXaDatabaseInfo();
            RefreshDupPlotsList();
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S429ADCE82E86413F") + "##dup")))
            RefreshDupPlotsList();
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SA9C3179C9323C281", charInfo.Count));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Plugin Status --
        DrawTaskPluginStatus(dupDoCollectPersonalPlotInfo || dupDoParseForXaDatabase);

        ImGui.Separator();
        ImGui.Spacing();

        // -- Duplicate summary (always visible) --
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
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S46D649E6A303841B"));
            foreach (var g in estateGroups)
            {
                var world = GetDisplayWorldFromKey(g.First().Key, anonymizeCharacters);
                var addr = g.First().Value.PersonalEstate;
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    global::XASlave.Localization.UiLocale.FormatKey("S5CFD1CD4BCF56ACE", world, addr, string.Join(", ", g.Select(kv => GetDisplayCharacterName(kv.Key, anonymizeCharacters)))));
            }
            foreach (var g in aptGroups)
            {
                var world = GetDisplayWorldFromKey(g.First().Key, anonymizeCharacters);
                var addr = g.First().Value.Apartment;
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    global::XASlave.Localization.UiLocale.FormatKey("S7844D012AA4BB9EB", world, addr, string.Join(", ", g.Select(kv => GetDisplayCharacterName(kv.Key, anonymizeCharacters)))));
            }
            foreach (var g in fcEstateGroups)
            {
                var world = GetDisplayWorldFromKey(g.First().Key, anonymizeCharacters);
                var addr = g.First().Value.FcEstate;
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                    global::XASlave.Localization.UiLocale.FormatKey("S5C6E464AFDF942C0", world, addr, string.Join(", ", g.Select(kv => GetDisplayCharacterName(kv.Key, anonymizeCharacters)))));
            }
        }
        else if (allWithHousing.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S27A0896F1989FC77"));
        }
        else
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE1223A39DC49AD26"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Run controls --
        if (runner.IsRunning && runner.CurrentTaskName == "Check Duplicate Plots")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", runner.LocalizedStatusText));
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabelDisplay);

            DrawProcessingList(runner);

            ImGui.Spacing();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + "##dup")))
                runner.Cancel();
        }
        else
        {
            var selectedChars = Enumerable.Range(0, dupPlotsCharList.Count)
                .Where(dupPlotsSelectedIndices.Contains)
                .Select(i => dupPlotsCharList[i].CharName)
                .ToList();

            var canStart = selectedChars.Count > 0 && !runner.IsRunning
                && plugin.IpcClient.IsAutoRetainerAvailable()
                && plugin.IpcClient.IsLifestreamAvailable()
                && (!(dupDoCollectPersonalPlotInfo || dupDoParseForXaDatabase) || plugin.IpcClient.IsXaDatabaseAvailable());

            var started = DrawPriorityTaskActionButton(
                SlaveTask.CheckDuplicatePlots,
                (string.Format(global::XASlave.Localization.UiLocale.Native("SC0F20E306C7E38D9"), selectedChars.Count) + $"##dup"),
                canStart,
                () => StartTaskWithConfig(global::XASlave.Localization.UiMessage.Key("SA455BD4F4CF7E92B"), selectedChars, dupPlotsCharList, dupPlotsSelectedIndices,
                    dupDoTextAdvance, dupDoRemoveSprout, dupDoOpenInventory, dupDoOpenArmoury,
                    dupDoOpenSaddlebags, dupDoOpenJournal, dupDoReturnToHome, dupDoCollectPersonalPlotInfo,
                    dupDoReturnToFc, dupDoParseForXaDatabase, dupDoLogoutOnComplete, dupDoKillGameOnComplete, dupDoEnableArMulti),
                global::XASlave.Localization.UiLocale.Get("S9C7F83339653A405"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("SC0F20E306C7E38D9", selectedChars.Count));
            if (started)
                AutoOpenTaskLogIfVerbose(ref dupPlotsShowLog);

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8D36DE8ED9DBBB1B") + "##dup")))
            {
                for (int i = 0; i < dupPlotsCharList.Count; i++)
                    dupPlotsSelectedIndices.Add(i);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##dup")))
                dupPlotsSelectedIndices.Clear();
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SFBCD4E880E79B807")))
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
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SBB95B50D6EC62872"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Character table with housing columns --
        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("SD004830B5B6937F6"), global::XASlave.Localization.UiLocale.FormatKey("S9C7EACDA63DAB78B", dupPlotsCharList.Count), "dupPlotsAnonymize");
        anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        using (var imguiScope218 = ImRaii.Table("DupPlotsTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        if (imguiScope218)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(25f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S78AE647DC5544D22"), ImGuiTableColumnFlags.WidthFixed, Scale(85f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S48E9FC2874B297FF"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S3FC028FFBB163293"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCEFAADD2A645EFF3"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

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
                var displayName = GetDisplayCharacterName(charName, anonymizeCharacters);
                var world = GetDisplayWorldFromKey(charName, anonymizeCharacters);

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

        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Actions Per Character --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SD077709C2945CC78"));
        ImGui.Spacing();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S457274504CDF43AA"));
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S799FDA81A78E3803") + "##dup"), ref dupDoTextAdvance);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S4714B2D36A35833C") + "##dup"), ref dupDoRemoveSprout);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S166DBD737506990A") + "##dup"), ref dupDoOpenInventory);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S53649CACF2611F56") + "##dup"), ref dupDoOpenArmoury);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S04135E2D90F4F408") + "##dup"), ref dupDoOpenSaddlebags);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF7B51D61398CF038") + "##dup"), ref dupDoOpenJournal);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S621D93AD01358374") + "##dup"), ref dupDoReturnToHome);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SFF3C7116AEE8239B") + "##dup"), ref dupDoCollectPersonalPlotInfo);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SF5DB3C15B4DCD96C"));
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S45CB3AAD43EE350D") + "##dup"), ref dupDoReturnToFc);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SD46EE40E0A0D26E7") + "##dup"), ref dupDoParseForXaDatabase);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SAB2CB5AF83659715"));
        DrawSharedCompletionAndLogFooter("dup", "duplog", ref dupDoLogoutOnComplete, ref dupDoKillGameOnComplete, ref dupDoEnableArMulti, ref dupPlotsShowLog, runner);
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
