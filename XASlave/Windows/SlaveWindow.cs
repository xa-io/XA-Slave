using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private const string WebsiteUrl = "https://aethertek.io/";
    private const string DiscordUrl = "https://discord.gg/g2NmYxPQCa";
    private const string LandingPageIconRelativePath = "images\\icon.png";
    private const float DefaultTaskMenuWidth = 100f;
    private const float MinTaskMenuWidth = 30f;
    private const float MaxTaskMenuWidth = DefaultTaskMenuWidth * 2f;
    private const float TaskLayoutColumnGap = 3f;
    private static readonly int[] CheckEveryHourOptions = { 0, 6, 12, 24, 48, 72, 168, 336, 720, 1440, 2160 };

    // Task menu
    private enum SlaveTask
    {
        // Tasks
        SaveToXaDatabase,
        CityChatFlooder,
        // City Shenanigans
        AutoGlamWeather,
        AutoRetainerTasks,
        // FC Relations
        MonthlyRelogger,
        CheckDuplicatePlots,
        PrepLogistics,
        Xagman,
        ReturnAltsToHomeworlds,
        RefreshArSubsBell,
        MultiFcPermissions,
        AutoAcceptFcInvite,
        // Utility
        WindowRenamer,
        PluginOperations,
        XAMods,
        // Reference
#if XA_SLAVE_TESTING_BUILD
        DebugCommands,
#endif
        ExportData,
        RepoList,
        IpcCallsAvailable,
        CommandsReference,
        SplashScreen,
    }

    private enum MenuSection
    {
        AutomatedTasks,
        CityShenanigans,
        FcRelations,
        Utility,
        Reference,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskItems =
    {
        (SlaveTask.AutoRetainerTasks, "AutoRetainer Helper"),
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
    };

    private static readonly (SlaveTask Task, string Label)[] CityShenanigansItems =
    {
        (SlaveTask.AutoGlamWeather, "Auto-Glam Weather"),
        (SlaveTask.CityChatFlooder, "City Chat Flooder"),
    };

    private static readonly (SlaveTask Task, string Label)[] FcItems =
    {
        (SlaveTask.Xagman, "Xagman"),
        (SlaveTask.MonthlyRelogger, "Monthly Relogger"),
        (SlaveTask.PrepLogistics, "Prep Logistics"),
        (SlaveTask.AutoAcceptFcInvite, "Auto-Accept FC Invites"),
        (SlaveTask.MultiFcPermissions, "FC Permissions Updater"),
        (SlaveTask.CheckDuplicatePlots, "Check Duplicate Plots"),
        (SlaveTask.ReturnAltsToHomeworlds, "Return Alts To Homeworlds"),
        (SlaveTask.RefreshArSubsBell, "Refresh AR Subs/Bell"),
    };

    private static readonly (SlaveTask Task, string Label)[] UtilityItems =
    {
        (SlaveTask.XAMods, "XA Mods"),
        (SlaveTask.WindowRenamer, "Window Renamer"),
        (SlaveTask.PluginOperations, "Plugin Operations"),
    };

    private static readonly (SlaveTask Task, string Label)[] ReferenceItems =
    {
#if XA_SLAVE_TESTING_BUILD
        (SlaveTask.DebugCommands, "Debug / Test"),
#endif
        (SlaveTask.ExportData, "Export Data"),
        (SlaveTask.RepoList, "Repo List"),
        (SlaveTask.IpcCallsAvailable, "IPC Calls Available"),
        (SlaveTask.CommandsReference, "Commands"),
        (SlaveTask.SplashScreen, "Splash Screen"),
    };

    private SlaveTask? selectedTask;

    private ITaskPanel? selectedExternalTask;

    public SlaveWindow(Plugin plugin)
        : base("XA Slave##SlaveWindow", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        RestoreLastSelectedTaskSelection();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Login += OnExportDataLogin;
        Plugin.ClientState.Logout += OnExportDataLogoutHandler;
#if XA_SLAVE_TESTING_BUILD
        Plugin.NamePlateGui.OnDataUpdate += OnXaAbuseNamePlateUpdate;
        Plugin.PluginInterface.UiBuilder.Draw += DrawXaAbuseOverlay;
#endif
        
        // Initialize Xagman peer event handlers for TCP task control
        InitializeXagmanPeerEventHandlers();
    }

    public void RebindXagmanPeerEventHandlers()
    {
        InitializeXagmanPeerEventHandlers();
    }

    public void OpenXAModsTask()
    {
        IsOpen = true;
        SelectBuiltInTask(SlaveTask.XAMods);
    }

    public void OpenCommandsReferenceTask()
    {
        IsOpen = true;
        SelectBuiltInTask(SlaveTask.CommandsReference);
    }

    public void Dispose()
    {
        CancelScheduledAutoCollection(true);
        ReleaseRefreshSubsArSuppression();
        StopXagmanTask();
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ClientState.Login -= OnExportDataLogin;
        Plugin.ClientState.Logout -= OnExportDataLogoutHandler;
#if XA_SLAVE_TESTING_BUILD
        xaAbuseEnabled = false;
        Plugin.NamePlateGui.RequestRedraw();
        Plugin.NamePlateGui.OnDataUpdate -= OnXaAbuseNamePlateUpdate;
        Plugin.PluginInterface.UiBuilder.Draw -= DrawXaAbuseOverlay;
#endif
    }

    private void RestoreLastSelectedTaskSelection()
    {
        selectedTask = null;
        selectedExternalTask = null;
    }

    private void PersistLastSelectedTaskSelection()
    {
        var cfg = plugin.Configuration;
        var builtInTaskName = selectedTask?.ToString() ?? string.Empty;
        var externalTaskName = selectedExternalTask?.Name ?? string.Empty;

        if (string.Equals(cfg.LastSelectedBuiltInTask, builtInTaskName, StringComparison.Ordinal)
            && string.Equals(cfg.LastSelectedExternalTaskName, externalTaskName, StringComparison.Ordinal))
            return;

        cfg.LastSelectedBuiltInTask = builtInTaskName;
        cfg.LastSelectedExternalTaskName = externalTaskName;
        cfg.Save();
    }

    private void SelectBuiltInTask(SlaveTask task)
    {
        selectedTask = task;
        selectedExternalTask = null;
        PersistLastSelectedTaskSelection();
    }

    private void SelectExternalTask(ITaskPanel task)
    {
        selectedTask = null;
        selectedExternalTask = task;
        PersistLastSelectedTaskSelection();
    }

    private void ClearTaskSelection()
    {
        selectedTask = null;
        selectedExternalTask = null;
        PersistLastSelectedTaskSelection();
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

        if (normalized % 24 == 0)
        {
            var days = normalized / 24;
            return days == 1 ? "1 day" : $"{days} days";
        }

        return normalized == 1 ? "1 hour" : $"{normalized} hours";
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
        OnExportDataFrameworkTick();
        UpdateXagmanFrameworkTick();

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
        var leftWidth = ClampTaskMenuWidth(plugin.Configuration.TaskMenuWidth);
        if (Math.Abs(leftWidth - plugin.Configuration.TaskMenuWidth) > 0.01f)
        {
            plugin.Configuration.TaskMenuWidth = leftWidth;
            plugin.Configuration.Save();
        }

        var panelHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 30f);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(TaskLayoutColumnGap, 0f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, new Vector4(0.40f, 0.40f, 0.40f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, new Vector4(0.55f, 0.55f, 0.55f, 1.0f));
        if (ImGui.BeginTable("SlaveLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings, new Vector2(0f, panelHeight)))
        {
            ImGui.TableSetupColumn("TaskMenuColumn", ImGuiTableColumnFlags.WidthFixed, leftWidth);
            ImGui.TableSetupColumn("TaskContentColumn", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawTaskMenuPanel(panelHeight);
            var currentWidth = ClampTaskMenuWidth(ImGui.GetColumnWidth(0));

            if (Math.Abs(plugin.Configuration.TaskMenuWidth - currentWidth) > 1f)
            {
                plugin.Configuration.TaskMenuWidth = currentWidth;
                plugin.Configuration.Save();
            }

            ImGui.TableNextColumn();
            DrawTaskContentPanel(panelHeight);
            ImGui.EndTable();
        }
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        // ── Status bar ──
        ImGui.Separator();
        DrawStatusBar();
    }

    private void DrawTaskMenuPanel(float panelHeight)
    {
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        using (var child = ImRaii.Child("TaskMenu", new Vector2(0f, panelHeight), true))
        {
            if (child.Success)
            {
                DrawMenuSection(MenuSection.AutomatedTasks, "Automated Tasks", TaskItems, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));
                DrawMenuSection(MenuSection.CityShenanigans, "City Shenanigans", CityShenanigansItems, new Vector4(1.0f, 0.7f, 0.4f, 1.0f));
                DrawMenuSection(MenuSection.FcRelations, "FC Relations", FcItems, new Vector4(0.8f, 0.6f, 1.0f, 1.0f));
                DrawMenuSection(MenuSection.Utility, "Utility", UtilityItems, new Vector4(0.6f, 1.0f, 0.6f, 1.0f));

                foreach (var ext in plugin.ExternalTaskLoader.Tasks)
                {
                    if (!TryGetVisibleTaskLabel(ext.Label, out var visibleLabel))
                        continue;
                    var isSelected = selectedExternalTask == ext;
                    if (ImGui.Selectable(visibleLabel, isSelected))
                    {
                        if (isSelected && ImGui.GetIO().KeyCtrl)
                            ClearTaskSelection();
                        else
                            SelectExternalTask(ext);
                    }
                }

                DrawMenuSection(MenuSection.Reference, "Reference", ReferenceItems, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
            }
        }
        ImGui.PopStyleColor();
    }

    private void DrawTaskContentPanel(float panelHeight)
    {
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        using (var child = ImRaii.Child("TaskContent", new Vector2(0f, panelHeight), true))
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
                        case null:
                            DrawDefaultLandingPage();
                            break;
                        case SlaveTask.SaveToXaDatabase:
                            DrawSaveToXaDatabaseTask();
                            break;
                        case SlaveTask.MonthlyRelogger:
                            DrawMonthlyReloggerTask();
                            break;
                        case SlaveTask.CheckDuplicatePlots:
                            DrawCheckDuplicatePlotsTask();
                            break;
                        case SlaveTask.Xagman:
                            DrawXagmanTask();
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
                        case SlaveTask.XAMods:
                            DrawXAModsTask();
                            break;
#if XA_SLAVE_TESTING_BUILD
                        case SlaveTask.DebugCommands:
                            DrawDebugCommands();
                            break;
#endif
                        case SlaveTask.ExportData:
                            DrawExportData();
                            break;
                        case SlaveTask.RepoList:
                            DrawRepoList();
                            break;
                        case SlaveTask.IpcCallsAvailable:
                            DrawIpcCallsAvailable();
                            break;
                        case SlaveTask.CommandsReference:
                            DrawCommandsReference();
                            break;
                    }
                }
            }
        }
        ImGui.PopStyleColor();
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
            or SlaveTask.Xagman
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
            or SlaveTask.Xagman
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
            case "Xagman":
                task = SlaveTask.Xagman;
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
            SlaveTask.Xagman => "Xagman",
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

    private string GetPriorityTaskStatusLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.Xagman => GetXagmanPriorityStatusLabel(),
            _ => GetPriorityTaskLabel(task),
        };
    }

    private string GetPriorityTaskExternalStatusLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.Xagman => GetXagmanPriorityStatusLabel(),
            _ => GetPriorityTaskDtrLabel(task),
        };
    }

    private bool TryGetActivePriorityTask(out SlaveTask task, out string label)
    {
        if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out task))
        {
            label = GetPriorityTaskLabel(task);
            return true;
        }

        if (xagmanRunning)
        {
            task = SlaveTask.Xagman;
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
            plugin.TaskRunner.SetExternalStatus(GetPriorityTaskExternalStatusLabel(activeTask));
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

        if (task == SlaveTask.Xagman)
        {
            StopXagmanTask();
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

    private float ClampTaskMenuWidth(float width)
    {
        return Math.Clamp(width <= 0f ? DefaultTaskMenuWidth : width, MinTaskMenuWidth, MaxTaskMenuWidth);
    }

    /// <summary>Renders a menu section with header and selectable items.</summary>
    private void DrawMenuSection(MenuSection section, string header, (SlaveTask Task, string Label)[] items, Vector4 headerColor)
    {
        ImGui.Spacing();
        var expanded = GetMenuSectionExpanded(section);
        ImGui.PushStyleColor(ImGuiCol.Text, headerColor);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.SetNextItemOpen(expanded, ImGuiCond.Always);
        var isOpen = ImGui.CollapsingHeader($"{header}##{section}");
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
        if (isOpen != expanded)
            SetMenuSectionExpanded(section, isOpen);
        if (!isOpen)
            return;

        foreach (var (task, label) in items)
        {
            if (!TryGetVisibleTaskLabel(label, out var visibleLabel))
                continue;

            var shouldPulse = ShouldPulseMenuTaskItem(task);
            if (shouldPulse)
                ImGui.PushStyleColor(ImGuiCol.Text, GetPriorityTaskPulseColor());

            var isSplashScreen = task == SlaveTask.SplashScreen;
            var isSelected = isSplashScreen
                ? selectedExternalTask == null && selectedTask == null
                : selectedExternalTask == null && selectedTask == task;
            if (ImGui.Selectable(visibleLabel, isSelected))
            {
                if (isSplashScreen)
                    ClearTaskSelection();
                else if (isSelected && ImGui.GetIO().KeyCtrl)
                    ClearTaskSelection();
                else
                    SelectBuiltInTask(task);
            }

            if (shouldPulse)
                ImGui.PopStyleColor();
        }
    }

    private bool ShouldPulseMenuTaskItem(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.SaveToXaDatabase => IsSaveToXaDatabaseActive(),
            SlaveTask.AutoRetainerTasks => IsAutoRetainerHelperActive(),
            _ => IsPriorityTask(task) && IsTaskActive(task),
        };
    }

    private bool GetMenuSectionExpanded(MenuSection section)
    {
        return section switch
        {
            MenuSection.AutomatedTasks => plugin.Configuration.MenuAutomatedTasksExpanded,
            MenuSection.CityShenanigans => plugin.Configuration.MenuCityShenanigansExpanded,
            MenuSection.FcRelations => plugin.Configuration.MenuFcRelationsExpanded,
            MenuSection.Utility => plugin.Configuration.MenuUtilityExpanded,
            MenuSection.Reference => plugin.Configuration.MenuReferenceExpanded,
            _ => true,
        };
    }

    private void SetMenuSectionExpanded(MenuSection section, bool expanded)
    {
        switch (section)
        {
            case MenuSection.AutomatedTasks:
                if (plugin.Configuration.MenuAutomatedTasksExpanded == expanded) return;
                plugin.Configuration.MenuAutomatedTasksExpanded = expanded;
                break;
            case MenuSection.CityShenanigans:
                if (plugin.Configuration.MenuCityShenanigansExpanded == expanded) return;
                plugin.Configuration.MenuCityShenanigansExpanded = expanded;
                break;
            case MenuSection.FcRelations:
                if (plugin.Configuration.MenuFcRelationsExpanded == expanded) return;
                plugin.Configuration.MenuFcRelationsExpanded = expanded;
                break;
            case MenuSection.Utility:
                if (plugin.Configuration.MenuUtilityExpanded == expanded) return;
                plugin.Configuration.MenuUtilityExpanded = expanded;
                break;
            case MenuSection.Reference:
                if (plugin.Configuration.MenuReferenceExpanded == expanded) return;
                plugin.Configuration.MenuReferenceExpanded = expanded;
                break;
            default:
                return;
        }

        plugin.Configuration.Save();
    }

    private bool IsSaveToXaDatabaseActive()
    {
        return plugin.Configuration.AutoCollectOnLogin || autoCollectScheduledAt.HasValue || plugin.AutoCollector.IsRunning;
    }

    private bool IsAutoRetainerHelperActive()
    {
        return plugin.Configuration.ArShipExplorationBailoutEnabled
            || plugin.Configuration.ArPreProcessEnabled
            || plugin.Configuration.ArPostProcessEnabled
            || plugin.ArPostProcessor.IsRegistered
            || plugin.ArPostProcessor.IsRunning;
    }

    private void DrawDefaultLandingPage()
    {
        DrawDefaultLandingPageIcon();

        ImGui.PushTextWrapPos(0f);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.50f, 1.0f));
        ImGui.TextWrapped($"XA Slave v{PluginVersion}");
        ImGui.PopStyleColor();
        ImGui.Text("General information, support links, and setup guidance for XA Slave.");
        ImGui.TextDisabled("Select a task from the left menu to open its panel. Ctrl-click the selected task to return here.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("https://Aethertek.io"))
            OpenExternalLink(WebsiteUrl);

        var remainingWidth = ImGui.GetContentRegionAvail().X;
        var discordButtonWidth = ImGui.CalcTextSize("Join Discord").X + (ImGui.GetStyle().FramePadding.X * 2f);
        if (remainingWidth >= discordButtonWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
        if (ImGui.Button("Join Discord"))
            OpenExternalLink(DiscordUrl);

        ImGui.Separator();
        ImGui.PushTextWrapPos(0f);
        ImGui.Text("XA Slave is still in a very early phase. Expect ongoing changes, new automation surfaces, and many months of additional features.");
        ImGui.Spacing();
        DrawWrappedDisabledText("General Terms");
        DrawWrappedBulletText("Treat every automation workflow as supervised until you trust the exact task and configuration you are running.");
        DrawWrappedBulletText("Enable Show Log when you need visibility, or enable Plugin Operations > Verbose Task Logging when reporting issues.");
        DrawWrappedBulletText("Make sure supporting plugins and character data are configured before running tasks that depend on AutoRetainer, XA Database, Lifestream, or Dropbox.");
        DrawWrappedBulletText("Use Logout on Complete or Enable AR Multi on Complete when you do not want characters left idle in game after a run.");

        ImGui.Spacing();
        DrawWrappedDisabledText("First Time");
        DrawWrappedBulletText("Import characters from AutoRetainer before expecting XA Slave task lists to populate cleanly.");
        DrawWrappedBulletText("Pull XA Database info before relying on matching/selection features that use stored inventory or character data.");
        DrawWrappedBulletText("Running Monthly Relogger across your roster first is the best way to sweep and normalize character data.");

        ImGui.Spacing();
        DrawWrappedDisabledText("Support");
        DrawWrappedBulletText("The website includes general plugin information and project-facing links.");
        DrawWrappedBulletText("The Discord server is the fastest route for reporting issues, feature requests, and feedback.");
        ImGui.PopTextWrapPos();
    }

    private static void DrawWrappedDisabledText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static void DrawWrappedBulletText(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private void DrawDefaultLandingPageIcon()
    {
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            return;

        var imagePath = Path.Combine(pluginDirectory, LandingPageIconRelativePath);
        if (!File.Exists(imagePath))
            return;

        var sharedTexture = Plugin.TextureProvider.GetFromFile(imagePath);
        if (!sharedTexture.TryGetWrap(out var wrap, out _))
            return;

        const float targetHeight = 110f;
        var scale = targetHeight / MathF.Max(1f, (float)wrap.Height);
        var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
        var offset = MathF.Max(0f, (ImGui.GetContentRegionAvail().X - size.X) * 0.5f);
        if (offset > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        ImGui.Image(wrap.Handle, size);
        ImGui.Spacing();
    }

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[XASlave] Failed to open external link: {url}");
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
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), GetPriorityTaskStatusLabel(activePriorityTask));
        }
    }
}
