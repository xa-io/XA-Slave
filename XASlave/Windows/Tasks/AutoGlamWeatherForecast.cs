using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed record AutoGlamForecastRow(string StartsAt, string Weather);

    private DateTime glamForecastNextRefresh = DateTime.MinValue;
    private uint glamForecastTerritory;
    private int glamForecastLanguage = -1;
    private long glamForecastPeriod = -1;
    private string glamForecastZone = string.Empty;
    private string glamForecastCurrent = "Unavailable";
    private string glamForecastNotice = "Log in to view the current zone's weather.";
    private bool glamForecastSpecialZone;
    private uint glamForecastNaturalWeather;
    private readonly Dictionary<uint, string> glamForecastWeatherNames = new();
    private AutoGlamForecastRow[] glamForecastRows = Array.Empty<AutoGlamForecastRow>();

    // Called by the existing framework subscription, including while monitoring is stopped.
    // Draw only consumes these cached strings: no sheet/native work occurs there.
    private unsafe void UpdateAutoGlamForecast()
    {
        var now = DateTime.UtcNow;
        if (now < glamForecastNextRefresh)
            return;
        glamForecastNextRefresh = now.AddSeconds(1);

        if (!Plugin.PlayerState.IsLoaded || Plugin.ClientState.TerritoryType == 0)
        {
            ClearAutoGlamForecast("Log in to view the current zone's weather.");
            return;
        }

        try
        {
            var territoryId = (uint)Plugin.ClientState.TerritoryType;
            var language = (int)Plugin.DataManager.Language;
            var period = WeatherForecast.PeriodStart(new DateTimeOffset(now).ToUnixTimeSeconds());
            if (territoryId != glamForecastTerritory || language != glamForecastLanguage || period != glamForecastPeriod)
            {
                ClearAutoGlamForecast("Forecast unavailable for this zone.");
                var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
                if (!territory.HasValue)
                    return;

                glamForecastZone = territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Zone {territoryId}";
                if (string.IsNullOrWhiteSpace(glamForecastZone))
                    glamForecastZone = $"Zone {territoryId}";
                glamForecastSpecialZone = territory.Value.ContentFinderCondition.RowId != 0
                    || territory.Value.IndividualWeather.RowId != 0;

                var rateRow = territory.Value.WeatherRate.ValueNullable;
                if (rateRow.HasValue)
                {
                    var rates = new List<WeatherForecastRate>();
                    // Keep each rate paired with its original weather, including zero-rate slots.
                    for (var i = 0; i < rateRow.Value.Rate.Count && i < rateRow.Value.Weather.Count; i++)
                        rates.Add(new WeatherForecastRate(rateRow.Value.Weather[i].RowId, rateRow.Value.Rate[i]));

                    glamForecastNaturalWeather = WeatherForecast.ResolveWeather(rates, WeatherForecast.CalculateTarget(period));
                    if (glamForecastNaturalWeather != 0)
                    {
                        var rows = new AutoGlamForecastRow[WeatherForecast.FuturePeriods];
                        for (var i = 0; i < rows.Length; i++)
                        {
                            var start = period + (i + 1) * WeatherForecast.PeriodSeconds;
                            var weatherId = WeatherForecast.ResolveWeather(rates, WeatherForecast.CalculateTarget(start));
                            var local = DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime();
                            var eorzeaHour = (start / 175) % 24;
                            rows[i] = new AutoGlamForecastRow($"{local:HH:mm:ss} / {eorzeaHour:00}:00 ET", GetAutoGlamForecastWeatherName(weatherId));
                        }
                        glamForecastRows = rows;
                    }
                }

                // Missing sheets are retried on the next tick; valid no-forecast zones stay cached.
                if (rateRow.HasValue)
                {
                    glamForecastTerritory = territoryId;
                    glamForecastLanguage = language;
                    glamForecastPeriod = period;
                }
            }

            var manager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
            var currentWeather = manager == null ? 0u : manager->GetCurrentWeather();
            glamForecastCurrent = GetAutoGlamForecastWeatherName(currentWeather);
            var overridden = manager != null && manager->WeatherOverride != 0;
            var differs = currentWeather != 0 && glamForecastNaturalWeather != 0 && currentWeather != glamForecastNaturalWeather;
            glamForecastNotice = glamForecastRows.Length == 0
                ? "Forecast unavailable for this zone."
                : overridden || differs || glamForecastSpecialZone
                    ? "Normal zone forecast only; quest, duty or special weather can override it."
                    : "Next six normal zone periods; special weather can override them. Start times: local / Eorzea time (ET).";
        }
        catch (Exception)
        {
            // Clear stale territory/weather on a missing row or a transient zone transition.
            ClearAutoGlamForecast("Weather data is temporarily unavailable.");
        }
    }

    private string GetAutoGlamForecastWeatherName(uint weatherId)
    {
        if (weatherId == 0)
            return "Unavailable";
        if (glamForecastWeatherNames.TryGetValue(weatherId, out var cached))
            return cached;
        var row = Plugin.DataManager.GetExcelSheet<Weather>()?.GetRowOrDefault(weatherId);
        var name = row?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name))
            return $"Unknown weather ({weatherId})";
        glamForecastWeatherNames[weatherId] = name;
        return name;
    }

    private void ClearAutoGlamForecast(string notice)
    {
        glamForecastTerritory = 0;
        glamForecastLanguage = -1;
        glamForecastPeriod = -1;
        glamForecastZone = string.Empty;
        glamForecastCurrent = "Unavailable";
        glamForecastNotice = notice;
        glamForecastSpecialZone = false;
        glamForecastNaturalWeather = 0;
        glamForecastRows = Array.Empty<AutoGlamForecastRow>();
        glamForecastWeatherNames.Clear();
    }

    private void DrawAutoGlamForecast()
    {
        ImGui.Spacing();
        if (!string.IsNullOrEmpty(glamForecastZone))
        {
            ImGui.TextUnformatted($"Weather in {glamForecastZone}");
            ImGui.TextUnformatted($"Current: {glamForecastCurrent}");
        }
        ImGui.TextWrapped(glamForecastNotice);
        if (glamForecastRows.Length == 0)
            return;

        if (ImGui.BeginTable("##autoGlamForecast", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Starts (local / ET)");
            ImGui.TableSetupColumn("Normal forecast");
            ImGui.TableHeadersRow();
            foreach (var row in glamForecastRows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.StartsAt);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Weather);
            }
            ImGui.EndTable();
        }
    }
}
