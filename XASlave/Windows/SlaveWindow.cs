using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Data.Sqlite;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Main window for XA Slave — left-hand task menu with right-side content panel.
/// Tasks are automation jobs that interact with the game and push data to XA Database via IPC.
/// Partial class — task/panel UI split into Windows/Tasks/ and Windows/Panels/.
/// </summary>
public partial class SlaveWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string PluginVersion = BuildInfo.Version;
    private static readonly int[] CheckEveryHourOptions = { 0, 6, 12, 24, 48, 72, 168, 336, 720, 1440, 2160 };

    // Task menu
    private enum SlaveTask
    {
        // Tasks
        SaveToXaDatabase,
        CityChatFlooder,
        AutoGlamWeather,
        AutoRetainerTasks,
        // FC
        MonthlyRelogger,
        CheckDuplicatePlots,
        PrepLogistics,
        ReturnAltsToHomeworlds,
        RefreshArSubsBell,
        MultiFcPermissions,
        AutoAcceptFcInvite,
        // Utility
        WindowRenamer,
        PluginOperations,
        // Reference
#if XA_SLAVE_TESTING_BUILD
        DebugCommands,
#endif
        IpcCallsAvailable,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskItems =
    {
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
        (SlaveTask.AutoRetainerTasks, "AutoRetainer Helper"),
    };

    private static readonly (SlaveTask Task, string Label)[] CityShenanigansItems =
    {
        (SlaveTask.AutoGlamWeather, "Auto-Glam Weather"),
        (SlaveTask.CityChatFlooder, "City Chat Flooder"),
    };

    private static readonly (SlaveTask Task, string Label)[] FcItems =
    {
        (SlaveTask.MonthlyRelogger, "Monthly Relogger"),
        (SlaveTask.CheckDuplicatePlots, "Check Duplicate Plots"),
        (SlaveTask.ReturnAltsToHomeworlds, "Return Alts To Homeworlds"),
        (SlaveTask.PrepLogistics, "Prep Logistics"),
        (SlaveTask.RefreshArSubsBell, "Refresh AR Subs/Bell"),
        (SlaveTask.MultiFcPermissions, "FC Permissions Updater"),
        (SlaveTask.AutoAcceptFcInvite, "Auto-Accept FC Invites"),
    };

    private static readonly (SlaveTask Task, string Label)[] UtilityItems =
    {
        (SlaveTask.WindowRenamer, "Window Renamer"),
        (SlaveTask.PluginOperations, "Plugin Operations"),
    };

    private static readonly (SlaveTask Task, string Label)[] ReferenceItems =
    {
#if XA_SLAVE_TESTING_BUILD
        (SlaveTask.DebugCommands, "Debug / Test"),
#endif
        (SlaveTask.IpcCallsAvailable, "IPC Calls Available"),
    };

    private SlaveTask selectedTask = SlaveTask.SaveToXaDatabase;

    private ITaskPanel? selectedExternalTask;

    public SlaveWindow(Plugin plugin)
        : base("XA Slave##SlaveWindow", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(550, 350),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        CancelScheduledAutoCollection(true);
        ReleaseRefreshSubsArSuppression();
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private static int NormalizeCheckEveryHours(int hours)
    {
        return CheckEveryHourOptions.Contains(hours) ? hours : 0;
    }

    private static int GetCheckEveryIndex(int hours)
    {
        var normalized = NormalizeCheckEveryHours(hours);
        for (var i = 0; i < CheckEveryHourOptions.Length; i++)
        {
            if (CheckEveryHourOptions[i] == normalized)
                return i;
        }

        return 0;
    }

    private static string FormatCheckEveryHours(int hours)
    {
        var normalized = NormalizeCheckEveryHours(hours);
        if (normalized == 0)
            return "Always";

        if (normalized > 72 && normalized % 24 == 0)
            return $"{normalized / 24}d";

        return $"{normalized}hr";
    }

    public void ScheduleAutoCollection()
    {
        CancelScheduledAutoCollection(false);

        if (TryGetActivePriorityTask(out var activeTask, out var activeLabel) && IsFcRelationPriorityTask(activeTask))
        {
            Plugin.Log.Information($"[XASlave] Auto-collection login checkpoint skipped because {activeLabel} has priority.");
            return;
        }

        var arMultiEnabled = plugin.IpcClient.AutoRetainerGetMultiModeEnabled();
        if (arMultiEnabled && plugin.Configuration.ArPreProcessEnabled)
        {
            Plugin.Log.Information("[XASlave] Auto-collection login checkpoint skipped because AR pre-processing already owns the login checkpoint.");
            return;
        }

        var cadenceHours = NormalizeCheckEveryHours(plugin.Configuration.AutoCollectCheckEveryHours);
        autoCollectScheduledAt = DateTime.UtcNow;
        autoCollectScheduledDelaySeconds = plugin.Configuration.AutoCollectDelaySeconds;
        autoCollectResumeArOnCompletion = arMultiEnabled;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = string.Empty;

        if (arMultiEnabled)
            plugin.IpcClient.AutoRetainerSetSuppressed(true);

        if (!plugin.IsCurrentCharacterSyncDue(cadenceHours))
        {
            autoCollectSkipPending = true;
            autoCollectScheduledDelaySeconds = arMultiEnabled ? 1f : 0f;
            autoCollectSkipMessage = $"XA Database sync skipped — last sync is still within the {FormatCheckEveryHours(cadenceHours)} window.";
        }

        if (arMultiEnabled && plugin.Configuration.AutoCollectDisableWhenArMultiEnabled)
        {
            autoCollectSkipPending = true;
            autoCollectScheduledDelaySeconds = 1f;
            autoCollectSkipMessage = "AR Multi enabled, skipping XA Database push.";
        }

        Plugin.Log.Information($"[XASlave] Auto-collection scheduled (delay {autoCollectScheduledDelaySeconds}s, arMulti={arMultiEnabled}, skipPending={autoCollectSkipPending}).");
    }

    public void CancelScheduledAutoCollection(bool resumeAr)
    {
        var shouldResumeAr = resumeAr && autoCollectResumeArOnCompletion;
        autoCollectScheduledAt = null;
        autoCollectScheduledDelaySeconds = 0f;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = string.Empty;
        autoCollectResumeArOnCompletion = false;

        if (shouldResumeAr)
            plugin.IpcClient.AutoRetainerSetSuppressed(false);
    }

    private void HaltAutoCollectionForPriorityTask(string taskLabel)
    {
        var hadScheduled = autoCollectScheduledAt.HasValue;
        var collectorRunning = plugin.AutoCollector.IsRunning;
        if (!hadScheduled && !collectorRunning)
            return;

        Plugin.Log.Information($"[XASlave] Halting Save to XA Database auto-collection because {taskLabel} took priority.");
        CancelScheduledAutoCollection(true);

        if (collectorRunning)
        {
            plugin.AutoCollector.AddLog($"Auto-collection cancelled because {taskLabel} took priority.");
            plugin.AutoCollector.Cancel();
        }
        else
        {
            plugin.AutoCollector.AddLog($"Scheduled auto-collection skipped because {taskLabel} took priority.");
            SetIpcResult($"{taskLabel} took priority over login auto-collection.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        UpdatePriorityTaskMonitors();
        UpdatePriorityTaskExternalStatus();

        if (!autoCollectScheduledAt.HasValue || !Plugin.PlayerState.IsLoaded || plugin.AutoCollector.IsRunning)
            return;

        var delay = (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
        if (delay < autoCollectScheduledDelaySeconds || !plugin.AutoCollector.IsNormalCondition())
            return;

        autoCollectScheduledAt = null;

        if (autoCollectSkipPending)
        {
            if (!string.IsNullOrWhiteSpace(autoCollectSkipMessage))
                SetIpcResult(autoCollectSkipMessage);

            if (autoCollectResumeArOnCompletion)
                plugin.IpcClient.AutoRetainerSetSuppressed(false);

            autoCollectScheduledDelaySeconds = 0f;
            autoCollectSkipPending = false;
            autoCollectSkipMessage = string.Empty;
            autoCollectResumeArOnCompletion = false;
            return;
        }

        var resumeArAfterCollection = autoCollectResumeArOnCompletion;
        autoCollectScheduledDelaySeconds = 0f;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = string.Empty;
        autoCollectResumeArOnCompletion = false;
        RunAutoCollection(resumeArAfterCollection);
    }

    private void UpdatePriorityTaskMonitors()
    {
        if (glamWeatherRunning)
        {
            var glamInterval = Math.Max(1f, plugin.Configuration.AutoGlamWeatherCheckIntervalSeconds);
            if ((DateTime.UtcNow - glamLastCheck).TotalSeconds >= glamInterval)
            {
                glamLastCheck = DateTime.UtcNow;
                CheckWeatherAndChangeGlamour();
            }
        }

        if (fcFloaterRunning)
        {
            var elapsed = (DateTime.UtcNow - fcFloaterStartTime).TotalSeconds;
            var remaining = (fcFloaterTimeoutMinutes * 60) - elapsed;
            if (remaining <= 0)
            {
                fcFloaterRunning = false;
                plugin.TaskRunner.AddLog($"[FC Floater] Timeout reached ({fcFloaterTimeoutMinutes} minutes). Stopping.");
            }
            else if ((DateTime.UtcNow - fcFloaterLastCheck).TotalSeconds >= fcFloaterCheckInterval)
            {
                fcFloaterLastCheck = DateTime.UtcNow;
                PollFcInvitations();
            }
        }
    }

    public override void Draw()
    {
        // ── Left panel: Task menu ──
        var leftWidth = 180f;
        using (var child = ImRaii.Child("TaskMenu", new Vector2(leftWidth, -30), true))
        {
            if (child.Success)
            {
                DrawMenuSection("Automated Tasks", TaskItems, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));
                DrawMenuSection("City Shenanigans", CityShenanigansItems, new Vector4(1.0f, 0.7f, 0.4f, 1.0f));
                DrawMenuSection("FC Relations", FcItems, new Vector4(0.8f, 0.6f, 1.0f, 1.0f));
                DrawMenuSection("Utility", UtilityItems, new Vector4(0.6f, 1.0f, 0.6f, 1.0f));

                foreach (var ext in plugin.ExternalTaskLoader.Tasks)
                {
                    if (!TryGetVisibleTaskLabel(ext.Label, out var visibleLabel))
                        continue;
                    var isSelected = selectedExternalTask == ext;
                    if (ImGui.Selectable(visibleLabel, isSelected))
                        selectedExternalTask = ext;
                }

                DrawMenuSection("Reference", ReferenceItems, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
            }
        }

        ImGui.SameLine();

        // ── Right panel: Task content ──
        using (var child = ImRaii.Child("TaskContent", new Vector2(0, -30), true))
        {
            if (child.Success)
            {
                if (selectedExternalTask != null)
                {
                    try { selectedExternalTask.Draw(); }
                    catch (Exception ex)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"Error: {ex.Message}");
                    }
                }
                else
                {
                    switch (selectedTask)
                    {
                        case SlaveTask.SaveToXaDatabase:
                            DrawSaveToXaDatabaseTask();
                            break;
                        case SlaveTask.MonthlyRelogger:
                            DrawMonthlyReloggerTask();
                            break;
                        case SlaveTask.CheckDuplicatePlots:
                            DrawCheckDuplicatePlotsTask();
                            break;
                        case SlaveTask.ReturnAltsToHomeworlds:
                            DrawReturnAltsToHomeworldsTask();
                            break;
                        case SlaveTask.CityChatFlooder:
                            DrawCityChatFlooder();
                            break;
                        case SlaveTask.AutoGlamWeather:
                            DrawAutoGlamWeatherTask();
                            break;
                        case SlaveTask.AutoRetainerTasks:
                            DrawArPostProcessTask();
                            break;
                        case SlaveTask.PrepLogistics:
                            DrawPrepLogisticsTask();
                            break;
                        case SlaveTask.RefreshArSubsBell:
                            DrawRefreshArSubsBellTask();
                            break;
                        case SlaveTask.AutoAcceptFcInvite:
                            DrawAutoAcceptFcInviteTask();
                            break;
                        case SlaveTask.MultiFcPermissions:
                            DrawMultiFcPermissionsTask();
                            break;
                        case SlaveTask.WindowRenamer:
                            DrawWindowRenamerTask();
                            break;
                        case SlaveTask.PluginOperations:
                            DrawPluginOperationsTask();
                            break;
#if XA_SLAVE_TESTING_BUILD
                        case SlaveTask.DebugCommands:
                            DrawDebugCommands();
                            break;
#endif
                        case SlaveTask.IpcCallsAvailable:
                            DrawIpcCallsAvailable();
                            break;
                    }
                }
            }
        }

        // ── Status bar ──
        ImGui.Separator();
        DrawStatusBar();
    }

    // ───────────────────────────────────────────────
    //  Status Bar
    // ───────────────────────────────────────────────
    private static bool TryGetVisibleTaskLabel(string rawLabel, out string visibleLabel)
    {
        const string testingMarker = "[IF=Testing]";
        if (rawLabel.StartsWith(testingMarker, StringComparison.OrdinalIgnoreCase))
        {
#if XA_SLAVE_TESTING_BUILD
            visibleLabel = rawLabel.Substring(testingMarker.Length).TrimStart();
            return true;
#else
            visibleLabel = string.Empty;
            return false;
#endif
        }

        visibleLabel = rawLabel;
        return true;
    }

    private static bool IsPriorityTask(SlaveTask task)
    {
        return task is SlaveTask.CityChatFlooder
            or SlaveTask.AutoGlamWeather
            or SlaveTask.MonthlyRelogger
            or SlaveTask.CheckDuplicatePlots
            or SlaveTask.ReturnAltsToHomeworlds
            or SlaveTask.PrepLogistics
            or SlaveTask.RefreshArSubsBell
            or SlaveTask.MultiFcPermissions
            or SlaveTask.AutoAcceptFcInvite;
    }

    private static bool IsFcRelationPriorityTask(SlaveTask task)
    {
        return task is SlaveTask.MonthlyRelogger
            or SlaveTask.CheckDuplicatePlots
            or SlaveTask.ReturnAltsToHomeworlds
            or SlaveTask.PrepLogistics
            or SlaveTask.RefreshArSubsBell
            or SlaveTask.MultiFcPermissions;
    }

    private static bool TryMapPriorityTaskName(string taskName, out SlaveTask task)
    {
        switch (taskName)
        {
            case "City Chat Flooder":
                task = SlaveTask.CityChatFlooder;
                return true;
            case "Auto-Accept FC Invites":
            case "FC Floater: Process Invite":
                task = SlaveTask.AutoAcceptFcInvite;
                return true;
            case "Monthly Relogger":
                task = SlaveTask.MonthlyRelogger;
                return true;
            case "Check Duplicate Plots":
                task = SlaveTask.CheckDuplicatePlots;
                return true;
            case "Return Alts To Homeworlds":
                task = SlaveTask.ReturnAltsToHomeworlds;
                return true;
            case "Prep Logistics":
                task = SlaveTask.PrepLogistics;
                return true;
            case "Refresh AR Subs/Bell":
                task = SlaveTask.RefreshArSubsBell;
                return true;
            case "FC Permissions Updater":
                task = SlaveTask.MultiFcPermissions;
                return true;
            default:
                task = default;
                return false;
        }
    }

    private static string GetPriorityTaskLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.CityChatFlooder => "City Chat Flooder",
            SlaveTask.AutoGlamWeather => "Auto-Glam Weather",
            SlaveTask.MonthlyRelogger => "Monthly Relogger",
            SlaveTask.CheckDuplicatePlots => "Check Duplicate Plots",
            SlaveTask.ReturnAltsToHomeworlds => "Return Alts To Homeworlds",
            SlaveTask.PrepLogistics => "Prep Logistics",
            SlaveTask.RefreshArSubsBell => "Refresh AR Subs/Bell",
            SlaveTask.MultiFcPermissions => "FC Permissions Updater",
            SlaveTask.AutoAcceptFcInvite => "Auto-Accept FC Invites",
            _ => string.Empty,
        };
    }

    private static string GetPriorityTaskDtrLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.AutoGlamWeather => "Auto-Glam",
            _ => GetPriorityTaskLabel(task),
        };
    }

    private bool TryGetActivePriorityTask(out SlaveTask task, out string label)
    {
        if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out task))
        {
            label = GetPriorityTaskLabel(task);
            return true;
        }

        if (glamWeatherRunning)
        {
            task = SlaveTask.AutoGlamWeather;
            label = GetPriorityTaskLabel(task);
            return true;
        }

        if (fcFloaterRunning)
        {
            task = SlaveTask.AutoAcceptFcInvite;
            label = GetPriorityTaskLabel(task);
            return true;
        }

        task = default;
        label = string.Empty;
        return false;
    }

    private bool IsTaskActive(SlaveTask task)
    {
        return TryGetActivePriorityTask(out var activeTask, out _) && activeTask == task;
    }

    private void UpdatePriorityTaskExternalStatus()
    {
        if (plugin.TaskRunner.IsRunning)
        {
            plugin.TaskRunner.ClearExternalStatus();
            return;
        }

        if (TryGetActivePriorityTask(out var activeTask, out _))
            plugin.TaskRunner.SetExternalStatus(GetPriorityTaskDtrLabel(activeTask));
        else
            plugin.TaskRunner.ClearExternalStatus();
    }

    private void StopPriorityTask(SlaveTask task)
    {
        if (task == SlaveTask.AutoGlamWeather)
        {
            StopAutoGlamWeatherTask();
            return;
        }

        if (task == SlaveTask.AutoAcceptFcInvite)
        {
            StopAutoAcceptFcInviteTask();
            return;
        }

        if (task == SlaveTask.RefreshArSubsBell)
            ReleaseRefreshSubsArSuppression();

        if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var runningTask) && runningTask == task)
            plugin.TaskRunner.Cancel();

        UpdatePriorityTaskExternalStatus();
    }

    private static Vector4 GetPriorityTaskPulseColor()
    {
        var pulse = (MathF.Sin((float)ImGui.GetTime() * 3.25f) + 1f) * 0.5f;
        var colorScale = 1f - (0.6f * pulse);
        return new Vector4(colorScale, 1f, colorScale, 1f);
    }

    /// <summary>Renders a menu section with header and selectable items.</summary>
    private void DrawMenuSection(string header, (SlaveTask Task, string Label)[] items, Vector4 headerColor)
    {
        ImGui.Spacing();
        ImGui.TextColored(headerColor, header);
        ImGui.Separator();
        foreach (var (task, label) in items)
        {
            if (!TryGetVisibleTaskLabel(label, out var visibleLabel))
                continue;

            var isActivePriority = IsPriorityTask(task) && IsTaskActive(task);
            if (isActivePriority)
                ImGui.PushStyleColor(ImGuiCol.Text, GetPriorityTaskPulseColor());

            var isSelected = selectedExternalTask == null && selectedTask == task;
            if (ImGui.Selectable(visibleLabel, isSelected))
            {
                selectedTask = task;
                selectedExternalTask = null;
            }

            if (isActivePriority)
                ImGui.PopStyleColor();
        }
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
            var taskLabel = TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var activeTask)
                ? GetPriorityTaskLabel(activeTask)
                : plugin.TaskRunner.CurrentTaskName;
            var label = plugin.TaskRunner.TotalItems > 0
                ? $"{taskLabel}: {plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}"
                : taskLabel;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), label);
        }
        else if (TryGetActivePriorityTask(out var activePriorityTask, out _))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), GetPriorityTaskLabel(activePriorityTask));
        }
    }
}
