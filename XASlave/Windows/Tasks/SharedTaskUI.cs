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
            ImGui.TextColored(green, global::XASlave.Localization.UiLocale.Get("SCB98E3ED3C5CB275"));
        else
            ImGui.TextColored(red, global::XASlave.Localization.UiLocale.Get("SEBA5BEF6412F53DD"));

        ImGui.Text(global::XASlave.Localization.UiLocale.Get("S4588EB2A8F8AB239"));
        ImGui.SameLine(); ImGui.TextColored(arOk ? green : red, arOk ? global::XASlave.Localization.UiLocale.Get("S4BD2C435DD24A0CE") : global::XASlave.Localization.UiLocale.Get("S62CBEBFD43D7680D"));
        ImGui.SameLine(); ImGui.TextColored(lsOk ? green : red, lsOk ? global::XASlave.Localization.UiLocale.Get("S3D7411ED3FFD4E68") : global::XASlave.Localization.UiLocale.Get("S3C1330608BBB5379"));
        ImGui.SameLine(); ImGui.TextColored(taOk ? green : red, taOk ? global::XASlave.Localization.UiLocale.Get("S777A45C0AF0AA9B1") : global::XASlave.Localization.UiLocale.Get("S89DC3A6CA0FA1D6F"));
        ImGui.SameLine(); ImGui.TextColored(vnavOk ? green : red, vnavOk ? global::XASlave.Localization.UiLocale.Get("S4782008340817630") : global::XASlave.Localization.UiLocale.Get("SF2557AFFFD77B87D"));
        if (requiresXaDb)
        {
            ImGui.SameLine(); ImGui.TextColored(xaDbOk ? green : red, xaDbOk ? global::XASlave.Localization.UiLocale.Get("S5D509E9600642D79") : global::XASlave.Localization.UiLocale.Get("S15B7BFFF1B64CBC0"));
        }

        var optional = new (string Name, bool Available)[]
        {
            ("YesAlready", ipc.IsYesAlreadyAvailable()),
            ("PandorasBox", ipc.IsPandorasBoxAvailable()),
        };
        if (!requiresXaDb)
        {
            ImGui.Text(global::XASlave.Localization.UiLocale.Get("S36B1A941DF5FFC4F"));
            ImGui.SameLine(); ImGui.TextColored(xaDbOk ? green : dim, xaDbOk ? global::XASlave.Localization.UiLocale.Get("S5D509E9600642D79") : global::XASlave.Localization.UiLocale.Get("S02260938526E839B"));
            foreach (var p in optional)
            {
                ImGui.SameLine(); ImGui.TextColored(p.Available ? green : dim, p.Available ? $"[{p.Name}]" : $"[{p.Name} -]");
            }
        }
        else
        {
            ImGui.Text(global::XASlave.Localization.UiLocale.Get("S36B1A941DF5FFC4F"));
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
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S2B57EB9069991494") + $"##{id}"), ref showLog)) { }
        if (showLog)
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SDBA23F14C1094CBD") + $"##{id}")))
            {
                if (logMessages.Count > 0)
                {
                    ImGui.SetClipboardText(string.Join("\n", logMessages));
                    arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S4F2FF305D4E6CC44", logMessages.Count));
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
                }
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S533E26BDCCDDF82C") + $"##{id}")))
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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S9FB365AACE6A8E0D"));
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SB41DF0D656590B9A") + $"##{optionId}"), ref logoutOnComplete))
        {
            if (logoutOnComplete)
                killGameOnComplete = false;
            changed = true;
        }

        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S0E5E994288B71CE3") + $"##{optionId}"), ref killGameOnComplete))
        {
            if (killGameOnComplete)
            {
                logoutOnComplete = false;
                enableArMultiOnComplete = false;
            }

            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S1C870EEBFD9A0137"));

        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S1EFB09E1AC9C8CAD") + $"##{optionId}"), ref enableArMultiOnComplete))
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

    private bool DrawPriorityTaskActionButton(SlaveTask task, string buttonLabel, bool canStart, Action startAction, string disabledTooltip = "", string? displayButtonLabel = null)
    {
        if (TryGetActivePriorityTask(out var activeTask, out var activeLabel))
        {
            var taskLabel = activeTask == task ? GetPriorityTaskLabel(task) : activeLabel;
            var stopLabel = string.Format(global::XASlave.Localization.UiLocale.Native("S9E2B5ED901CBBCF6"), taskLabel) + $"##priorityStop{task}";
            if (global::XASlave.Localization.UiNative.Button(stopLabel, display: global::XASlave.Localization.UiLocale.FormatKey("S9E2B5ED901CBBCF6", global::XASlave.Localization.UiLocale.T(taskLabel))))
                StopPriorityTask(activeTask);
            return false;
        }

        bool clicked;
        using (ImRaii.Disabled(!canStart))
        {
            clicked = global::XASlave.Localization.UiNative.Button(buttonLabel, display: displayButtonLabel);
            if (clicked)
                startAction();
        }
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
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SB249930E113A2F8A", safeCompleted, runList.Count));
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
                ImGui.TextColored(ProcessingFailedColor, global::XASlave.Localization.UiLocale.Get("S029F15BB9567E1F5"));
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
            return global::XASlave.Localization.UiLocale.FormatKey("SC4C041F888329C4F", total);
        var minutes = total / 60;
        var secs = total % 60;
        if (minutes < 60)
            return global::XASlave.Localization.UiLocale.FormatKey("SD8E33CB74C7EE10B", minutes, secs);
        var hours = minutes / 60;
        minutes %= 60;
        return global::XASlave.Localization.UiLocale.FormatKey("S85AE847670C7E847", hours, minutes);
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
            return global::XASlave.Localization.UiLocale.FormatKey("S701D777BF761312B", FormatReloggerDuration(avg));
        return global::XASlave.Localization.UiLocale.FormatKey("SF8DE09C2B6F95A9B", FormatReloggerDuration(avg), remaining, FormatReloggerDuration(avg * remaining));
    }

    /// <summary>
    /// Start a task using MonthlyReloggerTask with specific Do* flags.
    /// Reused by CheckDuplicatePlots, ReturnAltsToHomeworlds, and any future task.
    /// </summary>
    private void StartTaskWithConfig(
        global::XASlave.Localization.UiMessage taskName,
        List<string> characters,
        IReadOnlyList<(string CharName, ReloggerCharacterData Info)> backingList,
        HashSet<int> selectedIndices,
        bool doTextAdvance, bool doRemoveSprout, bool doOpenInventory, bool doOpenArmoury,
        bool doOpenSaddlebags, bool doOpenJournal, bool doReturnToHome, bool doCollectPersonalPlotInfo,
        bool doReturnToFc, bool doParseForXaDatabase, bool doLogoutOnComplete, bool doKillGameOnComplete, bool doEnableArMulti)
    {
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
            for (var i = 0; i < backingList.Count; i++)
            {
                if (backingList[i].CharName.Equals(charName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndices.Remove(i);
                    break;
                }
            }
        });

        // Start with onLog callback that prints to Dalamud console as [TaskLogs]
        if (!plugin.TaskRunner.Start(taskName, steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }, totalItems: characters.Count, suppressLogoutCancel: true))
        {
            plugin.TaskRunner.AddLog($"{taskName} was not started because another task owns the runner.");
            return;
        }

        HaltAutoCollectionForPriorityTask(taskName.Original);
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
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S03A849641AF34070") + $"##{checkboxId}"), ref anonymize))
        {
            plugin.Configuration.GlobalCharacterListAnonymizeEnabled = anonymize;
            plugin.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SCB3F4F9D613E59B5"));
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

    private static readonly string[] RegionFilterOptions = { global::XASlave.Localization.UiLocale.Native("SA52ACE420F2175D0"), global::XASlave.Localization.UiLocale.Native("S20EF0F0C8D0EEA98"), global::XASlave.Localization.UiLocale.Native("S9170A8B2FB3234BA"), global::XASlave.Localization.UiLocale.Native("S569EC6135D377E8A"), global::XASlave.Localization.UiLocale.Native("S79F53E6930DDF3F9") };

    private bool DrawRegionFilterCombo(string label, ref string regionFilter, float width = 80f)
    {
        var regionIdx = Array.IndexOf(RegionFilterOptions, regionFilter);
        if (regionIdx < 0)
            regionIdx = 0;

        ImGui.SetNextItemWidth(Scale(width));
        if (!global::XASlave.Localization.UiNative.Combo(label, ref regionIdx, RegionFilterOptions, RegionFilterOptions.Length))
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

    private static string GetFcMemberRankLabel(ReloggerCharacterData? info) => GetFcMemberRankLabelMessage(info).Original;

    private static global::XASlave.Localization.UiMessage GetFcMemberRankLabelMessage(ReloggerCharacterData? info)
    {
        if (info == null || info.FCID == 0)
            return global::XASlave.Localization.UiMessage.Raw(string.Empty);

        if (!string.IsNullOrWhiteSpace(info.FcMemberRankName))
            return global::XASlave.Localization.UiMessage.Raw(info.FcMemberRankName);

        return (info.FcMemberRankSort != int.MaxValue ? global::XASlave.Localization.UiMessage.Format("S1DC73B9111CCDC2F", info.FcMemberRankSort + 1) : global::XASlave.Localization.UiMessage.Raw(string.Empty));
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
