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
///   8. CharacterSafeWait, next character
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

    private void DrawRefreshArSubsBellTask()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.RefreshSubsCharacters;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Refresh AR Subs/Bell");
        ImGui.TextDisabled("Rotate characters, teleport to FC house, interact with sub console & summoning bell.");
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
        DrawTaskPluginStatus(false);

        // Config
        ImGui.Checkbox("Enter Workshop (Additional Chambers)##refreshSubsWorkshop", ref refreshSubsGoWorkshop);
        ImGui.SameLine();
        ImGui.TextDisabled("Uncheck if sub console is in main FC room");

        ImGui.SetNextItemWidth(80);
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
        var isRunning = plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName == "Refresh AR Subs/Bell";
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
                plugin.TaskRunner.Cancel();
        }
        else
        {
            var selectedChars = GetSelectedRefreshSubsCharacters();
            var canStart = selectedChars.Count > 0 && !plugin.TaskRunner.IsRunning;
            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button($"Start ({selectedChars.Count} chars)##refreshSubsStart"))
                StartRefreshArSubsBell();
            if (!canStart) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Check All##refreshSubsAll"))
                for (int i = 0; i < chars.Count; i++) refreshSubsSelectedIndices.Add(i);
            ImGui.SameLine();
            if (ImGui.Button("Clear All##refreshSubsNone"))
                refreshSubsSelectedIndices.Clear();
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
        ImGui.InputTextWithHint("##refreshSubsSearch", "Search name or world...", ref refreshSubsSearchFilter, 128);
        ImGui.Spacing();

        // Character table — columns: checkbox, #, character, world, retainers, submarines, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        if (ImGui.BeginTable("RefreshSubsCharTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable,
            new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 30);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 30);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Submarines", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 25);
            ImGui.TableHeadersRow();

            // Build filtered list
            var filtered = new List<(int OrigIdx, string CharName, string World, ReloggerCharacterData? Info)>();
            for (int idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                var nameParts = charName.Split('@');
                var world = nameParts.Length > 1 ? nameParts[1] : "";

                if (!string.IsNullOrEmpty(refreshSubsSearchFilter) &&
                    !charName.Contains(refreshSubsSearchFilter, StringComparison.OrdinalIgnoreCase))
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
                ImGui.Text(charName);

                // World
                ImGui.TableNextColumn();
                ImGui.TextDisabled(world);

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
        ImGui.SetNextItemWidth(250);
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

        DrawTaskLog("refreshSubs", ref refreshSubsShowLog, plugin.TaskRunner);
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

    private void StartRefreshArSubsBell()
    {
        var chars = plugin.Configuration.RefreshSubsCharacters;
        var selected = refreshSubsSelectedIndices.OrderBy(i => i)
            .Where(i => i < chars.Count)
            .Select(i => chars[i])
            .ToList();

        var steps = BuildRefreshSubsBellSteps(selected, plugin.TaskRunner);

        // Set reloggerRunList for DrawProcessingList
        reloggerRunList = new List<string>(selected);

        plugin.TaskRunner.Start("Refresh AR Subs/Bell", steps, onLog: (msg) =>
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
                TimeoutSec = 120f,
            });

            // SafeWait 3-pass
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"SafeWait ({charName})"))
            {
                steps.Add(sw);
            }

            // Extra wait
            if (refreshSubsExtraWait > 0)
                steps.Add(MonthlyReloggerTask.MakeDelay($"Extra Wait: {charName}", refreshSubsExtraWait));

            // Teleport to FC via Lifestream
            steps.Add(new TaskStep
            {
                Name = $"Teleport FC: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog($"Teleporting to FC house...");
                    plugin.IpcClient.LifestreamExecuteCommand("fc");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"TP FC Init: {charName}", 1.0f));

            // Wait for Lifestream busy
            steps.Add(new TaskStep
            {
                Name = $"TP FC Wait Start: {charName}",
                IsComplete = () =>
                {
                    try { return plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 8f,
            });

            // Wait for Lifestream complete
            steps.Add(new TaskStep
            {
                Name = $"TP FC Wait Complete: {charName}",
                IsComplete = () =>
                {
                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 60f,
            });

            // SafeWait after teleport
            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"TP FC SafeWait ({charName})"))
            {
                steps.Add(sw);
            }
            steps.Add(MonthlyReloggerTask.MakeDelay($"TP FC Settle: {charName}", 2.0f));

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
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Wait: {charName}", 2.0f));

                // SafeWait after zone
                foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Workshop SafeWait ({charName})"))
                {
                    steps.Add(sw);
                }
                steps.Add(MonthlyReloggerTask.MakeDelay($"Workshop Settle: {charName}", 1.0f));
            }

            // Interact with Voyage Control Panel (sub console)
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

            // Click on "View previous reports" (SelectString index 1) to force AR to read subs
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

            // Check if AR is busy, if not press ESC after 2 seconds
            steps.Add(new TaskStep
            {
                Name = $"AR Busy Check (Sub): {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Checking if AutoRetainer is busy after sub console...");
                },
                IsComplete = () =>
                {
                    try { return !plugin.IpcClient.AutoRetainerGetSuppressed(); }
                    catch { return true; }
                },
                TimeoutSec = 30f,
            });

            // Press ESC to close any remaining sub console windows
            steps.Add(new TaskStep
            {
                Name = $"Close Sub Console: {charName}",
                OnEnter = () =>
                {
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Sub Close: {charName}", 0.5f));
            steps.Add(new TaskStep
            {
                Name = $"Close Sub Console 2: {charName}",
                OnEnter = () =>
                {
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Sub Close 2: {charName}", 0.5f));

            // Now target and walk to Summoning Bell
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

            // Check if AR is busy after bell
            steps.Add(new TaskStep
            {
                Name = $"AR Busy Check (Bell): {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Checking if AutoRetainer is busy after summoning bell...");
                },
                IsComplete = () =>
                {
                    try { return !plugin.IpcClient.AutoRetainerGetSuppressed(); }
                    catch { return true; }
                },
                TimeoutSec = 30f,
            });

            // Press ESC to close bell
            steps.Add(new TaskStep
            {
                Name = $"Close Bell: {charName}",
                OnEnter = () =>
                {
                    KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"Bell Close: {charName}", 1.0f));

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
                    runner.AddLog($"Finished {capturedName} ({capturedIndex}/{charTotal})");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        // Summary
        steps.Add(new TaskStep
        {
            Name = "Refresh Subs/Bell Summary",
            OnEnter = () =>
            {
                runner.SuppressLogoutCancel = false;
                runner.AddLog($"══ SUMMARY: All {characters.Count} character(s) processed ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        return steps;
    }

    private class RefreshSubsRelogState
    {
        public bool Ready;
    }
}
