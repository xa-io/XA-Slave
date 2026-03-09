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
    private static readonly int[] CheckEveryHourOptions = { 0, 6, 12, 24, 48, 72 };

    // Task menu
    private enum SlaveTask
    {
        // Tasks
        SaveToXaDatabase,
        CityChatFlooder,
        AutoGlamWeather,
        ArPostProcess,
        // FC
        MonthlyRelogger,
        CheckDuplicatePlots,
        ReturnAltsToHomeworlds,
        RefreshArSubsBell,
        MultiFcPermissions,
        AutoAcceptFcInvite,
        // Utility
        WindowRenamer,
        PluginOperations,
        // Reference
        DebugCommands,
        IpcCallsAvailable,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskItems =
    {
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
        (SlaveTask.CityChatFlooder, "City Chat Flooder"),
        (SlaveTask.AutoGlamWeather, "Auto-Glam Weather"),
        (SlaveTask.ArPostProcess, "AR Pre/Post Processing"),
    };

    private static readonly (SlaveTask Task, string Label)[] FcItems =
    {
        (SlaveTask.MonthlyRelogger, "Monthly Relogger"),
        (SlaveTask.CheckDuplicatePlots, "Check Duplicate Plots"),
        (SlaveTask.ReturnAltsToHomeworlds, "Return Alts To Homeworlds"),
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
        (SlaveTask.DebugCommands, "Debug / Test"),
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
        return normalized == 0 ? "Always" : $"{normalized}hr";
    }

    public void ScheduleAutoCollection()
    {
        CancelScheduledAutoCollection(false);

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

    private void OnFrameworkUpdate(IFramework framework)
    {
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

    public override void Draw()
    {
        // ── Left panel: Task menu ──
        var leftWidth = 180f;
        using (var child = ImRaii.Child("TaskMenu", new Vector2(leftWidth, -30), true))
        {
            if (child.Success)
            {
                DrawMenuSection("Tasks", TaskItems, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));
                DrawMenuSection("FC", FcItems, new Vector4(0.8f, 0.6f, 1.0f, 1.0f));
                DrawMenuSection("Utility", UtilityItems, new Vector4(0.6f, 1.0f, 0.6f, 1.0f));

                foreach (var ext in plugin.ExternalTaskLoader.Tasks)
                {
                    var isSelected = selectedExternalTask == ext;
                    if (ImGui.Selectable(ext.Label, isSelected))
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
                        case SlaveTask.ArPostProcess:
                            DrawArPostProcessTask();
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
                        case SlaveTask.DebugCommands:
                            DrawDebugCommands();
                            break;
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
    /// <summary>Renders a menu section with header and selectable items.</summary>
    private void DrawMenuSection(string header, (SlaveTask Task, string Label)[] items, Vector4 headerColor)
    {
        ImGui.Spacing();
        ImGui.TextColored(headerColor, header);
        ImGui.Separator();
        foreach (var (task, label) in items)
        {
            var isSelected = selectedExternalTask == null && selectedTask == task;
            if (ImGui.Selectable(label, isSelected))
            {
                selectedTask = task;
                selectedExternalTask = null;
            }
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
            var label = plugin.TaskRunner.TotalItems > 0
                ? $"{plugin.TaskRunner.CurrentTaskName}: {plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}"
                : plugin.TaskRunner.CurrentTaskName;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), label);
        }
    }
}
