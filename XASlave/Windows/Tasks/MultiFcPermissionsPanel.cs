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
/// Multi-FC Permissions Updater — logs into multiple characters and updates
/// FC member role permissions to have all permissions enabled.
///
/// Converted from: 7.35 XA FC Permissions.lua
///
/// Flow per character:
///   1. Relog to character
///   2. CharacterSafeWait
///   3. Open FC window (/freecompanycmd)
///   4. Fire FC callbacks to navigate to rank editor
///   5. Apply all permissions via FreeCompanyMemberRankEdit callback
///   6. Next character
///
/// Callback sequence (from Lua):
///   FreeCompany true 0 2           → navigate to ranks tab
///   FreeCompanyRank true 2 2 ...   → select rank
///   FreeCompanyRank true 4 3 1513 557 ... → edit rank
///   FreeCompanyRank true 3 2 ...   → confirm
///   ContextMenu true 0 0 0 ...     → open context
///   FreeCompanyMemberRankEdit true 0 ... 1 1 1 1 1 ... → apply all permissions
/// </summary>
public partial class SlaveWindow
{
    // ── FC Permissions state ──
    private readonly HashSet<int> fcPermsSelectedIndices = new();
    private string fcPermsNewChar = "";
    private string fcPermsSearchFilter = "";
    private bool fcPermsShowLog;

    private void DrawMultiFcPermissionsTask()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.FcPermsCharacters;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Multi-FC Permissions Updater");
        ImGui.TextDisabled("Log into multiple characters and update FC member role to have all permissions.");
        ImGui.Spacing();

        // ── Import / Refresh buttons ──
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (ImGui.Button("Import from AutoRetainer##fcPermsImportAR"))
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
        if (ImGui.Button("Pull XA Database Info##fcPermsPullXA"))
            PullXaDatabaseInfo();
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? "Read XA Database to update FC name for all characters."
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
        DrawTaskPluginStatus(false);

        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
            "Note: Each character must have Master rank in their FC to edit permissions. \nIt's best to do this when you're rank 6+ so you have access to plot bidding.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Run controls ──
        var isRunning = plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName == "FC Permissions Updater";
        if (isRunning)
        {
            var progress = plugin.TaskRunner.TotalItems > 0 ? (float)plugin.TaskRunner.CompletedItems / plugin.TaskRunner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {plugin.TaskRunner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}");
            if (!string.IsNullOrEmpty(plugin.TaskRunner.CurrentItemLabel))
                ImGui.TextDisabled(plugin.TaskRunner.CurrentItemLabel);
            DrawProcessingList(plugin.TaskRunner);
            ImGui.Spacing();
            if (ImGui.Button("Cancel##fcPermsCancel"))
                plugin.TaskRunner.Cancel();
        }
        else
        {
            var selectedChars = GetSelectedFcPermsCharacters();
            var canStart = selectedChars.Count > 0 && !plugin.TaskRunner.IsRunning;
            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button($"Start ({selectedChars.Count} chars)##fcPermsStart"))
                StartFcPermissionsUpdater();
            if (!canStart) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Check All##fcPermsAll"))
                for (int i = 0; i < chars.Count; i++) fcPermsSelectedIndices.Add(i);
            ImGui.SameLine();
            if (ImGui.Button("Clear All##fcPermsNone"))
                fcPermsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Character List ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Character List");
        ImGui.SameLine();
        ImGui.TextDisabled($"({chars.Count} total)");
        ImGui.Spacing();

        // Search filter
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##fcPermsSearch", "Search name or world...", ref fcPermsSearchFilter, 128);
        ImGui.Spacing();

        // Character table — columns: checkbox, #, character, world, FC name, in FC, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        if (ImGui.BeginTable("FcPermsCharTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable,
            new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 30);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 30);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("FC Name", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("In FC", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 25);
            ImGui.TableHeadersRow();

            // Build filtered list
            var filtered = new List<(int OrigIdx, string CharName, string World, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                var nameParts = charName.Split('@');
                var world = nameParts.Length > 1 ? nameParts[1] : "";

                if (!string.IsNullOrEmpty(fcPermsSearchFilter) &&
                    !charName.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase))
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
                            4 => string.Compare(a.Info?.FcName ?? "", b.Info?.FcName ?? "", StringComparison.OrdinalIgnoreCase),
                            5 => (a.Info != null && a.Info.FCID != 0).CompareTo(b.Info != null && b.Info.FCID != 0),
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
                var selected = fcPermsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##fpSel{i}", ref selected))
                {
                    if (selected) fcPermsSelectedIndices.Add(i);
                    else fcPermsSelectedIndices.Remove(i);
                }

                // #
                ImGui.TableNextColumn();
                ImGui.Text(displayIndex.ToString());

                // Character
                ImGui.TableNextColumn();
                ImGui.Text(charName);

                // World
                ImGui.TableNextColumn();
                ImGui.TextDisabled(world);

                // FC Name
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

                // Remove
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##fpRm{i}"))
                {
                    chars.RemoveAt(i);
                    fcPermsSelectedIndices.Remove(i);
                    var newSet = new HashSet<int>();
                    foreach (var idx in fcPermsSelectedIndices)
                        newSet.Add(idx > i ? idx - 1 : idx);
                    fcPermsSelectedIndices.Clear();
                    foreach (var idx in newSet) fcPermsSelectedIndices.Add(idx);
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
        var enterPressed = ImGui.InputTextWithHint("##fcPermsAdd", "Name Surname@World", ref fcPermsNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add##fcPermsAddBtn") || enterPressed) && !string.IsNullOrWhiteSpace(fcPermsNewChar))
        {
            var trimmed = fcPermsNewChar.Trim();
            if (!chars.Contains(trimmed))
            {
                chars.Add(trimmed);
                cfg.Save();
            }
            fcPermsNewChar = "";
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Format: FirstName LastName@World");

        DrawTaskLog("fcPerms", ref fcPermsShowLog, plugin.TaskRunner);
    }

    private List<string> GetSelectedFcPermsCharacters()
    {
        var chars = plugin.Configuration.FcPermsCharacters;
        return fcPermsSelectedIndices
            .Where(i => i >= 0 && i < chars.Count)
            .OrderBy(i => i)
            .Select(i => chars[i])
            .ToList();
    }

    private void StartFcPermissionsUpdater()
    {
        var chars = plugin.Configuration.FcPermsCharacters;
        var selected = fcPermsSelectedIndices.OrderBy(i => i)
            .Where(i => i < chars.Count)
            .Select(i => chars[i])
            .ToList();

        var steps = BuildFcPermissionsSteps(selected, plugin.TaskRunner);

        reloggerRunList = new List<string>(selected);

        plugin.TaskRunner.Start("FC Permissions Updater", steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });
    }

    private List<TaskStep> BuildFcPermissionsSteps(List<string> characters, TaskRunner runner)
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
            var relogReady = false;
            steps.Add(new TaskStep
            {
                Name = $"Relog: {charName}",
                OnEnter = () =>
                {
                    var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Already logged in as {charName}");
                        relogReady = true;
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
                    if (relogReady) return true;
                    try
                    {
                        if (!Plugin.PlayerState.IsLoaded) return false;
                        return MonthlyReloggerTask.IsNamePlateReady() && MonthlyReloggerTask.IsPlayerAvailable();
                    }
                    catch { return false; }
                },
                TimeoutSec = 120f,
            });

            // SafeWait 3-pass
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"SafeWait ({charName})"))
            {
                steps.Add(sw);
            }

            // Open FC window
            steps.Add(new TaskStep
            {
                Name = $"Open FC Window: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Opening FC window...");
                    ChatHelper.SendMessage("/freecompanycmd");
                },
                IsComplete = () => AddonHelper.IsAddonVisible("FreeCompany"),
                TimeoutSec = 5f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Window: {charName}", 0.5f));

            // FC callback sequence (from Lua: callbackXA("FreeCompany true 0 2"))
            steps.Add(new TaskStep
            {
                Name = $"FC Navigate Ranks: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Navigating to FC ranks...");
                    AddonHelper.FireCallbackTrueInt("FreeCompany", 0);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Ranks Nav: {charName}", 0.3f));

            // FreeCompanyRank callbacks
            // callbackXA("FreeCompanyRank true 2 2 Undefined Undefined Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Rank Select: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Selecting rank for editing...");
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        AddonHelper.FireCallback("FreeCompanyRank", 2, 2);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Rank Select Wait: {charName}", 0.3f));

            // callbackXA("FreeCompanyRank true 4 3 1513 557 Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Rank Edit: {charName}",
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        AddonHelper.FireCallback("FreeCompanyRank", 4, 3, 1513, 557);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Rank Edit Wait: {charName}", 0.1f));

            // callbackXA("FreeCompanyRank true 3 2 Undefined Undefined Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Rank Confirm: {charName}",
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        AddonHelper.FireCallback("FreeCompanyRank", 3, 2);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Rank Confirm Wait: {charName}", 0.1f));

            // callbackXA("ContextMenu true 0 0 0 Undefined Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Context Menu: {charName}",
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("ContextMenu"))
                        AddonHelper.FireCallback("ContextMenu", 0, 0, 0);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Context Wait: {charName}", 0.1f));

            // Apply all permissions
            // callbackXA("FreeCompanyMemberRankEdit true 0 Undefined 1 1 1 1 1 1 1 1 1 1 1 1 3 1 1 1 1 1 3 1 1 1 1 1 1 1 1 -1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 -1 1 1 1")
            steps.Add(new TaskStep
            {
                Name = $"Apply Permissions: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Applying all FC permissions...");
                    if (AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit"))
                    {
                        // The permission values from the Lua script
                        AddonHelper.FireCallback("FreeCompanyMemberRankEdit",
                            0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 1, 1, 1, 1, 1,
                            3, 1, 1, 1, 1, 1, 1, 1, 1, -1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                            1, 1, 1, 1, 1, 1, 1, 1, 1, -1, 1, 1, 1);
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Permissions Applied: {charName}", 0.5f));

            // Close any remaining FC windows
            steps.Add(new TaskStep
            {
                Name = $"Close FC Windows: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Closing FC windows...");
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Close: {charName}", 0.5f));
            steps.Add(new TaskStep
            {
                Name = $"Close FC Windows 2: {charName}",
                OnEnter = () => KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Close 2: {charName}", 0.5f));

            // Mark complete
            var capturedIndex = charIndex;
            var capturedName = charName;
            steps.Add(new TaskStep
            {
                Name = $"Complete: {capturedName}",
                OnEnter = () =>
                {
                    runner.CompletedItems = capturedIndex;
                    runner.AddLog($"Finished {capturedName} ({capturedIndex}/{charTotal})");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        // Summary
        steps.Add(new TaskStep
        {
            Name = "FC Permissions Summary",
            OnEnter = () =>
            {
                runner.SuppressLogoutCancel = false;
                runner.AddLog($"══ SUMMARY: All {characters.Count} character(s) permissions updated ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        return steps;
    }
}
