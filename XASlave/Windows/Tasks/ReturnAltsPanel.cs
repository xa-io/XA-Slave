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
/// Return Alts To Homeworlds task panel - partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // -- Return Alts To Homeworlds state --
    private readonly HashSet<int> returnAltsSelectedIndices = new();
    private List<(string CharName, ReloggerCharacterData Info)> returnAltsCharList = new();
    private bool returnAltsShowLog;

    // Return Alts - per-character action config
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

    // -----------------------------------------------
    //  Task: Return Alts To Homeworlds
    // -----------------------------------------------
    private void DrawReturnAltsToHomeworldsTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S99446740287A7C89"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S75CE0E3D97FCF0E5"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE7150C565DA54E91"));
        ImGui.Spacing();

        // -- Data source buttons --
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S36E98D468AE144B3"));
        ImGui.Spacing();

        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        using (ImRaii.Disabled(!arConfigExists))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S74BC538828226264") + "##ra")))
            { ImportFromAutoRetainer(cfg.ReturnAltsHonorArExclusions); RefreshReturnAltsList(); }
        }

        DrawHonorAutoRetainerExclusions("returnAlts", cfg.ReturnAltsHonorArExclusions,
            value => cfg.ReturnAltsHonorArExclusions = value,
            () => PruneAutoRetainerExcludedSelections(returnAltsCharList.Select(row => row.CharName).ToList(),
                returnAltsSelectedIndices, cfg.ReturnAltsHonorArExclusions));
        PruneAutoRetainerExcludedSelections(returnAltsCharList.Select(row => row.CharName).ToList(),
            returnAltsSelectedIndices, cfg.ReturnAltsHonorArExclusions);

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S2D11B96CDCB0C642") + "##ra")))
        { RefreshArCharacterCache(cfg.ReturnAltsHonorArExclusions); RefreshReturnAltsList(); }

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        using (ImRaii.Disabled(!xaDbAvailable))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S658431DD798692FF") + "##ra")))
            { PullXaDatabaseInfo(RefreshReturnAltsList); }
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S429ADCE82E86413F") + "##ra")))
            RefreshReturnAltsList();

        // Import status message
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Plugin Status --
        DrawTaskPluginStatus(raDoCollectPersonalPlotInfo || raDoParseForXaDatabase);

        ImGui.Separator();
        ImGui.Spacing();

        // -- Run controls --
        if (runner.IsRunning && runner.CurrentTaskName == "Return Alts To Homeworlds")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", runner.LocalizedStatusText));
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabelDisplay);

            DrawProcessingList(runner);

            ImGui.Spacing();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + "##ra")))
                runner.Cancel();
        }
        else
        {
            var selectedChars = Enumerable.Range(0, returnAltsCharList.Count)
                .Where(returnAltsSelectedIndices.Contains)
                .Where(i => IsAutoRetainerCharacterAllowed(returnAltsCharList[i].CharName, cfg.ReturnAltsHonorArExclusions))
                .Select(i => returnAltsCharList[i].CharName)
                .ToList();

            var canStart = selectedChars.Count > 0 && !runner.IsRunning
                && plugin.IpcClient.IsAutoRetainerAvailable()
                && plugin.IpcClient.IsLifestreamAvailable()
                && (!(raDoCollectPersonalPlotInfo || raDoParseForXaDatabase) || plugin.IpcClient.IsXaDatabaseAvailable());

            var started = DrawPriorityTaskActionButton(
                SlaveTask.ReturnAltsToHomeworlds,
                (string.Format(global::XASlave.Localization.UiLocale.Native("SC0F20E306C7E38D9"), selectedChars.Count) + $"##ra"),
                canStart,
                () => StartTaskWithConfig(global::XASlave.Localization.UiMessage.Key("S99446740287A7C89"), selectedChars, returnAltsCharList, returnAltsSelectedIndices,
                    raDoTextAdvance, raDoRemoveSprout, raDoOpenInventory, raDoOpenArmoury,
                    raDoOpenSaddlebags, raDoOpenJournal, raDoReturnToHome, raDoCollectPersonalPlotInfo,
                    raDoReturnToFc, raDoParseForXaDatabase, raDoLogoutOnComplete, raDoKillGameOnComplete, raDoEnableArMulti),
                global::XASlave.Localization.UiLocale.Get("S9C7F83339653A405"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("SC0F20E306C7E38D9", selectedChars.Count));
            if (started)
                AutoOpenTaskLogIfVerbose(ref returnAltsShowLog);

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8D36DE8ED9DBBB1B") + "##ra")))
            {
                for (int i = 0; i < returnAltsCharList.Count; i++)
                    if (IsAutoRetainerCharacterAllowed(returnAltsCharList[i].CharName, cfg.ReturnAltsHonorArExclusions))
                        returnAltsSelectedIndices.Add(i);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##ra")))
                returnAltsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Character table - only shows non-homeworld characters --
        var returnAltsVisibleIndices = Enumerable.Range(0, returnAltsCharList.Count)
            .Where(i => IsAutoRetainerCharacterAllowed(returnAltsCharList[i].CharName, cfg.ReturnAltsHonorArExclusions))
            .ToList();
        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("SA3DE23C8527D41C0"), global::XASlave.Localization.UiLocale.FormatKey("S9C7EACDA63DAB78B", returnAltsVisibleIndices.Count), "returnAltsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        using (var imguiScope148 = ImRaii.Table("ReturnAltsTable", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        if (imguiScope148)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(25f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S5733E35FD7609001"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S804810D864C2645C"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

            // Sort
            var sortedRa = returnAltsVisibleIndices;
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

            var displayIndex = 0;
            foreach (var i in sortedRa)
            {
                displayIndex++;
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
                ImGui.TableNextColumn(); ImGui.Text(displayIndex.ToString());
                ImGui.TableNextColumn(); ImGui.Text(displayName);
                ImGui.TableNextColumn(); ImGui.TextDisabled(displayHomeworld);
                ImGui.TableNextColumn();
                if (currentWorld != homeworld)
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), displayCurrentWorld);
                else
                    ImGui.TextDisabled(displayCurrentWorld);
            }

        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Actions Per Character --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SD077709C2945CC78"));
        ImGui.Spacing();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S457274504CDF43AA"));
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S799FDA81A78E3803") + "##ra"), ref raDoTextAdvance);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S4714B2D36A35833C") + "##ra"), ref raDoRemoveSprout);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S166DBD737506990A") + "##ra"), ref raDoOpenInventory);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S53649CACF2611F56") + "##ra"), ref raDoOpenArmoury);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S04135E2D90F4F408") + "##ra"), ref raDoOpenSaddlebags);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF7B51D61398CF038") + "##ra"), ref raDoOpenJournal);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S621D93AD01358374") + "##ra"), ref raDoReturnToHome);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SFF3C7116AEE8239B") + "##ra"), ref raDoCollectPersonalPlotInfo);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SF5DB3C15B4DCD96C"));
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S45CB3AAD43EE350D") + "##ra"), ref raDoReturnToFc);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SD46EE40E0A0D26E7") + "##ra"), ref raDoParseForXaDatabase);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SAB2CB5AF83659715"));
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
