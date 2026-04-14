using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private readonly HashSet<int> prepLogisticsSelectedIndices = new();
    private string prepLogisticsNewChar = string.Empty;
    private string prepLogisticsSearchFilter = string.Empty;
    private string prepLogisticsWorldFilter = string.Empty;
    private string prepLogisticsAetheryteFilter = string.Empty;
    private bool prepLogisticsShowLog;
    private List<string>? prepLogisticsAetheryteNames;

    private void DrawPrepLogisticsTask()
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var chars = cfg.PrepLogisticsCharacters;
        var arOk = plugin.IpcClient.IsAutoRetainerAvailable();
        var lsOk = plugin.IpcClient.IsLifestreamAvailable();
        var allRequired = arOk && lsOk;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Prep Logistics");
        ImGui.TextDisabled("Relog selected characters and move each one to a chosen world and optional location via Lifestream.");
        ImGui.TextDisabled("Flow: /ays relog -> CharacterSafeWait -> /li world[, aetheryte] -> CharacterSafeWait.");
        ImGui.Spacing();

        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        if (!arConfigExists) ImGui.BeginDisabled();
        if (ImGui.Button("Import from AutoRetainer##prepLogisticsImportAR"))
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
        if (ImGui.Button("Pull XA Database Info##prepLogisticsPullXA"))
            PullXaDatabaseInfo();
        if (!xaDbAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(xaDbAvailable
                ? "Read XA Database to update character info shown in the table."
                : "XA Database plugin not available.");

        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), arImportStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (allRequired)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "All required plugins loaded.");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "WARNING: Missing required plugins.");

        ImGui.Text("Required: ");
        ImGui.SameLine(); ImGui.TextColored(arOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), arOk ? "[AutoRetainer]" : "[AutoRetainer ]");
        ImGui.SameLine(); ImGui.TextColored(lsOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), lsOk ? "[Lifestream]" : "[Lifestream ]");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var isRunning = runner.IsRunning && runner.CurrentTaskName == "Prep Logistics";
        if (isRunning)
        {
            var progress = runner.TotalItems > 0 ? (float)runner.CompletedItems / runner.TotalItems : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Running: {runner.StatusText}");
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{runner.CompletedItems}/{runner.TotalItems}");
            if (!string.IsNullOrEmpty(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabel);
            DrawProcessingList(runner);
            ImGui.Spacing();
            if (ImGui.Button("Cancel##prepLogisticsCancel"))
                runner.Cancel();
        }
        else
        {
            var selectedChars = GetSelectedPrepLogisticsCharacters();
            var hasWorld = !string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld);
            var canStart = selectedChars.Count > 0 && hasWorld && !runner.IsRunning && allRequired;

            var started = DrawPriorityTaskActionButton(
                SlaveTask.PrepLogistics,
                $"Start ({selectedChars.Count} chars)##prepLogisticsStart",
                canStart,
                StartPrepLogistics,
                !allRequired
                    ? "Missing required plugins. Check the plugin status above."
                    : !hasWorld
                        ? "Select a target world first."
                        : "Select at least one character to start.");
            if (started)
                AutoOpenTaskLogIfVerbose(ref prepLogisticsShowLog);

            ImGui.SameLine();
            if (ImGui.Button("Check All##prepLogisticsAll"))
                SelectVisiblePrepLogisticsCharacters();

            ImGui.SameLine();
            if (ImGui.Button("Clear All##prepLogisticsNone"))
                prepLogisticsSelectedIndices.Clear();

            ImGui.SameLine();
            DrawPrepLogisticsWorldSelector(cfg);

            ImGui.SameLine();
            DrawPrepLogisticsAetheryteSelector(cfg);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Character List");
        ImGui.SameLine();
        ImGui.TextDisabled($"({chars.Count} total)");
        ImGui.Spacing();

        var prepRegionFilter = cfg.PrepLogisticsRegionFilter;
        if (DrawRegionFilterCombo("Region##prepLogisticsRegion", ref prepRegionFilter))
        {
            cfg.PrepLogisticsRegionFilter = prepRegionFilter;
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##prepLogisticsSearch", "Search name or world...", ref prepLogisticsSearchFilter, 128);
        ImGui.Spacing();

        if (ImGui.BeginTable("PrepLogisticsCharTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable,
            new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 30);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 30);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Region / DC", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Inv Free", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 25);
            ImGui.TableHeadersRow();

            var charInfo = cfg.ReloggerCharacterInfo;
            var filtered = new List<(int OrigIdx, string CharName, string World, string RegionDc, ReloggerCharacterData? Info)>();
            for (var idx = 0; idx < chars.Count; idx++)
            {
                var charName = chars[idx];
                var world = GetWorldFromKey(charName);
                var regionDc = WorldData.GetRegionDcLabel(world);

                if (!MatchesRegionFilter(world, cfg.PrepLogisticsRegionFilter))
                    continue;

                if (!string.IsNullOrEmpty(prepLogisticsSearchFilter)
                    && !charName.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                    && !world.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                    && !regionDc.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                charInfo.TryGetValue(charName, out var info);
                filtered.Add((idx, charName, world, regionDc, info));
            }

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
                        var cmp = colIdx switch
                        {
                            1 => a.OrigIdx.CompareTo(b.OrigIdx),
                            2 => string.Compare(a.CharName, b.CharName, StringComparison.OrdinalIgnoreCase),
                            3 => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
                            4 => string.Compare(a.RegionDc, b.RegionDc, StringComparison.OrdinalIgnoreCase),
                            5 => (a.Info?.MainInventoryFreeSlots ?? 0).CompareTo(b.Info?.MainInventoryFreeSlots ?? 0),
                            _ => a.OrigIdx.CompareTo(b.OrigIdx),
                        };
                        return ascending ? cmp : -cmp;
                    });
                }
            }

            var displayIndex = 0;
            foreach (var (i, charName, world, regionDc, info) in filtered)
            {
                displayIndex++;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = prepLogisticsSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##prepLogisticsSel{i}", ref selected))
                {
                    if (selected) prepLogisticsSelectedIndices.Add(i);
                    else prepLogisticsSelectedIndices.Remove(i);
                }

                ImGui.TableNextColumn();
                ImGui.Text(displayIndex.ToString());

                ImGui.TableNextColumn();
                ImGui.Text(charName);

                ImGui.TableNextColumn();
                ImGui.TextDisabled(world);

                ImGui.TableNextColumn();
                ImGui.TextDisabled(regionDc);

                ImGui.TableNextColumn();
                var inventoryLabel = GetInventoryFreeSlotsLabel(info);
                if (!string.IsNullOrEmpty(inventoryLabel))
                    ImGui.TextDisabled(inventoryLabel);
                else
                    ImGui.TextDisabled("-");

                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##prepLogisticsRm{i}"))
                {
                    chars.RemoveAt(i);
                    prepLogisticsSelectedIndices.Remove(i);
                    var newSet = new HashSet<int>();
                    foreach (var idx in prepLogisticsSelectedIndices)
                        newSet.Add(idx > i ? idx - 1 : idx);
                    prepLogisticsSelectedIndices.Clear();
                    foreach (var idx in newSet)
                        prepLogisticsSelectedIndices.Add(idx);
                    cfg.Save();
                    ImGui.PopStyleColor();
                    break;
                }
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(250);
        var enterPressed = ImGui.InputTextWithHint("##prepLogisticsAdd", "Name Surname@World", ref prepLogisticsNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add##prepLogisticsAddBtn") || enterPressed) && !string.IsNullOrWhiteSpace(prepLogisticsNewChar))
        {
            var trimmed = prepLogisticsNewChar.Trim();
            if (!chars.Contains(trimmed))
            {
                chars.Add(trimmed);
                cfg.Save();
            }
            prepLogisticsNewChar = string.Empty;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Format: FirstName LastName@World");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var logoutOnComplete = cfg.PrepLogisticsLogoutOnComplete;
        var killGameOnComplete = cfg.PrepLogisticsKillGameOnComplete;
        var enableArMultiOnComplete = cfg.PrepLogisticsEnableArMultiOnComplete;

        var targetDestination = GetPrepLogisticsDestinationLabel(cfg.PrepLogisticsTargetWorld, cfg.PrepLogisticsTargetAetheryte);
        if (!string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld))
            ImGui.TextDisabled($"Target Destination: {targetDestination}");
        else
            ImGui.TextDisabled("Target Destination: not selected");
        ImGui.TextDisabled("World is required. Location is optional.");
        if (DrawSharedCompletionAndLogFooter("prepLogistics", "prepLogistics", ref logoutOnComplete, ref killGameOnComplete, ref enableArMultiOnComplete, ref prepLogisticsShowLog, runner))
        {
            cfg.PrepLogisticsLogoutOnComplete = logoutOnComplete;
            cfg.PrepLogisticsKillGameOnComplete = killGameOnComplete;
            cfg.PrepLogisticsEnableArMultiOnComplete = enableArMultiOnComplete;
            cfg.Save();
        }
    }

    private void DrawPrepLogisticsWorldSelector(XASlave.Configuration cfg)
    {
        var label = string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld)
            ? "Select World##prepLogisticsWorldButton"
            : $"{cfg.PrepLogisticsTargetWorld}##prepLogisticsWorldButton";

        if (ImGui.Button(label))
            ImGui.OpenPopup("PrepLogisticsWorldPopup");

        if (!ImGui.BeginPopup("PrepLogisticsWorldPopup"))
            return;

        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##prepLogisticsWorldFilter", "Type a world name...", ref prepLogisticsWorldFilter, 128);
        ImGui.Separator();

        foreach (var region in WorldData.RegionOrder)
        {
            var regionWorlds = WorldData.Worlds.Where(w => w.Region == region).ToList();
            if (!regionWorlds.Any(w => string.IsNullOrWhiteSpace(prepLogisticsWorldFilter) || w.Name.Contains(prepLogisticsWorldFilter, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!ImGui.TreeNode(region))
                continue;

            if (!WorldData.DataCenterOrder.TryGetValue(region, out var dcs))
            {
                ImGui.TreePop();
                continue;
            }

            foreach (var dc in dcs)
            {
                var dcWorlds = regionWorlds
                    .Where(w => w.DataCenter == dc)
                    .Where(w => string.IsNullOrWhiteSpace(prepLogisticsWorldFilter) || w.Name.Contains(prepLogisticsWorldFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(w => w.Name)
                    .ToList();
                if (dcWorlds.Count == 0)
                    continue;

                if (!ImGui.TreeNode(dc))
                    continue;

                foreach (var world in dcWorlds)
                {
                    if (!ImGui.Selectable(world.Name, string.Equals(cfg.PrepLogisticsTargetWorld, world.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    cfg.PrepLogisticsTargetWorld = world.Name;
                    cfg.Save();
                    prepLogisticsWorldFilter = string.Empty;
                    ImGui.CloseCurrentPopup();
                    break;
                }

                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        ImGui.EndPopup();
    }

    private void DrawPrepLogisticsAetheryteSelector(XASlave.Configuration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetWorld))
        {
            ImGui.BeginDisabled();
            ImGui.Button("Select Location##prepLogisticsAetheryteButton");
            ImGui.EndDisabled();
            return;
        }

        var label = string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetAetheryte)
            ? "Select Location##prepLogisticsAetheryteButton"
            : $"{cfg.PrepLogisticsTargetAetheryte}##prepLogisticsAetheryteButton";

        if (ImGui.Button(label))
            ImGui.OpenPopup("PrepLogisticsAetherytePopup");

        if (!ImGui.BeginPopup("PrepLogisticsAetherytePopup"))
            return;

        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##prepLogisticsAetheryteFilter", "Type a location...", ref prepLogisticsAetheryteFilter, 128);
        ImGui.Separator();

        if (ImGui.Selectable("(World only)", string.IsNullOrWhiteSpace(cfg.PrepLogisticsTargetAetheryte)))
        {
            cfg.PrepLogisticsTargetAetheryte = string.Empty;
            cfg.Save();
            prepLogisticsAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        foreach (var aetheryte in GetPrepLogisticsAetheryteNames())
        {
            if (!string.IsNullOrWhiteSpace(prepLogisticsAetheryteFilter)
                && !aetheryte.Contains(prepLogisticsAetheryteFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ImGui.Selectable(aetheryte, string.Equals(cfg.PrepLogisticsTargetAetheryte, aetheryte, StringComparison.OrdinalIgnoreCase)))
                continue;

            cfg.PrepLogisticsTargetAetheryte = aetheryte;
            cfg.Save();
            prepLogisticsAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
            break;
        }

        ImGui.EndPopup();
    }

    private List<string> GetPrepLogisticsAetheryteNames()
    {
        if (prepLogisticsAetheryteNames != null)
            return prepLogisticsAetheryteNames;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte)
                    continue;

                var name = row.PlaceName.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch
        {
        }

        prepLogisticsAetheryteNames = names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return prepLogisticsAetheryteNames;
    }

    private static string GetPrepLogisticsAetheryteZoneName(string aetheryteName)
    {
        if (string.IsNullOrWhiteSpace(aetheryteName))
            return string.Empty;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte)
                    continue;

                var placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                if (!placeName.Equals(aetheryteName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var zoneName = row.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                return zoneName;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private List<string> GetSelectedPrepLogisticsCharacters()
    {
        var chars = plugin.Configuration.PrepLogisticsCharacters;
        return prepLogisticsSelectedIndices
            .Where(i => i >= 0 && i < chars.Count)
            .OrderBy(i => i)
            .Select(i => chars[i])
            .ToList();
    }

    private void SelectVisiblePrepLogisticsCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.PrepLogisticsCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            var world = GetWorldFromKey(charName);
            var regionDc = WorldData.GetRegionDcLabel(world);
            if (!MatchesRegionFilter(world, cfg.PrepLogisticsRegionFilter))
                continue;
            if (!string.IsNullOrEmpty(prepLogisticsSearchFilter)
                && !charName.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !world.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase)
                && !regionDc.Contains(prepLogisticsSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            prepLogisticsSelectedIndices.Add(i);
        }
    }

    private void StartPrepLogistics()
    {
        var cfg = plugin.Configuration;
        var selected = GetSelectedPrepLogisticsCharacters();
        var targetWorld = cfg.PrepLogisticsTargetWorld;
        var targetAetheryte = cfg.PrepLogisticsTargetAetheryte;
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(targetWorld))
            return;

        HaltAutoCollectionForPriorityTask("Prep Logistics");

        var steps = BuildPrepLogisticsSteps(
            selected,
            targetWorld,
            targetAetheryte,
            cfg.PrepLogisticsEnableArMultiOnComplete,
            cfg.PrepLogisticsLogoutOnComplete,
            cfg.PrepLogisticsKillGameOnComplete,
            plugin.TaskRunner);
        reloggerRunList = new List<string>(selected);
        AutoOpenTaskLogIfVerbose(ref prepLogisticsShowLog);

        plugin.TaskRunner.Start("Prep Logistics", steps, onLog: msg =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });
    }

    private List<TaskStep> BuildPrepLogisticsSteps(List<string> characters, string targetWorld, string targetAetheryte, bool enableArMultiOnComplete, bool logoutOnComplete, bool killGameOnComplete, TaskRunner runner)
    {
        var steps = new List<TaskStep>();
        var targetDestination = GetPrepLogisticsDestinationLabel(targetWorld, targetAetheryte);
        var lifestreamDestination = BuildPrepLogisticsDestinationCommand(targetWorld, targetAetheryte);
        runner.TotalItems = characters.Count;
        runner.CompletedItems = 0;
        runner.SuppressLogoutCancel = true;

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

        for (var i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;
            var relogFailed = false;
            var skipTravel = false;

            steps.Add(new TaskStep
            {
                Name = $"[{charIndex}/{charTotal}] {charName}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName} -> {targetDestination}";
                    runner.AddLog($"── Prep Logistics: {charName} ({charIndex}/{charTotal}) -> {targetDestination} ──");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            steps.Add(new TaskStep
            {
                Name = $"Relog + Wait: {charName}",
                OnEnter = () =>
                {
                    var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (current.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        runner.AddLog($"Already logged in as {charName}.");
                        return;
                    }

                    runner.AddLog($"Relogging to {charName}...");
                    ChatHelper.SendMessage($"/ays relog {charName}");
                },
                IsComplete = () => MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                    && CharacterSafetyHelper.IsCharacterSafeWaitReady(),
                TimeoutSec = 600f,
                MaxRetries = 2,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    runner.FailedCharacters.Add(charName);
                    runner.AddLog($"FAILED: Could not relog to {charName} after 3 attempt(s). Skipping travel.");
                },
            });

            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Relog SafeWait ({charName})", 30f))
            {
                var origComplete = sw.IsComplete;
                steps.Add(new TaskStep
                {
                    Name = sw.Name,
                    OnEnter = sw.OnEnter,
                    IsComplete = () => relogFailed || origComplete(),
                    TimeoutSec = sw.TimeoutSec,
                });
            }

            steps.Add(new TaskStep
            {
                Name = $"Travel to {targetDestination}: {charName}",
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;

                    var currentWorld = GetCurrentWorldName();
                    var currentLocation = GetCurrentLocationName();
                    var alreadyOnTargetWorld = currentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase);
                    var hasTargetAetheryte = !string.IsNullOrWhiteSpace(targetAetheryte);

                    if (alreadyOnTargetWorld && (!hasTargetAetheryte || currentLocation.Equals(targetAetheryte, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipTravel = true;
                        runner.AddLog(hasTargetAetheryte
                            ? $"Already at {targetDestination}; skipping travel."
                            : $"Already on {targetWorld}; skipping world travel.");
                        return;
                    }

                    if (alreadyOnTargetWorld && hasTargetAetheryte)
                    {
                        var targetZone = GetPrepLogisticsAetheryteZoneName(targetAetheryte);
                        if (!string.IsNullOrWhiteSpace(targetZone)
                            && currentLocation.Equals(targetZone, StringComparison.OrdinalIgnoreCase))
                        {
                            runner.AddLog($"Already on {targetWorld} in zone '{targetZone}'; treating destination as reached.");
                            skipTravel = true;
                            return;
                        }
                    }

                    runner.AddLog($"Traveling to {targetDestination} via Lifestream...");
                    plugin.IpcClient.LifestreamExecuteCommand(lifestreamDestination);
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });

            steps.Add(MonthlyReloggerTask.MakeDelay($"Travel Init: {charName}", 1.0f));

            steps.Add(new TaskStep
            {
                Name = $"Travel Wait Start: {charName}",
                IsComplete = () =>
                {
                    if (relogFailed || skipTravel)
                        return true;

                    try { return plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 10f,
            });

            steps.Add(new TaskStep
            {
                Name = $"Travel Wait Complete: {charName}",
                IsComplete = () =>
                {
                    if (relogFailed || skipTravel)
                        return true;

                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 120f,
            });

            foreach (var sw in MonthlyReloggerTask.BuildCharacterSafeWait3Pass($"Travel SafeWait ({charName})", 30f))
            {
                var origComplete = sw.IsComplete;
                steps.Add(new TaskStep
                {
                    Name = sw.Name,
                    OnEnter = sw.OnEnter,
                    IsComplete = () => relogFailed || origComplete(),
                    TimeoutSec = sw.TimeoutSec,
                });
            }

            steps.Add(new TaskStep
            {
                Name = $"Verify Destination: {charName}",
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;

                    var currentWorld = GetCurrentWorldName();
                    if (currentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(targetAetheryte))
                        {
                            runner.AddLog($"Confirmed {charName} is now on {targetWorld}.");
                        }
                        else
                        {
                            var targetZone = GetPrepLogisticsAetheryteZoneName(targetAetheryte);
                            if (!string.IsNullOrWhiteSpace(targetZone))
                                runner.AddLog($"Confirmed {charName} is now on {targetWorld}. Target location '{targetAetheryte}' maps to zone '{targetZone}'.");
                            else
                                runner.AddLog($"Confirmed {charName} is now on {targetWorld}. Target location was '{targetAetheryte}'.");
                        }
                    }
                    else
                    {
                        runner.AddLog($"WARNING: Expected world '{targetWorld}', but current location is '{GetCurrentPrepLogisticsLocationLabel()}'.");
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            steps.Add(new TaskStep
            {
                Name = $"Complete: {charName}",
                OnEnter = () =>
                {
                    runner.CompletedItems = Math.Min(runner.CompletedItems + 1, runner.TotalItems);

                    if (relogFailed)
                    {
                        runner.AddLog($"Prep Logistics: leaving {charName} checked because the relog failed.");
                        return;
                    }

                    UncheckPrepLogisticsCharacter(charName, runner);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        steps.Add(new TaskStep
        {
            Name = "Prep Logistics Summary",
            OnEnter = () =>
            {
                if (!MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(logoutOnComplete, killGameOnComplete))
                    runner.SuppressLogoutCancel = false;
                if (runner.FailedCharacters.Count > 0)
                    runner.AddLog($"══ SUMMARY: {characters.Count - runner.FailedCharacters.Count}/{characters.Count} character(s) processed for {targetDestination}, {runner.FailedCharacters.Count} failed ══");
                else
                    runner.AddLog($"══ SUMMARY: All {characters.Count} character(s) processed for {targetDestination} ══");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, logoutOnComplete, killGameOnComplete, enableArMultiOnComplete);

        return steps;
    }

    private static string GetPrepLogisticsDestinationLabel(string targetWorld, string targetAetheryte)
    {
        if (string.IsNullOrWhiteSpace(targetWorld))
            return string.Empty;

        return string.IsNullOrWhiteSpace(targetAetheryte)
            ? targetWorld
            : $"{targetWorld}, {targetAetheryte}";
    }

    private static string BuildPrepLogisticsDestinationCommand(string targetWorld, string targetAetheryte)
    {
        return GetPrepLogisticsDestinationLabel(targetWorld, targetAetheryte);
    }

    private static string GetCurrentWorldName()
    {
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null)
                return string.Empty;

            return WorldData.GetById(local.CurrentWorld.RowId)?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetCurrentLocationName()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet.TryGetRow(territoryId, out var row))
                return row.PlaceName.Value.Name.ToString();
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string GetCurrentPrepLogisticsLocationLabel()
    {
        var currentWorld = GetCurrentWorldName();
        var currentLocation = GetCurrentLocationName();
        if (string.IsNullOrWhiteSpace(currentWorld))
            return "Unknown";

        return string.IsNullOrWhiteSpace(currentLocation)
            ? currentWorld
            : $"{currentWorld}, {currentLocation}";
    }

    private void UncheckPrepLogisticsCharacter(string characterName, TaskRunner runner)
    {
        var chars = plugin.Configuration.PrepLogisticsCharacters;
        for (var idx = 0; idx < chars.Count; idx++)
        {
            if (!chars[idx].Equals(characterName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prepLogisticsSelectedIndices.Remove(idx))
                runner.AddLog($"Prep Logistics: unchecked {characterName} from the character list.");

            break;
        }
    }
}
