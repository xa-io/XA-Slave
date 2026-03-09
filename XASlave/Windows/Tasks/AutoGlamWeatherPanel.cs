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
    private int glamClassToAssign = 1;
    private int glamPlateSunny = 2;
    private int glamPlateRain = 3;
    private int glamPlateFreeze = 1;
    private float glamCheckIntervalSec = 3.0f;
    private string glamLastWeatherGroup = "";
    private DateTime glamLastCheck = DateTime.MinValue;

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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Auto-Glam Against Weather");
        ImGui.TextDisabled("Automatically changes glamour plate based on current weather conditions.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Configuration
        ImGui.Text("Glamour Plate Configuration:");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Class/Job to Assign##glamClass", ref glamClassToAssign);
        if (glamClassToAssign < 1) glamClassToAssign = 1;

        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Sunny Plate##glamSunny", ref glamPlateSunny);
        if (glamPlateSunny < 1) glamPlateSunny = 1;
        ImGui.SameLine();
        ImGui.TextDisabled("(Clear, Fair, Clouds, Wind, Dust, Heat, Gloom, etc.)");

        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Rain Plate##glamRain", ref glamPlateRain);
        if (glamPlateRain < 1) glamPlateRain = 1;
        ImGui.SameLine();
        ImGui.TextDisabled("(Fog, Gales, Rain, Showers, Thunder, Thunderstorms)");

        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Freeze Plate##glamFreeze", ref glamPlateFreeze);
        if (glamPlateFreeze < 1) glamPlateFreeze = 1;
        ImGui.SameLine();
        ImGui.TextDisabled("(Snow, Blizzards)");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(80);
        var interval = glamCheckIntervalSec;
        if (ImGui.InputFloat("Check Interval (sec)##glamInterval", ref interval, 0.5f, 1.0f, "%.1f"))
        {
            if (interval < 1.0f) interval = 1.0f;
            if (interval > 60.0f) interval = 60.0f;
            glamCheckIntervalSec = interval;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Start/Stop
        if (!glamWeatherRunning)
        {
            if (ImGui.Button("Start Monitoring##glamStart"))
            {
                glamWeatherRunning = true;
                glamLastWeatherGroup = "";
                glamLastCheck = DateTime.MinValue;
                plugin.TaskRunner.AddLog("[Auto-Glam] Weather monitoring started.");
            }
        }
        else
        {
            if (ImGui.Button("Stop Monitoring##glamStop"))
            {
                glamWeatherRunning = false;
                plugin.TaskRunner.AddLog("[Auto-Glam] Weather monitoring stopped.");
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Monitoring active...");

            // Polling logic — runs each frame but only acts on interval
            if ((DateTime.UtcNow - glamLastCheck).TotalSeconds >= glamCheckIntervalSec)
            {
                glamLastCheck = DateTime.UtcNow;
                CheckWeatherAndChangeGlamour();
            }
        }

        // Current weather info
        if (glamWeatherRunning || !string.IsNullOrEmpty(glamLastWeatherGroup))
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Last weather group: {(string.IsNullOrEmpty(glamLastWeatherGroup) ? "(none yet)" : glamLastWeatherGroup)}");
        }

        DrawTaskLog("glamWeather", ref glamWeatherShowLog, plugin.TaskRunner);
    }

    private unsafe void CheckWeatherAndChangeGlamour()
    {
        try
        {
            // Get current weather ID via FFXIVClientStructs
            var weatherManager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
            if (weatherManager == null) return;

            var weatherId = (int)weatherManager->GetCurrentWeather();

            string group;
            int plate;
            if (SunnyWeatherIds.Contains(weatherId))
            {
                group = "sunny";
                plate = glamPlateSunny;
            }
            else if (RainWeatherIds.Contains(weatherId))
            {
                group = "rain";
                plate = glamPlateRain;
            }
            else if (FreezeWeatherIds.Contains(weatherId))
            {
                group = "freeze";
                plate = glamPlateFreeze;
            }
            else
            {
                group = "sunny"; // default
                plate = glamPlateSunny;
            }

            if (group != glamLastWeatherGroup)
            {
                var weatherName = WeatherNames.TryGetValue(weatherId, out var wn) ? wn : $"Unknown({weatherId})";
                plugin.TaskRunner.AddLog($"[Auto-Glam] Weather changed to {weatherName} ({group}) — applying plate {plate}");
                ChatHelper.SendMessage($"/gs change {glamClassToAssign} {plate}");
                glamLastWeatherGroup = group;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] Auto-Glam weather check error: {ex.Message}");
        }
    }
}
