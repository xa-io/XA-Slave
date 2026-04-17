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
    private int glamLastWeatherId = -1;
    private DateTime glamLastCheck = DateTime.MinValue;
    private string glamClassJobInput = string.Empty;
    private readonly Dictionary<int, string> glamWeatherPlateInputs = new();
    private bool glamWeatherInputsInitialized;

    private const float AutoGlamClassJobInputWidth = 180f;
    private const float AutoGlamWeatherPlateInputWidth = 90f;
    private const int AutoGlamWeatherColumnCount = 3;
    private const float AutoGlamWeatherColumnWidth = 95f;

    // Weather ID → group mapping
    private static readonly (int Id, string Label)[] AutoGlamWeatherDefinitions =
    {
        (1, "Clear Skies"),
        (2, "Fair Skies"),
        (3, "Clouds"),
        (4, "Fog"),
        (5, "Wind"),
        (6, "Gales"),
        (7, "Rain"),
        (8, "Showers"),
        (9, "Thunder"),
        (10, "Thunderstorms"),
        (11, "Dust Storms"),
        (14, "Heat Waves"),
        (15, "Snow"),
        (16, "Blizzards"),
        (17, "Gloom"),
        (49, "Umbral Wind"),
        (50, "Umbral Static"),
        (148, "Moon Dust"),
        (149, "Astro Storm"),
    };

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

        // Start/Stop button moved to top
        var classJobOptions = ParseAutoGlamInput(glamClassJobInput, 1, null);
        var firstMissingWeatherLabel = GetFirstMissingAutoGlamWeatherLabel(cfg);

        var canStart = Plugin.PlayerState.IsLoaded
            && classJobOptions.Count > 0
            && string.IsNullOrEmpty(firstMissingWeatherLabel);
        var startDisabledReason = !Plugin.PlayerState.IsLoaded
            ? "Player must be loaded to start Auto-Glam Weather."
            : classJobOptions.Count == 0
                ? "Enter at least one valid class/job number."
                : !string.IsNullOrEmpty(firstMissingWeatherLabel)
                    ? $"Enter at least one valid plate number between 1 and 20 for {firstMissingWeatherLabel}."
                    : string.Empty;
        var started = DrawPriorityTaskActionButton(
            SlaveTask.AutoGlamWeather,
            "Start Monitoring##glamStart",
            canStart,
            StartAutoGlamWeatherTask,
            startDisabledReason);
        if (started)
            AutoOpenTaskLogIfVerbose(ref glamWeatherShowLog);

        if (glamWeatherRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Monitoring active...");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Configuration
        ImGui.Text("Glamour Plate Configuration:");
        ImGui.Spacing();

        var glamConfigChanged = false;

        ImGui.SetNextItemWidth(Scale(AutoGlamClassJobInputWidth));
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

        ImGui.TextDisabled("Each weather box accepts comma-separated glamour plate values from 1-20.");
        if (DrawAutoGlamWeatherPlateGrid(cfg))
            glamConfigChanged = true;

        ImGui.Spacing();
        ImGui.SetNextItemWidth(Scale(80f));
        var interval = cfg.AutoGlamWeatherCheckIntervalSeconds;
        if (ImGui.InputFloat("Check Interval (sec)##glamInterval", ref interval, 0.5f, 1.0f, "%.1f"))
        {
            if (interval < 1.0f) interval = 1.0f;
            if (interval > 60.0f) interval = 60.0f;
            cfg.AutoGlamWeatherCheckIntervalSeconds = interval;
            cfg.Save();
        }

        if (glamConfigChanged && glamWeatherRunning)
            glamLastWeatherId = -1;

        // Current weather info
        if (glamWeatherRunning || glamLastWeatherId >= 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Last weather: {GetLastAutoGlamWeatherLabel()}");
        }

        DrawTaskLog("glamWeather", ref glamWeatherShowLog, plugin.TaskRunner);
    }

    private void InitializeAutoGlamInputs(XASlave.Configuration cfg)
    {
        if (glamWeatherInputsInitialized)
            return;

        glamClassJobInput = CommitAutoGlamInput(cfg.AutoGlamWeatherClassJobOptions, 1, null);

        var changed = false;
        if (!string.Equals(cfg.AutoGlamWeatherClassJobOptions, glamClassJobInput, StringComparison.Ordinal))
        {
            cfg.AutoGlamWeatherClassJobOptions = glamClassJobInput;
            changed = true;
        }
        foreach (var definition in AutoGlamWeatherDefinitions)
        {
            var committed = CommitAutoGlamInput(GetAutoGlamWeatherPlateOptions(cfg, definition.Id), 1, 20);
            glamWeatherPlateInputs[definition.Id] = committed;
            if (!string.Equals(GetAutoGlamWeatherPlateOptions(cfg, definition.Id), committed, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherPlateOptionsByWeatherId[definition.Id] = committed;
                changed = true;
            }
        }
        if (changed)
            cfg.Save();

        glamWeatherInputsInitialized = true;
    }

    private bool DrawAutoGlamWeatherPlateGrid(XASlave.Configuration cfg)
    {
        var changed = false;
        var style = ImGui.GetStyle();
        var startPos = ImGui.GetCursorPos();
        var startX = MathF.Ceiling(startPos.X);
        var startY = MathF.Ceiling(startPos.Y);
        var columnWidth = GetAutoGlamWeatherGridColumnWidth();
        var columnStride = MathF.Ceiling(columnWidth + style.ItemSpacing.X);
        var maxHeight = 0f;

        for (var columnIndex = 0; columnIndex < AutoGlamWeatherColumnCount; columnIndex++)
        {
            ImGui.SetCursorPos(new Vector2(startX + (columnIndex * columnStride), startY));
            ImGui.BeginGroup();
            if (DrawAutoGlamWeatherPlateColumn(cfg, columnIndex))
                changed = true;
            ImGui.EndGroup();

            var columnHeight = MathF.Ceiling(ImGui.GetCursorPosY() - startY);
            if (columnHeight > maxHeight)
                maxHeight = columnHeight;
        }

        ImGui.SetCursorPos(new Vector2(startX, startY + maxHeight));
        return changed;
    }

    private float GetAutoGlamWeatherGridColumnWidth()
    {
        var width = MathF.Max(Scale(AutoGlamWeatherColumnWidth), Scale(AutoGlamWeatherPlateInputWidth));
        foreach (var definition in AutoGlamWeatherDefinitions)
        {
            var labelWidth = MathF.Ceiling(ImGui.CalcTextSize(definition.Label).X);
            if (labelWidth > width)
                width = labelWidth;
        }

        return width;
    }

    private bool DrawAutoGlamWeatherPlateColumn(XASlave.Configuration cfg, int columnIndex)
    {
        var changed = false;
        for (var i = columnIndex; i < AutoGlamWeatherDefinitions.Length; i += AutoGlamWeatherColumnCount)
        {
            var definition = AutoGlamWeatherDefinitions[i];
            if (DrawAutoGlamWeatherPlateInput(cfg, definition.Id, definition.Label))
                changed = true;
        }

        return changed;
    }

    private bool DrawAutoGlamWeatherPlateInput(XASlave.Configuration cfg, int weatherId, string label)
    {
        glamWeatherPlateInputs.TryGetValue(weatherId, out var input);
        input ??= CommitAutoGlamInput(GetAutoGlamWeatherPlateOptions(cfg, weatherId), 1, 20);

        var changed = false;
        var startY = MathF.Ceiling(ImGui.GetCursorPosY());
        ImGui.SetCursorPosY(startY);
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(Scale(AutoGlamWeatherPlateInputWidth));
        if (ImGui.InputText($"##glamWeather{weatherId}", ref input, 128))
        {
            input = SanitizeAutoGlamInput(input, 1, 20, true);
            glamWeatherPlateInputs[weatherId] = input;
            var committed = CommitAutoGlamInput(input, 1, 20);
            if (!string.Equals(GetAutoGlamWeatherPlateOptions(cfg, weatherId), committed, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherPlateOptionsByWeatherId[weatherId] = committed;
                cfg.Save();
                changed = true;
            }
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            input = CommitAutoGlamInput(input, 1, 20);
            glamWeatherPlateInputs[weatherId] = input;
            if (!string.Equals(GetAutoGlamWeatherPlateOptions(cfg, weatherId), input, StringComparison.Ordinal))
            {
                cfg.AutoGlamWeatherPlateOptionsByWeatherId[weatherId] = input;
                cfg.Save();
                changed = true;
            }
        }

        if (ImGui.IsItemHovered() && WeatherNames.TryGetValue(weatherId, out var weatherName))
            ImGui.SetTooltip($"{weatherName} plate options (1-20, comma-separated).");

        var style = ImGui.GetStyle();
        var entryHeight = MathF.Ceiling(ImGui.CalcTextSize("Ag").Y + style.ItemSpacing.Y + ImGui.GetFrameHeight() + style.ItemSpacing.Y);
        var nextY = MathF.Ceiling(startY + entryHeight);
        if (ImGui.GetCursorPosY() < nextY)
            ImGui.SetCursorPosY(nextY);

        return changed;
    }

    private string GetFirstMissingAutoGlamWeatherLabel(XASlave.Configuration cfg)
    {
        foreach (var definition in AutoGlamWeatherDefinitions)
        {
            if (ParseAutoGlamInput(GetAutoGlamWeatherPlateOptions(cfg, definition.Id), 1, 20).Count == 0)
                return definition.Label;
        }

        return string.Empty;
    }

    private static string GetAutoGlamWeatherPlateOptions(XASlave.Configuration cfg, int weatherId)
    {
        if (cfg.AutoGlamWeatherPlateOptionsByWeatherId.TryGetValue(weatherId, out var options))
            return options ?? string.Empty;

        return string.Empty;
    }

    private string GetLastAutoGlamWeatherLabel()
    {
        if (glamLastWeatherId < 0)
            return "(none yet)";

        return WeatherNames.TryGetValue(glamLastWeatherId, out var weatherName)
            ? weatherName
            : $"Unknown({glamLastWeatherId})";
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

            var weatherOptionsId = cfg.AutoGlamWeatherPlateOptionsByWeatherId.ContainsKey(weatherId) ? weatherId : 1;
            var plateOptions = ParseAutoGlamInput(GetAutoGlamWeatherPlateOptions(cfg, weatherOptionsId), 1, 20);

            if (weatherId != glamLastWeatherId)
            {
                var weatherName = WeatherNames.TryGetValue(weatherId, out var wn) ? wn : $"Unknown({weatherId})";
                var classJob = ChooseAutoGlamRandomValue(classJobOptions);
                var plate = ChooseAutoGlamRandomValue(plateOptions);
                if (!classJob.HasValue || !plate.HasValue)
                {
                    plugin.TaskRunner.AddLog($"[Auto-Glam] Weather changed to {weatherName} but no valid class/job or plate options are configured.");
                    glamLastWeatherId = weatherId;
                    return;
                }

                plugin.TaskRunner.AddLog($"[Auto-Glam] Weather changed to {weatherName} — applying class/job {classJob.Value}, plate {plate.Value}");
                ChatHelper.SendMessage($"/gs change {classJob.Value} {plate.Value}");
                glamLastWeatherId = weatherId;
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
        glamLastWeatherId = -1;
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
