using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    // ── Monthly Relogger state ──
    private MonthlyReloggerTask? reloggerTask;
    private readonly HashSet<int> reloggerSelectedIndices = new();
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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
            var allRequiredPluginsOk = ipc.IsAutoRetainerAvailable()
                && ipc.IsLifestreamAvailable()
                && ipc.IsTextAdvanceAvailable()
                && ipc.IsVnavAvailable();
            var canStart = selectedChars.Count > 0 && allRequiredPluginsOk;

            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button($"Start Relogger ({selectedChars.Count} chars)"))
                StartMonthlyRelogger(selectedChars);
            if (!canStart) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canStart)
                ImGui.SetTooltip(!allRequiredPluginsOk
                    ? "Missing required plugins. Check the Plugin Status section above."
                    : "Select at least one character to start.");

            ImGui.SameLine();
            if (ImGui.Button("Check All"))
            {
                for (int i = 0; i < chars.Count; i++)
                {
                    var cn = chars[i];
                    var np = cn.Split('@');
                    var w = np.Length > 1 ? np[1] : "";
                    var wi = WorldData.GetByName(w);
                    var rd = WorldData.GetRegionDcLabel(w);
                    if (cfg.ReloggerRegionFilter != "All" && wi != null && wi.Region != cfg.ReloggerRegionFilter)
                        continue;
                    if (!string.IsNullOrEmpty(reloggerSearchFilter) &&
                        !cn.Contains(reloggerSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                        !rd.Contains(reloggerSearchFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    reloggerSelectedIndices.Add(i);
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear All"))
                reloggerSelectedIndices.Clear();
            ImGui.SameLine();
            if (ImGui.Button("Select Stale (>20d)"))
            {
                var cutoff = DateTime.UtcNow.AddDays(-20);
                for (int i = 0; i < chars.Count; i++)
                {
                    var cn = chars[i];
                    cfg.ReloggerCharacterInfo.TryGetValue(cn, out var info);
                    if (info == null || info.LastLoggedIn == default || info.LastLoggedIn < cutoff)
                        reloggerSelectedIndices.Add(i);
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Select all characters with Last Logged In over 20 days ago (or never logged in).");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character List ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Character List");
        ImGui.SameLine();
        ImGui.TextDisabled($"({chars.Count} total)");
        ImGui.Spacing();

        // Region filter
        var regionOptions = new[] { "All", "NA", "EU", "JP", "OCE" };
        var regionIdx = Array.IndexOf(regionOptions, cfg.ReloggerRegionFilter);
        if (regionIdx < 0) regionIdx = 0;
        ImGui.SetNextItemWidth(80);
        if (ImGui.Combo("Region##RelogFilter", ref regionIdx, regionOptions, regionOptions.Length))
        {
            cfg.ReloggerRegionFilter = regionOptions[regionIdx];
            cfg.Save();
        }

        // Search filter
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##RelogSearch", "Search name or world...", ref reloggerSearchFilter, 128);

        ImGui.Spacing();

        // Character table — always shows all columns from persistent ReloggerCharacterInfo
        // Columns: checkbox, #, character, region, lv, gil, fc, in fc, last logged in, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        if (ImGui.BeginTable("ReloggerCharTable", 10,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti,
            new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 30); // checkbox
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 30);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Region / DC", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("In FC", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Last Logged In", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 25); // remove
            ImGui.TableHeadersRow();

            // Build filtered list using persistent data
            var filtered = new List<(int OrigIdx, string CharName, string World, string RegionDc, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                var nameParts = charName.Split('@');
                var world = nameParts.Length > 1 ? nameParts[1] : "";
                var regionDc = WorldData.GetRegionDcLabel(world);
                var worldInfo = WorldData.GetByName(world);

                if (cfg.ReloggerRegionFilter != "All" && worldInfo != null && worldInfo.Region != cfg.ReloggerRegionFilter)
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
                        // 0=checkbox, 1=#, 2=char, 3=region, 4=lv, 5=gil, 6=fc, 7=infc, 8=lastloggedin, 9=remove
                        switch (colIdx)
                        {
                            case 1: cmp = a.OrigIdx.CompareTo(b.OrigIdx); break;
                            case 2: cmp = string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase); break;
                            case 3: cmp = string.Compare(WorldData.GetSortKey(a.World), WorldData.GetSortKey(b.World), StringComparison.Ordinal); break;
                            case 4: cmp = (a.Info?.HighestLevel ?? 0).CompareTo(b.Info?.HighestLevel ?? 0); break;
                            case 5: cmp = (a.Info?.Gil ?? 0).CompareTo(b.Info?.Gil ?? 0); break;
                            case 6: cmp = string.Compare(a.Info?.FcName ?? "", b.Info?.FcName ?? "", StringComparison.OrdinalIgnoreCase); break;
                            case 7:
                                var aFc = a.Info != null && a.Info.FCID != 0;
                                var bFc = b.Info != null && b.Info.FCID != 0;
                                cmp = aFc.CompareTo(bFc); break;
                            case 8:
                                var aLi = a.Info?.LastLoggedIn ?? DateTime.MinValue;
                                var bLi = b.Info?.LastLoggedIn ?? DateTime.MinValue;
                                cmp = aLi.CompareTo(bLi); break;
                            default: cmp = a.OrigIdx.CompareTo(b.OrigIdx); break;
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
                ImGui.Text(charName);
                if (info != null && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(charName);
                    ImGui.Separator();
                    ImGui.Text($"Level: {info.HighestLevel}");
                    ImGui.Text($"Gil: {info.Gil:N0}");
                    if (!string.IsNullOrEmpty(info.FcName))
                        ImGui.Text($"FC: {info.FcName}");
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
        ImGui.SetNextItemWidth(250);
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
        var v6 = cfg.ReloggerDoReturnToHome;
        var v7 = cfg.ReloggerDoReturnToFc;
        var v8 = cfg.ReloggerDoParseForXaDatabase;
        var v9 = cfg.ReloggerDoEnableArMultiOnComplete;

        changed |= ImGui.Checkbox("Enable TextAdvance (/at y)", ref v1);
        changed |= ImGui.Checkbox("Remove Sprout (/nastatus off)", ref v2);
        changed |= ImGui.Checkbox("Open Inventory", ref v3);
        changed |= ImGui.Checkbox("Open Armoury Chest", ref v4);
        changed |= ImGui.Checkbox("Open Saddlebags", ref v5);
        changed |= ImGui.Checkbox("Teleport Home (Lifestream)", ref v6);
        changed |= ImGui.Checkbox("Teleport FC (Lifestream)", ref v7);
        changed |= ImGui.Checkbox("Parse for XA Database (FC window + save)", ref v8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens FC window to collect FC data (name, members, points, plot),\nthen saves all collected data to XA Database.");
        ImGui.Separator();
        changed |= ImGui.Checkbox("Enable AR Multi Mode on completion", ref v9);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-enables AutoRetainer Multi Mode after ALL characters have been processed.\nOnly fires after the summary step at the very end.");

        if (changed)
        {
            cfg.ReloggerDoTextAdvance = v1;
            cfg.ReloggerDoRemoveSprout = v2;
            cfg.ReloggerDoOpenInventory = v3;
            cfg.ReloggerDoOpenArmouryChest = v4;
            cfg.ReloggerDoOpenSaddlebags = v5;
            cfg.ReloggerDoReturnToHome = v6;
            cfg.ReloggerDoReturnToFc = v7;
            cfg.ReloggerDoParseForXaDatabase = v8;
            cfg.ReloggerDoEnableArMultiOnComplete = v9;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Log output ──
        if (ImGui.Checkbox("Show Log", ref reloggerShowLog)) { }
        if (reloggerShowLog)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Log"))
            {
                if (runner.LogMessages.Count > 0)
                {
                    var logText = string.Join("\n", runner.LogMessages);
                    ImGui.SetClipboardText(logText);
                    arImportStatus = $"Copied {runner.LogMessages.Count} log lines to clipboard";
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copy all log messages to clipboard for debugging/exporting.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Clear Log"))
            {
                runner.ClearLog();
            }

            if (runner.LogMessages.Count > 0)
            {
                ImGui.Spacing();
                using (var logChild = ImRaii.Child("ReloggerLog", new Vector2(0, 150), true))
                {
                    if (logChild.Success)
                    {
                        foreach (var msg in runner.LogMessages)
                            ImGui.TextWrapped(msg);

                        // Auto-scroll to bottom
                        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                            ImGui.SetScrollHereY(1.0f);
                    }
                }
            }
        }

        // Status display
        if (runner.CurrentTaskName == "Monthly Relogger" && runner.StatusText == "Complete")
        {
            ImGui.Spacing();
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

    /// <summary>Start the Monthly Relogger task with the given character list.</summary>
    private void StartMonthlyRelogger(List<string> characters)
    {
        reloggerTask = new MonthlyReloggerTask(plugin)
        {
            DoEnableTextAdvance = plugin.Configuration.ReloggerDoTextAdvance,
            DoRemoveSprout = plugin.Configuration.ReloggerDoRemoveSprout,
            DoOpenInventory = plugin.Configuration.ReloggerDoOpenInventory,
            DoOpenArmouryChest = plugin.Configuration.ReloggerDoOpenArmouryChest,
            DoOpenSaddlebags = plugin.Configuration.ReloggerDoOpenSaddlebags,
            DoReturnToHome = plugin.Configuration.ReloggerDoReturnToHome,
            DoReturnToFc = plugin.Configuration.ReloggerDoReturnToFc,
            DoParseForXaDatabase = plugin.Configuration.ReloggerDoParseForXaDatabase,
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
        reloggerShowLog = true;
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
                SELECT c.content_id, c.name, c.world, c.last_seen_utc,
                       c.personal_estate, c.apartment,
                       COALESCE(g.amount, 0) AS gil,
                       COALESCE(j.max_level, 0) AS highest_level,
                       fc.name AS fc_name, fc.fc_id, fc.estate AS fc_estate,
                       COALESCE(r.retainer_count, 0) AS retainer_count
                FROM characters c
                LEFT JOIN currency_balances g ON g.content_id = c.content_id AND g.currency_name = 'Gil'
                LEFT JOIN (SELECT content_id, MAX(level) AS max_level FROM job_levels GROUP BY content_id) j ON j.content_id = c.content_id
                LEFT JOIN free_companies fc ON fc.content_id = c.content_id
                LEFT JOIN (SELECT content_id, COUNT(*) AS retainer_count FROM retainers GROUP BY content_id) r ON r.content_id = c.content_id
                ORDER BY c.name";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader["name"].ToString() ?? "";
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

                var dbRetainerCount = Convert.ToInt32(reader["retainer_count"]);
                if (dbRetainerCount > 0)
                    data.RetainerCount = dbRetainerCount;

                var lastSeenStr = reader["last_seen_utc"].ToString() ?? "";
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
