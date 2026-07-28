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
/// Shared UI helpers used by all task panels.
/// Contains: DrawTaskLog, DrawTaskPluginStatus, DrawProcessingList, StartTaskWithConfig, DrawActionCheckboxes.
/// </summary>
public partial class SlaveWindow
{
    /// <summary>Draw plugin status checker for tasks - same pattern as Monthly Relogger.
    /// XA Database becomes required when parseForXaDb is checked.</summary>
    private void DrawTaskPluginStatus(bool requiresXaDb)
    {
        var ipc = plugin.IpcClient;
        var green = new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
        var red = new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
        var dim = new Vector4(0.6f, 0.6f, 0.6f, 1.0f);

        var arOk = ipc.IsAutoRetainerAvailable();
        var lsOk = ipc.IsLifestreamAvailable();
        var taOk = ipc.IsTextAdvanceAvailable();
        var vnavOk = ipc.IsVnavAvailable();
        var xaDbOk = ipc.IsXaDatabaseAvailable();

        var allRequired = arOk && lsOk && taOk && vnavOk && (!requiresXaDb || xaDbOk);

        if (allRequired)
            ImGui.TextColored(green, "All required plugins loaded.");
        else
            ImGui.TextColored(red, "WARNING: Missing required plugins.");

        ImGui.Text("Required: ");
        ImGui.SameLine(); ImGui.TextColored(arOk ? green : red, arOk ? "[AutoRetainer]" : "[AutoRetainer \u2717]");
        ImGui.SameLine(); ImGui.TextColored(lsOk ? green : red, lsOk ? "[Lifestream]" : "[Lifestream \u2717]");
        ImGui.SameLine(); ImGui.TextColored(taOk ? green : red, taOk ? "[TextAdvance]" : "[TextAdvance \u2717]");
        ImGui.SameLine(); ImGui.TextColored(vnavOk ? green : red, vnavOk ? "[vnavmesh]" : "[vnavmesh \u2717]");
        if (requiresXaDb)
        {
            ImGui.SameLine(); ImGui.TextColored(xaDbOk ? green : red, xaDbOk ? "[XA Database]" : "[XA Database \u2717]");
        }

        var optional = new (string Name, bool Available)[]
        {
            ("YesAlready", ipc.IsYesAlreadyAvailable()),
            ("PandorasBox", ipc.IsPandorasBoxAvailable()),
        };
        if (!requiresXaDb)
        {
            ImGui.Text("Optional: ");
            ImGui.SameLine(); ImGui.TextColored(xaDbOk ? green : dim, xaDbOk ? "[XA Database]" : "[XA Database -]");
            foreach (var p in optional)
            {
                ImGui.SameLine(); ImGui.TextColored(p.Available ? green : dim, p.Available ? $"[{p.Name}]" : $"[{p.Name} -]");
            }
        }
        else
        {
            ImGui.Text("Optional: ");
            foreach (var p in optional)
            {
                ImGui.SameLine(); ImGui.TextColored(p.Available ? green : dim, p.Available ? $"[{p.Name}]" : $"[{p.Name} -]");
            }
        }

        ImGui.Spacing();
    }

    /// <summary>Shared log display with Copy/Clear buttons, used by all task panels.</summary>
    private void DrawTaskLog(string id, ref bool showLog, Services.TaskRunner runner)
    {
        DrawTaskLog(id, ref showLog, runner.LogMessages, runner.ClearLog);
    }

    private void DrawTaskLog(string id, ref bool showLog, Services.AutoCollectionService collector)
    {
        DrawTaskLog(id, ref showLog, collector.LogMessages, collector.ClearLog);
    }

    private void DrawTaskLog(string id, ref bool showLog, IReadOnlyList<string> logMessages, Action clearLog)
    {
        ImGui.Spacing();
        if (ImGui.Checkbox($"Show Log##{id}", ref showLog)) { }
        if (showLog)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy Log##{id}"))
            {
                if (logMessages.Count > 0)
                {
                    ImGui.SetClipboardText(string.Join("\n", logMessages));
                    arImportStatus = $"Copied {logMessages.Count} log lines to clipboard";
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear Log##{id}"))
                clearLog();

            if (logMessages.Count > 0)
            {
                ImGui.Spacing();
                using (var logChild = ImRaii.Child($"TaskLog{id}", ScaledVector(0f, 150f), true))
                {
                    if (logChild.Success)
                    {
                        foreach (var msg in logMessages)
                            ImGui.TextWrapped(msg);
                        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                            ImGui.SetScrollHereY(1.0f);
                    }
                }
            }
        }
    }

    private void AutoOpenTaskLogIfVerbose(ref bool showLog)
    {
        if (plugin.Configuration.VerboseTaskLogging)
            showLog = true;
    }

    private bool DrawSharedCompletionAndLogFooter(string optionId, string logId,
        ref bool logoutOnComplete, ref bool killGameOnComplete, ref bool enableArMultiOnComplete, ref bool showLog,
        Services.TaskRunner runner)
    {
        var changed = false;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Task Options on Complete");
        if (ImGui.Checkbox($"Logout on completion##{optionId}", ref logoutOnComplete))
        {
            if (logoutOnComplete)
                killGameOnComplete = false;
            changed = true;
        }

        if (ImGui.Checkbox($"Kill Game on completion##{optionId}", ref killGameOnComplete))
        {
            if (killGameOnComplete)
            {
                logoutOnComplete = false;
                enableArMultiOnComplete = false;
            }

            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Forces XA's hard logout + close-client flow even if Instant Logout is disabled in XA Mods.");

        if (ImGui.Checkbox($"Enable AR Multi Mode on completion##{optionId}", ref enableArMultiOnComplete))
        {
            if (enableArMultiOnComplete)
                killGameOnComplete = false;
            changed = true;
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawTaskLog(logId, ref showLog, runner);
        return changed;
    }

    private bool DrawPriorityTaskActionButton(SlaveTask task, string buttonLabel, bool canStart, Action startAction, string disabledTooltip = "")
    {
        if (TryGetActivePriorityTask(out var activeTask, out var activeLabel))
        {
            var stopLabel = activeTask == task
                ? $"Stop {GetPriorityTaskLabel(task)} Task##priorityStop{task}"
                : $"Stop {activeLabel} Task##priorityStop{task}";
            if (ImGui.Button(stopLabel))
                StopPriorityTask(activeTask);
            return false;
        }

        if (!canStart) ImGui.BeginDisabled();
        var clicked = ImGui.Button(buttonLabel);
        if (clicked)
            startAction();
        if (!canStart) ImGui.EndDisabled();
        if (!canStart && !string.IsNullOrWhiteSpace(disabledTooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(disabledTooltip);
        return clicked;
    }

    // Processing-list status colors (shared with the relogger last-run snapshot).
    // Mirrors the Xagman order-list scheme: red = failed, purple = incomplete/skipped,
    // green = completed, yellow = in progress.
    private static readonly Vector4 ProcessingFailedColor = new(1.0f, 0.4f, 0.4f, 1.0f);    // RED - failed to log in
    private static readonly Vector4 ProcessingIncompleteColor = new(0.72f, 0.45f, 1.0f, 1.0f); // PURPLE - logged in, did not finish
    private static readonly Vector4 ProcessingCompletedColor = new(0.4f, 1.0f, 0.4f, 1.0f);  // GREEN - completed
    private static readonly Vector4 ProcessingActiveColor = new(1.0f, 0.8f, 0.3f, 1.0f);     // YELLOW - in progress

    /// <summary>Draw the processing list shown during a running task.</summary>
    private void DrawProcessingList(Services.TaskRunner runner)
    {
        if (reloggerRunList.Count == 0)
            return;

        ImGui.Spacing();
        var eta = GetReloggerEtaLabel(reloggerRunList, runner.ItemDurations);
        if (!string.IsNullOrWhiteSpace(eta))
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), eta);
        DrawReloggerProcessingList(reloggerRunList, runner.CompletedItems, runner.FailedCharacters, runner.IncompleteCharacters, runner.ItemDurations);
    }

    /// <summary>
    /// Renders a relogger processing/order list with per-character status colors, processing
    /// timers, and a "(not found in AR)" marker. Used both live (during a run) and for the
    /// persisted last-run snapshot.
    /// </summary>
    private void DrawReloggerProcessingList(
        IReadOnlyList<string> runList,
        int completed,
        IReadOnlyCollection<string> failed,
        IReadOnlyCollection<string> incomplete,
        IReadOnlyDictionary<string, double>? durations = null)
    {
        if (runList.Count == 0)
            return;

        var safeCompleted = Math.Clamp(completed, 0, runList.Count);
        ImGui.TextDisabled($"Processing Order ({safeCompleted}/{runList.Count}):");
        for (int ci = 0; ci < runList.Count; ci++)
        {
            var ch = runList[ci];
            var timeSuffix = durations != null && durations.TryGetValue(ch, out var secs)
                ? $"  ({FormatReloggerDuration(secs)})"
                : string.Empty;

            if (ProcessingListContains(failed, ch))
                ImGui.TextColored(ProcessingFailedColor, $"  ✗ {ci + 1}. {ch}{timeSuffix}");
            else if (ProcessingListContains(incomplete, ch))
                ImGui.TextColored(ProcessingIncompleteColor, $"  ~ {ci + 1}. {ch}{timeSuffix}");
            else if (ci < safeCompleted)
                ImGui.TextColored(ProcessingCompletedColor, $"  ✓ {ci + 1}. {ch}{timeSuffix}");
            else if (ci == safeCompleted)
                ImGui.TextColored(ProcessingActiveColor, $"  → {ci + 1}. {ch}{timeSuffix}");
            else
                ImGui.TextDisabled($"     {ci + 1}. {ch}{timeSuffix}");

            // Flag characters AutoRetainer has no data for - the likely cause of a login failure.
            if (IsReloggerCharNotFoundInAr(ch))
            {
                ImGui.SameLine();
                ImGui.TextColored(ProcessingFailedColor, "(not found in AR)");
            }
        }
    }

    private static bool ProcessingListContains(IReadOnlyCollection<string> keys, string character)
    {
        if (keys == null || keys.Count == 0)
            return false;
        foreach (var key in keys)
        {
            if (key.Equals(character, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True if AutoRetainer's last import/refresh had no data for this character.</summary>
    private bool IsReloggerCharNotFoundInAr(string characterNameWorld)
    {
        return plugin.Configuration.ReloggerCharacterInfo.TryGetValue(characterNameWorld, out var data)
            && data.FoundInAutoRetainer == false;
    }

    /// <summary>Formats a processing duration (seconds) as a compact human string.</summary>
    private static string FormatReloggerDuration(double seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds));
        if (total < 60)
            return $"{total}s";
        var minutes = total / 60;
        var secs = total % 60;
        if (minutes < 60)
            return $"{minutes}m {secs:00}s";
        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours}h {minutes:00}m";
    }

    /// <summary>
    /// Rolling completion estimate: average processed-character time, count remaining, and an ETA
    /// (average x remaining). Returns empty until at least one character has finished.
    /// </summary>
    private static string GetReloggerEtaLabel(IReadOnlyList<string> runList, IReadOnlyDictionary<string, double> durations)
    {
        if (runList == null || runList.Count == 0 || durations == null || durations.Count == 0)
            return string.Empty;

        var avg = durations.Values.Average();
        var processed = durations.Count;
        var remaining = Math.Max(0, runList.Count - processed);
        if (remaining == 0)
            return $"Avg {FormatReloggerDuration(avg)}/char.";
        return $"Avg {FormatReloggerDuration(avg)}/char, {remaining} remaining, ETA ~{FormatReloggerDuration(avg * remaining)}.";
    }

    /// <summary>
    /// Start a task using MonthlyReloggerTask with specific Do* flags.
    /// Reused by CheckDuplicatePlots, ReturnAltsToHomeworlds, and any future task.
    /// </summary>
    private void StartTaskWithConfig(string taskName, List<string> characters, HashSet<int> selectedIndices,
        bool doTextAdvance, bool doRemoveSprout, bool doOpenInventory, bool doOpenArmoury,
        bool doOpenSaddlebags, bool doOpenJournal, bool doReturnToHome, bool doCollectPersonalPlotInfo,
        bool doReturnToFc, bool doParseForXaDatabase, bool doLogoutOnComplete, bool doKillGameOnComplete, bool doEnableArMulti)
    {
        HaltAutoCollectionForPriorityTask(taskName);

        reloggerTask = new MonthlyReloggerTask(plugin)
        {
            DoEnableTextAdvance = doTextAdvance,
            DoRemoveSprout = doRemoveSprout,
            DoOpenInventory = doOpenInventory,
            DoOpenArmouryChest = doOpenArmoury,
            DoOpenSaddlebags = doOpenSaddlebags,
            DoOpenJournal = doOpenJournal,
            DoReturnToHome = doReturnToHome,
            DoCollectPersonalPlotInfo = doCollectPersonalPlotInfo,
            DoReturnToFc = doReturnToFc,
            DoParseForXaDatabase = doParseForXaDatabase,
            DoLogoutOnComplete = doLogoutOnComplete,
            DoKillGameOnComplete = doKillGameOnComplete,
            DoEnableArMultiOnComplete = doEnableArMulti,
        };

        reloggerRunList = new List<string>(characters);

        var steps = reloggerTask.BuildSteps(characters, plugin.TaskRunner, onCharacterCompleted: (charName) =>
        {
            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i] == charName)
                {
                    selectedIndices.Remove(i);
                    break;
                }
            }
        });

        // Start with onLog callback that prints to Dalamud console as [TaskLogs]
        plugin.TaskRunner.Start(taskName, steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });
    }

    /// <summary>Helper: extract world from "Name@World" key.</summary>
    private static string GetWorldFromKey(string key)
    {
        return CharacterAliasHelper.GetWorldFromKey(key);
    }

    private bool IsCharacterListAnonymizationEnabled()
    {
        return plugin.Configuration.GlobalCharacterListAnonymizeEnabled;
    }

    private void DrawCharacterListHeader(string title, string countLabel, string checkboxId)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), title);
        ImGui.SameLine();
        ImGui.TextDisabled(countLabel);
        ImGui.SameLine();
        var anonymize = plugin.Configuration.GlobalCharacterListAnonymizeEnabled;
        if (ImGui.Checkbox($"Anonymize##{checkboxId}", ref anonymize))
        {
            plugin.Configuration.GlobalCharacterListAnonymizeEnabled = anonymize;
            plugin.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Hide character names and worlds locally with deterministic aliases for screenshot-safe tables. This checkbox is shared across every XA Slave task list.");
        }
    }

    private static string GetDisplayCharacterKey(string characterNameWorld, bool anonymize)
    {
        return anonymize
            ? CharacterAliasHelper.Resolve(characterNameWorld).NameWorld
            : CharacterAliasHelper.NormalizeNameWorldKey(characterNameWorld);
    }

    private static string GetDisplayCharacterName(string characterNameWorld, bool anonymize)
    {
        return anonymize
            ? CharacterAliasHelper.Resolve(characterNameWorld).Name
            : CharacterAliasHelper.GetNameFromKey(characterNameWorld);
    }

    private static string GetDisplayWorld(string world, bool anonymize)
    {
        return anonymize
            ? CharacterAliasHelper.ResolveWorldAlias(world)
            : CharacterAliasHelper.NormalizeIdentityPart(world);
    }

    private static string GetDisplayWorldFromKey(string characterNameWorld, bool anonymize)
    {
        return anonymize
            ? CharacterAliasHelper.Resolve(characterNameWorld).World
            : CharacterAliasHelper.GetWorldFromKey(characterNameWorld);
    }

    private static readonly string[] RegionFilterOptions = { "All", "NA", "EU", "JP", "OCE" };

    private bool DrawRegionFilterCombo(string label, ref string regionFilter, float width = 80f)
    {
        var regionIdx = Array.IndexOf(RegionFilterOptions, regionFilter);
        if (regionIdx < 0)
            regionIdx = 0;

        ImGui.SetNextItemWidth(Scale(width));
        if (!ImGui.Combo(label, ref regionIdx, RegionFilterOptions, RegionFilterOptions.Length))
            return false;

        regionFilter = RegionFilterOptions[regionIdx];
        return true;
    }

    private static bool MatchesRegionFilter(string world, string regionFilter)
    {
        if (string.IsNullOrWhiteSpace(regionFilter) || regionFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            return true;

        var worldInfo = WorldData.GetByName(world);
        return worldInfo == null || worldInfo.Region.Equals(regionFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPersonalEstate(ReloggerCharacterData? info)
    {
        return info != null && !string.IsNullOrWhiteSpace(info.PersonalEstate);
    }

    private static bool IsFcMasterRank(ReloggerCharacterData? info)
    {
        if (info == null || info.FCID == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(info.FcMemberRankName))
            return info.FcMemberRankName.Equals("Master", StringComparison.OrdinalIgnoreCase);

        return info.FcMemberRankSort == 0;
    }

    private static string GetFcMemberRankLabel(ReloggerCharacterData? info)
    {
        if (info == null || info.FCID == 0)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(info.FcMemberRankName))
            return info.FcMemberRankName;

        return info.FcMemberRankSort != int.MaxValue
            ? $"Rank {info.FcMemberRankSort + 1}"
            : string.Empty;
    }

    private static string GetFreeCompanyRankLabel(ReloggerCharacterData? info)
    {
        return info != null && info.FreeCompanyRank > 0
            ? info.FreeCompanyRank.ToString()
            : string.Empty;
    }

    private static string GetInventoryFreeSlotsLabel(ReloggerCharacterData? info)
    {
        return info != null && info.MainInventoryTotalSlots > 0
            ? $"{info.MainInventoryFreeSlots}/{info.MainInventoryTotalSlots}"
            : string.Empty;
    }
}
