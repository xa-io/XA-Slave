using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Refresh AR Subs/Bell - Rotates through characters, teleports to FC house,
/// interacts with Voyage Control Panel (submarine console) and Summoning Bell
/// to ensure AutoRetainer has all submarine data correctly.
///
/// Flow per character:
///   1. Relog to character
///   2. Teleport to FC house via Lifestream (/li fc)
///   3. Enter Additional Chambers (workshop)
///   4. Target and interact with Voyage Control Panel
///   5. Check if AutoRetainer is Busy, if not press ESC after 2s
///   6. Target and walk to Summoning Bell, interact
///   7. Check if AutoRetainer is Busy, if not press ESC after 2s
///   8. If still inside the workshop, target Company Chest, path to 1.5y, save FC chest gil to XA Database, and close the window
///   9. CharacterSafeWait, next character
/// </summary>
public partial class SlaveWindow
{
    // Current game strings mirrored from AutoRetainer's language table. Selection remains exact
    // and fail-closed so an unexpected menu shape can never fall back to a numeric row.
    private static readonly (string Text, bool Contains)[] RefreshPreviousVoyageLogEntries =
    {
        ("View previous voyage log", false),
        ("å‰å›žã®ãƒœã‚¤ã‚¸ãƒ£ãƒ¼å ±å‘Š", false),
        ("ä¸Šæ¬¡çš„è¿œèˆªæŠ¥å‘Š", false),
        ("ä¸Šæ¬¡çš„é èˆªå ±å‘Š", false),
        ("Bericht der letzten Erkundung", false),
        ("Consulter le journal de la prÃ©cÃ©dente expÃ©dition", false),
        ("ì´ì „ íƒì‚¬ ë³´ê³ ì„œ", false),
    };

    // -- Refresh AR Subs/Bell state --
    private readonly HashSet<int> refreshSubsSelectedIndices = new();
    private readonly ConcurrentQueue<string> refreshSubsCompletedCharacterQueue = new();
    private string refreshSubsNewChar = "";
    private string refreshSubsSearchFilter = "";
    private bool refreshSubsShowLog;
    private bool refreshSubsGoWorkshop = true;
    private float refreshSubsExtraWait = 3.0f;
    private bool refreshSubsArSuppressedByTask;
    private bool refreshSubsDoLogoutOnComplete;
    private bool refreshSubsDoKillGameOnComplete;
    private bool refreshSubsDoEnableArMulti = true;
    private bool refreshSubsDoOpenArmoury;
    private bool refreshSubsDoOpenSaddlebags;
    private bool refreshSubsDoOpenJournal;
    private bool refreshSubsDoReturnToHome;
    private bool refreshSubsDoCollectPersonalPlotInfo;
    private bool refreshSubsActionOptionsInitialized;

    private void ReleaseRefreshSubsArSuppression()
    {
        if (!refreshSubsArSuppressedByTask)
            return;

        plugin.IpcClient.AutoRetainerSetSuppressed(false);
        refreshSubsArSuppressedByTask = false;
    }

    private void EnsureRefreshSubsActionOptionsInitialized()
    {
        if (refreshSubsActionOptionsInitialized)
            return;

        var config = plugin.Configuration;
        refreshSubsGoWorkshop = config.RefreshSubsGoWorkshop;
        refreshSubsExtraWait = Math.Clamp(config.RefreshSubsExtraWaitSeconds, 0f, 30f);
        refreshSubsDoLogoutOnComplete = config.RefreshSubsLogoutOnComplete;
        refreshSubsDoKillGameOnComplete = config.RefreshSubsKillGameOnComplete;
        refreshSubsDoEnableArMulti = config.RefreshSubsEnableArMultiOnComplete;
        refreshSubsDoOpenArmoury = config.RefreshSubsOpenArmoury;
        refreshSubsDoOpenSaddlebags = config.RefreshSubsOpenSaddlebags;
        refreshSubsDoOpenJournal = config.RefreshSubsOpenJournal;
        refreshSubsDoCollectPersonalPlotInfo = config.RefreshSubsCollectPersonalPlotInfo;
        refreshSubsDoReturnToHome = config.RefreshSubsReturnToHome;
        refreshSubsActionOptionsInitialized = true;
    }

    private void DrawRefreshArSubsBellTask()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.RefreshSubsCharacters;
        while (refreshSubsCompletedCharacterQueue.TryDequeue(out var completedCharacter))
        {
            if (SelectionCompletion.RemoveCompletedCharacter(chars, refreshSubsSelectedIndices, completedCharacter))
                plugin.TaskRunner.AddLog($"Refresh Sub/Bell/Chest: unchecked {completedCharacter} from the character list.");
        }
        EnsureRefreshSubsActionOptionsInitialized();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S85CC8B46B386EB84"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S4398B2CD0B387C5C"));
        ImGui.Spacing();

        // -- Import / Refresh buttons --
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        using (ImRaii.Disabled(!arConfigExists))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S74BC538828226264") + "##refreshSubsImportAR")))
            {
                try
                {
                    var (added, total) = ImportCharactersFromArToList(chars, cfg.RefreshSubsHonorArExclusions);
                    cfg.Save();
                    arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (added > 0 ? global::XASlave.Localization.UiMessage.Format("SAFF25205F56B2E82", added, total) : global::XASlave.Localization.UiMessage.Format("SECB3F6AE67AFB0B6", total)));
                    if (cfg.RefreshSubsHonorArExclusions)
                        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref arImportStatusUiMessage, arImportStatus), plugin.ArConfigReader.ExclusionStatusMessage));
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                }
                catch (Exception ex)
                {
                    arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S002C95411A310E05", ex.Message));
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(arConfigExists
                ? global::XASlave.Localization.UiLocale.FormatKey("S4F45236F63C59987", plugin.ArConfigReader.GetAutoRetainerConfigPath())
                : global::XASlave.Localization.UiLocale.FormatKey("S7F848B59CF50E7D8", plugin.ArConfigReader.GetAutoRetainerConfigPath()));

        DrawHonorAutoRetainerExclusions("refreshSubs", cfg.RefreshSubsHonorArExclusions,
            value => cfg.RefreshSubsHonorArExclusions = value,
            () => PruneAutoRetainerExcludedSelections(chars, refreshSubsSelectedIndices, cfg.RefreshSubsHonorArExclusions));
        PruneAutoRetainerExcludedSelections(chars, refreshSubsSelectedIndices, cfg.RefreshSubsHonorArExclusions);

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        using (ImRaii.Disabled(!xaDbAvailable))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S658431DD798692FF") + "##refreshSubsPullXA")))
                PullXaDatabaseInfo();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? global::XASlave.Localization.UiLocale.Get("S527758F195A7F287")
                : global::XASlave.Localization.UiLocale.Get("S2A0686AE2571326E"));

        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Plugin status
        DrawTaskPluginStatus(refreshSubsDoCollectPersonalPlotInfo);

        // Config
        if (ImGui.Checkbox("##refreshSubsWorkshop", ref refreshSubsGoWorkshop))
            SaveRefreshSubsOptions();
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        using (ImRaii.Group())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.Get("SD32A6F9854E816A5"));
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.Get("S2A4ACEDC283E5710"));
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.Get("S9800D2F9E726C212"));
            ImGui.Spacing();
        }
        if (ImGui.IsItemClicked())
        {
            refreshSubsGoWorkshop = !refreshSubsGoWorkshop;
            SaveRefreshSubsOptions();
        }

        ImGui.SetNextItemWidth(Scale(80f));
        var wait = refreshSubsExtraWait;
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("S2AD98BDBAE0CB01A") + "##refreshSubsWait"), ref wait, 0.5f, 1.0f, "%.1f"))
        {
            if (wait < 0f) wait = 0f;
            if (wait > 30f) wait = 30f;
            refreshSubsExtraWait = wait;
            SaveRefreshSubsOptions();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Run controls --
        var isRunning = plugin.TaskRunner.IsRunning
            && (plugin.TaskRunner.CurrentTaskName == "Refresh Sub/Bell/Chest"
                || plugin.TaskRunner.CurrentTaskName == "Refresh AR Subs/Bell");
        if (isRunning)
        {
            var progress = plugin.TaskRunner.TotalItems > 0 ? (float)plugin.TaskRunner.CompletedItems / plugin.TaskRunner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S8EB69248AA11B2FD", plugin.TaskRunner.LocalizedStatusText));
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}");
            if (!string.IsNullOrEmpty(plugin.TaskRunner.CurrentItemLabel))
                ImGui.TextDisabled(plugin.TaskRunner.CurrentItemLabelDisplay);
            DrawProcessingList(plugin.TaskRunner);
            ImGui.Spacing();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + "##refreshSubsCancel")))
            {
                ReleaseRefreshSubsArSuppression();
                plugin.TaskRunner.Cancel();
            }
        }
        else
        {
            var selectedChars = GetSelectedRefreshSubsCharacters();
            var canStart = selectedChars.Count > 0
                && !plugin.TaskRunner.IsRunning
                && plugin.IpcClient.IsAutoRetainerAvailable()
                && plugin.IpcClient.IsLifestreamAvailable()
                && (!refreshSubsDoCollectPersonalPlotInfo || plugin.IpcClient.IsXaDatabaseAvailable());
            var started = DrawPriorityTaskActionButton(
                SlaveTask.RefreshArSubsBell,
                (string.Format(global::XASlave.Localization.UiLocale.Native("SC0F20E306C7E38D9"), selectedChars.Count) + $"##refreshSubsStart"),
                canStart,
                StartRefreshArSubsBell,
                !plugin.IpcClient.IsAutoRetainerAvailable()
                    || !plugin.IpcClient.IsLifestreamAvailable()
                    || (refreshSubsDoCollectPersonalPlotInfo && !plugin.IpcClient.IsXaDatabaseAvailable())
                    ? global::XASlave.Localization.UiLocale.Get("S0A4C6C0B785E7FB1")
                    : global::XASlave.Localization.UiLocale.Get("S630BBC121AA0D071"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("SC0F20E306C7E38D9", selectedChars.Count));
            if (started)
                AutoOpenTaskLogIfVerbose(ref refreshSubsShowLog);

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8D36DE8ED9DBBB1B") + "##refreshSubsAll")))
                SelectVisibleRefreshSubsCharacters();
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SDDCEB7ADFDB8816E") + "##refreshSubsNone")))
                refreshSubsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Character List --
        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("S9127EB7A3D10F5D6"), global::XASlave.Localization.UiLocale.FormatKey("S261FA25B9E69A2B0", chars.Count), "refreshSubsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        var refreshSubsRegionFilter = cfg.RefreshSubsRegionFilter;
        if (DrawRegionFilterCombo((global::XASlave.Localization.UiLocale.Native("SD3A008EF1335692D") + "##refreshSubsRegion"), ref refreshSubsRegionFilter))
        {
            cfg.RefreshSubsRegionFilter = refreshSubsRegionFilter;
            cfg.Save();
        }

        ImGui.SameLine();
        // Search filter
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##refreshSubsSearch", global::XASlave.Localization.UiLocale.Get("SF62AB4CE4380C1B9"), ref refreshSubsSearchFilter, 128);
        ImGui.Spacing();

        // Character table - columns: checkbox, #, character, world, retainers, submarines, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        using (var imguiScope262 = ImRaii.Table("RefreshSubsCharTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        if (imguiScope262)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S78AE647DC5544D22"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S3F91391122918036"), ImGuiTableColumnFlags.WidthFixed, Scale(65f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S358FD9EED262500E"), ImGuiTableColumnFlags.WidthFixed, Scale(75f));
            global::XASlave.Localization.UiNative.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(25f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

            // Build filtered list
            var filtered = new List<(int OrigIdx, string CharName, string World, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                if (!IsAutoRetainerCharacterAllowed(charName, cfg.RefreshSubsHonorArExclusions))
                    continue;
                var nameParts = charName.Split('@');
                var world = nameParts.Length > 1 ? nameParts[1] : "";
                var regionDc = WorldData.GetRegionDcLabel(world);

                if (!MatchesRegionFilter(world, cfg.RefreshSubsRegionFilter))
                    continue;

                if (!string.IsNullOrEmpty(refreshSubsSearchFilter) &&
                    !charName.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                    !world.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                    !regionDc.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                charInfo.TryGetValue(charName, out var info);
                filtered.Add((idx, charName, world, info));
            }

            // Sort
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty) sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var colIdx = spec.ColumnIndex;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        int cmp = colIdx switch
                        {
                            1 => a.OrigIdx.CompareTo(b.OrigIdx),
                            2 => string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase),
                            3 => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
                            4 => (a.Info?.RetainerCount ?? 0).CompareTo(b.Info?.RetainerCount ?? 0),
                            5 => (a.Info?.SubmarineCount ?? 0).CompareTo(b.Info?.SubmarineCount ?? 0),
                            _ => a.OrigIdx.CompareTo(b.OrigIdx),
                        };
                        return ascending ? cmp : -cmp;
                    });
                }
            }

            var displayIndex = 0;
            foreach (var (i, charName, world, info) in filtered)
            {
                displayIndex++;
                ImGui.TableNextRow();

                // Checkbox
                ImGui.TableNextColumn();
                var selected = refreshSubsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##rsSel{i}", ref selected))
                {
                    if (selected) refreshSubsSelectedIndices.Add(i);
                    else refreshSubsSelectedIndices.Remove(i);
                }

                // #
                ImGui.TableNextColumn();
                ImGui.Text(displayIndex.ToString());

                // Character
                ImGui.TableNextColumn();
                ImGui.Text(GetDisplayCharacterKey(charName, anonymizeCharacters));

                // World
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetDisplayWorldFromKey(charName, anonymizeCharacters));

                // Retainers
                ImGui.TableNextColumn();
                if (info != null && info.RetainerCount > 0)
                    ImGui.Text(info.RetainerCount.ToString());
                else
                    ImGui.TextDisabled("-");

                // Submarines
                ImGui.TableNextColumn();
                if (info != null && info.SubmarineCount > 0)
                    ImGui.Text(info.SubmarineCount.ToString());
                else
                    ImGui.TextDisabled("-");

                // Remove
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##rsRm{i}")))
                    {
                        chars.RemoveAt(i);
                        refreshSubsSelectedIndices.Remove(i);
                        var newSet = new HashSet<int>();
                        foreach (var idx in refreshSubsSelectedIndices)
                            newSet.Add(idx > i ? idx - 1 : idx);
                        refreshSubsSelectedIndices.Clear();
                        foreach (var idx in newSet) refreshSubsSelectedIndices.Add(idx);
                        cfg.Save();
                        break;
                    }
                }
            }


        }

        ImGui.Spacing();

        // Add character input
        ImGui.SetNextItemWidth(Scale(250f));
        var enterPressed = ImGui.InputTextWithHint("##refreshSubsAdd", global::XASlave.Localization.UiLocale.Get("SF32C73F5DCC35D82"), ref refreshSubsNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9FD728C66C9A256B") + "##refreshSubsAddBtn")) || enterPressed) && !string.IsNullOrWhiteSpace(refreshSubsNewChar))
        {
            var trimmed = refreshSubsNewChar.Trim();
            if (!chars.Contains(trimmed))
            {
                chars.Add(trimmed);
                cfg.Save();
            }
            refreshSubsNewChar = "";
        }
        ImGui.SameLine();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1B7CB3CFDA6217B9"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SD077709C2945CC78"));
        ImGui.Spacing();
        var refreshOptionsChanged = false;
        refreshOptionsChanged |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S53649CACF2611F56") + "##refreshSubsArmoury"), ref refreshSubsDoOpenArmoury);
        refreshOptionsChanged |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S04135E2D90F4F408") + "##refreshSubsSaddlebags"), ref refreshSubsDoOpenSaddlebags);
        refreshOptionsChanged |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF7B51D61398CF038") + "##refreshSubsJournal"), ref refreshSubsDoOpenJournal);
        refreshOptionsChanged |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S621D93AD01358374") + "##refreshSubsHome"), ref refreshSubsDoReturnToHome);
        refreshOptionsChanged |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SFF3C7116AEE8239B") + "##refreshSubsPlot"), ref refreshSubsDoCollectPersonalPlotInfo);
        if (refreshOptionsChanged)
            SaveRefreshSubsOptions();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SF5DB3C15B4DCD96C"));

        var completionBefore = (refreshSubsDoLogoutOnComplete, refreshSubsDoKillGameOnComplete, refreshSubsDoEnableArMulti);
        DrawSharedCompletionAndLogFooter("refreshSubs", "refreshSubs", ref refreshSubsDoLogoutOnComplete, ref refreshSubsDoKillGameOnComplete, ref refreshSubsDoEnableArMulti, ref refreshSubsShowLog, plugin.TaskRunner);
        if (completionBefore != (refreshSubsDoLogoutOnComplete, refreshSubsDoKillGameOnComplete, refreshSubsDoEnableArMulti))
            SaveRefreshSubsOptions();
    }

    private void SaveRefreshSubsOptions()
    {
        var config = plugin.Configuration;
        config.RefreshSubsGoWorkshop = refreshSubsGoWorkshop;
        config.RefreshSubsExtraWaitSeconds = refreshSubsExtraWait;
        config.RefreshSubsLogoutOnComplete = refreshSubsDoLogoutOnComplete;
        config.RefreshSubsKillGameOnComplete = refreshSubsDoKillGameOnComplete;
        config.RefreshSubsEnableArMultiOnComplete = refreshSubsDoEnableArMulti;
        config.RefreshSubsOpenArmoury = refreshSubsDoOpenArmoury;
        config.RefreshSubsOpenSaddlebags = refreshSubsDoOpenSaddlebags;
        config.RefreshSubsOpenJournal = refreshSubsDoOpenJournal;
        config.RefreshSubsReturnToHome = refreshSubsDoReturnToHome;
        config.RefreshSubsCollectPersonalPlotInfo = refreshSubsDoCollectPersonalPlotInfo;
        config.SaveDeferred();
    }

    private List<string> GetSelectedRefreshSubsCharacters()
    {
        var chars = plugin.Configuration.RefreshSubsCharacters;
        return refreshSubsSelectedIndices
            .Where(i => i >= 0 && i < chars.Count
                && IsAutoRetainerCharacterAllowed(chars[i], plugin.Configuration.RefreshSubsHonorArExclusions))
            .OrderBy(i => i)
            .Select(i => chars[i])
            .ToList();
    }

    private void SelectVisibleRefreshSubsCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.RefreshSubsCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            if (!IsAutoRetainerCharacterAllowed(charName, cfg.RefreshSubsHonorArExclusions))
                continue;
            var world = GetWorldFromKey(charName);
            var regionDc = WorldData.GetRegionDcLabel(world);
            if (!MatchesRegionFilter(world, cfg.RefreshSubsRegionFilter))
                continue;
            if (!string.IsNullOrEmpty(refreshSubsSearchFilter)
                && !charName.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !world.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !regionDc.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            refreshSubsSelectedIndices.Add(i);
        }
    }

    private void StartRefreshArSubsBell()
    {
        var selected = GetSelectedRefreshSubsCharacters();
        if (selected.Count == 0)
            return;

        var steps = BuildRefreshSubsBellSteps(selected, plugin.TaskRunner);

        if (!plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S85CC8B46B386EB84"), steps, onFinished: () =>
        {
            ReleaseRefreshSubsArSuppression();
        }, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }, totalItems: selected.Count, suppressLogoutCancel: true))
            return;

        HaltAutoCollectionForPriorityTask("Refresh Sub/Bell/Chest");
        refreshSubsArSuppressedByTask = false;
        reloggerRunList = new List<string>(selected);
    }

    private List<TaskStep> BuildRefreshSubsBellSteps(List<string> characters, TaskRunner runner)
    {
        var steps = new List<TaskStep>();

        // Disable AR Multi Mode
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S22C41FFCBA42741B"),
            OnEnter = () =>
            {
                runner.AddLog("Disabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SC4B6369ECA28F664"), 1.0f));

        for (int i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;

            // Label
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S5FB6F2415218C668", charIndex, charTotal, charName),
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"-- Processing {charName} ({charIndex}/{charTotal}) --");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog
            var relogState = new RefreshSubsRelogState();
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB8BF19049E887413", charName),
                OnEnter = () =>
                {
                    var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Already logged in as {charName}");
                        relogState.Ready = true;
                        return;
                    }
                    runner.AddLog($"Relogging to {charName}...");
                    ChatHelper.SendMessage($"/ays relog {charName}");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S27A359F38EA15525", charName), 2.0f));

            // Wait for relog
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S4B35E54CFF820E18", charName),
                IsComplete = () =>
                {
                    if (relogState.Ready) return true;
                    try
                    {
                        if (!Plugin.PlayerState.IsLoaded) return false;
                        if (!MonthlyReloggerTask.IsNamePlateReady() || !MonthlyReloggerTask.IsPlayerAvailable())
                            return false;
                        // Verify we actually landed on the intended character before running the
                        // downstream sequence; a silently-failed /ays relog would otherwise leave the
                        // previous character logged in and run everything against the wrong one.
                        return MonthlyReloggerTask.GetCurrentCharacterNameWorld()
                            .Equals(charName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                },
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    relogState.Failed = true;
                    runner.RecordFailedCharacter(charName);
                    runner.AddLog($"FAILED: Could not relog to {charName}. Leaving the character checked.");
                },
            });

            // Everything until the Complete step must run only on the correct character; the
            // downstream range is guarded behind relog success below.
            var downstreamStart = steps.Count;

            // SafeWait 3-pass
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("SFA31CB70A88EB43A", charName)))
            {
                steps.Add(sw);
            }

            // Extra wait
            if (refreshSubsExtraWait > 0)
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S9B1F3A1AEEAB099C", charName), refreshSubsExtraWait));

            AddRefreshSubsSelectedActionSteps(steps, runner, charName);
            AddRefreshSubsTeleportSteps(steps, runner, charName, "FC", "fc", 8f);
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SE27712F35A85C724", charName), 1.0f));

            // Enter workshop if configured
            if (refreshSubsGoWorkshop)
            {
                // Target and enter Additional Chambers
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SA029DF8EAB10D42F", charName),
                    OnEnter = () =>
                    {
                        runner.AddLog("Entering Additional Chambers (workshop)...");
                        ChatHelper.SendMessage("/target \"Entrance to Additional Chambers\"");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S56625C53EEDA2ECD", charName), 0.5f));

                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SDBC8EFAE9E7D120C", charName),
                    OnEnter = () =>
                    {
                        ChatHelper.SendMessage("/lockon on");
                        ChatHelper.SendMessage("/automove on");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SA75BDE33BDB47DCD", charName), 3.0f));

                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S4E533EC5CE78DB50", charName),
                    OnEnter = () =>
                    {
                        ChatHelper.SendMessage("/automove off");
                        AddonHelper.InteractWithTarget();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SED1F20FE005958E3", charName), 1.0f));

                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SCA89D606E8E98EC5", charName),
                    OnEnter = () =>
                    {
                        if (!AddonHelper.IsAddonReady("SelectString"))
                            return;

                        AddonHelper.SelectFirstAddonListText(
                            "SelectString",
                            out _,
                            out _,
                            ("Move to the company workshop", false));
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SFEE26FC99D0C965D", charName), 1.0f));

                // SafeWait after zone
                foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("SB0DDAC94B3144DF4", charName)))
                {
                    steps.Add(sw);
                }
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SCD99159A4AA6CAC5", charName), 1.0f));
            }

            if (refreshSubsGoWorkshop)
            {
                AddRefreshSubsSuppressArSteps(steps, runner, charName, "Sub/Bell");
                AddRefreshSubsSubConsoleSteps(steps, runner, charName);
                AddRefreshSubsBellSteps(steps, runner, charName);
                AddRefreshSubsFcChestSteps(steps, runner, charName);
                AddRefreshSubsResumeArSteps(steps, runner, charName, "sub/bell");
            }
            else
            {
                AddRefreshSubsSuppressArSteps(steps, runner, charName, "Bell");
                AddRefreshSubsBellSteps(steps, runner, charName);
                AddRefreshSubsResumeArSteps(steps, runner, charName, "bell");
            }

            // SafeWait before next
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("S8D6C1BCD252A00BA", charName)))
            {
                steps.Add(sw);
            }

            // Guard every downstream step behind relog success: if the relog failed (wrong
            // character or timed out), skip the teleport/workshop/sub/bell/chest actions instead of
            // running them against whoever is currently logged in.
            for (var s = downstreamStart; s < steps.Count; s++)
                steps[s] = MonthlyReloggerTask.WithSkip(steps[s], () => relogState.Failed);

            // Mark complete
            var capturedIndex = charIndex;
            var capturedName = charName;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB5063DC812CCDD2B", capturedName),
                OnEnter = () =>
                {
                    runner.CompletedItems = capturedIndex;
                    if (relogState.Failed)
                    {
                        runner.AddLog($"Skipped {capturedName} ({capturedIndex}/{charTotal}) - relog failed");
                        return;
                    }

                    runner.AddLog($"Finished {capturedName} ({capturedIndex}/{charTotal})");
                    UncheckRefreshSubsCharacter(capturedName, runner);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        // Summary
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SB74F3562768E1910"),
            OnEnter = () =>
            {
                if (!MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(refreshSubsDoLogoutOnComplete, refreshSubsDoKillGameOnComplete))
                    runner.SuppressLogoutCancel = false;
                if (runner.FailedCharacters.Count > 0)
                {
                    runner.AddLog($"Refresh Sub/Bell/Chest summary: {characters.Count - runner.FailedCharacters.Count}/{characters.Count} character(s) processed, {runner.FailedCharacters.Count} failed.");
                    return;
                }
                runner.AddLog($"══ SUMMARY: All {characters.Count} character(s) processed ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, refreshSubsDoLogoutOnComplete, refreshSubsDoKillGameOnComplete, refreshSubsDoEnableArMulti);

        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SB4678F98BF3E7AA8"), 1.0f));

        return steps;
    }

    private void AddRefreshSubsSuppressArSteps(List<TaskStep> steps, TaskRunner runner, string charName, string sequenceLabel)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S3C909C2DB19DF6B8", sequenceLabel, charName),
            OnEnter = () =>
            {
                var alreadySuppressed = false;
                try { alreadySuppressed = plugin.IpcClient.AutoRetainerGetSuppressed(); }
                catch { }

                if (alreadySuppressed)
                {
                    refreshSubsArSuppressedByTask = false;
                    runner.AddVerboseLog($"AutoRetainer was already suppressed before the manual {sequenceLabel.ToLowerInvariant()} sequence.");
                    return;
                }

                runner.AddLog($"Suppressing AutoRetainer so the manual {sequenceLabel.ToLowerInvariant()} interactions stay under XA Slave control...");
                plugin.IpcClient.AutoRetainerSetSuppressed(true);
                refreshSubsArSuppressedByTask = true;
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SE33D11F396469358", charName), 0.5f));
    }

    private void AddRefreshSubsSelectedActionSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        if (refreshSubsDoOpenArmoury)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S71231B7F19BDE9AE", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SF9960A327CD719B6", charName), 0.5f));
        }

        if (refreshSubsDoOpenSaddlebags)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SAD6733BD059FC5AC", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SDC9237CAB0509B21", charName), 1.0f));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S7612FEBE5DF538F5", charName),
                OnEnter = () => AddonHelper.CloseAddon("InventoryBuddy"),
                IsComplete = () => !AddonHelper.IsAddonVisible("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SD70C1D57F3E8A2F3", charName), 0.5f));
        }

        if (refreshSubsDoOpenJournal)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SE3B98044DD638E06", charName),
                OnEnter = () =>
                {
                    runner.AddLog("Opening Journal...");
                    ChatHelper.SendMessage("/journal");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("Journal"),
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SAB83714CDBAC5C52", charName), 0.5f));
            if (plugin.IpcClient.IsXaDatabaseAvailable())
            {
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S1BDA4991F1C6723A", charName),
                    ShouldSkip = () => !AddonHelper.IsAddonVisible("Journal"),
                    OnEnter = () =>
                    {
                        runner.AddLog("Saving Journal data to XA Database...");
                        if (plugin.SaveToXaDatabaseAndRecordSync())
                            runner.AddLog("Saved Journal data to XA Database.");
                        else
                            runner.AddLog("XA Database Journal save failed.");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SCD2E90E245B4863E", charName), 0.5f, () => !AddonHelper.IsAddonVisible("Journal")));
            }
        }

        var needsHomeLeg = refreshSubsDoReturnToHome || refreshSubsDoCollectPersonalPlotInfo;
        if (needsHomeLeg)
            AddRefreshSubsTeleportSteps(steps, runner, charName, "Home", "home", 5f);

        if (refreshSubsDoCollectPersonalPlotInfo)
            steps.AddRange(MonthlyReloggerTask.BuildCollectPersonalPlotInfoSteps(plugin, runner.AddLog));
    }

    private static void AddRefreshSubsTeleportSteps(List<TaskStep> steps, TaskRunner runner, string charName, string label, string command, float waitBusyTimeoutSec)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SB0C94D048DE7AB2A", label, charName),
            OnEnter = () =>
            {
                runner.AddLog($"Teleporting to {label} (/li {command})...");
                Plugin.Instance!.IpcClient.LifestreamExecuteCommand(command);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S7311FE0B9AC55A3A", label, charName), 1.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SC2AEB1C364FADCE2", label, charName),
            IsComplete = () =>
            {
                try { return Plugin.Instance!.IpcClient.LifestreamIsBusy(); }
                catch { return true; }
            },
            TimeoutSec = waitBusyTimeoutSec,
        });

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S0736ED50BA73BFEF", label, charName),
            IsComplete = () =>
            {
                try { return !Plugin.Instance!.IpcClient.LifestreamIsBusy(); }
                catch { return true; }
            },
            TimeoutSec = 60f,
        });

        var confirmCount = 0;
        DateTime lastCheck = DateTime.MinValue;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S9F8F68DB32FFE98C", label, charName),
            OnEnter = () =>
            {
                confirmCount = 0;
                lastCheck = DateTime.UtcNow;
            },
            IsComplete = () =>
            {
                try
                {
                    if ((DateTime.UtcNow - lastCheck).TotalSeconds < 1.0)
                        return false;

                    lastCheck = DateTime.UtcNow;
                    if (!Plugin.Instance!.IpcClient.LifestreamIsBusy())
                    {
                        confirmCount++;
                        return confirmCount >= 3;
                    }

                    confirmCount = 0;
                    return false;
                }
                catch
                {
                    return true;
                }
            },
            TimeoutSec = 15f,
        });

        foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("S66E20AD11B3CB01D", label, charName)))
            steps.Add(sw);

        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S8307AABB4D836DA7", label, charName), 1.0f));
    }

    private static void AddRefreshSubsAddonExitSequenceSteps(List<TaskStep> steps, TaskRunner runner, string charName, string label, string addonName, Func<bool>? shouldSkip = null)
    {
        AddRefreshSubsAddonCloseSteps(steps, runner, charName, $"{label} Close", addonName, shouldSkip);
        AddRefreshSubsGuardedSafeWaitSteps(steps, runner, charName, $"{label} SafeWait", addonName, shouldSkip);
        AddRefreshSubsAddonCloseSteps(steps, runner, charName, $"{label} Final Close Check", addonName, shouldSkip);
    }

    private static void AddRefreshSubsAddonCloseSteps(List<TaskStep> steps, TaskRunner runner, string charName, string label, string addonName, Func<bool>? shouldSkip = null)
    {
        DateTime lastCheck = DateTime.MinValue;
        var clearChecks = 0;
        var escAttempts = 0;

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SFB62FE2AE6B2AE6C", label, charName),
            ShouldSkip = shouldSkip,
            OnEnter = () =>
            {
                lastCheck = DateTime.UtcNow.AddSeconds(-1.0);
                clearChecks = 0;
                escAttempts = 0;

                if (!AddonHelper.IsAddonVisible(addonName))
                    return;

                escAttempts = 1;
                runner.AddVerboseLog($"{label}: '{addonName}' is open; pressing ESC.");
                KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
            },
            IsComplete = () =>
            {
                if ((DateTime.UtcNow - lastCheck).TotalSeconds < 1.0)
                    return false;

                lastCheck = DateTime.UtcNow;
                if (AddonHelper.IsAddonVisible(addonName))
                {
                    clearChecks = 0;
                    escAttempts++;
                    runner.AddVerboseLog($"{label}: '{addonName}' still open after ESC attempt {escAttempts - 1}; pressing ESC again.");
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                    return false;
                }

                clearChecks++;
                return clearChecks >= 2;
            },
            TimeoutSec = 20f,
            OnTimeout = () => runner.AddLog($"{label}: timed out waiting for '{addonName}' to close; continuing."),
        });
    }

    private static void AddRefreshSubsGuardedSafeWaitSteps(List<TaskStep> steps, TaskRunner runner, string charName, string label, string addonName, Func<bool>? shouldSkip = null)
    {
        for (int pass = 1; pass <= 3; pass++)
        {
            var capturedPass = pass;
            DateTime lastEscAt = DateTime.MinValue;

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S7C50286C95DC8AB3", label, capturedPass, charName),
                ShouldSkip = shouldSkip,
                OnEnter = () =>
                {
                    lastEscAt = DateTime.UtcNow.AddSeconds(-1.0);
                },
                IsComplete = () =>
                {
                    if (CharacterSafetyHelper.IsCharacterSafeWaitReady())
                        return true;

                    if (!AddonHelper.IsAddonVisible(addonName))
                        return false;

                    if ((DateTime.UtcNow - lastEscAt).TotalSeconds < 1.0)
                        return false;

                    lastEscAt = DateTime.UtcNow;
                    runner.AddVerboseLog($"{label}: '{addonName}' is still visible during CharacterSafeWait pass {capturedPass}/3; pressing ESC again.");
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                    return false;
                },
                TimeoutSec = 15f,
                OnTimeout = () =>
                {
                    if (AddonHelper.IsAddonVisible(addonName))
                    {
                        runner.AddLog($"{label}: CharacterSafeWait pass {capturedPass}/3 timed out while '{addonName}' was still open; pressing ESC and continuing.");
                        KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                    }
                },
            });

            if (capturedPass < 3)
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S32A44A6DA5D18FA7", label, charName), 1.0f));
        }
    }

    private static void AddRefreshSubsSubConsoleSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        var voyageLogSelected = false;

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S1BC2D2D4CBE7759D", charName),
            OnEnter = () =>
            {
                runner.AddLog("Targeting Voyage Control Panel...");
                ChatHelper.SendMessage("/target \"Voyage Control Panel\"");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S20FB99DCAB571EDB", charName), 0.5f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SC898FDC4DA9206B3", charName),
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/lockon on");
                ChatHelper.SendMessage("/automove on");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SCD95B10651E06BE0", charName), 1.5f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SBDF73576DE1E2828", charName),
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/automove off");
                AddonHelper.InteractWithTarget();
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S3E848BFAB5936F24", charName), 1.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SEED8BD8210AD10FE", charName),
            OnEnter = () =>
            {
                voyageLogSelected = AddonHelper.SelectFirstAddonListText(
                    "SelectString",
                    out _,
                    out var matchedText,
                    RefreshPreviousVoyageLogEntries);
                if (voyageLogSelected)
                    runner.AddLog($"Selected voyage menu entry by text: {matchedText}");
            },
            IsComplete = () => voyageLogSelected &&
                               (!AddonHelper.IsAddonReady("SelectString") || AddonHelper.IsAddonVisible("AirShipExplorationResult")),
            TimeoutSec = 3f,
            MaxRetries = 1,
            OnTimeout = () => runner.AddLog("Could not resolve 'View previous voyage log' in the Voyage Control Panel; no numeric fallback was used."),
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S7760953BFBDCAE4F", charName), 1.0f));

        AddRefreshSubsAddonExitSequenceSteps(steps, runner, charName, "Sub Console Recovery", "SelectString");
    }

    private static void AddRefreshSubsBellSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SD5108833D1C5B222", charName),
            OnEnter = () =>
            {
                runner.AddLog("Targeting Summoning Bell...");
                ChatHelper.SendMessage("/target \"Summoning Bell\"");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S59BF5F30FD647B96", charName), 0.5f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S4D85060965AE916F", charName),
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/lockon on");
                ChatHelper.SendMessage("/automove on");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S0734A70A4D78AE76", charName), 3.0f));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S07871353BFF510F3", charName),
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/automove off");
                AddonHelper.InteractWithTarget();
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S8B374EDDC947F736", charName), 2.0f));

        AddRefreshSubsAddonExitSequenceSteps(steps, runner, charName, "Retainer List Recovery", "RetainerList");
    }

    private static void AddRefreshSubsFcChestSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        var skipFcChest = false;
        var fcChestRecoveryPhase = 0;
        var fcChestRecoveryAttempts = 0;
        var fcChestRecoveryPhaseStartedAt = DateTime.MinValue;
        const int maxFcChestRecoveryAttempts = 2;
        const float fcChestRecoveryMoveSeconds = 0.5f;
        const float fcChestRecoveryResetDelaySeconds = 0.25f;
        const float fcChestRecoveryRetrySettleSeconds = 0.35f;

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S2FB2F946D04C7284", charName),
            OnEnter = () =>
            {
                var plugin = Plugin.Instance!;
                var zoneName = AddonHelper.GetCurrentZoneName();
                if (!plugin.IpcClient.IsXaDatabaseAvailable())
                {
                    runner.AddLog("XA Database not available - skipping FC chest gil capture.");
                    skipFcChest = true;
                }
                else if (!AddonHelper.IsInWorkshop())
                {
                    runner.AddLog($"Current zone '{zoneName}' does not match Company Workshop - skipping FC chest gil capture.");
                    skipFcChest = true;
                }
                else if (!plugin.IpcClient.VnavIsReady())
                {
                    runner.AddLog("vnav not available - skipping FC chest gil capture.");
                    skipFcChest = true;
                }
                else
                {
                    runner.AddLog($"Current zone '{zoneName}' matches Company Workshop - checking FC chest for gil...");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S27674B92CCEB4A8A", charName),
            ShouldSkip = () => skipFcChest,
            OnEnter = () =>
            {
                runner.AddLog("Targeting Company Chest...");
                AddonHelper.TargetByName("Company Chest");
            },
            IsComplete = () => AddonHelper.CurrentTargetMatches("Company Chest"),
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SCD9DC7C9DE85F388", charName), 0.5f, () => skipFcChest));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SF318271AE72842F8", charName),
            ShouldSkip = () => skipFcChest,
            OnEnter = () =>
            {
                runner.AddLog("Pathing to Company Chest (1.5y stop)...");
                AddonHelper.TryPathToCurrentTarget(1.5f);
            },
            IsComplete = () => AddonHelper.IsCurrentTargetWithinStopDistanceAndStopped("Company Chest", 1.5f),
            TimeoutSec = 20f,
            OnTimeout = () =>
            {
                runner.AddLog("FC chest pathing timed out; skipping FC chest gil capture.");
                skipFcChest = true;
                Plugin.Instance!.IpcClient.VnavStop();
            },
        });

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SFE5A75FAE3FF34C6", charName),
            ShouldSkip = () => skipFcChest,
            OnEnter = () =>
            {
                fcChestRecoveryPhase = 0;
                fcChestRecoveryAttempts = 0;
                fcChestRecoveryPhaseStartedAt = DateTime.MinValue;
                runner.AddLog("Interacting with Company Chest...");
                AddonHelper.DismissTextError();
                AddonHelper.InteractWithTarget();
            },
            IsComplete = () =>
            {
                if (AddonHelper.IsAddonVisible("FreeCompanyChest"))
                    return true;

                var now = DateTime.UtcNow;

                if (fcChestRecoveryPhase == 1)
                {
                    if ((now - fcChestRecoveryPhaseStartedAt).TotalSeconds >= fcChestRecoveryMoveSeconds)
                    {
                        Plugin.Instance!.IpcClient.VnavStop();
                        runner.AddLog("Company Chest recovery: stopping brief re-path and resetting camera.");
                        AddonHelper.ResetCamera();
                        AddonHelper.DismissTextError();
                        fcChestRecoveryPhase = 2;
                        fcChestRecoveryPhaseStartedAt = now;
                    }

                    return false;
                }

                if (fcChestRecoveryPhase == 2)
                {
                    if ((now - fcChestRecoveryPhaseStartedAt).TotalSeconds >= fcChestRecoveryResetDelaySeconds)
                    {
                        runner.AddLog("Retrying Company Chest interaction after reset camera...");
                        AddonHelper.TargetByName("Company Chest");
                        AddonHelper.DismissTextError();
                        AddonHelper.InteractWithTarget();
                        fcChestRecoveryPhase = 3;
                        fcChestRecoveryPhaseStartedAt = now;
                    }

                    return false;
                }

                if (fcChestRecoveryPhase == 3)
                {
                    if ((now - fcChestRecoveryPhaseStartedAt).TotalSeconds < fcChestRecoveryRetrySettleSeconds)
                        return false;

                    fcChestRecoveryPhase = 0;
                }

                if (AddonHelper.TryGetCannotSeeTargetTextError(out var matchedText))
                {
                    if (fcChestRecoveryAttempts >= maxFcChestRecoveryAttempts)
                    {
                        runner.AddLog($"Company Chest interaction still reports _TextError '{matchedText}' after {maxFcChestRecoveryAttempts} recovery attempts - skipping FC chest gil capture.");
                        AddonHelper.DismissTextError();
                        skipFcChest = true;
                        return true;
                    }

                    fcChestRecoveryAttempts++;
                    runner.AddLog($"Company Chest interaction reported _TextError '{matchedText}' - re-pathing for 0.5s, stopping vnav, and resetting camera ({fcChestRecoveryAttempts}/{maxFcChestRecoveryAttempts}).");
                    AddonHelper.DismissTextError();
                    AddonHelper.TryPathToCurrentTarget(1.5f);
                    fcChestRecoveryPhase = 1;
                    fcChestRecoveryPhaseStartedAt = now;
                    return false;
                }

                return false;
            },
            TimeoutSec = 12f,
            OnTimeout = () =>
            {
                runner.AddLog("FreeCompanyChest did not open after Company Chest interaction/recovery; skipping FC chest gil capture.");
                skipFcChest = true;
            },
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S4941D6F40191541A", charName), 0.5f, () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest")));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SA5714A8AADF0A795", charName),
            ShouldSkip = () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest"),
            OnEnter = () =>
            {
                runner.AddLog("Saving FC chest gil to XA Database...");
                if (Plugin.Instance!.SaveToXaDatabaseAndRecordSync())
                    runner.AddLog("Saved FC chest gil to XA Database.");
                else
                    runner.AddLog("XA Database FC chest save failed.");
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SB2C97B2449185F3E", charName), 0.5f, () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest")));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S2856F1B1C292A641", charName),
            ShouldSkip = () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest"),
            OnEnter = () => runner.AddLog("Closing FC chest window..."),
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        AddRefreshSubsAddonExitSequenceSteps(
            steps,
            runner,
            charName,
            "FC Chest Recovery",
            "FreeCompanyChest",
            () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest"));
    }

    private void AddRefreshSubsResumeArSteps(List<TaskStep> steps, TaskRunner runner, string charName, string sequenceLabel)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S2BEEE73ADB42C3F3", sequenceLabel, charName),
            OnEnter = () =>
            {
                if (!refreshSubsArSuppressedByTask)
                {
                    runner.AddVerboseLog($"Refresh Sub/Bell/Chest did not own AutoRetainer suppression for this {sequenceLabel} sequence; leaving it unchanged.");
                    return;
                }

                runner.AddLog($"Resuming AutoRetainer after the manual {sequenceLabel} sequence...");
                plugin.IpcClient.AutoRetainerSetSuppressed(false);
                refreshSubsArSuppressedByTask = false;
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S4BCBB507CD7B86B7", charName), 0.5f));
    }

    private void UncheckRefreshSubsCharacter(string characterName, TaskRunner runner)
    {
        refreshSubsCompletedCharacterQueue.Enqueue(characterName);
    }

    private class RefreshSubsRelogState
    {
        public bool Ready;
        public bool Failed;
    }
}
