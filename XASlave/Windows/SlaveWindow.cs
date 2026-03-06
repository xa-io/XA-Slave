using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
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
    private const string PluginVersion = "0.0.0.8";

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
        // Reference
        WindowRenamer,
        DebugCommands,
        IpcCallsAvailable,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskItems =
    {
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
        (SlaveTask.CityChatFlooder, "City Chat Flooder"),
        (SlaveTask.AutoGlamWeather, "Auto-Glam Weather"),
        (SlaveTask.ArPostProcess, "AR Post-Processing"),
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

    private static readonly (SlaveTask Task, string Label)[] ReferenceItems =
    {
        (SlaveTask.WindowRenamer, "Window Renamer"),
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
    }

    public void Dispose() { }

    /// <summary>Schedule auto-collection after login with a delay.</summary>
    public void ScheduleAutoCollection()
    {
        autoCollectScheduledAt = DateTime.UtcNow;
        Plugin.Log.Information($"[XASlave] Auto-collection scheduled (will start in {plugin.Configuration.AutoCollectDelaySeconds}s).");
    }

    public override void Draw()
    {
        // Auto-collection: start after login delay
        if (autoCollectScheduledAt.HasValue && Plugin.PlayerState.IsLoaded && !plugin.AutoCollector.IsRunning)
        {
            var delay = (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
            if (delay >= plugin.Configuration.AutoCollectDelaySeconds && plugin.AutoCollector.IsNormalCondition())
            {
                autoCollectScheduledAt = null;
                RunAutoCollection();
            }
        }

        // ── Left panel: Task menu ──
        var leftWidth = 180f;
        using (var child = ImRaii.Child("TaskMenu", new Vector2(leftWidth, -30), true))
        {
            if (child.Success)
            {
                DrawMenuSection("Tasks", TaskItems, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));
                DrawMenuSection("FC", FcItems, new Vector4(0.8f, 0.6f, 1.0f, 1.0f));

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
