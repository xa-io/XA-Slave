using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed record AutoGlamForecastRow(string StartsAt, global::XASlave.Localization.UiMessage Weather);

    private DateTime glamForecastNextRefresh = DateTime.MinValue;
    private uint glamForecastTerritory;
    private int glamForecastLanguage = -1;
    private long glamForecastPeriod = -1;
    private string glamForecastZone = global::XASlave.Localization.UiMessage.Raw(string.Empty).Original;
    private global::XASlave.Localization.UiMessage? glamForecastZoneMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private string glamForecastCurrent = global::XASlave.Localization.UiMessage.Key("SCA184496974204A0").Original;
    private global::XASlave.Localization.UiMessage? glamForecastCurrentMessage = global::XASlave.Localization.UiMessage.Key("SCA184496974204A0");
    private string glamForecastNotice = global::XASlave.Localization.UiMessage.Key("SB1AB5EE30D922D7A").Original;
    private global::XASlave.Localization.UiMessage? glamForecastNoticeMessage = global::XASlave.Localization.UiMessage.Key("SB1AB5EE30D922D7A");
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
            ClearAutoGlamForecast(global::XASlave.Localization.UiMessage.Key("SB1AB5EE30D922D7A"));
            return;
        }

        try
        {
            var territoryId = (uint)Plugin.ClientState.TerritoryType;
            var language = (int)Plugin.DataManager.Language;
            var period = WeatherForecast.PeriodStart(new DateTimeOffset(now).ToUnixTimeSeconds());
            if (territoryId != glamForecastTerritory || language != glamForecastLanguage || period != glamForecastPeriod)
            {
                ClearAutoGlamForecast(global::XASlave.Localization.UiMessage.Key("SF96EE281310C9151"));
                var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
                if (!territory.HasValue)
                    return;

                glamForecastZone = global::XASlave.Localization.UiMessage.Store(ref glamForecastZoneMessage, (territory.Value.PlaceName.ValueNullable?.Name.ExtractText() is { } zoneName ? global::XASlave.Localization.UiMessage.Raw(zoneName) : global::XASlave.Localization.UiMessage.Format("S565669559E71B77C", territoryId)));
                if (string.IsNullOrWhiteSpace(glamForecastZone))
                    glamForecastZone = global::XASlave.Localization.UiMessage.Store(ref glamForecastZoneMessage, global::XASlave.Localization.UiMessage.Format("S565669559E71B77C", territoryId));
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
            glamForecastCurrent = global::XASlave.Localization.UiMessage.Store(ref glamForecastCurrentMessage, GetAutoGlamForecastWeatherName(currentWeather));
            var overridden = manager != null && manager->WeatherOverride != 0;
            var differs = currentWeather != 0 && glamForecastNaturalWeather != 0 && currentWeather != glamForecastNaturalWeather;
            glamForecastNotice = global::XASlave.Localization.UiMessage.Store(ref glamForecastNoticeMessage, (glamForecastRows.Length == 0 ? global::XASlave.Localization.UiMessage.Key("SF96EE281310C9151") : (overridden || differs || glamForecastSpecialZone ? global::XASlave.Localization.UiMessage.Key("S44DB55A2F24D889E") : global::XASlave.Localization.UiMessage.Key("SA9BADC05603ACA22"))));
        }
        catch (Exception)
        {
            // Clear stale territory/weather on a missing row or a transient zone transition.
            ClearAutoGlamForecast(global::XASlave.Localization.UiMessage.Key("S745FD07B41ABA708"));
        }
    }

    private global::XASlave.Localization.UiMessage GetAutoGlamForecastWeatherName(uint weatherId)
    {
        if (weatherId == 0)
            return global::XASlave.Localization.UiMessage.Key("SCA184496974204A0");
        if (glamForecastWeatherNames.TryGetValue(weatherId, out var cached))
            return global::XASlave.Localization.UiMessage.Raw(cached);
        var row = Plugin.DataManager.GetExcelSheet<Weather>()?.GetRowOrDefault(weatherId);
        var name = row?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name))
            return global::XASlave.Localization.UiMessage.Format("SF32C0C76C2AA95F9", weatherId);
        glamForecastWeatherNames[weatherId] = name;
        return global::XASlave.Localization.UiMessage.Raw(name);
    }

    private void ClearAutoGlamForecast(global::XASlave.Localization.UiMessage notice)
    {
        glamForecastTerritory = 0;
        glamForecastLanguage = -1;
        glamForecastPeriod = -1;
        glamForecastZone = global::XASlave.Localization.UiMessage.Store(ref glamForecastZoneMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        glamForecastCurrent = global::XASlave.Localization.UiMessage.Store(ref glamForecastCurrentMessage, global::XASlave.Localization.UiMessage.Key("SCA184496974204A0"));
        glamForecastNotice = global::XASlave.Localization.UiMessage.Store(ref glamForecastNoticeMessage, notice);
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
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S1A17CAFF6E846B91", global::XASlave.Localization.UiMessage.Resolve(ref glamForecastZoneMessage, glamForecastZone)));
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S4C1CA9FE0E115CC8", global::XASlave.Localization.UiMessage.Resolve(ref glamForecastCurrentMessage, glamForecastCurrent)));
        }
        ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref glamForecastNoticeMessage, glamForecastNotice));
        if (glamForecastRows.Length == 0)
            return;

        if (ImGui.BeginTable("##autoGlamForecast", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S63EA14A7572A3E48"));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S47E4AE84C45D86F7"));
            global::XASlave.Localization.UiNative.TableHeadersRow();
            foreach (var row in glamForecastRows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.StartsAt);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Weather.Display);
            }
            ImGui.EndTable();
        }
    }
}
