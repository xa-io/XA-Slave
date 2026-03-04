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
    private const string PluginVersion = "0.0.0.6";

    // Task menu
    private enum SlaveTask
    {
        SaveToXaDatabase,
        MonthlyRelogger,
        CheckDuplicatePlots,
        ReturnAltsToHomeworlds,
        CityChatFlooder,
        DebugCommands,
        IpcCallsAvailable,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskList =
    {
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
        (SlaveTask.MonthlyRelogger, "Monthly Relogger"),
        (SlaveTask.CheckDuplicatePlots, "Check Duplicate Plots"),
        (SlaveTask.ReturnAltsToHomeworlds, "Return Alts To Homeworlds"),
        (SlaveTask.CityChatFlooder, "City Chat Flooder"),
        (SlaveTask.DebugCommands, "Debug / Test"),
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
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Tasks");
                ImGui.Separator();
                ImGui.Spacing();

                foreach (var (task, label) in TaskList)
                {
                    var isSelected = selectedExternalTask == null && selectedTask == task;
                    if (ImGui.Selectable(label, isSelected))
                    {
                        selectedTask = task;
                        selectedExternalTask = null;
                    }
                }

                foreach (var ext in plugin.ExternalTaskLoader.Tasks)
                {
                    var isSelected = selectedExternalTask == ext;
                    if (ImGui.Selectable(ext.Label, isSelected))
                        selectedExternalTask = ext;
                }

                // IPC reference at the bottom of the task list
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Reference");
                {
                    var isSelected = selectedExternalTask == null && selectedTask == SlaveTask.IpcCallsAvailable;
                    if (ImGui.Selectable("IPC Calls Available", isSelected))
                    {
                        selectedTask = SlaveTask.IpcCallsAvailable;
                        selectedExternalTask = null;
                    }
                }
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
