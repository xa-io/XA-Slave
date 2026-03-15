using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

/// <summary>
/// Auto-Glam Against Weather — automatically changes glamour plate based on current weather.
/// Converted from: auto-glam-against-weather.lua
///
/// Weather groups:
///   Sunny  (1,2,3,5,11,14,17,49,50,148,149) → glamour plate for sunny
///   Rain   (4,6,7,8,9,10)                    → glamour plate for rain
///   Freeze (15,16)                            → glamour plate for freezing
///
/// Uses: /gs change {class} {plate}, /echo, game weather API
/// </summary>
public partial class SlaveWindow
{
    // ── Auto-Glam state ──
    private bool glamWeatherRunning;
    private bool glamWeatherShowLog;
    private string glamLastWeatherGroup = "";
    private DateTime glamLastCheck = DateTime.MinValue;
    private string glamClassJobInput = string.Empty;
    private string glamSunnyPlateInput = string.Empty;
    private string glamRainPlateInput = string.Empty;
    private string glamFreezePlateInput = string.Empty;
    private bool glamWeatherInputsInitialized;

    // Weather ID → group mapping
    private static readonly HashSet<int> SunnyWeatherIds = new() { 1, 2, 3, 5, 11, 14, 17, 49, 50, 148, 149 };
    private static readonly HashSet<int> RainWeatherIds = new() { 4, 6, 7, 8, 9, 10 };
    private static readonly HashSet<int> FreezeWeatherIds = new() { 15, 16 };

    private static readonly Dictionary<int, string> WeatherNames = new()
    {
        { 1, "Clear Skies" }, { 2, "Fair Skies" }, { 3, "Clouds" }, { 4, "Fog" },
        { 5, "Wind" }, { 6, "Gales" }, { 7, "Rain" }, { 8, "Showers" },
        { 9, "Thunder" }, { 10, "Thunderstorms" }, { 11, "Dust Storms" },
        { 14, "Heat Waves" }, { 15, "Snow" }, { 16, "Blizzards" }, { 17, "Gloom" },
        { 49, "Umbral Wind" }, { 50, "Umbral Static" }, { 148, "Moon Dust" }, { 149, "Astromagnetic Storm" },
    };

    private void DrawAutoGlamWeatherTask()
    {
        var cfg = plugin.Configuration;
        InitializeAutoGlamInputs(cfg);

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Auto-Glam Against Weather");
        ImGui.TextDisabled("Automatically changes glamour plate based on current weather conditions.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Configuration
        ImGui.Text("Glamour Plate Configuration:");
        ImGui.Spacing();

        var glamConfigChanged = false;

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Class/Job(s) to Assign##glamClass", ref glamClassJobInput, 128))
        {
            glamClassJobInput = SanitizeAutoGlamInput(glamClassJobInput, 1, null, true);
            var committedClassJobs = CommitAutoGlamInput(glamClassJobInput, 1, null);
            if (!string.Equals(cfg.AutoGlamWeatherClassJobOptions, committedClassJobs, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherClassJobOptions = committedClassJobs;
                cfg.Save();
                glamConfigChanged = true;
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            glamClassJobInput = CommitAutoGlamInput(glamClassJobInput, 1, null);
        ImGui.SameLine();
        ImGui.TextDisabled("(Comma-separated numbers, no spaces)");

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Sunny Plate(s)##glamSunny", ref glamSunnyPlateInput, 128))
        {
            glamSunnyPlateInput = SanitizeAutoGlamInput(glamSunnyPlateInput, 1, 20, true);
            var committedSunnyPlates = CommitAutoGlamInput(glamSunnyPlateInput, 1, 20);
            if (!string.Equals(cfg.AutoGlamWeatherSunnyPlateOptions, committedSunnyPlates, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherSunnyPlateOptions = committedSunnyPlates;
                cfg.Save();
                glamConfigChanged = true;
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            glamSunnyPlateInput = CommitAutoGlamInput(glamSunnyPlateInput, 1, 20);
        ImGui.SameLine();
        ImGui.TextDisabled("(1-20, comma-separated; Clear, Fair, Clouds, Wind, Dust, Heat, Gloom, etc.)");

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Rain Plate(s)##glamRain", ref glamRainPlateInput, 128))
        {
            glamRainPlateInput = SanitizeAutoGlamInput(glamRainPlateInput, 1, 20, true);
            var committedRainPlates = CommitAutoGlamInput(glamRainPlateInput, 1, 20);
            if (!string.Equals(cfg.AutoGlamWeatherRainPlateOptions, committedRainPlates, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherRainPlateOptions = committedRainPlates;
                cfg.Save();
                glamConfigChanged = true;
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            glamRainPlateInput = CommitAutoGlamInput(glamRainPlateInput, 1, 20);
        ImGui.SameLine();
        ImGui.TextDisabled("(1-20, comma-separated; Fog, Gales, Rain, Showers, Thunder, Thunderstorms)");

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Freeze Plate(s)##glamFreeze", ref glamFreezePlateInput, 128))
        {
            glamFreezePlateInput = SanitizeAutoGlamInput(glamFreezePlateInput, 1, 20, true);
            var committedFreezePlates = CommitAutoGlamInput(glamFreezePlateInput, 1, 20);
            if (!string.Equals(cfg.AutoGlamWeatherFreezePlateOptions, committedFreezePlates, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherFreezePlateOptions = committedFreezePlates;
                cfg.Save();
                glamConfigChanged = true;
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            glamFreezePlateInput = CommitAutoGlamInput(glamFreezePlateInput, 1, 20);
        ImGui.SameLine();
        ImGui.TextDisabled("(1-20, comma-separated; Snow, Blizzards)");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(80);
        var interval = cfg.AutoGlamWeatherCheckIntervalSeconds;
        if (ImGui.InputFloat("Check Interval (sec)##glamInterval", ref interval, 0.5f, 1.0f, "%.1f"))
        {
            if (interval < 1.0f) interval = 1.0f;
            if (interval > 60.0f) interval = 60.0f;
            cfg.AutoGlamWeatherCheckIntervalSeconds = interval;
            cfg.Save();
        }

        if (glamConfigChanged && glamWeatherRunning)
            glamLastWeatherGroup = string.Empty;

        var classJobOptions = ParseAutoGlamInput(glamClassJobInput, 1, null);
        var sunnyPlateOptions = ParseAutoGlamInput(glamSunnyPlateInput, 1, 20);
        var rainPlateOptions = ParseAutoGlamInput(glamRainPlateInput, 1, 20);
        var freezePlateOptions = ParseAutoGlamInput(glamFreezePlateInput, 1, 20);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Start/Stop
        var canStart = Plugin.PlayerState.IsLoaded
            && classJobOptions.Count > 0
            && sunnyPlateOptions.Count > 0
            && rainPlateOptions.Count > 0
            && freezePlateOptions.Count > 0;
        var startDisabledReason = !Plugin.PlayerState.IsLoaded
            ? "Player must be loaded to start Auto-Glam Weather."
            : classJobOptions.Count == 0
                ? "Enter at least one valid class/job number."
                : sunnyPlateOptions.Count == 0
                    ? "Enter at least one valid sunny plate number between 1 and 20."
                    : rainPlateOptions.Count == 0
                        ? "Enter at least one valid rain plate number between 1 and 20."
                        : freezePlateOptions.Count == 0
                            ? "Enter at least one valid freeze plate number between 1 and 20."
                            : string.Empty;
        var started = DrawPriorityTaskActionButton(
            SlaveTask.AutoGlamWeather,
            "Start Monitoring##glamStart",
            canStart,
            StartAutoGlamWeatherTask,
            startDisabledReason);
        if (started)
            glamWeatherShowLog = true;

        if (glamWeatherRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Monitoring active...");
        }

        // Current weather info
        if (glamWeatherRunning || !string.IsNullOrEmpty(glamLastWeatherGroup))
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Last weather group: {(string.IsNullOrEmpty(glamLastWeatherGroup) ? "(none yet)" : glamLastWeatherGroup)}");
        }

        DrawTaskLog("glamWeather", ref glamWeatherShowLog, plugin.TaskRunner);
    }

    private void InitializeAutoGlamInputs(XASlave.Configuration cfg)
    {
        if (glamWeatherInputsInitialized)
            return;

        glamClassJobInput = CommitAutoGlamInput(cfg.AutoGlamWeatherClassJobOptions, 1, null);
        glamSunnyPlateInput = CommitAutoGlamInput(cfg.AutoGlamWeatherSunnyPlateOptions, 1, 20);
        glamRainPlateInput = CommitAutoGlamInput(cfg.AutoGlamWeatherRainPlateOptions, 1, 20);
        glamFreezePlateInput = CommitAutoGlamInput(cfg.AutoGlamWeatherFreezePlateOptions, 1, 20);

        var changed = false;
        if (!string.Equals(cfg.AutoGlamWeatherClassJobOptions, glamClassJobInput, StringComparison.Ordinal))
        {
            cfg.AutoGlamWeatherClassJobOptions = glamClassJobInput;
            changed = true;
        }
        if (!string.Equals(cfg.AutoGlamWeatherSunnyPlateOptions, glamSunnyPlateInput, StringComparison.Ordinal))
        {
            cfg.AutoGlamWeatherSunnyPlateOptions = glamSunnyPlateInput;
            changed = true;
        }
        if (!string.Equals(cfg.AutoGlamWeatherRainPlateOptions, glamRainPlateInput, StringComparison.Ordinal))
        {
            cfg.AutoGlamWeatherRainPlateOptions = glamRainPlateInput;
            changed = true;
        }
        if (!string.Equals(cfg.AutoGlamWeatherFreezePlateOptions, glamFreezePlateInput, StringComparison.Ordinal))
        {
            cfg.AutoGlamWeatherFreezePlateOptions = glamFreezePlateInput;
            changed = true;
        }
        if (changed)
            cfg.Save();

        glamWeatherInputsInitialized = true;
    }

    private static string SanitizeAutoGlamInput(string input, int minValue, int? maxValue, bool preserveTrailingComma)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var compactInput = input.Replace(" ", string.Empty);
        var hadTrailingComma = compactInput.EndsWith(",", StringComparison.Ordinal);
        var sanitizedValues = new List<string>();

        foreach (var rawToken in compactInput.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var isNumericToken = true;
            foreach (var ch in rawToken)
            {
                if (!char.IsDigit(ch))
                {
                    isNumericToken = false;
                    break;
                }
            }

            if (!isNumericToken || rawToken.Length == 0)
                continue;

            if (!int.TryParse(rawToken, out var value))
                continue;
            if (value < minValue)
                continue;
            if (maxValue.HasValue && value > maxValue.Value)
                continue;

            sanitizedValues.Add(value.ToString());
        }

        var sanitized = string.Join(",", sanitizedValues);
        if (preserveTrailingComma && hadTrailingComma && sanitized.Length > 0)
            sanitized += ",";
        return sanitized;
    }

    private static string CommitAutoGlamInput(string input, int minValue, int? maxValue)
    {
        return SanitizeAutoGlamInput(input, minValue, maxValue, false);
    }

    private static List<int> ParseAutoGlamInput(string input, int minValue, int? maxValue)
    {
        var committed = CommitAutoGlamInput(input, minValue, maxValue);
        var values = new List<int>();
        if (string.IsNullOrEmpty(committed))
            return values;

        foreach (var token in committed.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var value))
                values.Add(value);
        }

        return values;
    }

    private static int? ChooseAutoGlamRandomValue(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return null;

        return values[Random.Shared.Next(values.Count)];
    }

    private unsafe void CheckWeatherAndChangeGlamour()
    {
        try
        {
            var cfg = plugin.Configuration;
            var classJobOptions = ParseAutoGlamInput(cfg.AutoGlamWeatherClassJobOptions, 1, null);

            // Get current weather ID via FFXIVClientStructs
            var weatherManager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
            if (weatherManager == null) return;

            var weatherId = (int)weatherManager->GetCurrentWeather();

            string group;
            List<int> plateOptions;
            if (SunnyWeatherIds.Contains(weatherId))
            {
                group = "sunny";
                plateOptions = ParseAutoGlamInput(cfg.AutoGlamWeatherSunnyPlateOptions, 1, 20);
            }
            else if (RainWeatherIds.Contains(weatherId))
            {
                group = "rain";
                plateOptions = ParseAutoGlamInput(cfg.AutoGlamWeatherRainPlateOptions, 1, 20);
            }
            else if (FreezeWeatherIds.Contains(weatherId))
            {
                group = "freeze";
                plateOptions = ParseAutoGlamInput(cfg.AutoGlamWeatherFreezePlateOptions, 1, 20);
            }
            else
            {
                group = "sunny"; // default
                plateOptions = ParseAutoGlamInput(cfg.AutoGlamWeatherSunnyPlateOptions, 1, 20);
            }

            if (group != glamLastWeatherGroup)
            {
                var weatherName = WeatherNames.TryGetValue(weatherId, out var wn) ? wn : $"Unknown({weatherId})";
                var classJob = ChooseAutoGlamRandomValue(classJobOptions);
                var plate = ChooseAutoGlamRandomValue(plateOptions);
                if (!classJob.HasValue || !plate.HasValue)
                {
                    plugin.TaskRunner.AddLog($"[Auto-Glam] Weather changed to {weatherName} ({group}) but no valid class/job or plate options are configured.");
                    glamLastWeatherGroup = group;
                    return;
                }

                plugin.TaskRunner.AddLog($"[Auto-Glam] Weather changed to {weatherName} ({group}) — applying class/job {classJob.Value}, plate {plate.Value}");
                ChatHelper.SendMessage($"/gs change {classJob.Value} {plate.Value}");
                glamLastWeatherGroup = group;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] Auto-Glam weather check error: {ex.Message}");
        }
    }

    private void StartAutoGlamWeatherTask()
    {
        if (glamWeatherRunning)
            return;

        glamWeatherRunning = true;
        glamLastWeatherGroup = string.Empty;
        glamLastCheck = DateTime.MinValue;
        plugin.TaskRunner.AddLog("[Auto-Glam] Weather monitoring started.");
        UpdatePriorityTaskExternalStatus();
    }

    private void StopAutoGlamWeatherTask()
    {
        if (!glamWeatherRunning)
            return;

        glamWeatherRunning = false;
        plugin.TaskRunner.AddLog("[Auto-Glam] Weather monitoring stopped.");
        UpdatePriorityTaskExternalStatus();
    }
}
