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
/// Multi-FC Permissions Updater - logs into multiple characters and updates
/// FC member role permissions to have all permissions enabled.
///
/// Flow per character:
///   1. Relog to character
///   2. CharacterSafeWait
///   3. Open FC window (/freecompanycmd)
///   4. Fire FC callbacks to navigate to rank editor
///   5. Apply all permissions via FreeCompanyMemberRankEdit callback
///   6. Next character
///
/// Callback sequence:
///   FreeCompany true 0 2           → navigate to ranks tab
///   FreeCompanyRank true 2 2 ...   → select rank
///   FreeCompanyRank true 4 3 1513 557 ... → edit rank
///   FreeCompanyRank true 3 2 ...   → confirm
///   ContextMenu true 0 0 0 ...     → open context
///   FreeCompanyMemberRankEdit true 0 ... 1 1 1 1 1 ... → apply all permissions
/// </summary>
public partial class SlaveWindow
{
    // -- FC Permissions state --
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

        // -- Import / Refresh buttons --
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        using (ImRaii.Disabled(!arConfigExists))
        {
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
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(arConfigExists
                ? "Read AutoRetainer's DefaultConfig.json to import all characters.\nPath: " + plugin.ArConfigReader.GetAutoRetainerConfigPath()
                : "AutoRetainer config not found.\nExpected: " + plugin.ArConfigReader.GetAutoRetainerConfigPath());

        ImGui.SameLine();
        var xaDbAvailable = plugin.IpcClient.IsXaDatabaseAvailable();
        using (ImRaii.Disabled(!xaDbAvailable))
        {
            if (ImGui.Button("Pull XA Database Info##fcPermsPullXA"))
                PullXaDatabaseInfo();
        }
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

        // -- Run controls --
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
            var started = DrawPriorityTaskActionButton(
                SlaveTask.MultiFcPermissions,
                $"Start ({selectedChars.Count} chars)##fcPermsStart",
                canStart,
                StartFcPermissionsUpdater,
                "Select at least one character to start.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref fcPermsShowLog);

            ImGui.SameLine();
            if (ImGui.Button("Check All##fcPermsAll"))
                SelectVisibleFcPermsCharacters();
            ImGui.SameLine();
            if (ImGui.Button("Clear All##fcPermsNone"))
                fcPermsSelectedIndices.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Character List --
        DrawCharacterListHeader("Character List", $"({chars.Count} total)", "fcPermsAnonymize");
        var anonymizeCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();

        var fcPermsRegionFilter = cfg.FcPermsRegionFilter;
        if (DrawRegionFilterCombo("Region##fcPermsRegion", ref fcPermsRegionFilter))
        {
            cfg.FcPermsRegionFilter = fcPermsRegionFilter;
            cfg.Save();
        }

        ImGui.SameLine();
        // Search filter
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##fcPermsSearch", "Search name or world...", ref fcPermsSearchFilter, 128);
        ImGui.Spacing();

        // Character table - columns: checkbox, #, character, world, FC name, member rank, FC rank, in FC, remove
        var charInfo = cfg.ReloggerCharacterInfo;

        using (var imguiScope167 = ImRaii.Table("FcPermsCharTable", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable,
            ScaledVector(0f, 250f)))
        if (imguiScope167)
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Scale(30f));
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, Scale(30f));
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            ImGui.TableSetupColumn("FC Name", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableSetupColumn("Member Rank", ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            ImGui.TableSetupColumn("FC Rank", ImGuiTableColumnFlags.WidthFixed, Scale(60f));
            ImGui.TableSetupColumn("In FC", ImGuiTableColumnFlags.WidthFixed, Scale(45f));
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

                if (!MatchesRegionFilter(world, cfg.FcPermsRegionFilter))
                    continue;

                if (!string.IsNullOrEmpty(fcPermsSearchFilter) &&
                    !charName.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                    !world.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase))
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
                            5 => (a.Info?.FcMemberRankSort ?? int.MaxValue).CompareTo(b.Info?.FcMemberRankSort ?? int.MaxValue),
                            6 => (a.Info?.FreeCompanyRank ?? 0).CompareTo(b.Info?.FreeCompanyRank ?? 0),
                            7 => (a.Info != null && a.Info.FCID != 0).CompareTo(b.Info != null && b.Info.FCID != 0),
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
                ImGui.Text(GetDisplayCharacterKey(charName, anonymizeCharacters));

                // World
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetDisplayWorldFromKey(charName, anonymizeCharacters));

                // FC Name
                ImGui.TableNextColumn();
                if (info != null && !string.IsNullOrEmpty(info.FcName))
                    ImGui.TextDisabled(info.FcName);
                else
                    ImGui.TextDisabled("-");

                // Member Rank
                ImGui.TableNextColumn();
                var memberRankLabel = GetFcMemberRankLabel(info);
                if (!string.IsNullOrEmpty(memberRankLabel))
                    ImGui.TextDisabled(memberRankLabel);
                else
                    ImGui.TextDisabled("-");

                // FC Rank
                ImGui.TableNextColumn();
                var fcRankLabel = GetFreeCompanyRankLabel(info);
                if (!string.IsNullOrEmpty(fcRankLabel))
                    ImGui.TextDisabled(fcRankLabel);
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
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
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
                        break;
                    }
                }
            }


        }

        ImGui.Spacing();

        // Add character input
        ImGui.SetNextItemWidth(Scale(250f));
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

        var logoutOnComplete = cfg.FcPermsLogoutOnComplete;
        var killGameOnComplete = cfg.FcPermsKillGameOnComplete;
        var enableArMultiOnComplete = cfg.FcPermsEnableArMultiOnComplete;
        if (DrawSharedCompletionAndLogFooter("fcPerms", "fcPerms", ref logoutOnComplete, ref killGameOnComplete, ref enableArMultiOnComplete, ref fcPermsShowLog, plugin.TaskRunner))
        {
            cfg.FcPermsLogoutOnComplete = logoutOnComplete;
            cfg.FcPermsKillGameOnComplete = killGameOnComplete;
            cfg.FcPermsEnableArMultiOnComplete = enableArMultiOnComplete;
            cfg.Save();
        }
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

    private void SelectVisibleFcPermsCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.FcPermsCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            var world = GetWorldFromKey(charName);
            if (!MatchesRegionFilter(world, cfg.FcPermsRegionFilter))
                continue;
            if (!string.IsNullOrEmpty(fcPermsSearchFilter)
                && !charName.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !world.Contains(fcPermsSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            fcPermsSelectedIndices.Add(i);
        }
    }

    private void StartFcPermissionsUpdater()
    {
        var chars = plugin.Configuration.FcPermsCharacters;
        var selected = fcPermsSelectedIndices.OrderBy(i => i)
            .Where(i => i < chars.Count)
            .Select(i => chars[i])
            .ToList();

        var steps = BuildFcPermissionsSteps(selected, plugin.TaskRunner);

        if (!plugin.TaskRunner.Start("FC Permissions Updater", steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }, totalItems: selected.Count, suppressLogoutCancel: true))
            return;

        HaltAutoCollectionForPriorityTask("FC Permissions Updater");
        reloggerRunList = new List<string>(selected);
    }

    private List<TaskStep> BuildFcPermissionsSteps(List<string> characters, TaskRunner runner)
    {
        var cfg = plugin.Configuration;
        var steps = new List<TaskStep>();

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
                    runner.AddLog($"-- Processing {charName} ({charIndex}/{charTotal}) --");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog
            var relogReady = false;
            var relogFailed = false;
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
                        if (!MonthlyReloggerTask.IsNamePlateReady() || !MonthlyReloggerTask.IsPlayerAvailable())
                            return false;
                        // Verify the intended character is actually loaded before applying FC
                        // permission edits; a silently-failed /ays relog would otherwise apply the
                        // destructive rank changes to whichever character is still logged in.
                        return MonthlyReloggerTask.GetCurrentCharacterNameWorld()
                            .Equals(charName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                },
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    runner.RecordFailedCharacter(charName);
                    runner.AddLog($"FAILED: Could not relog to {charName}. Skipping FC permission edits for this character.");
                },
            });

            // Guard the whole downstream FC-permission sequence behind relog success.
            var downstreamStart = steps.Count;
            var permissionFlowFailed = false;
            var ranksNavigationDispatched = false;
            var rankSelectionDispatched = false;
            var rankEditDispatched = false;
            var rankConfirmationDispatched = false;
            var contextMenuDispatched = false;
            var permissionsDispatched = false;

            void FailPermissionFlow(string message)
            {
                if (permissionFlowFailed)
                    return;

                permissionFlowFailed = true;
                if (!runner.IncompleteCharacters.Contains(charName, StringComparer.OrdinalIgnoreCase))
                    runner.RecordIncompleteCharacter(charName);
                runner.AddLog($"FAILED: {message} The permission update for {charName} is unverified; remaining callbacks were stopped.");
            }

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
                OnTimeout = () => FailPermissionFlow("The Free Company window did not open."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Window: {charName}", 0.5f));

            // FC callback sequence ("FreeCompany true 0 2")
            steps.Add(new TaskStep
            {
                Name = $"FC Navigate Ranks: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Navigating to FC ranks...");
                    ranksNavigationDispatched = AddonHelper.IsAddonReady("FreeCompany") &&
                                                AddonHelper.FireCallbackTrueInt("FreeCompany", 0);
                },
                IsComplete = () => ranksNavigationDispatched && AddonHelper.IsAddonReady("FreeCompanyRank"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The ranks tab did not open after its callback."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Ranks Nav: {charName}", 0.3f));

            // FreeCompanyRank callbacks
            // ("FreeCompanyRank true 2 2 Undefined Undefined Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Rank Select: {charName}",
                OnEnter = () =>
                {
                    runner.AddLog("Selecting rank for editing...");
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        rankSelectionDispatched = AddonHelper.FireCallback("FreeCompanyRank", 2, 2);
                },
                IsComplete = () => rankSelectionDispatched && AddonHelper.IsAddonReady("FreeCompanyRank"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The target rank selection callback was not accepted."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Rank Select Wait: {charName}", 0.3f));

            // ("FreeCompanyRank true 4 3 1513 557 Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Rank Edit: {charName}",
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        rankEditDispatched = AddonHelper.FireCallback("FreeCompanyRank", 4, 3, 1513, 557);
                },
                IsComplete = () => rankEditDispatched && AddonHelper.IsAddonReady("FreeCompanyRank"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The target rank edit callback was not accepted."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Rank Edit Wait: {charName}", 0.1f));

            // ("FreeCompanyRank true 3 2 Undefined Undefined Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Rank Confirm: {charName}",
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("FreeCompanyRank"))
                        rankConfirmationDispatched = AddonHelper.FireCallback("FreeCompanyRank", 3, 2);
                },
                IsComplete = () => rankConfirmationDispatched && AddonHelper.IsAddonReady("ContextMenu"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The selected rank did not expose its context menu."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Rank Confirm Wait: {charName}", 0.1f));

            // ("ContextMenu true 0 0 0 Undefined Undefined")
            steps.Add(new TaskStep
            {
                Name = $"FC Context Menu: {charName}",
                OnEnter = () =>
                {
                    if (AddonHelper.IsAddonReady("ContextMenu"))
                        contextMenuDispatched = AddonHelper.FireCallback("ContextMenu", 0, 0, 0);
                },
                IsComplete = () => contextMenuDispatched && AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit"),
                TimeoutSec = 2f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The member-rank permissions editor did not open."),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay($"FC Context Wait: {charName}", 0.1f));

            // Apply all permissions
            // ("FreeCompanyMemberRankEdit true 0 Undefined 1 1 1 1 1 1 1 1 1 1 1 1 3 1 1 1 1 1 3 1 1 1 1 1 1 1 1 -1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 -1 1 1 1")
            steps.Add(new TaskStep
            {
                Name = $"Apply Permissions: {charName}",
                OnEnter = () =>
                {
                    if (!AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit") ||
                        !AddonHelper.AddonHasText("FreeCompanyMemberRankEdit", "Member", contains: true))
                    {
                        FailPermissionFlow("The open permissions editor could not be positively identified as the Member rank.");
                        return;
                    }

                    runner.AddLog("Applying all FC permissions to the verified Member rank editor...");
                    permissionsDispatched = AddonHelper.FireCallback("FreeCompanyMemberRankEdit",
                        0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 1, 1, 1, 1, 1,
                        3, 1, 1, 1, 1, 1, 1, 1, 1, -1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                        1, 1, 1, 1, 1, 1, 1, 1, 1, -1, 1, 1, 1);
                },
                IsComplete = () => permissionFlowFailed ||
                                   permissionsDispatched && !AddonHelper.IsAddonReady("FreeCompanyMemberRankEdit"),
                TimeoutSec = 3f,
                MaxRetries = 1,
                OnTimeout = () => FailPermissionFlow("The verified Member rank permission callback was not accepted."),
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

            // If relogging or any expected UI transition fails, skip the remaining permission
            // sequence so a later callback can never land on a stale or unintended rank editor.
            for (var s = downstreamStart; s < steps.Count; s++)
                steps[s] = MonthlyReloggerTask.WithSkip(steps[s], () => relogFailed || permissionFlowFailed);

            // Mark complete
            var capturedIndex = charIndex;
            var capturedName = charName;
            steps.Add(new TaskStep
            {
                Name = $"Complete: {capturedName}",
                OnEnter = () =>
                {
                    runner.CompletedItems = capturedIndex;
                    // Read the live relogFailed (set by the Wait Relog OnTimeout at runtime), not a
                    // build-time snapshot, so a failed character is reported as skipped.
                    if (relogFailed)
                    {
                        runner.AddLog($"Skipped {capturedName} ({capturedIndex}/{charTotal}) - relog failed");
                        return;
                    }
                    if (permissionFlowFailed)
                    {
                        runner.AddLog($"Skipped {capturedName} ({capturedIndex}/{charTotal}) - permission UI verification failed");
                        return;
                    }
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
                if (!MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.FcPermsLogoutOnComplete, cfg.FcPermsKillGameOnComplete))
                    runner.SuppressLogoutCancel = false;
                runner.AddLog($"══ SUMMARY: processed {characters.Count} character(s); verify failed/incomplete rows above before treating permissions as updated ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        MonthlyReloggerTask.AddSharedCompletionSteps(
            steps,
            runner,
            cfg.FcPermsLogoutOnComplete,
            cfg.FcPermsKillGameOnComplete,
            cfg.FcPermsEnableArMultiOnComplete);

        return steps;
    }
}
