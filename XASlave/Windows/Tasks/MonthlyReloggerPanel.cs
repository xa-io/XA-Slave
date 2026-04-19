using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Monthly Relogger task panel — partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    private const int ReloggerStaleSliderMaxValue = 45;

    // ── Monthly Relogger state ──
    private MonthlyReloggerTask? reloggerTask;
    private readonly HashSet<int> reloggerSelectedIndices = new();
    private int reloggerStaleSelectDaysInput = -1;
    private string reloggerAddInput = string.Empty;
    private string reloggerSearchFilter = string.Empty;
    private bool reloggerShowLog;
    private List<string> reloggerRunList = new(); // full ordered char list for current run

    private string arImportStatus = string.Empty;
    private DateTime arImportStatusExpiry = DateTime.MinValue;

    // ───────────────────────────────────────────────
    //  Task: Monthly Relogger
    // ───────────────────────────────────────────────
    private void DrawMonthlyReloggerTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var chars = cfg.ReloggerCharacters;
        var configuredStaleSelectDays = Math.Clamp(cfg.ReloggerStaleSelectDays, 1, ReloggerStaleSliderMaxValue);
        if (configuredStaleSelectDays != cfg.ReloggerStaleSelectDays)
        {
            cfg.ReloggerStaleSelectDays = configuredStaleSelectDays;
            cfg.Save();
        }

        if (reloggerStaleSelectDaysInput < 1 || reloggerStaleSelectDaysInput > ReloggerStaleSliderMaxValue)
            reloggerStaleSelectDaysInput = configuredStaleSelectDays;

        var staleSelectDays = reloggerStaleSelectDaysInput;

        // ── Title + Description ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Monthly Relogger");
        ImGui.TextDisabled("Rotate through characters, executing actions on each.");
        ImGui.TextDisabled("This is used to ensure you do not lose FC Master Rank or Plots.");
        ImGui.TextDisabled("Requires Lifestream Path To Door, and Enter House in Lifestream settings, to work correctly.");
        ImGui.Spacing();

        // ── Import / Refresh buttons ──
        ImGui.TextDisabled("Press buttons in order: Import from AutoRetainer → Refresh AR Data → Pull XA Database Info.");
        ImGui.Spacing();

        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (ImGui.Button("Import from AutoRetainer"))
            ImportFromAutoRetainer();
        if (!arConfigExists) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(arConfigExists
                ? "Read AutoRetainer's DefaultConfig.json to import all characters.\nPath: " + plugin.ArConfigReader.GetAutoRetainerConfigPath()
                : "AutoRetainer config not found.\nExpected: " + plugin.ArConfigReader.GetAutoRetainerConfigPath());

        ImGui.SameLine();
        if (ImGui.Button("Refresh AR Data"))
            RefreshArCharacterCache();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reload character details (Lv, Gil, FC) from AutoRetainer config without adding/removing characters.");

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        if (!xaDbAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Pull XA Database Info"))
            PullXaDatabaseInfo();
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? "Read XA Database to update Lv, Gil, FC, Last Logged In for all characters.\nPulls directly from the SQLite database."
                : "XA Database plugin not available.");

        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), arImportStatus);
        }


        // ── Plugin Status ──
        DrawPluginStatusChecker();

        ImGui.Separator();
        ImGui.Spacing();

        // ── Run controls ──
        if (runner.IsRunning && runner.CurrentTaskName == "Monthly Relogger")
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {runner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);

            DrawProcessingList(runner);

            ImGui.Spacing();
            if (ImGui.Button("Cancel Relogger"))
                runner.Cancel();
        }
        else
        {
            var selectedChars = GetSelectedReloggerCharacters();
            var ipc = plugin.IpcClient;
            var requiresXaDb = plugin.Configuration.ReloggerDoCollectPersonalPlotInfo || plugin.Configuration.ReloggerDoParseForXaDatabase;
            var allRequiredPluginsOk = ipc.IsAutoRetainerAvailable()
                && ipc.IsLifestreamAvailable()
                && ipc.IsTextAdvanceAvailable()
                && ipc.IsVnavAvailable()
                && (!requiresXaDb || ipc.IsXaDatabaseAvailable());
            var canStart = selectedChars.Count > 0 && allRequiredPluginsOk;

            var started = DrawPriorityTaskActionButton(
                SlaveTask.MonthlyRelogger,
                $"Start Relogger ({selectedChars.Count} chars)",
                canStart,
                () => StartMonthlyRelogger(selectedChars),
                !allRequiredPluginsOk
                    ? "Missing required plugins. Check the Plugin Status section above."
                    : "Select at least one character to start.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref reloggerShowLog);

            ImGui.SameLine();
            if (ImGui.Button("Check All"))
                SelectVisibleReloggerCharacters(_ => true);
            ImGui.SameLine();
            if (ImGui.Button("Clear All"))
                reloggerSelectedIndices.Clear();

            if (ImGui.Button("Check Masters"))
                SelectVisibleReloggerCharacters(IsFcMasterRank);
            ImGui.SameLine();
            if (ImGui.Button("Check Personal"))
                SelectVisibleReloggerCharacters(HasPersonalEstate);

            var staleButtonLabel = $"Select Stale ({GetReloggerStaleButtonLabel(staleSelectDays)})";
            var staleButtonWidth = ImGui.CalcTextSize("Select Stale (>45d)").X + ImGui.GetStyle().FramePadding.X * 2f;
            if (ImGui.Button(staleButtonLabel, new Vector2(staleButtonWidth, 0f)))
                SelectReloggerStaleCharacters(staleSelectDays);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Select all characters with Last Logged In {GetReloggerStaleTooltipText(staleSelectDays)} (or never logged in).");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(Scale(170f));
            if (ImGui.SliderInt("##ReloggerStaleDays", ref staleSelectDays, 1, ReloggerStaleSliderMaxValue, "%d"))
                reloggerStaleSelectDaysInput = staleSelectDays;
            if (ImGui.IsItemDeactivatedAfterEdit() && cfg.ReloggerStaleSelectDays != reloggerStaleSelectDaysInput)
            {
                cfg.ReloggerStaleSelectDays = reloggerStaleSelectDaysInput;
                cfg.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Set the stale-day threshold used by the stale mass-select button. The maximum slider value is 45, which selects characters with Last Logged In over 45 days ago.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character List ──
        DrawCharacterListHeader("Character List", $"({chars.Count} total)", "reloggerAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        // Region filter
        var reloggerRegionFilter = cfg.ReloggerRegionFilter;
        if (DrawRegionFilterCombo("Region##RelogFilter", ref reloggerRegionFilter))
        {
            cfg.ReloggerRegionFilter = reloggerRegionFilter;
            cfg.Save();
        }

        // Search filter
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##RelogSearch", "Search name or world...", ref reloggerSearchFilter, 128);

        ImGui.Spacing();

        // Character table — always shows all columns from persistent ReloggerCharacterInfo
        // Columns: checkbox, #, character, region, lv, gil, current rank, fc, in fc, personal, last logged in, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        if (ImGui.BeginTable("ReloggerCharTable", 12,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f)); // checkbox
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(30f));
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Region / DC", ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, Scale(30f));
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Current Rank", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupColumn("In FC", ImGuiTableColumnFlags.WidthFixed, Scale(45f));
            ImGui.TableSetupColumn("Personal", ImGuiTableColumnFlags.WidthFixed, Scale(55f));
            ImGui.TableSetupColumn("Last Logged In", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(25f)); // remove
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            // Build filtered list using persistent data
            var filtered = new List<(int OrigIdx, string CharName, string World, string RegionDc, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                var nameParts = charName.Split('@');
                var world = nameParts.Length > 1 ? nameParts[1] : "";
                var regionDc = WorldData.GetRegionDcLabel(world);

                if (!MatchesRegionFilter(world, cfg.ReloggerRegionFilter))
                    continue;
                if (!string.IsNullOrEmpty(reloggerSearchFilter) &&
                    !charName.Contains(reloggerSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                    !regionDc.Contains(reloggerSearchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                charInfo.TryGetValue(charName, out var info);
                filtered.Add((idx, charName, world, regionDc, info));
            }

            // Sort based on ImGui sort specs
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
                sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var colIdx = spec.ColumnIndex;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        int cmp = 0;
                        // 0=checkbox, 1=#, 2=char, 3=region, 4=lv, 5=gil, 6=current rank, 7=fc, 8=infc, 9=personal, 10=lastloggedin, 11=remove
                        switch (colIdx)
                        {
                            case 1: cmp = a.OrigIdx.CompareTo(b.OrigIdx); break;
                            case 2: cmp = string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase); break;
                            case 3: cmp = string.Compare(WorldData.GetSortKey(a.World), WorldData.GetSortKey(b.World), StringComparison.Ordinal); break;
                            case 4: cmp = (a.Info?.HighestLevel ?? 0).CompareTo(b.Info?.HighestLevel ?? 0); break;
                            case 5: cmp = (a.Info?.Gil ?? 0).CompareTo(b.Info?.Gil ?? 0); break;
                            case 6:
                                cmp = GetCurrentRankSortValue(a.Info).CompareTo(GetCurrentRankSortValue(b.Info));
                                if (cmp == 0)
                                    cmp = string.Compare(GetFcMemberRankLabel(a.Info), GetFcMemberRankLabel(b.Info), StringComparison.OrdinalIgnoreCase);
                                break;
                            case 7: cmp = string.Compare(a.Info?.FcName ?? "", b.Info?.FcName ?? "", StringComparison.OrdinalIgnoreCase); break;
                            case 8:
                                var aFc = a.Info != null && a.Info.FCID != 0;
                                var bFc = b.Info != null && b.Info.FCID != 0;
                                cmp = aFc.CompareTo(bFc); break;
                            case 9:
                                cmp = HasPersonalEstate(a.Info).CompareTo(HasPersonalEstate(b.Info)); break;
                            case 10:
                                var aLi = a.Info?.LastLoggedIn ?? DateTime.MinValue;
                                var bLi = b.Info?.LastLoggedIn ?? DateTime.MinValue;
                                cmp = aLi.CompareTo(bLi); break;
                            default: cmp = a.OrigIdx.CompareTo(b.OrigIdx); break;
                        }

                        if (cmp == 0)
                        {
                            cmp = string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase);
                            if (cmp == 0)
                                cmp = a.OrigIdx.CompareTo(b.OrigIdx);
                        }

                        return ascending ? cmp : -cmp;
                    });
                }
            }

            var displayIndex = 0;
            foreach (var (i, charName, world, regionDc, info) in filtered)
            {
                displayIndex++;
                ImGui.TableNextRow();

                // Checkbox
                ImGui.TableNextColumn();
                var selected = reloggerSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##sel{i}", ref selected))
                {
                    if (selected) reloggerSelectedIndices.Add(i);
                    else reloggerSelectedIndices.Remove(i);
                }

                // Index
                ImGui.TableNextColumn();
                ImGui.Text(displayIndex.ToString());

                // Name
                ImGui.TableNextColumn();
                var displayCharName = GetDisplayCharacterKey(charName, anonymizeCharacters);
                ImGui.Text(displayCharName);
                if (info != null && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(displayCharName);
                    ImGui.Separator();
                    ImGui.Text($"Level: {info.HighestLevel}");
                    ImGui.Text($"Gil: {info.Gil:N0}");
                    var rankLabel = GetFcMemberRankLabel(info);
                    if (!string.IsNullOrEmpty(rankLabel))
                        ImGui.Text($"Current Rank: {rankLabel}");
                    if (!string.IsNullOrEmpty(info.FcName))
                        ImGui.Text($"FC: {info.FcName}");
                    if (!string.IsNullOrWhiteSpace(info.PersonalEstate))
                        ImGui.Text($"Personal Estate: {info.PersonalEstate}");
                    if (info.LastLoggedIn != DateTime.MinValue)
                        ImGui.Text($"Last Logged In: {info.LastLoggedIn.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    ImGui.EndTooltip();
                }

                // Region / DC
                ImGui.TableNextColumn();
                ImGui.TextDisabled(regionDc);

                // Level
                ImGui.TableNextColumn();
                if (info != null && info.HighestLevel > 0)
                    ImGui.Text(info.HighestLevel.ToString());
                else
                    ImGui.TextDisabled("-");

                // Gil
                ImGui.TableNextColumn();
                if (info != null && info.Gil > 0)
                    ImGui.Text($"{info.Gil:N0}");
                else
                    ImGui.TextDisabled("-");

                // Current Rank
                ImGui.TableNextColumn();
                var currentRankLabel = GetFcMemberRankLabel(info);
                if (!string.IsNullOrEmpty(currentRankLabel))
                    ImGui.TextDisabled(currentRankLabel);
                else
                    ImGui.TextDisabled("-");

                // FC
                ImGui.TableNextColumn();
                if (info != null && !string.IsNullOrEmpty(info.FcName))
                    ImGui.TextDisabled(info.FcName);
                else
                    ImGui.TextDisabled("-");

                // In FC
                ImGui.TableNextColumn();
                if (info != null && info.FCID != 0)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Yes");
                else
                    ImGui.TextDisabled("-");

                // Personal
                ImGui.TableNextColumn();
                if (HasPersonalEstate(info))
                {
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Yes");
                    if (ImGui.IsItemHovered() && info != null)
                        ImGui.SetTooltip(info.PersonalEstate);
                }
                else
                {
                    ImGui.TextDisabled("-");
                }

                // Last Logged In
                ImGui.TableNextColumn();
                if (info != null && info.LastLoggedIn != DateTime.MinValue)
                {
                    var days = (int)(DateTime.UtcNow - info.LastLoggedIn).TotalDays;
                    var label = days == 0 ? "Today" : days == 1 ? "1 day" : $"{days} days";
                    if (days > 30)
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), label);
                    else if (days > 7)
                        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), label);
                    else
                        ImGui.TextDisabled(label);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Last logged in: {info.LastLoggedIn.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                }
                else
                    ImGui.TextDisabled("\u2014");

                // Remove button
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##rm{i}"))
                {
                    chars.RemoveAt(i);
                    reloggerSelectedIndices.Remove(i);
                    var newSet = new HashSet<int>();
                    foreach (var idx in reloggerSelectedIndices)
                        newSet.Add(idx > i ? idx - 1 : idx);
                    reloggerSelectedIndices.Clear();
                    foreach (var idx in newSet) reloggerSelectedIndices.Add(idx);
                    cfg.Save();
                    ImGui.PopStyleColor();
                    break;
                }
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // Add character input
        ImGui.SetNextItemWidth(Scale(250f));
        var enterPressed = ImGui.InputTextWithHint("##RelogAdd", "Name Surname@World", ref reloggerAddInput, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add") || enterPressed) && !string.IsNullOrWhiteSpace(reloggerAddInput))
        {
            var trimmed = reloggerAddInput.Trim();
            if (!chars.Contains(trimmed))
            {
                chars.Add(trimmed);
                cfg.Save();
            }
            reloggerAddInput = string.Empty;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Format: FirstName LastName@World");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Per-Character Action Config ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Actions Per Character");
        ImGui.Spacing();

        var changed = false;
        var v1 = cfg.ReloggerDoTextAdvance;
        var v2 = cfg.ReloggerDoRemoveSprout;
        var v3 = cfg.ReloggerDoOpenInventory;
        var v4 = cfg.ReloggerDoOpenArmouryChest;
        var v5 = cfg.ReloggerDoOpenSaddlebags;
        var v6 = cfg.ReloggerDoOpenJournal;
        var v7 = cfg.ReloggerDoReturnToHome;
        var v8 = cfg.ReloggerDoCollectPersonalPlotInfo;
        var v9 = cfg.ReloggerDoReturnToFc;
        var v10 = cfg.ReloggerDoParseForXaDatabase;
        var v11 = cfg.ReloggerDoLogoutOnComplete;
        var v12 = cfg.ReloggerDoKillGameOnComplete;
        var v13 = cfg.ReloggerDoEnableArMultiOnComplete;

        changed |= ImGui.Checkbox("Enable TextAdvance (/at y)", ref v1);
        changed |= ImGui.Checkbox("Remove Sprout (/nastatus off)", ref v2);
        changed |= ImGui.Checkbox("Open Inventory", ref v3);
        changed |= ImGui.Checkbox("Open Armoury Chest", ref v4);
        changed |= ImGui.Checkbox("Open Saddlebags", ref v5);
        changed |= ImGui.Checkbox("Open Journal", ref v6);
        changed |= ImGui.Checkbox("Teleport Home (Lifestream)", ref v7);
        changed |= ImGui.Checkbox("Collect Personal Plot Info (/housing -> signboard -> save)", ref v8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens the housing menu, checks Apartment, Private Estate, and Shared Estate signboards when available,\nand triggers XA Database saves after each housing pass.");
        changed |= ImGui.Checkbox("Teleport FC (Lifestream)", ref v9);
        changed |= ImGui.Checkbox("Parse for XA Database (FC window + save)", ref v10);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens FC window to collect FC data (name, members, points, plot),\nthen saves all collected data to XA Database.");
        changed |= DrawSharedCompletionAndLogFooter("relogger", "relogger", ref v11, ref v12, ref v13, ref reloggerShowLog, runner);

        if (changed)
        {
            cfg.ReloggerDoTextAdvance = v1;
            cfg.ReloggerDoRemoveSprout = v2;
            cfg.ReloggerDoOpenInventory = v3;
            cfg.ReloggerDoOpenArmouryChest = v4;
            cfg.ReloggerDoOpenSaddlebags = v5;
            cfg.ReloggerDoOpenJournal = v6;
            cfg.ReloggerDoReturnToHome = v7;
            cfg.ReloggerDoCollectPersonalPlotInfo = v8;
            cfg.ReloggerDoReturnToFc = v9;
            cfg.ReloggerDoParseForXaDatabase = v10;
            cfg.ReloggerDoLogoutOnComplete = v11;
            cfg.ReloggerDoKillGameOnComplete = v12;
            cfg.ReloggerDoEnableArMultiOnComplete = v13;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Log output ──
        // Status display
        if (runner.CurrentTaskName == "Monthly Relogger" && runner.StatusText == "Complete")
        {
            ImGui.Spacing();
            if (runner.FailedCharacters.Count > 0)
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Relogger completed with {runner.FailedCharacters.Count} failed character(s).");
            else
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Relogger completed successfully.");
        }
    }

    /// <summary>Get the selected character names in sorted order.</summary>
    private List<string> GetSelectedReloggerCharacters()
    {
        var chars = plugin.Configuration.ReloggerCharacters;
        return reloggerSelectedIndices
            .Where(i => i >= 0 && i < chars.Count)
            .OrderBy(i =>
            {
                var parts = chars[i].Split('@');
                var world = parts.Length > 1 ? parts[1] : "";
                return WorldData.GetSortKey(world);
            })
            .Select(i => chars[i])
            .ToList();
    }

    private void SelectReloggerStaleCharacters(int staleDays)
    {
        var cfg = plugin.Configuration;
        var chars = cfg.ReloggerCharacters;

        for (int i = 0; i < chars.Count; i++)
        {
            var cn = chars[i];
            cfg.ReloggerCharacterInfo.TryGetValue(cn, out var info);
            if (info == null || info.LastLoggedIn == default)
            {
                reloggerSelectedIndices.Add(i);
                continue;
            }

            if ((DateTime.UtcNow - info.LastLoggedIn).TotalDays > staleDays)
                reloggerSelectedIndices.Add(i);
        }
    }

    private void SelectVisibleReloggerCharacters(Func<ReloggerCharacterData?, bool> predicate)
    {
        var cfg = plugin.Configuration;
        var chars = cfg.ReloggerCharacters;
        for (int i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            var world = GetWorldFromKey(charName);
            var regionDc = WorldData.GetRegionDcLabel(world);
            if (!MatchesRegionFilter(world, cfg.ReloggerRegionFilter))
                continue;
            if (!string.IsNullOrEmpty(reloggerSearchFilter)
                && !charName.Contains(reloggerSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !regionDc.Contains(reloggerSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            cfg.ReloggerCharacterInfo.TryGetValue(charName, out var info);
            if (predicate(info))
                reloggerSelectedIndices.Add(i);
        }
    }

    private static string GetReloggerStaleButtonLabel(int staleDays)
    {
        return $">{staleDays}d";
    }

    private static string GetReloggerStaleTooltipText(int staleDays)
    {
        return $"over {staleDays} days ago";
    }

    /// <summary>Start the Monthly Relogger task with the given character list.</summary>
    private void StartMonthlyRelogger(List<string> characters)
    {
        HaltAutoCollectionForPriorityTask("Monthly Relogger");

        reloggerTask = new MonthlyReloggerTask(plugin)
        {
            DoEnableTextAdvance = plugin.Configuration.ReloggerDoTextAdvance,
            DoRemoveSprout = plugin.Configuration.ReloggerDoRemoveSprout,
            DoOpenInventory = plugin.Configuration.ReloggerDoOpenInventory,
            DoOpenArmouryChest = plugin.Configuration.ReloggerDoOpenArmouryChest,
            DoOpenSaddlebags = plugin.Configuration.ReloggerDoOpenSaddlebags,
            DoOpenJournal = plugin.Configuration.ReloggerDoOpenJournal,
            DoReturnToHome = plugin.Configuration.ReloggerDoReturnToHome,
            DoCollectPersonalPlotInfo = plugin.Configuration.ReloggerDoCollectPersonalPlotInfo,
            DoReturnToFc = plugin.Configuration.ReloggerDoReturnToFc,
            DoParseForXaDatabase = plugin.Configuration.ReloggerDoParseForXaDatabase,
            DoLogoutOnComplete = plugin.Configuration.ReloggerDoLogoutOnComplete,
            DoKillGameOnComplete = plugin.Configuration.ReloggerDoKillGameOnComplete,
            DoEnableArMultiOnComplete = plugin.Configuration.ReloggerDoEnableArMultiOnComplete,
        };

        // Store the full ordered list for UI display during the run
        reloggerRunList = new List<string>(characters);

        var steps = reloggerTask.BuildSteps(characters, plugin.TaskRunner, onCharacterCompleted: (charName) =>
        {
            // Deselect by finding the index of charName in the full list and removing from selected set
            var allChars = plugin.Configuration.ReloggerCharacters;
            var idx = allChars.IndexOf(charName);
            if (idx >= 0)
                reloggerSelectedIndices.Remove(idx);
        });
        plugin.TaskRunner.Start("Monthly Relogger", steps);
        AutoOpenTaskLogIfVerbose(ref reloggerShowLog);
    }

    /// <summary>
    /// Import all characters from AutoRetainer's DefaultConfig.json.
    /// Reads OfflineData entries and adds them as "Name@World" to the character list.
    /// Persists AR data (Lv, Gil, FC, FCID) into ReloggerCharacterInfo for permanent display.
    /// </summary>
    private void ImportFromAutoRetainer()
    {
        try
        {
            var arChars = plugin.ArConfigReader.ReadCharacters();
            if (arChars.Count == 0)
            {
                arImportStatus = "No characters found in AR config.";
                arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                return;
            }

            var cfg = plugin.Configuration;
            var added = 0;

            foreach (var c in arChars)
            {
                var key = $"{c.Name}@{c.World}";

                // Add to character list if not already present
                if (!cfg.ReloggerCharacters.Contains(key))
                {
                    cfg.ReloggerCharacters.Add(key);
                    added++;
                }

                // Persist AR data into ReloggerCharacterInfo
                UpdateCharacterInfo(cfg, key, c);
            }

            // Migrate legacy CID-based LastSeen into ReloggerCharacterInfo
            MigrateLegacyLastSeen(cfg, arChars);

            cfg.Save();

            arImportStatus = added > 0
                ? $"Imported {added} new ({arChars.Count} total)"
                : $"All {arChars.Count} already in list";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);

            Plugin.Log.Information($"[XASlave] AR Import: {arChars.Count} characters loaded, {added} new added, persistent data updated.");
        }
        catch (Exception ex)
        {
            arImportStatus = $"Import failed: {ex.Message}";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            Plugin.Log.Error($"[XASlave] AR Import error: {ex.Message}");
        }
    }

    /// <summary>
    /// Refresh AR data for all characters in the list.
    /// Updates persistent Lv/Gil/FC columns without adding/removing characters.
    /// </summary>
    private void RefreshArCharacterCache()
    {
        try
        {
            var arChars = plugin.ArConfigReader.ReadCharacters();
            var cfg = plugin.Configuration;
            var updated = 0;

            foreach (var c in arChars)
            {
                var key = $"{c.Name}@{c.World}";
                UpdateCharacterInfo(cfg, key, c);
                updated++;
            }

            MigrateLegacyLastSeen(cfg, arChars);
            cfg.Save();

            arImportStatus = $"Refreshed {updated} characters";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception ex)
        {
            arImportStatus = $"Refresh failed: {ex.Message}";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
    }

    /// <summary>
    /// Update or create persistent character data from an ArCharacterInfo entry.
    /// Preserves existing LastLoggedIn timestamp if already set.
    /// </summary>
    private static void UpdateCharacterInfo(Configuration cfg, string key, AutoRetainerConfigReader.ArCharacterInfo ar)
    {
        if (!cfg.ReloggerCharacterInfo.TryGetValue(key, out var data))
            data = new ReloggerCharacterData();

        data.CID = ar.CID;
        data.HighestLevel = ar.HighestLevel;
        data.Gil = ar.Gil;
        data.FcName = ar.FcName;
        data.FCID = ar.FCID;
        data.CurrentWorld = ar.CurrentWorld;
        data.RetainerCount = ar.RetainerCount;
        data.SubmarineCount = ar.SubmarineCount;
        cfg.ReloggerCharacterInfo[key] = data;
    }

    /// <summary>
    /// Migrate legacy CID-based ReloggerLastSeen into ReloggerCharacterInfo.
    /// Only runs if there are entries to migrate. Clears legacy dict after migration.
    /// </summary>
    private static void MigrateLegacyLastSeen(Configuration cfg, List<AutoRetainerConfigReader.ArCharacterInfo> arChars)
    {
        if (cfg.ReloggerLastSeen.Count == 0) return;

        foreach (var ar in arChars)
        {
            if (ar.CID == 0) continue;
            if (!cfg.ReloggerLastSeen.TryGetValue(ar.CID, out var lastSeen)) continue;

            var key = $"{ar.Name}@{ar.World}";
            if (cfg.ReloggerCharacterInfo.TryGetValue(key, out var data))
            {
                if (data.LastLoggedIn == DateTime.MinValue || lastSeen > data.LastLoggedIn)
                    data.LastLoggedIn = lastSeen;
            }
        }

        cfg.ReloggerLastSeen.Clear();
    }

    /// <summary>
    /// Shared helper: import characters from AutoRetainer into a target list and update shared data.
    /// Returns (added, total) count.
    /// </summary>
    private (int Added, int Total) ImportCharactersFromArToList(List<string> targetList)
    {
        var arChars = plugin.ArConfigReader.ReadCharacters();
        var cfg = plugin.Configuration;
        var added = 0;

        foreach (var c in arChars)
        {
            var key = $"{c.Name}@{c.World}";
            if (!targetList.Contains(key))
            {
                targetList.Add(key);
                added++;
            }
            UpdateCharacterInfo(cfg, key, c);
        }

        MigrateLegacyLastSeen(cfg, arChars);
        cfg.Save();
        return (added, arChars.Count);
    }

    /// <summary>
    /// Pull character data from XA Database SQLite file.
    /// Updates ReloggerCharacterInfo with Lv, Gil, FC, and Last Logged In for all known characters.
    /// Uses the DB path from XA.Database.GetDbPath IPC, then reads SQLite directly.
    /// </summary>
    private void PullXaDatabaseInfo()
    {
        try
        {
            var dbPath = plugin.IpcClient.GetDbPath();
            if (string.IsNullOrEmpty(dbPath) || !System.IO.File.Exists(dbPath))
            {
                arImportStatus = "XA Database file not found.";
                arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                return;
            }

            var cfg = plugin.Configuration;
            var updated = 0;

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            // Query all characters with gil, highest job level, housing, and FC estate
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT content_id, character_name, world, updated_utc,
                       personal_estate, apartment,
                       gil,
                       highest_job_level AS highest_level,
                       fc_name, fc_id, fc_estate,
                       retainer_count,
                       free_company_json,
                       fc_members_json,
                       inventory_summaries_json
                FROM xa_characters
                ORDER BY character_name";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader["character_name"].ToString() ?? "";
                var world = reader["world"].ToString() ?? "";
                var key = $"{name}@{world}";

                // Update all characters from DB (needed for duplicate detection tasks)
                if (!cfg.ReloggerCharacterInfo.TryGetValue(key, out var data))
                    data = new ReloggerCharacterData();

                var contentId = Convert.ToInt64(reader["content_id"]);
                data.CID = contentId;

                var dbLevel = Convert.ToInt32(reader["highest_level"]);
                if (dbLevel > 0)
                    data.HighestLevel = dbLevel;

                var dbGil = Convert.ToInt32(reader["gil"]);
                if (dbGil > 0)
                    data.Gil = dbGil;

                var fcName = reader["fc_name"].ToString() ?? "";
                if (!string.IsNullOrEmpty(fcName))
                    data.FcName = fcName;

                var fcIdObj = reader["fc_id"];
                if (fcIdObj != null && fcIdObj != DBNull.Value)
                    data.FCID = Convert.ToInt64(fcIdObj);

                // Housing data — always assign to clear stale values
                data.PersonalEstate = reader["personal_estate"]?.ToString() ?? "";
                data.Apartment = reader["apartment"]?.ToString() ?? "";
                data.FcEstate = reader["fc_estate"]?.ToString() ?? "";
                data.FcMemberRankName = string.Empty;
                data.FcMemberRankSort = int.MaxValue;
                data.FreeCompanyRank = 0;
                data.MainInventoryUsedSlots = 0;
                data.MainInventoryTotalSlots = 0;
                data.MainInventoryFreeSlots = 0;

                var dbRetainerCount = Convert.ToInt32(reader["retainer_count"]);
                if (dbRetainerCount > 0)
                    data.RetainerCount = dbRetainerCount;

                UpdateCharacterFcMemberRank(data, name, world, reader["fc_members_json"]?.ToString() ?? "");
                UpdateCharacterFreeCompanyRank(data, reader["free_company_json"]?.ToString() ?? "");
                UpdateCharacterInventorySummary(data, reader["inventory_summaries_json"]?.ToString() ?? "");

                var lastSeenStr = reader["updated_utc"].ToString() ?? "";
                if (DateTime.TryParse(lastSeenStr, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var lastSeen))
                {
                    lastSeen = lastSeen.ToUniversalTime();
                    if (lastSeen > data.LastLoggedIn)
                        data.LastLoggedIn = lastSeen;
                }

                cfg.ReloggerCharacterInfo[key] = data;
                updated++;
            }

            cfg.Save();

            arImportStatus = $"XA DB: Updated {updated} characters";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
            Plugin.Log.Information($"[XASlave] Pulled XA Database info for {updated} characters from {dbPath}");
        }
        catch (Exception ex)
        {
            arImportStatus = $"XA DB pull failed: {ex.Message}";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            Plugin.Log.Error($"[XASlave] PullXaDatabaseInfo error: {ex.Message}");
        }
    }

    private static void UpdateCharacterFcMemberRank(ReloggerCharacterData data, string name, string world, string fcMembersJson)
    {
        if (string.IsNullOrWhiteSpace(fcMembersJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(fcMembersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var member in doc.RootElement.EnumerateArray())
            {
                var matchesContentId = member.TryGetProperty("ContentId", out var memberContentId)
                    && TryGetJsonInt64(memberContentId, out var parsedContentId)
                    && parsedContentId == data.CID;

                var memberName = member.TryGetProperty("Name", out var memberNameElement)
                    ? memberNameElement.GetString() ?? string.Empty
                    : string.Empty;
                var homeWorldName = member.TryGetProperty("HomeWorldName", out var homeWorldElement)
                    ? homeWorldElement.GetString() ?? string.Empty
                    : string.Empty;
                var currentWorldName = member.TryGetProperty("CurrentWorldName", out var currentWorldElement)
                    ? currentWorldElement.GetString() ?? string.Empty
                    : string.Empty;
                var matchesNameWorld = memberName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && (homeWorldName.Equals(world, StringComparison.OrdinalIgnoreCase)
                        || currentWorldName.Equals(world, StringComparison.OrdinalIgnoreCase));

                if (!matchesContentId && !matchesNameWorld)
                    continue;

                if (member.TryGetProperty("RankName", out var rankNameElement))
                    data.FcMemberRankName = rankNameElement.GetString() ?? string.Empty;

                if (member.TryGetProperty("RankSort", out var rankSortElement)
                    && TryGetJsonInt32(rankSortElement, out var rankSort))
                    data.FcMemberRankSort = rankSort;

                return;
            }
        }
        catch
        {
        }
    }

    private static int GetCurrentRankSortValue(ReloggerCharacterData? info)
    {
        if (info == null || info.FCID == 0)
            return int.MaxValue;

        if (info.FcMemberRankSort != int.MaxValue)
            return info.FcMemberRankSort;

        if (info.FcMemberRankName.Equals("Master", StringComparison.OrdinalIgnoreCase))
            return 0;

        const string rankPrefix = "Rank ";
        if (info.FcMemberRankName.StartsWith(rankPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(info.FcMemberRankName.Substring(rankPrefix.Length), out var parsedRank)
            && parsedRank > 0)
            return parsedRank - 1;

        return string.IsNullOrWhiteSpace(info.FcMemberRankName)
            ? int.MaxValue
            : int.MaxValue - 1;
    }

    private static void UpdateCharacterFreeCompanyRank(ReloggerCharacterData data, string freeCompanyJson)
    {
        if (string.IsNullOrWhiteSpace(freeCompanyJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(freeCompanyJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            if (doc.RootElement.TryGetProperty("Rank", out var rankElement)
                && TryGetJsonInt32(rankElement, out var fcRank))
                data.FreeCompanyRank = fcRank;
        }
        catch
        {
        }
    }

    private static void UpdateCharacterInventorySummary(ReloggerCharacterData data, string inventorySummariesJson)
    {
        if (string.IsNullOrWhiteSpace(inventorySummariesJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(inventorySummariesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var summary in doc.RootElement.EnumerateArray())
            {
                if (!summary.TryGetProperty("Name", out var nameElement))
                    continue;

                var containerName = nameElement.GetString() ?? string.Empty;
                if (!IsMainInventorySummary(containerName))
                    continue;

                if (summary.TryGetProperty("UsedSlots", out var usedElement)
                    && TryGetJsonInt32(usedElement, out var usedSlots))
                    data.MainInventoryUsedSlots += usedSlots;

                if (summary.TryGetProperty("TotalSlots", out var totalElement)
                    && TryGetJsonInt32(totalElement, out var totalSlots))
                    data.MainInventoryTotalSlots += totalSlots;
            }

            data.MainInventoryFreeSlots = Math.Max(0, data.MainInventoryTotalSlots - data.MainInventoryUsedSlots);
        }
        catch
        {
        }
    }

    private static bool IsMainInventorySummary(string containerName)
    {
        return containerName.StartsWith("Inventory ", StringComparison.OrdinalIgnoreCase)
            && !containerName.Contains("retainer", StringComparison.OrdinalIgnoreCase)
            && !containerName.Contains("buddy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetJsonInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);

        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), out value);

        value = 0;
        return false;
    }

    private static bool TryGetJsonInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt64(out value);

        if (element.ValueKind == JsonValueKind.String)
            return long.TryParse(element.GetString(), out value);

        value = 0;
        return false;
    }

    // ───────────────────────────────────────────────
    //  Plugin Status Checker (equivalent to CheckPluginEnabledXA)
    // ───────────────────────────────────────────────
    private void DrawPluginStatusChecker()
    {
        var ipc = plugin.IpcClient;

        // Required plugins (relogger will not function without these)
        var required = new (string Name, bool Available)[]
        {
            ("AutoRetainer", ipc.IsAutoRetainerAvailable()),
            ("Lifestream", ipc.IsLifestreamAvailable()),
            ("TextAdvance", ipc.IsTextAdvanceAvailable()),
            ("vnavmesh", ipc.IsVnavAvailable()),
        };

        // Optional plugins (enhance functionality but not strictly required)
        var optional = new (string Name, bool Available)[]
        {
            ("XA Database", ipc.IsXaDatabaseAvailable()),
            ("YesAlready", ipc.IsYesAlreadyAvailable()),
            ("PandorasBox", ipc.IsPandorasBoxAvailable()),
            ("Deliveroo", ipc.IsDeliverooAvailable()),
            ("Artisan", ipc.IsArtisanAvailable()),
            ("Dropbox", ipc.IsDropboxAvailable()),
        };

        var allRequiredOk = required.All(p => p.Available);
        var green = new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
        var red = new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
        var yellow = new Vector4(1.0f, 0.8f, 0.3f, 1.0f);
        var dim = new Vector4(0.6f, 0.6f, 0.6f, 1.0f);

        // Summary line
        if (allRequiredOk)
            ImGui.TextColored(green, "All required plugins loaded.");
        else
            ImGui.TextColored(red, "WARNING: Missing required plugins — relogger may not function correctly.");

        // Required plugins row
        ImGui.Text("Required: ");
        foreach (var p in required)
        {
            ImGui.SameLine();
            ImGui.TextColored(p.Available ? green : red, p.Available ? $"[{p.Name}]" : $"[{p.Name} \u2717]");
        }

        // Optional plugins row
        ImGui.Text("Optional: ");
        foreach (var p in optional)
        {
            ImGui.SameLine();
            ImGui.TextColored(p.Available ? green : dim, p.Available ? $"[{p.Name}]" : $"[{p.Name} -]");
        }

        ImGui.Spacing();
    }
}
