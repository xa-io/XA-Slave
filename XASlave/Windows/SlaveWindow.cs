using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Microsoft.Data.Sqlite;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Main window for XA Slave — left-hand task menu with right-side content panel.
/// Tasks are automation jobs that interact with the game and push data to XA Database via IPC.
/// </summary>
public class SlaveWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string PluginVersion = "0.0.0.3";

    // Task menu
    private enum SlaveTask
    {
        SaveToXaDatabase,
        MonthlyRelogger,
        DebugCommands,
        IpcCallsAvailable,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskList =
    {
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
        (SlaveTask.MonthlyRelogger, "Monthly Relogger"),
        (SlaveTask.DebugCommands, "Debug / Test"),
    };

    private SlaveTask selectedTask = SlaveTask.SaveToXaDatabase;

    // Auto-collection state
    private DateTime? autoCollectScheduledAt;
    private string lastIpcResult = string.Empty;
    private DateTime lastIpcResultExpiry = DateTime.MinValue;

    // Cached IPC connectivity — refreshed every 5 seconds to avoid HITCH warnings
    private DateTime ipcCacheExpiry = DateTime.MinValue;
    private bool cachedArAvail, cachedLsAvail, cachedYaAvail, cachedDelAvail, cachedPbAvail, cachedDbxAvail;
    private bool cachedTaAvail, cachedArtAvail, cachedSplatAvail;

    // Live IPC bool polling
    private DateTime liveIpcExpiry = DateTime.MinValue;
    private readonly Dictionary<string, bool?> liveIpcValues = new();

    // ── Monthly Relogger state ──
    private MonthlyReloggerTask? reloggerTask;
    private readonly HashSet<int> reloggerSelectedIndices = new();
    private string reloggerAddInput = string.Empty;
    private string reloggerSearchFilter = string.Empty;
    private bool reloggerShowLog;

    private string arImportStatus = string.Empty;
    private DateTime arImportStatusExpiry = DateTime.MinValue;

    public SlaveWindow(Plugin plugin)
        : base("XA Slave##SlaveWindow", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(550, 350),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    /// <summary>Schedule auto-collection after login with a delay.</summary>
    public void ScheduleAutoCollection()
    {
        autoCollectScheduledAt = DateTime.UtcNow;
        Plugin.Log.Information($"[XASlave] Auto-collection scheduled (will start in {plugin.Configuration.AutoCollectDelaySeconds}s).");
    }

    public override void Draw()
    {
        // Auto-collection: start after login delay
        if (autoCollectScheduledAt.HasValue && Plugin.PlayerState.IsLoaded && !plugin.AutoCollector.IsRunning)
        {
            var delay = (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
            if (delay >= plugin.Configuration.AutoCollectDelaySeconds && plugin.AutoCollector.IsNormalCondition())
            {
                autoCollectScheduledAt = null;
                RunAutoCollection();
            }
        }

        // ── Left panel: Task menu ──
        var leftWidth = 180f;
        using (var child = ImRaii.Child("TaskMenu", new Vector2(leftWidth, -30), true))
        {
            if (child.Success)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Tasks");
                ImGui.Separator();
                ImGui.Spacing();

                foreach (var (task, label) in TaskList)
                {
                    var isSelected = selectedTask == task;
                    if (ImGui.Selectable(label, isSelected))
                        selectedTask = task;
                }

                // IPC reference at the bottom of the task list
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Reference");
                {
                    var isSelected = selectedTask == SlaveTask.IpcCallsAvailable;
                    if (ImGui.Selectable("IPC Calls Available", isSelected))
                        selectedTask = SlaveTask.IpcCallsAvailable;
                }
            }
        }

        ImGui.SameLine();

        // ── Right panel: Task content ──
        using (var child = ImRaii.Child("TaskContent", new Vector2(0, -30), true))
        {
            if (child.Success)
            {
                switch (selectedTask)
                {
                    case SlaveTask.SaveToXaDatabase:
                        DrawSaveToXaDatabaseTask();
                        break;
                    case SlaveTask.MonthlyRelogger:
                        DrawMonthlyReloggerTask();
                        break;
                    case SlaveTask.DebugCommands:
                        DrawDebugCommands();
                        break;
                    case SlaveTask.IpcCallsAvailable:
                        DrawIpcCallsAvailable();
                        break;
                }
            }
        }

        // ── Status bar ──
        ImGui.Separator();
        DrawStatusBar();
    }

    // ───────────────────────────────────────────────
    //  Task: Save to XA Database
    // ───────────────────────────────────────────────
    private void DrawSaveToXaDatabaseTask()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Save to XA Database");
        ImGui.TextDisabled("Collects data from game windows and saves to XA Database via IPC.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Connection status ──
        var dbReady = plugin.IpcClient.IsReady();
        var dbVersion = plugin.IpcClient.GetVersion();
        if (dbReady)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"XA Database: Connected (v{dbVersion})");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "XA Database: Not available");
            ImGui.TextDisabled("Make sure XA Database plugin is loaded.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Manual actions ──
        ImGui.Text("Manual Actions");
        ImGui.Spacing();

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Collecting: {plugin.AutoCollector.StatusText}");
            if (ImGui.Button("Cancel"))
                plugin.AutoCollector.Cancel();
        }
        else
        {
            if (ImGui.Button("Collect Now"))
                RunAutoCollection();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Opens saddlebag/FC windows to collect data, then saves to XA Database via IPC.");

            ImGui.SameLine();
            if (ImGui.Button("IPC Save"))
            {
                if (plugin.IpcClient.Save())
                    SetIpcResult("Save sent to XA Database");
                else
                    SetIpcResult("Save failed — XA Database not available");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Send a save command to XA Database without opening any game windows.");
        }

        // IPC result feedback
        if (!string.IsNullOrEmpty(lastIpcResult) && DateTime.UtcNow < lastIpcResultExpiry)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), lastIpcResult);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Auto-Collection on Login settings ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Auto-Collection on Login");
        ImGui.Spacing();

        var autoCollect = plugin.Configuration.AutoCollectOnLogin;
        if (ImGui.Checkbox("Enable Auto-Collection on Login", ref autoCollect))
        {
            plugin.Configuration.AutoCollectOnLogin = autoCollect;
            plugin.Configuration.Save();
        }
        ImGui.TextDisabled("Automatically opens saddlebag and FC windows after login to collect data.");

        if (autoCollect)
        {
            ImGui.Spacing();
            var acSaddlebag = plugin.Configuration.AutoCollectSaddlebag;
            if (ImGui.Checkbox("  Collect Saddlebag", ref acSaddlebag))
            {
                plugin.Configuration.AutoCollectSaddlebag = acSaddlebag;
                plugin.Configuration.Save();
            }

            var acFc = plugin.Configuration.AutoCollectFc;
            if (ImGui.Checkbox("  Collect FC Data (Members, Info, Housing)", ref acFc))
            {
                plugin.Configuration.AutoCollectFc = acFc;
                plugin.Configuration.Save();
            }

            ImGui.Spacing();
            var delay = plugin.Configuration.AutoCollectDelaySeconds;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputFloat("Login Delay (seconds)", ref delay, 1f, 5f, "%.0f"))
            {
                if (delay < 3f) delay = 3f;
                if (delay > 30f) delay = 30f;
                plugin.Configuration.AutoCollectDelaySeconds = delay;
                plugin.Configuration.Save();
            }
            ImGui.TextDisabled("Wait time after login before starting collection.");
        }

        // Scheduled status
        if (autoCollectScheduledAt.HasValue)
        {
            var remaining = plugin.Configuration.AutoCollectDelaySeconds - (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
            if (remaining > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Auto-collection starting in ~{remaining:F0}s...");
            }
        }
    }

    // ───────────────────────────────────────────────
    //  Task: Monthly Relogger
    // ───────────────────────────────────────────────
    private void DrawMonthlyReloggerTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var chars = cfg.ReloggerCharacters;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Monthly Relogger");
        ImGui.TextDisabled("Rotate through characters, executing actions on each. Converted from 7.35 XA Monthly Relogger.lua.");
        ImGui.Spacing();

        // ── Plugin Status Checker (equivalent to CheckPluginEnabledXA) ──
        DrawPluginStatusChecker();

        ImGui.Separator();
        ImGui.Spacing();

        // ── Run controls ──
        if (runner.IsRunning && runner.CurrentTaskName == "Monthly Relogger")
        {
            // Progress bar
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {runner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);

            ImGui.Spacing();
            if (ImGui.Button("Cancel Relogger"))
                runner.Cancel();
        }
        else
        {
            // Start button — requires selected characters AND all required plugins
            var selectedChars = GetSelectedReloggerCharacters();
            var ipc = plugin.IpcClient;
            var allRequiredPluginsOk = ipc.IsAutoRetainerAvailable()
                && ipc.IsLifestreamAvailable()
                && ipc.IsTextAdvanceAvailable()
                && ipc.IsVnavAvailable();
            var canStart = selectedChars.Count > 0 && allRequiredPluginsOk;

            if (!canStart) ImGui.BeginDisabled();
            if (!allRequiredPluginsOk)
            {
                if (ImGui.Button("Check Plugins"))
                { }
            }
            else
            {
                if (ImGui.Button($"Start Relogger ({selectedChars.Count} chars)"))
                {
                    StartMonthlyRelogger(selectedChars);
                }
            }
            if (!canStart) ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canStart)
            {
                if (!allRequiredPluginsOk)
                    ImGui.SetTooltip("Missing required plugins. Check the Plugin Status section below.");
                else
                    ImGui.SetTooltip("Select at least one character to start.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Check All"))
            {
                for (int i = 0; i < chars.Count; i++)
                    reloggerSelectedIndices.Add(i);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear All"))
                reloggerSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character List ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Character List");
        ImGui.SameLine();
        ImGui.TextDisabled($"({chars.Count} total)");
        ImGui.Spacing();

        // Import from AutoRetainer button
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (ImGui.Button("Import from AutoRetainer"))
        {
            ImportFromAutoRetainer();
        }
        if (!arConfigExists) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (arConfigExists)
                ImGui.SetTooltip("Read AutoRetainer's DefaultConfig.json to import all characters.\nPath: " + plugin.ArConfigReader.GetAutoRetainerConfigPath());
            else
                ImGui.SetTooltip("AutoRetainer config not found.\nExpected: " + plugin.ArConfigReader.GetAutoRetainerConfigPath());
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh AR Data"))
        {
            RefreshArCharacterCache();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reload character details (Lv, Gil, FC) from AutoRetainer config without adding/removing characters.");

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        if (!xaDbAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Pull XA Database Info"))
        {
            PullXaDatabaseInfo();
        }
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (xaDbAvailable)
                ImGui.SetTooltip("Read XA Database to update Lv, Gil, FC, Last Logged In for all characters.\nPulls directly from the SQLite database.");
            else
                ImGui.SetTooltip("XA Database plugin not available.");
        }

        // Import status message
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), arImportStatus);
        }

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
                    ImGui.TextDisabled("—");

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
        };

        var steps = reloggerTask.BuildSteps(characters, plugin.TaskRunner);
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

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            // Query all characters with their gil and highest job level
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT c.content_id, c.name, c.world, c.last_seen_utc,
                       COALESCE(g.amount, 0) AS gil,
                       COALESCE(j.max_level, 0) AS highest_level,
                       fc.name AS fc_name, fc.fc_id
                FROM characters c
                LEFT JOIN currency_balances g ON g.content_id = c.content_id AND g.currency_name = 'Gil'
                LEFT JOIN (SELECT content_id, MAX(level) AS max_level FROM job_levels GROUP BY content_id) j ON j.content_id = c.content_id
                LEFT JOIN free_companies fc ON fc.content_id = c.content_id
                ORDER BY c.name";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader["name"].ToString() ?? "";
                var world = reader["world"].ToString() ?? "";
                var key = $"{name}@{world}";

                // Only update characters that are in our relogger list or have persistent data
                if (!cfg.ReloggerCharacters.Contains(key) && !cfg.ReloggerCharacterInfo.ContainsKey(key))
                    continue;

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
            ImGui.TextColored(p.Available ? green : red, p.Available ? $"[{p.Name}]" : $"[{p.Name} ✗]");
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

    // ───────────────────────────────────────────────
    //  Debug / Test Commands
    //  Test buttons for all xafunc-referenced commands
    //  These functions will be used as templates for future tasks
    // ───────────────────────────────────────────────
    private string debugResult = string.Empty;
    private DateTime debugResultExpiry = DateTime.MinValue;

    private void DrawDebugCommands()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Debug / Test Commands");
        ImGui.TextDisabled("Test individual xafunc commands. These are the building blocks for all tasks.");
        ImGui.Spacing();

        // Status feedback
        if (!string.IsNullOrEmpty(debugResult) && DateTime.UtcNow < debugResultExpiry)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), debugResult);
            ImGui.Spacing();
        }

        // ── Plugin Status (same checker as Monthly Relogger) ──
        DrawPluginStatusChecker();

        ImGui.Separator();
        ImGui.Spacing();

        // ╔══════════════════════════════════════════════╗
        // ║  [Movement Functions]                        ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.TreeNode("Movement Functions"))
        {

        // ══════════════════════════════════════════════
        //  XA Lazy Movements
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("XA Lazy Movements"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Interact"))
        {
            var ok = AddonHelper.InteractWithTarget();
            SetDebugResult(ok ? "InteractWithTarget: OK" : "No target or interaction failed");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: Stop"))
        {
            plugin.IpcClient.VnavStop();
            SetDebugResult("Sent: vnavmesh.Path.Stop()");
        }
        ImGui.SameLine();
        if (ImGui.Button("PathToTarget"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null && plugin.IpcClient.VnavIsReady())
            {
                var targetPos = target.Position;
                var targetName = target.Name.ToString();
                var stopDist = 2.0f + target.HitboxRadius;
                var ok = plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist);
                if (ok)
                {
                    SetDebugResult($"Pathing to {targetName} (stop={stopDist:F1}y, no auto-interact)");
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        var distSamples = new System.Collections.Generic.List<float>();
                        const int maxSamples = 7;
                        const float stallThreshold = 0.3f;
                        const float closeEnough = 10.0f;
                        const int pollMs = 300;
                        const int maxTimeoutMs = 60000;
                        int elapsed = 0;
                        int jumpAttempts = 0;

                        await System.Threading.Tasks.Task.Delay(600);
                        elapsed += 600;

                        while (elapsed < maxTimeoutMs)
                        {
                            await System.Threading.Tasks.Task.Delay(pollMs);
                            elapsed += pollMs;

                            var (ringDist, pathActive) = await Plugin.Framework.RunOnFrameworkThread(() =>
                            {
                                var lp = Plugin.ObjectTable.LocalPlayer;
                                var tgt = lp?.TargetObject;
                                if (lp == null || tgt == null) return (-1f, false);
                                var pp = lp.Position; var tp = tgt.Position;
                                var dx = tp.X - pp.X; var dy = tp.Y - pp.Y; var dz = tp.Z - pp.Z;
                                var cd = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                var rd = cd - lp.HitboxRadius - tgt.HitboxRadius;
                                var active = plugin.IpcClient.VnavPathIsRunning() || plugin.IpcClient.VnavSimpleMovePathfindInProgress();
                                return (rd, active);
                            });

                            if (ringDist < 0) { SetDebugResult("Lost target — aborting"); break; }

                            distSamples.Add(ringDist);
                            if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                            bool stalled = false;
                            if (distSamples.Count >= maxSamples)
                            {
                                float maxD = distSamples[0], minD = distSamples[0];
                                foreach (var s in distSamples) { if (s > maxD) maxD = s; if (s < minD) minD = s; }
                                stalled = (maxD - minD) < stallThreshold;
                            }

                            if (!pathActive)
                            {
                                SetDebugResult($"Arrived near {targetName} (ring={ringDist:F1}y)");
                                break;
                            }
                            else if (stalled && ringDist <= closeEnough)
                            {
                                plugin.IpcClient.VnavStop();
                                SetDebugResult($"Arrived near {targetName} (ring={ringDist:F1}y, stalled — stopped)");
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled within {ringDist:F1}y of {targetName} — stopping");
                                break;
                            }
                            else if (stalled && jumpAttempts < 5)
                            {
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled at ring={ringDist:F1}y — jump attempt {jumpAttempts + 1}");
                                KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
                                jumpAttempts++;
                                distSamples.Clear();
                                await System.Threading.Tasks.Task.Delay(800);
                                elapsed += 800;
                            }
                            else
                            {
                                if (elapsed % 3000 < pollMs)
                                    SetDebugResult($"Pathing to {targetName}: ring={ringDist:F1}y");
                            }
                        }

                        if (elapsed >= maxTimeoutMs)
                        {
                            plugin.IpcClient.VnavStop();
                            SetDebugResult($"PathToTarget timeout (60s) for {targetName}");
                        }
                    });
                }
                else SetDebugResult("Pathfind failed");
            }
            else if (target == null) SetDebugResult("No target selected");
            else SetDebugResult("vnavmesh not ready");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ground pathfind to target, no auto-interact.\nStop distance = 2y + target hitbox radius.");

        ImGui.Spacing();

        if (ImGui.Button("PathToTargetThenInteract"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null && plugin.IpcClient.VnavIsReady())
            {
                var targetPos = target.Position;
                var targetName = target.Name.ToString();
                var targetHitbox = target.HitboxRadius;
                var playerHitbox = local.HitboxRadius;
                var stopDist = 2.0f + targetHitbox;
                var interactRange = stopDist + playerHitbox;

                var ok = plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist);
                if (ok)
                {
                    SetDebugResult($"Pathing to {targetName} (stop={stopDist:F1}y, interact<={interactRange:F1}y ring)");
                    Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: {targetName} hitbox={targetHitbox:F1} stopDist={stopDist:F1} interactRange={interactRange:F1}");
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        var distSamples = new System.Collections.Generic.List<float>();
                        const int maxSamples = 7;
                        const float stallThreshold = 0.3f;
                        const int pollMs = 300;
                        const int maxTimeoutMs = 60000;
                        int elapsed = 0;
                        bool interacted = false;
                        int jumpAttempts = 0;

                        await System.Threading.Tasks.Task.Delay(600);
                        elapsed += 600;

                        while (elapsed < maxTimeoutMs)
                        {
                            await System.Threading.Tasks.Task.Delay(pollMs);
                            elapsed += pollMs;

                            var (ringDist, centerDist, pathRunning, pathfinding) = await Plugin.Framework.RunOnFrameworkThread(() =>
                            {
                                var lp = Plugin.ObjectTable.LocalPlayer;
                                var tgt = lp?.TargetObject;
                                if (lp == null || tgt == null) return (-1f, -1f, false, false);
                                var pp = lp.Position;
                                var tp = tgt.Position;
                                var dx = tp.X - pp.X; var dy = tp.Y - pp.Y; var dz = tp.Z - pp.Z;
                                var cd = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                var rd = cd - lp.HitboxRadius - tgt.HitboxRadius;
                                return (rd, cd, plugin.IpcClient.VnavPathIsRunning(), plugin.IpcClient.VnavSimpleMovePathfindInProgress());
                            });

                            if (ringDist < 0) { SetDebugResult("Lost target — aborting"); break; }

                            bool pathActive = pathRunning || pathfinding;

                            distSamples.Add(ringDist);
                            if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                            bool stalled = false;
                            if (distSamples.Count >= maxSamples)
                            {
                                float maxD = distSamples[0], minD = distSamples[0];
                                foreach (var s in distSamples) { if (s > maxD) maxD = s; if (s < minD) minD = s; }
                                stalled = (maxD - minD) < stallThreshold;
                            }

                            if (ringDist <= interactRange)
                            {
                                SetDebugResult($"In range of {targetName}: ring={ringDist:F1}y — interacting");
                                await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                plugin.IpcClient.VnavStop();
                                Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: interacted with {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                                interacted = true;
                                break;
                            }
                            else if (stalled && ringDist < 20.0f)
                            {
                                SetDebugResult($"Stalled near {targetName}: ring={ringDist:F1}y — jumping to unstuck");
                                Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: stalled at ring={ringDist:F1}y — jump attempt {jumpAttempts + 1}");
                                if (jumpAttempts < 5)
                                {
                                    KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
                                    jumpAttempts++;
                                    distSamples.Clear();
                                    await System.Threading.Tasks.Task.Delay(800);
                                    elapsed += 800;
                                }
                            }
                            else if (!pathActive)
                            {
                                SetDebugResult($"Path ended at ring={ringDist:F1}y — vnav routing done");
                                Plugin.Log.Warning($"[XASlave] PathToTargetThenInteract: path ended for {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                                if (ringDist <= interactRange * 3)
                                {
                                    await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                    SetDebugResult($"Path ended — attempted interact at ring={ringDist:F1}y");
                                    interacted = true;
                                }
                                break;
                            }
                            else
                            {
                                if (elapsed % 3000 < pollMs)
                                    SetDebugResult($"Pathing to {targetName}: ring={ringDist:F1}y");
                            }
                        }

                        if (!interacted && elapsed >= maxTimeoutMs)
                        {
                            plugin.IpcClient.VnavStop();
                            SetDebugResult($"PathToTargetThenInteract timeout (60s) for {targetName}");
                        }
                    });
                }
                else SetDebugResult("Pathfind failed — vnav could not start route");
            }
            else if (target == null)
                SetDebugResult("No target selected");
            else
                SetDebugResult("vnavmesh not ready");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smart pathfind to target with auto-interact.\n" +
                "Stop distance = 2y + target hitbox (adapts to target size).\n" +
                "Attempts interact once within ring range.\n" +
                "Jumps to unstuck if stalled and interact fails.\n" +
                "Cancels movement on successful interaction.");

        ImGui.SameLine();
        if (ImGui.Button("PathSmartThenInteract"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null && plugin.IpcClient.VnavIsReady())
            {
                var targetPos = target.Position;
                var targetName = target.Name.ToString();
                var targetHitbox = target.HitboxRadius;
                var playerHitbox = local.HitboxRadius;
                var stopDist = 2.0f + targetHitbox;
                var interactRange = stopDist + playerHitbox;

                var lp0 = local.Position;
                var dx0 = targetPos.X - lp0.X; var dy0 = targetPos.Y - lp0.Y; var dz0 = targetPos.Z - lp0.Z;
                var ringDist0 = (float)Math.Sqrt(dx0 * dx0 + dy0 * dy0 + dz0 * dz0) - playerHitbox - targetHitbox;
                var canFly = HasFlightUnlocked();
                var shouldMount = canFly && ringDist0 > 35.0f;

                SetDebugResult($"PathSmart to {targetName}: ring={ringDist0:F0}y, fly={canFly}, mount={shouldMount}");
                Plugin.Log.Information($"[XASlave] PathSmartThenInteract: {targetName} ring={ringDist0:F1}y fly={canFly} mount={shouldMount} stop={stopDist:F1}");

                System.Threading.Tasks.Task.Run(async () =>
                {
                    if (shouldMount)
                    {
                        await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                        for (int w = 0; w < 20; w++)
                        {
                            await System.Threading.Tasks.Task.Delay(300);
                            var mounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (mounted) break;
                        }
                    }

                    var fly = shouldMount;
                    var pathOk = await Plugin.Framework.RunOnFrameworkThread(() =>
                        plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, fly, stopDist));
                    if (!pathOk) { SetDebugResult("Pathfind failed"); return; }

                    var distSamples = new System.Collections.Generic.List<float>();
                    const int maxSamples = 7;
                    const float stallThreshold = 0.3f;
                    const int pollMs = 100;
                    const int maxTimeoutMs = 60000;
                    int elapsed = 0;
                    bool interacted = false;
                    int jumpAttempts = 0;

                    await System.Threading.Tasks.Task.Delay(200);
                    elapsed += 200;

                    while (elapsed < maxTimeoutMs)
                    {
                        await System.Threading.Tasks.Task.Delay(pollMs);
                        elapsed += pollMs;

                        var (rd, cd, pathRunning, pathfinding) = await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            var lp2 = Plugin.ObjectTable.LocalPlayer;
                            var tgt = lp2?.TargetObject;
                            if (lp2 == null || tgt == null) return (-1f, -1f, false, false);
                            var pp = lp2.Position; var tp = tgt.Position;
                            var ddx = tp.X - pp.X; var ddy = tp.Y - pp.Y; var ddz = tp.Z - pp.Z;
                            var c = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                            var r = c - lp2.HitboxRadius - tgt.HitboxRadius;
                            return (r, c, plugin.IpcClient.VnavPathIsRunning(), plugin.IpcClient.VnavSimpleMovePathfindInProgress());
                        });

                        if (rd < 0) { SetDebugResult("Lost target — aborting"); break; }

                        bool pathActive = pathRunning || pathfinding;

                        distSamples.Add(rd);
                        if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                        bool stalled = false;
                        if (distSamples.Count >= maxSamples)
                        {
                            float maxD = distSamples[0], minD = distSamples[0];
                            foreach (var s in distSamples) { if (s > maxD) maxD = s; if (s < minD) minD = s; }
                            stalled = (maxD - minD) < stallThreshold;
                        }

                        if (rd <= interactRange)
                        {
                            var isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (isMounted)
                            {
                                plugin.IpcClient.VnavStop();
                                await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                                for (int w = 0; w < 50; w++)
                                {
                                    await System.Threading.Tasks.Task.Delay(100);
                                    isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                    if (!isMounted) break;
                                }
                            }

                            SetDebugResult($"Dismounted near {targetName}: ring={rd:F1}y — waiting for ready...");
                            for (int sw = 0; sw < 30; sw++)
                            {
                                await System.Threading.Tasks.Task.Delay(100);
                                var charReady = await Plugin.Framework.RunOnFrameworkThread(() =>
                                    MonthlyReloggerTask.IsNamePlateReady() &&
                                    MonthlyReloggerTask.IsPlayerAvailable() &&
                                    !Plugin.Condition[ConditionFlag.Casting] &&
                                    !Plugin.Condition[ConditionFlag.InCombat]);
                                if (charReady) break;
                            }

                            SetDebugResult($"In range of {targetName}: ring={rd:F1}y — interacting");
                            await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                            plugin.IpcClient.VnavStop();
                            Plugin.Log.Information($"[XASlave] PathSmartThenInteract: interacted with {targetName} (ring={rd:F1}y center={cd:F1}y)");
                            interacted = true;
                            break;
                        }
                        else if (stalled && rd < 20.0f)
                        {
                            var isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (isMounted)
                            {
                                plugin.IpcClient.VnavStop();
                                await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                                for (int w = 0; w < 50; w++)
                                {
                                    await System.Threading.Tasks.Task.Delay(100);
                                    isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                    if (!isMounted) break;
                                }
                                await Plugin.Framework.RunOnFrameworkThread(() =>
                                    plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                                distSamples.Clear();
                                await System.Threading.Tasks.Task.Delay(600);
                                elapsed += 600;
                            }
                            else
                            {
                                SetDebugResult($"Stalled near {targetName}: ring={rd:F1}y — jumping");
                                if (jumpAttempts < 5)
                                {
                                    KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
                                    jumpAttempts++;
                                    distSamples.Clear();
                                    await System.Threading.Tasks.Task.Delay(800);
                                    elapsed += 800;
                                }
                            }
                        }
                        else if (!pathActive)
                        {
                            SetDebugResult($"Path ended at ring={rd:F1}y — routing done");
                            Plugin.Log.Warning($"[XASlave] PathSmartThenInteract: path ended for {targetName} (ring={rd:F1}y)");
                            if (rd <= interactRange * 3)
                            {
                                var isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                    Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                if (isMounted)
                                {
                                    await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                                    for (int w = 0; w < 50; w++)
                                    {
                                        await System.Threading.Tasks.Task.Delay(100);
                                        isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                            Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                        if (!isMounted) break;
                                    }
                                }
                                await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                SetDebugResult($"Path ended — attempted interact at ring={rd:F1}y");
                                interacted = true;
                            }
                            break;
                        }
                        else
                        {
                            if (elapsed % 3000 < pollMs)
                                SetDebugResult($"PathSmart to {targetName}: ring={rd:F1}y");
                        }
                    }

                    if (!interacted && elapsed >= maxTimeoutMs)
                    {
                        plugin.IpcClient.VnavStop();
                        SetDebugResult($"PathSmartThenInteract timeout (60s) for {targetName}");
                    }
                });
            }
            else if (target == null) SetDebugResult("No target selected");
            else SetDebugResult("vnavmesh not ready");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smart pathfind: mounts if flying unlocked and target > 50y.\n" +
                "Flies to target, dismounts on arrival, then interacts.\n" +
                "Falls back to ground pathfind + interact if close or can't fly.");

        ImGui.Spacing();

        if (ImGui.Button("WalkThroughDottedWall"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_END);
            KeyInputHelper.HoldKeyForDuration(KeyInputHelper.VK_W, 2000);
            SetDebugResult("KeyInput: END (reset camera) + Hold W for 2s then auto-release (WalkThroughDottedWallXA)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Resets camera (VK_END) + holds W forward for 2 seconds, then auto-releases.\nFully automated — no manual release needed.");

        ImGui.SameLine();
        if (ImGui.Button("Release W (Emergency)"))
        {
            KeyInputHelper.ReleaseKey(KeyInputHelper.VK_W);
            SetDebugResult("KeyInput: Emergency released W key");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Emergency release if W key gets stuck. Normally not needed.");

        ImGui.Spacing();

        if (ImGui.Button("MovingCheater (Smart)"))
        {
            try
            {
                if (plugin.IpcClient.VnavIsReady())
                {
                    ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
                    var canFly = HasFlightUnlocked();
                    if (canFly)
                    {
                        ChatHelper.SendMessage("/vnav flyflag");
                        SetDebugResult("Smart: Mount + /vnav flyflag (flying unlocked in zone)");
                    }
                    else
                    {
                        ChatHelper.SendMessage("/vnav moveflag");
                        SetDebugResult("Smart: Mount + /vnav moveflag (flying NOT unlocked — ground pathfind)");
                    }
                }
                else SetDebugResult("vnavmesh not ready — cannot navigate");
            }
            catch (Exception ex) { SetDebugResult($"MovingCheater error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mounts + auto-detects flight: uses flyflag if flying unlocked, moveflag otherwise.\nMirrors DoNavFlySequenceXA logic from xafunc.lua (Player.CanFly check).");

        ImGui.SameLine();
        if (ImGui.Button("MovingCheater (Fly)"))
        {
            try
            {
                if (plugin.IpcClient.VnavIsReady())
                {
                    ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
                    ChatHelper.SendMessage("/vnav flyflag");
                    SetDebugResult("Sent: Mount + /vnav flyflag (force fly)");
                }
                else SetDebugResult("vnavmesh not ready — cannot navigate");
            }
            catch (Exception ex) { SetDebugResult($"MovingCheater error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force fly: Mounts + /vnav flyflag. Will fail if flying not unlocked.");

        ImGui.SameLine();
        if (ImGui.Button("MovingCheater (Walk)"))
        {
            try
            {
                if (plugin.IpcClient.VnavIsReady())
                {
                    ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
                    ChatHelper.SendMessage("/vnav moveflag");
                    SetDebugResult("Sent: Mount + /vnav moveflag (force ground)");
                }
                else SetDebugResult("vnavmesh not ready — cannot navigate");
            }
            catch (Exception ex) { SetDebugResult($"MovingCheater error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force ground: Mounts + /vnav moveflag. Works everywhere including towns.");

        ImGui.Spacing();

        if (ImGui.Button("PvpMoveTo (Flag)"))
        {
            ChatHelper.SendMessage("/vnav moveflag");
            SetDebugResult("Sent: /vnav moveflag (PvpMoveToXA — no mount, ground pathfind)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ground pathfinds to map flag without mounting (PvpMoveToXA).\nIn full implementation, waits for casting to finish first.");

        ImGui.Spacing();
        } // end XA Lazy Movements

        ImGui.TreePop();
        } // end Movement Functions

        ImGui.Separator();
        ImGui.Spacing();

        // ╔══════════════════════════════════════════════╗
        // ║  [Player Checkers]                           ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.TreeNode("Player Checkers"))
        {

        // ══════════════════════════════════════════════
        //  Game State Checks (XA)
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Game State Checks (XA)"))
        {
        ImGui.Spacing();

        if (ImGui.Button("CharacterSafeWait"))
        {
            SetDebugResult("CharacterSafeWait: checking...");
            System.Threading.Tasks.Task.Run(async () =>
            {
                int consecutivePasses = 0;
                int totalAttempts = 0;
                while (consecutivePasses < 3)
                {
                    await System.Threading.Tasks.Task.Delay(1000);
                    totalAttempts++;
                    var (np, pa, casting, combat, charName) = await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        return (MonthlyReloggerTask.IsNamePlateReady(),
                                MonthlyReloggerTask.IsPlayerAvailable(),
                                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting],
                                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
                                MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                    });
                    bool ready = np && pa && !casting && !combat;
                    if (ready)
                    {
                        consecutivePasses++;
                        SetDebugResult($"[{consecutivePasses}/3] OK — {charName} (attempt #{totalAttempts})");
                    }
                    else
                    {
                        if (consecutivePasses > 0)
                            Plugin.Log.Information($"[XASlave] CharacterSafeWait: reset at {consecutivePasses}/3 — NP={np} PA={pa} Cast={casting} Combat={combat}");
                        consecutivePasses = 0;
                        SetDebugResult($"[0/3] waiting... NP={np} PA={pa} Cast={casting} Combat={combat} (attempt #{totalAttempts})");
                    }
                }
                var finalName = await Plugin.Framework.RunOnFrameworkThread(() => MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                SetDebugResult($"[3/3] CONFIRMED READY — {finalName}");
            });
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Waits for 3 consecutive passes (1s apart) of:\nNamePlate ready + PlayerAvailable + not casting + not in combat.");

        ImGui.SameLine();
        if (ImGui.Button("GetLevel"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            SetDebugResult(lp != null ? $"Level: {lp.Level}" : "Player not available");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetVnavCoords"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var p = lp.Position;
                var coordStr = $"{p.X:F3}, {p.Y:F3}, {p.Z:F3}";
                ImGui.SetClipboardText(coordStr);
                SetDebugResult($"Coords: {coordStr} (copied)");
            }
            else SetDebugResult("Player not available");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Gets player X,Y,Z coordinates and copies to clipboard (GetVnavCoordsXA)");

        ImGui.Spacing();

        if (ImGui.Button("GetZoneID"))
        {
            var zoneId = Plugin.ClientState.TerritoryType;
            SetDebugResult($"Zone ID: {zoneId}");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetZoneName"))
        {
            try
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
                var row = sheet?.GetRowOrDefault(zoneId);
                var zoneName = row?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
                SetDebugResult($"Zone: {zoneName} [{zoneId}]");
            }
            catch (Exception ex) { SetDebugResult($"Zone lookup error: {ex.Message}"); }
        }
        ImGui.SameLine();
        if (ImGui.Button("GetWorldName"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var worldName = lp.CurrentWorld.ValueNullable?.Name.ToString() ?? "Unknown";
                var worldId = lp.CurrentWorld.RowId;
                SetDebugResult($"World: {worldName} [{worldId}]");
            }
            else SetDebugResult("Player not available");
        }

        ImGui.Spacing();

        if (ImGui.Button("GetPlayerName"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            SetDebugResult(lp != null ? $"Player: {lp.Name}" : "Player not available");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetPlayerNameAndWorld"))
        {
            var name = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
            SetDebugResult($"Character: {name}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsInFreeCompany"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                // CompanyTag is empty string if not in FC
                var fcTag = lp.CompanyTag.ToString();
                var inFc = !string.IsNullOrEmpty(fcTag);
                SetDebugResult($"IsInFC: {inFc} (tag: \"{fcTag}\")");
            }
            else SetDebugResult("Player not available");
        }

        ImGui.Spacing();

        if (ImGui.Button("IsInFCResults"))
        {
            ChatHelper.SendMessage("/freecompanycmd");
            var fcInfo = plugin.IpcClient.GetFcInfo();
            var plotInfo = plugin.IpcClient.GetPlotInfo();
            SetDebugResult($"FC: {fcInfo} | Plot: {plotInfo}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens FC window + reads XA Database FC/Plot data (IsInFreeCompanyResultsXA)");

        ImGui.SameLine();
        if (ImGui.Button("IsInParty"))
        {
            var count = Plugin.PartyList.Length;
            SetDebugResult($"IsInParty: {count > 0} (members: {count})");
        }
        ImGui.SameLine();
        if (ImGui.Button("PartyDisband"))
        {
            ChatHelper.SendMessage("/partycmd disband");
            SetDebugResult("Sent: /partycmd disband (PartyDisbandXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("PartyLeave"))
        {
            ChatHelper.SendMessage("/partycmd leave");
            SetDebugResult("Sent: /partycmd leave (PartyLeaveXA)");
        }

        ImGui.Spacing();

        if (ImGui.Button("SelectYesNo: Yes"))
        {
            var ok = AddonHelper.ClickYesNo(true);
            SetDebugResult(ok ? "SelectYesno: Clicked Yes" : "SelectYesno not visible");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fires SelectYesno callback to click Yes.\nUse after PartyDisband, PartyLeave, Logout, etc.");
        ImGui.SameLine();
        if (ImGui.Button("SelectYesNo: No"))
        {
            var ok = AddonHelper.ClickYesNo(false);
            SetDebugResult(ok ? "SelectYesno: Clicked No" : "SelectYesno not visible");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fires SelectYesno callback to click No.");

        ImGui.Spacing();
        } // end Game State Checks

        // ══════════════════════════════════════════════
        //  Target Game State Checks
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Target Checks"))
        {
        ImGui.Spacing();

        if (ImGui.Button("GetTargetName"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            SetDebugResult(target != null ? $"Target: {target.Name} (ID: {target.GameObjectId:X})" : "No target selected");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetTargetCoords"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target != null)
            {
                var p = target.Position;
                var coordStr = $"{p.X:F3}, {p.Y:F3}, {p.Z:F3}";
                ImGui.SetClipboardText(coordStr);
                SetDebugResult($"Target Coords: {coordStr} (copied)");
            }
            else SetDebugResult("No target selected");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetTargetDistance"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null)
            {
                var lp = local.Position;
                var tp = target.Position;
                var dx = tp.X - lp.X;
                var dy = tp.Y - lp.Y;
                var dz = tp.Z - lp.Z;
                var centerDist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var playerHitbox = local.HitboxRadius;
                var targetHitbox = target.HitboxRadius;
                var ringDist = centerDist - playerHitbox - targetHitbox;
                SetDebugResult($"Distance to {target.Name}: ring={ringDist:F2}y center={centerDist:F2}y (hitbox: player={playerHitbox:F2} target={targetHitbox:F2})");
            }
            else SetDebugResult("No target or player not available");
        }

        ImGui.Spacing();

        if (ImGui.Button("GetTargetKind"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target != null)
                SetDebugResult($"Target: {target.Name} | Kind: {target.ObjectKind} | BaseId: {target.BaseId}");
            else
                SetDebugResult("No target selected");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetTargetHP"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target is Dalamud.Game.ClientState.Objects.Types.IBattleChara bc)
                SetDebugResult($"Target HP: {bc.CurrentHp}/{bc.MaxHp} ({(bc.MaxHp > 0 ? (100.0 * bc.CurrentHp / bc.MaxHp) : 0):F1}%)");
            else if (target != null)
                SetDebugResult($"Target '{target.Name}' is not a battle character (Kind: {target.ObjectKind})");
            else
                SetDebugResult("No target selected");
        }

        ImGui.Spacing();
        } // end Target Checks

        // ══════════════════════════════════════════════
        //  Player State Checks (d)
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Player State Checks (d)"))
        {
        ImGui.Spacing();

        if (ImGui.Button("IsMounted?"))
        {
            var mounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];
            SetDebugResult($"IsMounted: {mounted}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsInCombat?"))
        {
            var combat = Plugin.Condition[ConditionFlag.InCombat];
            SetDebugResult($"IsInCombat: {combat}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsCasting?"))
        {
            var casting = Plugin.Condition[ConditionFlag.Casting];
            SetDebugResult($"IsCasting: {casting}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsFlying?"))
        {
            var flying = Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Diving];
            SetDebugResult($"IsFlying/Diving: {flying}");
        }

        ImGui.Spacing();

        if (ImGui.Button("GetGCRank"))
        {
            try
            {
                unsafe
                {
                    var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                    if (ps != null)
                    {
                        var flames = ps->GCRankImmortalFlames;
                        var adders = ps->GCRankTwinAdders;
                        var mael = ps->GCRankMaelstrom;
                        var highest = Math.Max(flames, Math.Max(adders, mael));
                        SetDebugResult($"GC Ranks: Flames={flames}, Adders={adders}, Mael={mael} (highest={highest})");
                    }
                    else SetDebugResult("PlayerState not available");
                }
            }
            catch (Exception ex) { SetDebugResult($"GCRank error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads all three GC ranks.\nEquivalent to dfunc GetGCRank/GetFlamesGCRank/GetAddersGCRank/GetMaelstromGCRank");

        ImGui.SameLine();
        if (ImGui.Button("PartyMemberCount"))
        {
            var count = Plugin.PartyList.Length;
            var members = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var m = Plugin.PartyList[i];
                if (m != null) members.Append($"{m.Name} (HP:{m.CurrentHP}/{m.MaxHP}), ");
            }
            SetDebugResult($"Party: {count} members. {members}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists party members with HP.\nEquivalent to dfunc BroCheck/GetPartyMemberName");

        ImGui.Spacing();

        if (ImGui.Button("Check All IPC"))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("IPC Availability:");
            sb.AppendLine($"  AutoRetainer    = {plugin.IpcClient.IsAutoRetainerAvailable()}");
            sb.AppendLine($"  Lifestream      = {plugin.IpcClient.IsLifestreamAvailable()}");
            sb.AppendLine($"  TextAdvance     = {plugin.IpcClient.IsTextAdvanceAvailable()}");
            sb.AppendLine($"  vnavmesh        = {plugin.IpcClient.IsVnavAvailable()}");
            sb.AppendLine($"  XA Database     = {plugin.IpcClient.IsXaDatabaseAvailable()}");
            sb.AppendLine($"  YesAlready      = {plugin.IpcClient.IsYesAlreadyAvailable()}");
            sb.AppendLine($"  PandorasBox     = {plugin.IpcClient.IsPandorasBoxAvailable()}");
            sb.AppendLine($"  Deliveroo       = {plugin.IpcClient.IsDeliverooAvailable()}");
            sb.AppendLine($"  Artisan         = {plugin.IpcClient.IsArtisanAvailable()}");
            sb.AppendLine($"  Dropbox         = {plugin.IpcClient.IsDropboxAvailable()}");
            sb.AppendLine($"  Splatoon        = {plugin.IpcClient.IsSplatoonAvailable()}");
            var result = sb.ToString();
            ImGui.SetClipboardText(result);
            SetDebugResult("IPC status copied to clipboard");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks all 11 IPC integrations and copies results to clipboard.\nEquivalent to dfunc GetInternalNamesIPC / GetIPCRegisteredTables");

        ImGui.SameLine();
        if (ImGui.Button("Installed Plugins"))
        {
            try
            {
                var installedPlugins = Plugin.PluginInterface.InstalledPlugins;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Loaded Dalamud Plugins:");
                foreach (var p in installedPlugins.Where(p => p.IsLoaded).OrderBy(p => p.InternalName))
                    sb.AppendLine($"  {p.InternalName}");
                sb.AppendLine($"\nTotal loaded: {installedPlugins.Count(p => p.IsLoaded)}");
                var result = sb.ToString();
                ImGui.SetClipboardText(result);
                SetDebugResult($"Plugin list ({installedPlugins.Count(p => p.IsLoaded)}) copied to clipboard");
            }
            catch (Exception ex) { SetDebugResult($"Plugin list error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists all loaded Dalamud plugins and copies to clipboard.\nEquivalent to dfunc GetInternalNamesIPC");

        ImGui.Spacing();
        } // end Player State Checks

        // ══════════════════════════════════════════════
        //  Character Actions (xafunc equivalents)
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Character Actions"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Remove Sprout"))
        {
            ChatHelper.SendMessage("/nastatus off");
            SetDebugResult("Sent: /nastatus off (RemoveSproutXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Mount Roulette"))
        {
            ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
            SetDebugResult("Sent: /gaction \"Mount Roulette\" (MountUpXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dismount"))
        {
            ChatHelper.SendMessage("/mount");
            SetDebugResult("Sent: /mount (DismountXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Logout"))
        {
            ChatHelper.SendMessage("/logout");
            SetDebugResult("Sent: /logout");
        }

        ImGui.Spacing();

        if (ImGui.Button("Open Inventory"))
        {
            ChatHelper.SendMessage("/inventory");
            SetDebugResult("Sent: /inventory (OpenInventoryXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open Armoury"))
        {
            ChatHelper.SendMessage("/armourychest");
            SetDebugResult("Sent: /armourychest (OpenArmouryChestXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open Saddlebags"))
        {
            ChatHelper.SendMessage("/saddlebag");
            SetDebugResult("Sent: /saddlebag (OpenSaddlebagsXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open FC Window"))
        {
            ChatHelper.SendMessage("/freecompanycmd");
            SetDebugResult("Sent: /freecompanycmd (FreeCompanyCmdXA)");
        }

        ImGui.Spacing();
        } // end Character Actions

        // ══════════════════════════════════════════════
        //  Player Commands
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Player Commands"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Interact"))
        {
            var ok = AddonHelper.InteractWithTarget();
            SetDebugResult(ok ? "InteractWithTarget: OK (InteractXA)" : "InteractWithTarget: No target or failed");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses TargetSystem.InteractWithObject — native replacement for SND /interact");

        ImGui.SameLine();
        if (ImGui.Button("EquipGear (SimpleTweaks)"))
        {
            ChatHelper.SendMessage("/equiprecommended");
            SetDebugResult("Sent: /equiprecommended (SimpleTweaks EquipRecommendedGearCmdXA)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses SimpleTweaks /equiprecommended command. Primary method.");

        ImGui.SameLine();
        if (ImGui.Button("EquipGear Step1: Open"))
        {
            ChatHelper.SendMessage("/character");
            SetDebugResult("Opened Character window — next: Step2 to fire callback");
        }
        ImGui.SameLine();
        if (ImGui.Button("EquipGear Step2: Recommend"))
        {
            var ok = AddonHelper.ClickAddonButton("Character", 74);
            SetDebugResult(ok ? "Clicked Character NodeList[74] (Button #12) → RecommendEquip should open" : "Character addon not visible — open it first with Step1");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks Button Component Node #12 at NodeList[74] in Character addon.\nOpens Recommended Gear window (RecommendEquip).\nConfirmed via /xldata Addon Inspector.");

        ImGui.Spacing();

        if (ImGui.Button("EquipGear Step3: Equip"))
        {
            var ok = AddonHelper.ClickAddonButton("RecommendEquip", 3);
            SetDebugResult(ok ? "Clicked RecommendEquip NodeList[3] (Button #11) → gear equipped" : "RecommendEquip addon not visible — run Step2 first");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks Button Component Node #11 at NodeList[3] in RecommendEquip addon.\nEquips recommended gear.\nConfirmed via /xldata Addon Inspector.");
        ImGui.SameLine();
        if (ImGui.Button("EquipGear: Close"))
        {
            AddonHelper.CloseAddon("RecommendEquip");
            AddonHelper.CloseAddon("Character");
            SetDebugResult("Closed RecommendEquip + Character addons");
        }

        ImGui.Spacing();

        if (ImGui.Button("Reset Camera"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_END);
            SetDebugResult("Sent: VK_END key press (ResetCameraXA) via KeyInputHelper");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Presses END key via Win32 keybd_event — native replacement for SND /send END");

        ImGui.Spacing();
        } // end Player Commands

        // ══════════════════════════════════════════════
        //  XA Database
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("XA Database##playerCheckers"))
        {
        ImGui.Spacing();

        if (ImGui.Button("XA: Save"))
        {
            var ok = plugin.IpcClient.Save();
            SetDebugResult($"XA.Database.Save: {(ok ? "OK" : "FAILED")}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: Refresh"))
        {
            var ok = plugin.IpcClient.Refresh();
            SetDebugResult($"XA.Database.Refresh: {(ok ? "OK" : "FAILED")}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: IsReady?"))
        {
            var ready = plugin.IpcClient.IsReady();
            SetDebugResult($"XA.Database.IsReady: {ready}");
        }

        ImGui.Spacing();

        if (ImGui.Button("XA: GetGil"))
        {
            var gil = plugin.IpcClient.GetGil();
            SetDebugResult($"Gil: {gil:N0}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetRetainerGil"))
        {
            var gil = plugin.IpcClient.GetRetainerGil();
            SetDebugResult($"Retainer Gil: {gil:N0}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetFcInfo"))
        {
            var info = plugin.IpcClient.GetFcInfo();
            SetDebugResult($"FC: {info}");
        }

        ImGui.Spacing();

        if (ImGui.Button("XA: GetPlotInfo"))
        {
            var info = plugin.IpcClient.GetPlotInfo();
            SetDebugResult($"Plot: {info}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetPersonalPlot"))
        {
            var info = plugin.IpcClient.GetPersonalPlotInfo();
            SetDebugResult($"Personal Plot: {info}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetVersion"))
        {
            var ver = plugin.IpcClient.GetVersion();
            SetDebugResult($"XA Database Version: {ver}");
        }

        ImGui.Spacing();
        } // end XA Database

        ImGui.TreePop();
        } // end Player Checkers

        // ╔══════════════════════════════════════════════╗
        // ║  [Punish]                                    ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.TreeNode("Punish"))
        {

        // ══════════════════════════════════════════════
        //  AutoRetainer
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("AutoRetainer##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Disable AR Multi"))
        {
            plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            SetDebugResult("Sent: AR Multi disabled (DisableARMultiXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Enable AR Multi"))
        {
            plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            SetDebugResult("Sent: AR Multi enabled (EnableARMultiXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR IsBusy?"))
        {
            var suppressed = plugin.IpcClient.AutoRetainerGetSuppressed();
            SetDebugResult($"AR Suppressed/Busy: {suppressed}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR Available?"))
        {
            var avail = plugin.IpcClient.IsAutoRetainerAvailable();
            SetDebugResult($"AutoRetainer available: {avail}");
        }

        ImGui.Spacing();

        if (ImGui.Button("ARDiscard"))
        {
            ChatHelper.SendMessage("/ays discard");
            SetDebugResult("Sent: /ays discard (ARDiscard)");
        }

        ImGui.Spacing();
        } // end AutoRetainer

        // ══════════════════════════════════════════════
        //  Lifestream
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Lifestream"))
        {
        ImGui.Spacing();

        if (ImGui.Button("LS: Teleport Home"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("home");
            SetDebugResult("Sent: /li home (return_to_homeXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Teleport FC"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("fc");
            SetDebugResult("Sent: /li fc (return_to_fcXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Home GC"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("hc");
            SetDebugResult("Sent: /li hc (RunToHomeGCXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Abort"))
        {
            plugin.IpcClient.LifestreamAbort();
            SetDebugResult("Sent: Lifestream.Abort()");
        }

        ImGui.Spacing();

        if (ImGui.Button("LS: Homeworld"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("");
            SetDebugResult("Sent: /li (return_to_homeworldXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Auto"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("auto");
            SetDebugResult("Sent: /li auto (return_to_autoXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: IsBusy?"))
        {
            try
            {
                var busy = plugin.IpcClient.LifestreamIsBusy();
                SetDebugResult($"Lifestream IsBusy: {busy}");
            }
            catch (Exception ex) { SetDebugResult($"Lifestream error: {ex.Message}"); }
        }
        ImGui.SameLine();
        if (ImGui.Button("LS Available?"))
        {
            var avail = plugin.IpcClient.IsLifestreamAvailable();
            var busy = avail && plugin.IpcClient.LifestreamIsBusy();
            SetDebugResult($"Lifestream: available={avail}, busy={busy}");
        }

        ImGui.Spacing();
        } // end Lifestream

        // ══════════════════════════════════════════════
        //  TextAdvance
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("TextAdvance##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Enable TextAdvance"))
        {
            ChatHelper.SendMessage("/at y");
            SetDebugResult("Sent: /at y (EnableTextAdvanceXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Disable TextAdvance"))
        {
            ChatHelper.SendMessage("/at n");
            SetDebugResult("Sent: /at n (DisableTextAdvanceXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("TA Available?"))
        {
            var avail = plugin.IpcClient.IsTextAdvanceAvailable();
            SetDebugResult($"TextAdvance available: {avail}");
        }

        ImGui.Spacing();
        } // end TextAdvance

        // ══════════════════════════════════════════════
        //  YesAlready
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("YesAlready##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("YA: Enable"))
        {
            plugin.IpcClient.YesAlreadySetEnabled(true);
            SetDebugResult("YesAlready: Enabled");
        }
        ImGui.SameLine();
        if (ImGui.Button("YA: Disable"))
        {
            plugin.IpcClient.YesAlreadySetEnabled(false);
            SetDebugResult("YesAlready: Disabled");
        }
        ImGui.SameLine();
        if (ImGui.Button("YA: IsEnabled?"))
        {
            var enabled = plugin.IpcClient.YesAlreadyIsEnabled();
            SetDebugResult($"YesAlready IsEnabled: {enabled}");
        }
        ImGui.SameLine();
        if (ImGui.Button("YA: Pause 5s"))
        {
            plugin.IpcClient.YesAlreadyPause(5000);
            SetDebugResult("YesAlready: Paused for 5 seconds");
        }

        ImGui.SameLine();
        if (ImGui.Button("YA Available?"))
        {
            var avail = plugin.IpcClient.IsYesAlreadyAvailable();
            SetDebugResult($"YesAlready available: {avail}");
        }

        ImGui.Spacing();
        } // end YesAlready

        // ══════════════════════════════════════════════
        //  Artisan
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Artisan##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Art: Enable"))
        {
            ChatHelper.SendMessage("/xlenableprofile Artisan");
            SetDebugResult("Sent: /xlenableprofile Artisan (EnableArtisanXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: Disable"))
        {
            ChatHelper.SendMessage("/xldisableprofile Artisan");
            SetDebugResult("Sent: /xldisableprofile Artisan (DisableArtisanXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: IsBusy?"))
        {
            var avail = plugin.IpcClient.IsArtisanAvailable();
            var busy = avail && plugin.IpcClient.ArtisanIsBusy();
            SetDebugResult($"Artisan: avail={avail}, busy={busy}");
        }

        ImGui.Spacing();

        if (ImGui.Button("Art: GetEndurance"))
        {
            var status = plugin.IpcClient.ArtisanGetEnduranceStatus();
            SetDebugResult($"Artisan Endurance: {status}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: EnduranceOn"))
        {
            plugin.IpcClient.ArtisanSetEnduranceStatus(true);
            SetDebugResult("Artisan Endurance: ON");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: EnduranceOff"))
        {
            plugin.IpcClient.ArtisanSetEnduranceStatus(false);
            SetDebugResult("Artisan Endurance: OFF");
        }

        ImGui.Spacing();

        if (ImGui.Button("Art: IsListRunning?"))
        {
            var running = plugin.IpcClient.ArtisanIsListRunning();
            SetDebugResult($"Artisan ListRunning: {running}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: IsListPaused?"))
        {
            var paused = plugin.IpcClient.ArtisanIsListPaused();
            SetDebugResult($"Artisan ListPaused: {paused}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: PauseList"))
        {
            plugin.IpcClient.ArtisanSetListPause(true);
            SetDebugResult("Artisan List: Paused");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: ResumeList"))
        {
            plugin.IpcClient.ArtisanSetListPause(false);
            SetDebugResult("Artisan List: Resumed");
        }

        ImGui.Spacing();

        if (ImGui.Button("Art: GetStopReq"))
        {
            var stop = plugin.IpcClient.ArtisanGetStopRequest();
            SetDebugResult($"Artisan StopRequest: {stop}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: SetStop"))
        {
            plugin.IpcClient.ArtisanSetStopRequest(true);
            SetDebugResult("Artisan StopRequest: true");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: ClearStop"))
        {
            plugin.IpcClient.ArtisanSetStopRequest(false);
            SetDebugResult("Artisan StopRequest: false (cleared)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art Available?"))
        {
            var avail = plugin.IpcClient.IsArtisanAvailable();
            SetDebugResult($"Artisan available: {avail}");
        }

        ImGui.Spacing();
        } // end Artisan

        // ══════════════════════════════════════════════
        //  Dropbox
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Dropbox"))
        {
        ImGui.Spacing();

        if (ImGui.Button("OpenDropbox"))
        {
            ChatHelper.SendMessage("/dropbox");
            ChatHelper.SendMessage("/dropbox OpenTradeTab");
            SetDebugResult("Sent: /dropbox + /dropbox OpenTradeTab (OpenDropboxXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dropbox IsBusy?"))
        {
            var busy = plugin.IpcClient.DropboxIsBusy();
            SetDebugResult($"Dropbox IsBusy: {busy}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dropbox Available?"))
        {
            var avail = plugin.IpcClient.IsDropboxAvailable();
            var busy = avail && plugin.IpcClient.DropboxIsBusy();
            SetDebugResult($"Dropbox: available={avail}, busy={busy}");
        }

        ImGui.Spacing();
        } // end Dropbox

        // ══════════════════════════════════════════════
        //  Pandoras Box
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Pandoras Box"))
        {
        ImGui.Spacing();

        if (ImGui.Button("EnableSprintInTown"))
        {
            var ok = plugin.IpcClient.PandoraSetFeatureEnabled("Auto-Sprint in Sanctuaries", true);
            SetDebugResult($"PandorasBox Auto-Sprint enabled: {ok} (EnableSprintingInTownXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("DisableSprintInTown"))
        {
            var ok = plugin.IpcClient.PandoraSetFeatureEnabled("Auto-Sprint in Sanctuaries", false);
            SetDebugResult($"PandorasBox Auto-Sprint disabled: {ok} (DisableSprintingInTownXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("PandorasBox?"))
        {
            var avail = plugin.IpcClient.IsPandorasBoxAvailable();
            SetDebugResult($"PandorasBox available: {avail}");
        }

        ImGui.Spacing();
        } // end Pandoras Box

        // ══════════════════════════════════════════════
        //  Deliveroo
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Deliveroo"))
        {
        ImGui.Spacing();

        if (ImGui.Button("EnableDeliveroo"))
        {
            ChatHelper.SendMessage("/deliveroo enable");
            SetDebugResult("Sent: /deliveroo enable (EnableDeliverooXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Deliveroo Running?"))
        {
            var running = plugin.IpcClient.DeliverooIsTurnInRunning();
            SetDebugResult($"Deliveroo turn-in running: {running}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Deliveroo?"))
        {
            var avail = plugin.IpcClient.IsDeliverooAvailable();
            SetDebugResult($"Deliveroo available: {avail}");
        }

        ImGui.Spacing();
        } // end Deliveroo

        // ══════════════════════════════════════════════
        //  Splatoon
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Splatoon"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Splatoon?"))
        {
            var avail = plugin.IpcClient.IsSplatoonAvailable();
            SetDebugResult($"Splatoon available: {avail}");
        }

        ImGui.Spacing();
        } // end Splatoon

        // ══════════════════════════════════════════════
        //  vnavmesh
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("vnavmesh##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("vnav: IsReady?"))
        {
            var ready = plugin.IpcClient.VnavIsReady();
            SetDebugResult($"vnavmesh IsReady: {ready}");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: PathRunning?"))
        {
            var running = plugin.IpcClient.VnavPathIsRunning();
            SetDebugResult($"vnavmesh PathIsRunning: {running}");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: Stop"))
        {
            plugin.IpcClient.VnavStop();
            SetDebugResult("Sent: vnavmesh.Path.Stop()");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: Rebuild"))
        {
            plugin.IpcClient.VnavRebuild();
            SetDebugResult("Sent: vnavmesh.Nav.Rebuild()");
        }

        ImGui.Spacing();

        if (ImGui.Button("HasFlightUnlocked?"))
        {
            var canFly = HasFlightUnlocked();
            SetDebugResult($"HasFlightUnlocked: {canFly} (zone {Plugin.ClientState.TerritoryType})");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses PlayerState.IsAetherCurrentZoneComplete.\nDirect equivalent of dfunc HasFlightUnlocked() / Player.CanFly.");

        ImGui.SameLine();
        if (ImGui.Button("InSanctuary?"))
        {
            var inSanc = InSanctuary();
            SetDebugResult($"InSanctuary: {inSanc} (CanMount: {!inSanc})");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks if player cannot mount → sanctuary.\nEquivalent to dfunc InSanctuary() / !Player.CanMount");

        ImGui.SameLine();
        if (ImGui.Button("vnav Available?"))
        {
            var avail = plugin.IpcClient.IsVnavAvailable();
            SetDebugResult($"vnavmesh available: {avail}");
        }

        ImGui.Spacing();
        } // end vnavmesh

        ImGui.TreePop();
        } // end Punish

        // ╔══════════════════════════════════════════════╗
        // ║  [Key Inputs]                                ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.CollapsingHeader("Key Inputs"))
        {
        ImGui.TextDisabled("Win32 keybd_event key simulation for FFXIV input");
        ImGui.Spacing();

        ImGui.TextDisabled("Methods:");
        ImGui.TextDisabled("  PressKey(vk)                — tap key (down+up)");
        ImGui.TextDisabled("  HoldKey(vk)                 — key down only");
        ImGui.TextDisabled("  ReleaseKey(vk)              — key up only");
        ImGui.TextDisabled("  HoldKeyForDuration(vk, ms)  — hold + auto-release");
        ImGui.Spacing();

        ImGui.TextDisabled("Available VK Constants:");
        ImGui.TextDisabled("  Movement:  VK_W (0x57)  VK_A (0x41)  VK_S (0x53)  VK_D (0x44)");
        ImGui.TextDisabled("  Special:   VK_END (0x23)  VK_HOME (0x24)  VK_ESCAPE (0x1B)  VK_RETURN (0x0D)");
        ImGui.TextDisabled("             VK_SPACE (0x20)  VK_TAB (0x09)  VK_DELETE (0x2E)  VK_INSERT (0x2D)");
        ImGui.TextDisabled("  Arrow:     VK_LEFT (0x25)  VK_UP (0x26)  VK_RIGHT (0x27)  VK_DOWN (0x28)");
        ImGui.TextDisabled("  Modifier:  VK_SHIFT (0x10)  VK_CONTROL (0x11)  VK_ALT (0x12)");
        ImGui.TextDisabled("  Numpad:    VK_NUMPAD0–9 (0x60–0x69)");
        ImGui.TextDisabled("  Function:  VK_F1–F12 (0x70–0x7B)");
        ImGui.TextDisabled("  Letters:   0x41–0x5A (A–Z)    Numbers: 0x30–0x39 (0–9)");

        ImGui.Spacing();
        } // end Key Inputs

        // ╔══════════════════════════════════════════════╗
        // ║  [Braindead Functions]                        ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.CollapsingHeader("Braindead Functions"))
        {
        ImGui.TextDisabled("Multi-step scripted sequences. These will be implemented as full task chains.");
        ImGui.Spacing();

        ImGui.TextDisabled("• FreshLimsaToSummer — Complete Limsa intro → Summerford Farms");
        ImGui.TextDisabled("• FreshLimsaToMist — Limsa intro → Summerford → Mist housing");
        ImGui.TextDisabled("• FreshUldahToHorizon — Ul'dah intro → Horizon");
        ImGui.TextDisabled("• FreshUldahToGoblet — Ul'dah intro → Horizon → Goblet housing");
        ImGui.TextDisabled("• FreshGridaniaToBentbranch — Gridania intro → Bentbranch Meadows");
        ImGui.TextDisabled("• FreshGridaniaToBeds — Gridania intro → Bentbranch → Lavender Beds");
        ImGui.TextDisabled("• ImNotNewbStopWatching — Remove sprout + enable TA + set camera");
        ImGui.TextDisabled("• EnterHousingWardFromMenu — Navigate housing ward selection");
        } // end Braindead Functions
    }

    private void SetDebugResult(string msg)
    {
        debugResult = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        debugResultExpiry = DateTime.UtcNow.AddSeconds(15);
        Plugin.Log.Information($"[XASlave] Debug: {msg}");
    }

    /// <summary>
    /// Checks if flying is unlocked in the current zone.
    /// Uses PlayerState.CanFly field (offset 0x601) — set during zone loading.
    /// This is the direct equivalent of SND's Player.CanFly / dfunc HasFlightUnlocked().
    /// </summary>
    private static unsafe bool HasFlightUnlocked()
    {
        try
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps == null)
            {
                Plugin.Log.Warning("[XASlave] HasFlightUnlocked: PlayerState.Instance() returned null");
                return false;
            }
            var territory = Plugin.ClientState.TerritoryType;
            var canFly = ps->CanFly;
            Plugin.Log.Information($"[XASlave] HasFlightUnlocked: territory={territory}, CanFly={canFly}");
            return canFly;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] HasFlightUnlocked error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if the player can mount in the current location.
    /// Equivalent to dfunc Player.CanMount (inverse of InSanctuary for mount-blocked zones).
    /// Uses ActionManager to check if Mount Roulette (GeneralAction 24) is usable.
    /// </summary>
    private static unsafe bool CanMount()
    {
        try
        {
            return FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 24) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks if the player is in a sanctuary — equivalent to dfunc InSanctuary().
    /// Returns true when the player CANNOT mount (inverse of CanMount).
    /// Matches SND's Player.CanMount logic: if CanMount == false then InSanctuary.
    /// </summary>
    private static unsafe bool InSanctuary()
    {
        return !CanMount();
    }

    // ───────────────────────────────────────────────
    //  IPC Calls Available
    // ───────────────────────────────────────────────
    private void RefreshIpcCache()
    {
        if (DateTime.UtcNow < ipcCacheExpiry) return;
        ipcCacheExpiry = DateTime.UtcNow.AddSeconds(5);
        cachedArAvail = plugin.IpcClient.IsAutoRetainerAvailable();
        cachedLsAvail = plugin.IpcClient.IsLifestreamAvailable();
        cachedYaAvail = plugin.IpcClient.IsYesAlreadyAvailable();
        cachedDelAvail = plugin.IpcClient.IsDeliverooAvailable();
        cachedPbAvail = plugin.IpcClient.IsPandorasBoxAvailable();
        cachedDbxAvail = plugin.IpcClient.IsDropboxAvailable();
        cachedTaAvail = plugin.IpcClient.IsTextAdvanceAvailable();
        cachedArtAvail = plugin.IpcClient.IsArtisanAvailable();
        cachedSplatAvail = plugin.IpcClient.IsSplatoonAvailable();
    }

    private void RefreshLiveIpcValues()
    {
        if (!plugin.Configuration.IpcLivePullsEnabled) return;
        if (DateTime.UtcNow < liveIpcExpiry) return;
        liveIpcExpiry = DateTime.UtcNow.AddSeconds(plugin.Configuration.IpcLivePullIntervalSeconds);

        try
        {
            // XA Database
            liveIpcValues["XA.Database.IsReady"] = plugin.IpcClient.IsReady();

            // vnavmesh
            liveIpcValues["vnavmesh.Nav.IsReady"] = plugin.IpcClient.VnavIsReady();
            liveIpcValues["vnavmesh.Path.IsRunning"] = plugin.IpcClient.VnavPathIsRunning();
            liveIpcValues["vnavmesh.Nav.PathfindInProgress"] = plugin.IpcClient.VnavNavPathfindInProgress();
            liveIpcValues["vnavmesh.SimpleMove.PathfindInProgress"] = plugin.IpcClient.VnavSimpleMovePathfindInProgress();

            // AutoRetainer
            if (cachedArAvail)
            {
                liveIpcValues["AutoRetainer.GetSuppressed"] = plugin.IpcClient.AutoRetainerGetSuppressed();
                liveIpcValues["AutoRetainer.GetMultiModeEnabled"] = plugin.IpcClient.AutoRetainerGetMultiModeEnabled();
            }

            // Lifestream
            if (cachedLsAvail)
                liveIpcValues["Lifestream.IsBusy"] = plugin.IpcClient.LifestreamIsBusy();

            // YesAlready
            if (cachedYaAvail)
                liveIpcValues["YesAlready.IsPluginEnabled"] = plugin.IpcClient.YesAlreadyIsEnabled();

            // Deliveroo
            if (cachedDelAvail)
                liveIpcValues["Deliveroo.IsTurnInRunning"] = plugin.IpcClient.DeliverooIsTurnInRunning();

            // Dropbox
            if (cachedDbxAvail)
                liveIpcValues["Dropbox.IsBusy"] = plugin.IpcClient.DropboxIsBusy();

            // TextAdvance
            if (cachedTaAvail)
            {
                liveIpcValues["TextAdvance.IsEnabled"] = plugin.IpcClient.TextAdvanceIsEnabled();
                liveIpcValues["TextAdvance.IsBusy"] = plugin.IpcClient.TextAdvanceIsBusy();
                liveIpcValues["TextAdvance.IsPaused"] = plugin.IpcClient.TextAdvanceIsPaused();
            }

            // Artisan
            if (cachedArtAvail)
            {
                liveIpcValues["Artisan.IsBusy"] = plugin.IpcClient.ArtisanIsBusy();
                liveIpcValues["Artisan.GetEnduranceStatus"] = plugin.IpcClient.ArtisanGetEnduranceStatus();
                liveIpcValues["Artisan.IsListRunning"] = plugin.IpcClient.ArtisanIsListRunning();
                liveIpcValues["Artisan.IsListPaused"] = plugin.IpcClient.ArtisanIsListPaused();
                liveIpcValues["Artisan.GetStopRequest"] = plugin.IpcClient.ArtisanGetStopRequest();
            }

            // Splatoon
            if (cachedSplatAvail)
                liveIpcValues["Splatoon.IsLoaded"] = plugin.IpcClient.SplatoonIsLoaded();
        }
        catch { /* individual failures already handled in IpcClient try/catch */ }
    }

    private void DrawIpcCallsAvailable()
    {
        RefreshIpcCache();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "IPC Calls Available");
        ImGui.SameLine();
        var livePulls = plugin.Configuration.IpcLivePullsEnabled;
        if (ImGui.Checkbox("Show Live Pulls", ref livePulls))
        {
            plugin.Configuration.IpcLivePullsEnabled = livePulls;
            plugin.Configuration.Save();
            if (!livePulls) liveIpcValues.Clear();
            else liveIpcExpiry = DateTime.MinValue;
        }
        if (livePulls)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            var interval = plugin.Configuration.IpcLivePullIntervalSeconds;
            if (ImGui.SliderInt("##IpcInterval", ref interval, 3, 30, $"{interval}s"))
            {
                plugin.Configuration.IpcLivePullIntervalSeconds = interval;
                plugin.Configuration.Save();
            }
            RefreshLiveIpcValues();
        }
        ImGui.TextDisabled("Shows all IPC integrations available to XA Slave (11 plugins, 52 calls).");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── XA Database ──
        var dbReady = plugin.IpcClient.IsReady();
        var dbVersion = plugin.IpcClient.GetVersion();
        DrawPluginStatus("XA Database", dbReady, dbReady ? $"v{dbVersion}" : null);

        var cols = livePulls ? 4 : 3;

        if (ImGui.BeginTable("IpcXaDb", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("XA.Database.Save", "Action", "Refresh + save snapshot to DB", livePulls);
            DrawIpcRow("XA.Database.Refresh", "Action", "Refresh live data only", livePulls);
            DrawIpcRow("XA.Database.IsReady", "bool", "True when player is loaded", livePulls);
            DrawIpcRow("XA.Database.GetVersion", "string", "Plugin version", livePulls);
            DrawIpcRow("XA.Database.GetDbPath", "string", "Path to xa.db", livePulls);
            DrawIpcRow("XA.Database.GetCharacterName", "string", "Current character name", livePulls);
            DrawIpcRow("XA.Database.GetGil", "int", "Character gil", livePulls);
            DrawIpcRow("XA.Database.GetRetainerGil", "int", "Total retainer gil", livePulls);
            DrawIpcRow("XA.Database.GetFcInfo", "string", "FC Name|Tag|Points|Rank", livePulls);
            DrawIpcRow("XA.Database.GetPlotInfo", "string", "FC estate location", livePulls);
            DrawIpcRow("XA.Database.GetPersonalPlotInfo", "string", "Estate|Apartment", livePulls);
            DrawIpcRow("XA.Database.SearchItems", "string", "Item search (takes query)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── vnavmesh ──
        var vnavReady = plugin.IpcClient.VnavIsReady();
        DrawPluginStatus("vnavmesh", vnavReady, null);

        if (ImGui.BeginTable("IpcVnav", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 340);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("vnavmesh.Nav.IsReady", "bool", "Nav mesh built and ready", livePulls);
            DrawIpcRow("vnavmesh.Nav.Rebuild", "Action", "Rebuild nav mesh", livePulls);
            DrawIpcRow("vnavmesh.SimpleMove.PathfindAndMoveTo", "bool", "Pathfind + move to Vector3 (pos, fly)", livePulls);
            DrawIpcRow("vnavmesh.Path.IsRunning", "bool", "Path movement active", livePulls);
            DrawIpcRow("vnavmesh.Nav.PathfindInProgress", "bool", "Nav pathfinding calculation active", livePulls);
            DrawIpcRow("vnavmesh.SimpleMove.PathfindInProgress", "bool", "SimpleMove pathfinding active", livePulls);
            DrawIpcRow("vnavmesh.Path.Stop", "Action", "Stop current path", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── AutoRetainer ──
        DrawPluginStatus("AutoRetainer", cachedArAvail, null);

        if (ImGui.BeginTable("IpcAR", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("AutoRetainer.GetSuppressed", "bool", "True when AR is suppressed/paused", livePulls);
            DrawIpcRow("AutoRetainer.SetSuppressed", "Action", "Set suppressed state (bool)", livePulls);
            DrawIpcRow("AutoRetainer.GetMultiModeEnabled", "bool", "Multi-mode enabled state", livePulls);
            DrawIpcRow("AutoRetainer.SetMultiModeEnabled", "Action", "Toggle multi-mode (bool)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Lifestream ──
        DrawPluginStatus("Lifestream", cachedLsAvail, null);

        if (ImGui.BeginTable("IpcLS", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Lifestream.IsBusy", "bool", "True during world/DC travel", livePulls);
            DrawIpcRow("Lifestream.Abort", "Action", "Cancel current travel operation", livePulls);
            DrawIpcRow("Lifestream.ExecuteCommand", "Action", "Execute command string", livePulls);
            DrawIpcRow("Lifestream.ChangeWorld", "bool", "Change to world by name", livePulls);
            DrawIpcRow("Lifestream.TeleportToFC", "bool", "Teleport to FC estate", livePulls);
            DrawIpcRow("Lifestream.TeleportToHome", "bool", "Teleport to personal house", livePulls);
            DrawIpcRow("Lifestream.TeleportToApartment", "bool", "Teleport to apartment", livePulls);
            DrawIpcRow("Lifestream.AethernetTeleport", "bool", "Aethernet teleport by name", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── YesAlready ──
        DrawPluginStatus("YesAlready", cachedYaAvail, null);

        if (ImGui.BeginTable("IpcYA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("YesAlready.IsPluginEnabled", "bool", "Check if YesAlready is enabled", livePulls);
            DrawIpcRow("YesAlready.SetPluginEnabled", "Action", "Enable/disable YesAlready (bool)", livePulls);
            DrawIpcRow("YesAlready.PausePlugin", "Action", "Pause for N milliseconds (int)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Deliveroo ──
        DrawPluginStatus("Deliveroo", cachedDelAvail, null);

        if (ImGui.BeginTable("IpcDel", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Deliveroo.IsTurnInRunning", "bool", "True during GC turn-in", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── PandorasBox ──
        DrawPluginStatus("PandorasBox", cachedPbAvail, null);

        if (ImGui.BeginTable("IpcPB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("PandorasBox.GetFeatureEnabled", "bool?", "Get feature enabled state by name", livePulls);
            DrawIpcRow("PandorasBox.SetFeatureEnabled", "Action", "Enable/disable a feature (name, bool)", livePulls);
            DrawIpcRow("PandorasBox.PauseFeature", "Action", "Pause feature for N ms (name, int)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Dropbox ──
        DrawPluginStatus("Dropbox", cachedDbxAvail, null);

        if (ImGui.BeginTable("IpcDB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Dropbox.SetItemQuantity", "Action", "Queue item (id, hq, qty)", livePulls);
            DrawIpcRow("Dropbox.IsBusy", "bool", "True when trading", livePulls);
            DrawIpcRow("Dropbox.BeginTradingQueue", "Action", "Start trading queued items", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── TextAdvance ──
        DrawPluginStatus("TextAdvance", cachedTaAvail, null);

        if (ImGui.BeginTable("IpcTA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("TextAdvance.IsEnabled", "bool", "Check if TextAdvance is enabled", livePulls);
            DrawIpcRow("TextAdvance.IsBusy", "bool", "True when task manager is busy", livePulls);
            DrawIpcRow("TextAdvance.IsPaused", "bool", "True when paused/blocked", livePulls);
            DrawIpcRow("TextAdvance.Stop", "Action", "Stop current task + movement", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Artisan ──
        DrawPluginStatus("Artisan", cachedArtAvail, null);

        if (ImGui.BeginTable("IpcArt", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Artisan.IsBusy", "bool", "True when crafting/processing", livePulls);
            DrawIpcRow("Artisan.GetEnduranceStatus", "bool", "Endurance mode enabled", livePulls);
            DrawIpcRow("Artisan.SetEnduranceStatus", "Action", "Toggle endurance (bool)", livePulls);
            DrawIpcRow("Artisan.IsListRunning", "bool", "Crafting list active", livePulls);
            DrawIpcRow("Artisan.IsListPaused", "bool", "Crafting list paused", livePulls);
            DrawIpcRow("Artisan.SetListPause", "Action", "Pause/resume list (bool)", livePulls);
            DrawIpcRow("Artisan.GetStopRequest", "bool", "Stop requested by external", livePulls);
            DrawIpcRow("Artisan.SetStopRequest", "Action", "Request stop/restart (bool)", livePulls);
            DrawIpcRow("Artisan.CraftItem", "Action", "Craft recipe x times (id, amount)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Splatoon ──
        DrawPluginStatus("Splatoon", cachedSplatAvail, null);

        if (ImGui.BeginTable("IpcSplat", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Splatoon.IsLoaded", "bool", "True when Splatoon is loaded", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("All calls are try/catch wrapped — missing plugins will not crash XA Slave.");
        ImGui.TextDisabled("Channel names verified from each plugin's IPC source code on GitHub.");
        ImGui.TextDisabled("Connectivity refreshed every 5 seconds.");
    }

    private static void DrawPluginStatus(string name, bool? connected, string? extra)
    {
        if (connected == true)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), extra != null ? $"{name}: Connected ({extra})" : $"{name}: Connected");
        else if (connected == false)
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"{name}: Not available");
        else
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), name);
        ImGui.Spacing();
    }

    private void DrawIpcRow(string channel, string type, string description, bool showValue = false)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.6f, 1.0f), channel);
        ImGui.TableNextColumn();
        ImGui.Text(type);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(description);
        if (showValue)
        {
            ImGui.TableNextColumn();
            if (type == "bool" && liveIpcValues.TryGetValue(channel, out var val) && val.HasValue)
            {
                if (val.Value)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "true");
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "false");
            }
            else
            {
                ImGui.TextDisabled("-");
            }
        }
    }

    // ───────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────
    private void RunAutoCollection()
    {
        if (plugin.AutoCollector.IsRunning) return;
        plugin.AutoCollector.StartCollection(
            plugin.Configuration.AutoCollectSaddlebag,
            plugin.Configuration.AutoCollectFc,
            () =>
            {
                // On completion, send IPC save to XA Database
                if (plugin.IpcClient.Save())
                    SetIpcResult("Collection complete — saved to XA Database");
                else
                    SetIpcResult("Collection complete — XA Database save failed");
            });
    }

    private void SetIpcResult(string message)
    {
        lastIpcResult = message;
        lastIpcResultExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void DrawStatusBar()
    {
        ImGui.TextDisabled($"XA Slave v{PluginVersion}");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled("/xa to toggle");

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), plugin.AutoCollector.StatusText);
        }

        if (plugin.TaskRunner.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            var label = plugin.TaskRunner.TotalItems > 0
                ? $"{plugin.TaskRunner.CurrentTaskName}: {plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}"
                : plugin.TaskRunner.CurrentTaskName;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), label);
        }
    }
}
