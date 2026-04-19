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
/// Refresh AR Subs/Bell — Rotates through characters, teleports to FC house,
/// interacts with Voyage Control Panel (submarine console) and Summoning Bell
/// to ensure AutoRetainer has all submarine data correctly.
///
/// Converted from: Rotate toons, refresh sub console.lua
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
    // ── Refresh AR Subs/Bell state ──
    private readonly HashSet<int> refreshSubsSelectedIndices = new();
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
        refreshSubsDoOpenArmoury = config.AutoCollectArmouryChest;
        refreshSubsDoOpenSaddlebags = config.AutoCollectSaddlebag;
        refreshSubsDoOpenJournal = config.AutoCollectJournal;
        refreshSubsDoCollectPersonalPlotInfo = config.AutoCollectPersonalPlotInfo;
        refreshSubsDoReturnToHome = config.AutoCollectPersonalPlotInfo;
        refreshSubsActionOptionsInitialized = true;
    }

    private void DrawRefreshArSubsBellTask()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.RefreshSubsCharacters;
        EnsureRefreshSubsActionOptionsInitialized();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Refresh Sub/Bell/Chest");
        ImGui.TextDisabled("Rotate characters, teleport to FC house, interact with sub console & summoning bell.\n\nTHIS REQUIRES LIFESTREAM TO HAVE PATH TO THE DOOR\nAND TO ENTER THE HOUSE, IF NOT IT WILL FAIL!");
        ImGui.Spacing();

        // ── Import / Refresh buttons ──
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (ImGui.Button("Import from AutoRetainer##refreshSubsImportAR"))
        {
            try
            {
                var (added, total) = ImportCharactersFromArToList(chars);
                cfg.Save();
                arImportStatus = added > 0
                    ? $"Imported {added} new ({total} total)"
                    : $"All {total} already in list";
                arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            }
            catch (Exception ex)
            {
                arImportStatus = $"Import failed: {ex.Message}";
                arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            }
        }
        if (!arConfigExists) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(arConfigExists
                ? "Read AutoRetainer's DefaultConfig.json to import all characters.\nPath: " + plugin.ArConfigReader.GetAutoRetainerConfigPath()
                : "AutoRetainer config not found.\nExpected: " + plugin.ArConfigReader.GetAutoRetainerConfigPath());

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        if (!xaDbAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Pull XA Database Info##refreshSubsPullXA"))
            PullXaDatabaseInfo();
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? "Read XA Database to update retainer/submarine counts for all characters."
                : "XA Database plugin not available.");

        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), arImportStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Plugin status
        DrawTaskPluginStatus(refreshSubsDoCollectPersonalPlotInfo);

        // Config
        ImGui.Checkbox("##refreshSubsWorkshop", ref refreshSubsGoWorkshop);
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Enter Workshop and tap the Voyage Console and Summoning bell to refresh AutoRetainer.");
        ImGui.TextUnformatted("While inside the workshop, XA Slave also checks Company Chest gil for XA Database before resuming AR.");
        ImGui.TextUnformatted("If not selected it will only collect the summoning bell inside the plot.");
        ImGui.Spacing();
        ImGui.EndGroup();
        if (ImGui.IsItemClicked())
            refreshSubsGoWorkshop = !refreshSubsGoWorkshop;

        ImGui.SetNextItemWidth(Scale(80f));
        var wait = refreshSubsExtraWait;
        if (ImGui.InputFloat("Extra Wait (sec)##refreshSubsWait", ref wait, 0.5f, 1.0f, "%.1f"))
        {
            if (wait < 0f) wait = 0f;
            if (wait > 30f) wait = 30f;
            refreshSubsExtraWait = wait;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Run controls ──
        var isRunning = plugin.TaskRunner.IsRunning
            && (plugin.TaskRunner.CurrentTaskName == "Refresh Sub/Bell/Chest"
                || plugin.TaskRunner.CurrentTaskName == "Refresh AR Subs/Bell");
        if (isRunning)
        {
            var progress = plugin.TaskRunner.TotalItems > 0 ? (float)plugin.TaskRunner.CompletedItems / plugin.TaskRunner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {plugin.TaskRunner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}");
            if (!string.IsNullOrEmpty(plugin.TaskRunner.CurrentItemLabel))
                ImGui.TextDisabled(plugin.TaskRunner.CurrentItemLabel);
            DrawProcessingList(plugin.TaskRunner);
            ImGui.Spacing();
            if (ImGui.Button("Cancel##refreshSubsCancel"))
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
                $"Start ({selectedChars.Count} chars)##refreshSubsStart",
                canStart,
                StartRefreshArSubsBell,
                !plugin.IpcClient.IsAutoRetainerAvailable()
                    || !plugin.IpcClient.IsLifestreamAvailable()
                    || (refreshSubsDoCollectPersonalPlotInfo && !plugin.IpcClient.IsXaDatabaseAvailable())
                    ? "Missing required plugins. Check the plugin status above."
                    : "Select at least one character to start.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref refreshSubsShowLog);

            ImGui.SameLine();
            if (ImGui.Button("Check All##refreshSubsAll"))
                SelectVisibleRefreshSubsCharacters();
            ImGui.SameLine();
            if (ImGui.Button("Clear All##refreshSubsNone"))
                refreshSubsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character List ──
        DrawCharacterListHeader("Character List", $"({chars.Count} total)", "refreshSubsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        var refreshSubsRegionFilter = cfg.RefreshSubsRegionFilter;
        if (DrawRegionFilterCombo("Region##refreshSubsRegion", ref refreshSubsRegionFilter))
        {
            cfg.RefreshSubsRegionFilter = refreshSubsRegionFilter;
            cfg.Save();
        }

        ImGui.SameLine();
        // Search filter
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##refreshSubsSearch", "Search name or world...", ref refreshSubsSearchFilter, 128);
        ImGui.Spacing();

        // Character table — columns: checkbox, #, character, world, retainers, submarines, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        if (ImGui.BeginTable("RefreshSubsCharTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(30f));
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, Scale(65f));
            ImGui.TableSetupColumn("Submarines", ImGuiTableColumnFlags.WidthFixed, Scale(75f));
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(25f));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            // Build filtered list
            var filtered = new List<(int OrigIdx, string CharName, string World, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
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
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##rsRm{i}"))
                {
                    chars.RemoveAt(i);
                    refreshSubsSelectedIndices.Remove(i);
                    var newSet = new HashSet<int>();
                    foreach (var idx in refreshSubsSelectedIndices)
                        newSet.Add(idx > i ? idx - 1 : idx);
                    refreshSubsSelectedIndices.Clear();
                    foreach (var idx in newSet) refreshSubsSelectedIndices.Add(idx);
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
        var enterPressed = ImGui.InputTextWithHint("##refreshSubsAdd", "Name Surname@World", ref refreshSubsNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add##refreshSubsAddBtn") || enterPressed) && !string.IsNullOrWhiteSpace(refreshSubsNewChar))
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
        ImGui.TextDisabled("Format: FirstName LastName@World");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Actions Per Character");
        ImGui.Spacing();
        ImGui.Checkbox("Open Armoury Chest##refreshSubsArmoury", ref refreshSubsDoOpenArmoury);
        ImGui.Checkbox("Open Saddlebags##refreshSubsSaddlebags", ref refreshSubsDoOpenSaddlebags);
        ImGui.Checkbox("Open Journal##refreshSubsJournal", ref refreshSubsDoOpenJournal);
        ImGui.Checkbox("Teleport Home (Lifestream)##refreshSubsHome", ref refreshSubsDoReturnToHome);
        ImGui.Checkbox("Collect Personal Plot Info##refreshSubsPlot", ref refreshSubsDoCollectPersonalPlotInfo);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens /housing, checks Apartment, Private Estate, and Shared Estate signboards when available,\nand triggers XA Database saves after each housing pass.");

        DrawSharedCompletionAndLogFooter("refreshSubs", "refreshSubs", ref refreshSubsDoLogoutOnComplete, ref refreshSubsDoKillGameOnComplete, ref refreshSubsDoEnableArMulti, ref refreshSubsShowLog, plugin.TaskRunner);
    }

    private List<string> GetSelectedRefreshSubsCharacters()
    {
        var chars = plugin.Configuration.RefreshSubsCharacters;
        return refreshSubsSelectedIndices
            .Where(i => i >= 0 && i < chars.Count)
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
        var chars = plugin.Configuration.RefreshSubsCharacters;
        var selected = refreshSubsSelectedIndices.OrderBy(i => i)
            .Where(i => i < chars.Count)
            .Select(i => chars[i])
            .ToList();

        HaltAutoCollectionForPriorityTask("Refresh Sub/Bell/Chest");

        refreshSubsArSuppressedByTask = false;

        var steps = BuildRefreshSubsBellSteps(selected, plugin.TaskRunner);

        // Set reloggerRunList for DrawProcessingList
        reloggerRunList = new List<string>(selected);

        plugin.TaskRunner.Start("Refresh Sub/Bell/Chest", steps, onFinished: () =>
        {
            ReleaseRefreshSubsArSuppression();
        }, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });
    }

    private List<TaskStep> BuildRefreshSubsBellSteps(List<string> characters, TaskRunner runner)
    {
        var steps = new List<TaskStep>();

        runner.TotalItems = characters.Count;
        runner.CompletedItems = 0;
        runner.SuppressLogoutCancel = true;

        // Disable AR Multi Mode
        steps.Add(new TaskStep
        {
            Name = "Disable AR Multi Mode",
            OnEnter = () =>
            {
                runner.AddLog("Disabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("AR Disable Cooldown", 1.0f));

        for (int i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;

            // Label
            steps.Add(new TaskStep
            {
                Name = $"[{charIndex}/{charTotal}] {charName}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"── Processing {charName} ({charIndex}/{charTotal}) ──");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog
            var relogState = new RefreshSubsRelogState();
            steps.Add(new TaskStep
            {
                Name = $"Relog: {charName}",
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
            steps.Add(MonthlyReloggerTask.MakeDelay($"Relog Init: {charName}", 2.0f));

            // Wait for relog
            steps.Add(new TaskStep
            {
                Name = $"Wait Relog: {charName}",
                IsComplete = () =>
                {
                    if (relogState.Ready) return true;
                    try
                    {
                        if (!Plugin.PlayerState.IsLoaded) return false;
                        return MonthlyReloggerTask.IsNamePlateReady() && MonthlyReloggerTask.IsPlayerAvailable();
                    }
                    catch { return false; }
                },
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    relogState.Failed = true;
                    runner.FailedCharacters.Add(charName);
                    runner.AddLog($"FAILED: Could not relog to {charName}. Leaving the character checked.");
                },
            });

            // SafeWait 3-pass
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"SafeWait ({charName})"))
            {
                steps.Add(sw);
            }

            // Extra wait
            if (refreshSubsExtraWait > 0)
                steps.Add(MonthlyReloggerTask.MakeDelay($"Extra Wait: {charName}", refreshSubsExtraWait));

            AddRefreshSubsSelectedActionSteps(steps, runner, charName);
            AddRefreshSubsTeleportSteps(steps, runner, charName, "FC", "fc", 8f);
            steps.Add(MonthlyReloggerTask.MakeDelay($"TP FC Final Settle: {charName}", 1.0f));

            // Enter workshop if configured
            if (refreshSubsGoWorkshop)
            {
                // Target and enter Additional Chambers
                steps.Add(new TaskStep
                {
                    Name = $"Enter Workshop: {charName}",
                    OnEnter = () =>
                    {
                        runner.AddLog("Entering Additional Chambers (workshop)...");
                        ChatHelper.SendMessage("/target \"Entrance to Additional Chambers\"");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Target: {charName}", 0.5f));

                steps.Add(new TaskStep
                {
                    Name = $"Workshop Lockon: {charName}",
                    OnEnter = () =>
                    {
                        ChatHelper.SendMessage("/lockon on");
                        ChatHelper.SendMessage("/automove on");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Walk: {charName}", 3.0f));

                steps.Add(new TaskStep
                {
                    Name = $"Workshop Interact: {charName}",
                    OnEnter = () =>
                    {
                        ChatHelper.SendMessage("/automove off");
                        AddonHelper.InteractWithTarget();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Wait: {charName}", 1.0f));

                steps.Add(new TaskStep
                {
                    Name = $"Workshop Menu: {charName}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Menu Wait: {charName}", 1.0f));

                // SafeWait after zone
                foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Workshop SafeWait ({charName})"))
                {
                    steps.Add(sw);
                }
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Settle: {charName}", 1.0f));
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
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Final SafeWait ({charName})"))
            {
                steps.Add(sw);
            }

            // Mark complete
            var capturedIndex = charIndex;
            var capturedName = charName;
            steps.Add(new TaskStep
            {
                Name = $"Complete: {capturedName}",
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
            Name = "Refresh Sub/Bell/Chest Summary",
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

        steps.Add(MonthlyReloggerTask.MakeDelay("Final Cooldown", 1.0f));

        return steps;
    }

    private void AddRefreshSubsSuppressArSteps(List<TaskStep> steps, TaskRunner runner, string charName, string sequenceLabel)
    {
        steps.Add(new TaskStep
        {
            Name = $"Suppress AR ({sequenceLabel}): {charName}",
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
        steps.Add(MonthlyReloggerTask.MakeDelay($"Suppress AR Wait: {charName}", 0.5f));
    }

    private void AddRefreshSubsSelectedActionSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        if (refreshSubsDoOpenArmoury)
        {
            steps.Add(new TaskStep
            {
                Name = $"Open Armoury Chest: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Armoury Delay: {charName}", 0.5f));
        }

        if (refreshSubsDoOpenSaddlebags)
        {
            steps.Add(new TaskStep
            {
                Name = $"Open Saddlebags: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Saddlebag Read Delay: {charName}", 1.0f));
            steps.Add(new TaskStep
            {
                Name = $"Close Saddlebags: {charName}",
                OnEnter = () => AddonHelper.CloseAddon("InventoryBuddy"),
                IsComplete = () => !AddonHelper.IsAddonVisible("InventoryBuddy"),
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Saddlebag Close Delay: {charName}", 0.5f));
        }

        if (refreshSubsDoOpenJournal)
        {
            steps.Add(new TaskStep
            {
                Name = $"Open Journal: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Journal...");
                    ChatHelper.SendMessage("/journal");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("Journal"),
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Journal Read Delay: {charName}", 0.5f));
            if (plugin.IpcClient.IsXaDatabaseAvailable())
            {
                steps.Add(new TaskStep
                {
                    Name = $"Journal Save: {charName}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Journal Save Delay: {charName}", 0.5f, () => !AddonHelper.IsAddonVisible("Journal")));
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
            Name = $"Teleport {label}: {charName}",
            OnEnter = () =>
            {
                runner.AddLog($"Teleporting to {label} (/li {command})...");
                Plugin.Instance!.IpcClient.LifestreamExecuteCommand(command);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Teleport {label} Init: {charName}", 1.0f));

        steps.Add(new TaskStep
        {
            Name = $"Teleport {label} Wait Start: {charName}",
            IsComplete = () =>
            {
                try { return Plugin.Instance!.IpcClient.LifestreamIsBusy(); }
                catch { return true; }
            },
            TimeoutSec = waitBusyTimeoutSec,
        });

        steps.Add(new TaskStep
        {
            Name = $"Teleport {label} Wait Complete: {charName}",
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
            Name = $"Teleport {label} Confirm: {charName}",
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

        foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Teleport {label} SafeWait ({charName})"))
            steps.Add(sw);

        steps.Add(MonthlyReloggerTask.MakeDelay($"Teleport {label} Settle: {charName}", 1.0f));
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
            Name = $"{label}: {charName}",
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
                Name = $"{label} [pass {capturedPass}/3]: {charName}",
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
                steps.Add(MonthlyReloggerTask.MakeDelay($"{label} [wait 1s]: {charName}", 1.0f));
        }
    }

    private static void AddRefreshSubsSubConsoleSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        steps.Add(new TaskStep
        {
            Name = $"Target Sub Console: {charName}",
            OnEnter = () =>
            {
                runner.AddLog("Targeting Voyage Control Panel...");
                ChatHelper.SendMessage("/target \"Voyage Control Panel\"");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Sub Target: {charName}", 0.5f));

        steps.Add(new TaskStep
        {
            Name = $"Walk to Sub Console: {charName}",
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/lockon on");
                ChatHelper.SendMessage("/automove on");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Sub Walk: {charName}", 1.5f));

        steps.Add(new TaskStep
        {
            Name = $"Interact Sub Console: {charName}",
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/automove off");
                AddonHelper.InteractWithTarget();
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Sub Interact: {charName}", 1.0f));

        steps.Add(new TaskStep
        {
            Name = $"Sub Reports: {charName}",
            OnEnter = () =>
            {
                if (AddonHelper.IsAddonReady("SelectString"))
                    AddonHelper.FireCallbackAndClose("SelectString", 1);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Sub Reports Wait: {charName}", 1.0f));

        AddRefreshSubsAddonExitSequenceSteps(steps, runner, charName, "Sub Console Recovery", "SelectString");
    }

    private static void AddRefreshSubsBellSteps(List<TaskStep> steps, TaskRunner runner, string charName)
    {
        steps.Add(new TaskStep
        {
            Name = $"Target Bell: {charName}",
            OnEnter = () =>
            {
                runner.AddLog("Targeting Summoning Bell...");
                ChatHelper.SendMessage("/target \"Summoning Bell\"");
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Bell Target: {charName}", 0.5f));

        steps.Add(new TaskStep
        {
            Name = $"Walk to Bell: {charName}",
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/lockon on");
                ChatHelper.SendMessage("/automove on");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Bell Walk: {charName}", 3.0f));

        steps.Add(new TaskStep
        {
            Name = $"Interact Bell: {charName}",
            OnEnter = () =>
            {
                ChatHelper.SendMessage("/automove off");
                AddonHelper.InteractWithTarget();
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"Bell Interact: {charName}", 2.0f));

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
            Name = $"FC Chest Check: {charName}",
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
            Name = $"Target FC Chest: {charName}",
            ShouldSkip = () => skipFcChest,
            OnEnter = () =>
            {
                runner.AddLog("Targeting Company Chest...");
                AddonHelper.TargetByName("Company Chest");
            },
            IsComplete = () => AddonHelper.CurrentTargetMatches("Company Chest"),
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay($"FC Chest Target Delay: {charName}", 0.5f, () => skipFcChest));

        steps.Add(new TaskStep
        {
            Name = $"Path to FC Chest: {charName}",
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
            Name = $"Interact FC Chest: {charName}",
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
        steps.Add(MonthlyReloggerTask.MakeDelay($"FC Chest Load Delay: {charName}", 0.5f, () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest")));

        steps.Add(new TaskStep
        {
            Name = $"FC Chest Save: {charName}",
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
        steps.Add(MonthlyReloggerTask.MakeDelay($"FC Chest Save Delay: {charName}", 0.5f, () => skipFcChest || !AddonHelper.IsAddonVisible("FreeCompanyChest")));

        steps.Add(new TaskStep
        {
            Name = $"FC Chest Close: {charName}",
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
            Name = $"Resume AR ({sequenceLabel}): {charName}",
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
        steps.Add(MonthlyReloggerTask.MakeDelay($"Resume AR Wait: {charName}", 0.5f));
    }

    private void UncheckRefreshSubsCharacter(string characterName, TaskRunner runner)
    {
        var chars = plugin.Configuration.RefreshSubsCharacters;
        for (var idx = 0; idx < chars.Count; idx++)
        {
            if (!chars[idx].Equals(characterName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (refreshSubsSelectedIndices.Remove(idx))
                runner.AddLog($"Refresh Sub/Bell/Chest: unchecked {characterName} from the character list.");

            break;
        }
    }

    private class RefreshSubsRelogState
    {
        public bool Ready;
        public bool Failed;
    }
}
