using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string eurekaHunterStatus = string.Empty;
    private DateTime eurekaHunterStatusExpiryUtc = DateTime.MinValue;
    private bool eurekaHunterStatusIsError;
    private bool eurekaHunterHowToUseExpanded = true;

    private void ApplyEurekaInstanceIdConfiguration()
        => plugin.EurekaInstanceId.ApplyConfiguration();

    private bool GetEurekaInstanceIdZoneEnabled(EurekaInstanceIdService.EurekaZone zone)
    {
        return zone switch
        {
            EurekaInstanceIdService.EurekaZone.Anemos => plugin.Configuration.EurekaInstanceIdAnemosEnabled,
            EurekaInstanceIdService.EurekaZone.Pagos => plugin.Configuration.EurekaInstanceIdPagosEnabled,
            EurekaInstanceIdService.EurekaZone.Pyros => plugin.Configuration.EurekaInstanceIdPyrosEnabled,
            EurekaInstanceIdService.EurekaZone.Hydatos => plugin.Configuration.EurekaInstanceIdHydatosEnabled,
            _ => false,
        };
    }

    private void SetEurekaInstanceIdZoneEnabled(EurekaInstanceIdService.EurekaZone zone, bool enabled)
    {
        switch (zone)
        {
            case EurekaInstanceIdService.EurekaZone.Anemos:
                plugin.Configuration.EurekaInstanceIdAnemosEnabled = enabled;
                break;
            case EurekaInstanceIdService.EurekaZone.Pagos:
                plugin.Configuration.EurekaInstanceIdPagosEnabled = enabled;
                break;
            case EurekaInstanceIdService.EurekaZone.Pyros:
                plugin.Configuration.EurekaInstanceIdPyrosEnabled = enabled;
                break;
            case EurekaInstanceIdService.EurekaZone.Hydatos:
                plugin.Configuration.EurekaInstanceIdHydatosEnabled = enabled;
                break;
        }
    }

    private int GetEurekaInstanceIdZoneBaseline(EurekaInstanceIdService.EurekaZone zone)
    {
        return zone switch
        {
            EurekaInstanceIdService.EurekaZone.Anemos => plugin.Configuration.EurekaInstanceIdAnemosBaselineInstanceId,
            EurekaInstanceIdService.EurekaZone.Pagos => plugin.Configuration.EurekaInstanceIdPagosBaselineInstanceId,
            EurekaInstanceIdService.EurekaZone.Pyros => plugin.Configuration.EurekaInstanceIdPyrosBaselineInstanceId,
            EurekaInstanceIdService.EurekaZone.Hydatos => plugin.Configuration.EurekaInstanceIdHydatosBaselineInstanceId,
            _ => 0,
        };
    }

    private void SetEurekaInstanceIdZoneBaseline(EurekaInstanceIdService.EurekaZone zone, int baselineInstanceId)
    {
        var normalizedBaseline = EurekaInstanceIdService.NormalizeInstanceId(baselineInstanceId);
        switch (zone)
        {
            case EurekaInstanceIdService.EurekaZone.Anemos:
                plugin.Configuration.EurekaInstanceIdAnemosBaselineInstanceId = normalizedBaseline;
                break;
            case EurekaInstanceIdService.EurekaZone.Pagos:
                plugin.Configuration.EurekaInstanceIdPagosBaselineInstanceId = normalizedBaseline;
                break;
            case EurekaInstanceIdService.EurekaZone.Pyros:
                plugin.Configuration.EurekaInstanceIdPyrosBaselineInstanceId = normalizedBaseline;
                break;
            case EurekaInstanceIdService.EurekaZone.Hydatos:
                plugin.Configuration.EurekaInstanceIdHydatosBaselineInstanceId = normalizedBaseline;
                break;
        }
    }

    private int GetEnabledEurekaInstanceIdZoneCount()
    {
        var count = 0;
        foreach (var zone in EurekaZoneOptions)
        {
            if (GetEurekaInstanceIdZoneEnabled(zone))
                count++;
        }

        return count;
    }

    private string BuildEnabledEurekaInstanceIdZoneSummary()
    {
        var enabledZones = new System.Collections.Generic.List<string>();
        foreach (var zone in EurekaZoneOptions)
        {
            if (GetEurekaInstanceIdZoneEnabled(zone))
                enabledZones.Add(EurekaInstanceIdService.GetZoneLabel(zone));
        }

        return enabledZones.Count == 0
            ? "no zones selected"
            : string.Join(", ", enabledZones);
    }

    private void DrawEurekaInstanceIdSharedDisplayOptions(string idSuffix)
    {
        var configuration = plugin.Configuration;
        var showInDtr = configuration.EurekaInstanceIdShowInDtr;
        if (ImGui.Checkbox($"Show live instance in DTR##{idSuffix}", ref showInDtr))
        {
            configuration.EurekaInstanceIdShowInDtr = showInDtr;
            ApplyEurekaInstanceIdConfiguration();
            configuration.Save();
        }
    }

    private void SetEurekaInstanceHunterStatus(string message, bool isError = false)
    {
        eurekaHunterStatus = message;
        eurekaHunterStatusIsError = isError;
        eurekaHunterStatusExpiryUtc = DateTime.UtcNow.AddSeconds(8);
    }

    private void PlayEurekaInstanceHunterSoundPreview()
    {
        var soundEffectValue = XAPeepData.GetSoundEffectValue(plugin.Configuration.EurekaInstanceIdSoundEffectId);
        if (soundEffectValue == 0)
            return;

        if (XAPeepSoundPlayer.TryPlayAlert(plugin.Configuration.EurekaInstanceIdSoundEffectId, plugin.Configuration.EurekaInstanceIdSoundVolume, Plugin.Log))
            return;

        try
        {
            UIGlobals.PlaySoundEffect(soundEffectValue);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] Eureka Instance Hunter could not play the configured preview sound.");
        }
    }

    private static Vector4 BrightenButtonColor(Vector4 color, float amount)
    {
        return new Vector4(
            Math.Clamp(color.X + amount, 0f, 1f),
            Math.Clamp(color.Y + amount, 0f, 1f),
            Math.Clamp(color.Z + amount, 0f, 1f),
            color.W);
    }

    private static Vector4 GetEurekaHunterStartButtonColor(bool isScanning)
    {
        if (!isScanning)
            return new Vector4(0.18f, 0.52f, 0.18f, 1.0f);

        var pulse = (MathF.Sin((float)ImGui.GetTime() * 4.2f) + 1f) * 0.5f;
        return Vector4.Lerp(
            new Vector4(0.14f, 0.42f, 0.14f, 1.0f),
            new Vector4(0.24f, 0.85f, 0.24f, 1.0f),
            pulse);
    }

    private static Vector4 GetEurekaHunterStopButtonColor()
        => new Vector4(0.62f, 0.18f, 0.18f, 1.0f);

    private void DrawEurekaInstanceHunterTask()
    {
        var configuration = plugin.Configuration;
        var isScanning = plugin.EurekaInstanceId.IsScanning;

        ImGui.TextColored(new Vector4(0.82f, 1.0f, 0.55f, 1.0f), "Eureka Instance Hunter");
        ImGui.TextDisabled("Field Operations task for Eureka re-entry scanning. The Instance ID toggle here is shared with XA Mods.");
        ImGui.Spacing();

        var enabled = configuration.EurekaInstanceIdEnabled;
        if (ImGui.Checkbox("Instance ID##EurekaInstanceHunterEnabled", ref enabled))
        {
            configuration.EurekaInstanceIdEnabled = plugin.EurekaInstanceId.SetEnabled(enabled);
            configuration.Save();
            SetEurekaInstanceHunterStatus(
                configuration.EurekaInstanceIdEnabled
                    ? "Enabled live Eureka instance display."
                    : "Disabled live Eureka instance display.");
        }

        ImGui.SameLine();
        var stateLabel = isScanning
            ? "Running"
            : configuration.EurekaInstanceIdEnabled
                ? "Idle"
                : "Disabled";
        var stateColor = isScanning
            ? new Vector4(0.30f, 1.0f, 0.40f, 1.0f)
            : configuration.EurekaInstanceIdEnabled
                ? new Vector4(1.0f, 0.85f, 0.35f, 1.0f)
                : new Vector4(1.0f, 0.45f, 0.45f, 1.0f);
        ImGui.TextColored(stateColor, $"Status: {stateLabel}");

        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(plugin.EurekaInstanceId.StatusText);
        ImGui.PopTextWrapPos();

        if (!string.IsNullOrEmpty(eurekaHunterStatus) && DateTime.UtcNow < eurekaHunterStatusExpiryUtc)
            ImGui.TextColored(eurekaHunterStatusIsError ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f) : new Vector4(0.4f, 1.0f, 0.4f, 1.0f), eurekaHunterStatus);

        ImGui.Spacing();
        if (ImGui.BeginTable(
                "EurekaInstanceHunterZones",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            ImGui.TableSetupColumn("Baseline", ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            ImGui.TableHeadersRow();

            foreach (var zone in EurekaZoneOptions)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(EurekaInstanceIdService.GetZoneLabel(zone));

                ImGui.TableSetColumnIndex(1);
                var enabledZone = GetEurekaInstanceIdZoneEnabled(zone);
                if (ImGui.Checkbox($"##EurekaInstanceHunterEnabled{zone}", ref enabledZone))
                {
                    SetEurekaInstanceIdZoneEnabled(zone, enabledZone);
                    ApplyEurekaInstanceIdConfiguration();
                    configuration.Save();
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.SetNextItemWidth(Scale(90f));
                var baselineInstanceId = GetEurekaInstanceIdZoneBaseline(zone);
                if (ImGui.InputInt($"##EurekaInstanceHunterBaseline{zone}", ref baselineInstanceId))
                {
                    SetEurekaInstanceIdZoneBaseline(zone, baselineInstanceId);
                    ApplyEurekaInstanceIdConfiguration();
                    configuration.Save();
                }
            }

            ImGui.EndTable();
        }

        if (ImGui.Button("Use Current Zone##EurekaInstanceHunter"))
        {
            if (plugin.EurekaInstanceId.TryUseCurrentInstance(out var currentZone, out var currentInstanceId, out var message))
            {
                configuration.EurekaInstanceIdZone = (int)currentZone;
                configuration.EurekaInstanceIdBaselineInstanceId = currentInstanceId;
                SetEurekaInstanceIdZoneBaseline(currentZone, currentInstanceId);
                ApplyEurekaInstanceIdConfiguration();
                configuration.Save();
                Plugin.ChatGui.Print($"[XASlave] {EurekaInstanceIdService.GetZoneLabel(currentZone)} Instance: {currentInstanceId}");
                SetEurekaInstanceHunterStatus(message);
            }
            else
            {
                SetEurekaInstanceHunterStatus(message, true);
            }
        }

        ImGui.SameLine();
        var startColor = GetEurekaHunterStartButtonColor(isScanning);
        ImGui.PushStyleColor(ImGuiCol.Button, startColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BrightenButtonColor(startColor, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, BrightenButtonColor(startColor, 0.16f));
        if (ImGui.Button("Start##EurekaInstanceHunter"))
        {
            if (isScanning)
            {
                SetEurekaInstanceHunterStatus("Eureka instance scanning is already running.");
            }
            else
            {
                ApplyEurekaInstanceIdConfiguration();
                if (!configuration.EurekaInstanceIdEnabled)
                {
                    configuration.EurekaInstanceIdEnabled = plugin.EurekaInstanceId.SetEnabled(true);
                    configuration.Save();
                }

                if (GetEnabledEurekaInstanceIdZoneCount() <= 0)
                {
                    SetEurekaInstanceHunterStatus("Enable at least one Eureka zone row before starting the scanner.", true);
                }
                else
                {
                    var applied = plugin.EurekaInstanceId.StartScanning();
                    SetEurekaInstanceHunterStatus(
                        applied
                            ? $"Started Eureka instance scanning across {BuildEnabledEurekaInstanceIdZoneSummary()}."
                            : plugin.EurekaInstanceId.StatusText,
                        !applied);
                }
            }
        }

        ImGui.PopStyleColor(3);
        ImGui.SameLine();
        var stopColor = GetEurekaHunterStopButtonColor();
        ImGui.PushStyleColor(ImGuiCol.Button, stopColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BrightenButtonColor(stopColor, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, BrightenButtonColor(stopColor, 0.16f));
        if (ImGui.Button("Stop##EurekaInstanceHunter"))
        {
            plugin.EurekaInstanceId.StopScanning();
            SetEurekaInstanceHunterStatus(
                configuration.EurekaInstanceIdEnabled
                    ? "Stopped Eureka instance scanning. Live instance display remains enabled."
                    : "Stopped Eureka instance scanning.");
        }

        ImGui.PopStyleColor(3);

        ImGui.Spacing();
        DrawEurekaInstanceIdSharedDisplayOptions("EurekaInstanceHunter");

        var leaveDutyDelaySeconds = configuration.EurekaInstanceIdLeaveDutyDelaySeconds;
        if (ImGui.SliderInt(
                "Leave duty delay##EurekaInstanceHunter",
                ref leaveDutyDelaySeconds,
                EurekaInstanceIdService.LeaveDutyDelaySecondsMinimum,
                EurekaInstanceIdService.LeaveDutyDelaySecondsMaximum,
                "%d sec"))
        {
            configuration.EurekaInstanceIdLeaveDutyDelaySeconds = leaveDutyDelaySeconds;
            ApplyEurekaInstanceIdConfiguration();
            configuration.Save();
        }

        var playSound = configuration.EurekaInstanceIdPlaySound;
        if (ImGui.Checkbox("Beep on new instance##EurekaInstanceHunter", ref playSound))
        {
            configuration.EurekaInstanceIdPlaySound = playSound;
            ApplyEurekaInstanceIdConfiguration();
            configuration.Save();
        }

        ImGui.BeginDisabled(!configuration.EurekaInstanceIdPlaySound);
        var soundEffectId = configuration.EurekaInstanceIdSoundEffectId;
        if (ImGui.BeginCombo("Sound##EurekaInstanceHunter", XAPeepData.GetSoundEffectLabel(soundEffectId)))
        {
            for (var id = 0; id <= XAPeepData.MaxSoundEffectId; id++)
            {
                var isSelected = soundEffectId == id;
                if (ImGui.Selectable(XAPeepData.GetSoundEffectLabel(id), isSelected))
                {
                    configuration.EurekaInstanceIdSoundEffectId = id;
                    ApplyEurekaInstanceIdConfiguration();
                    configuration.Save();
                    if (id > 0)
                        PlayEurekaInstanceHunterSoundPreview();

                    soundEffectId = id;
                }

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var soundVolumePercent = configuration.EurekaInstanceIdSoundVolume * 100f;
        if (ImGui.SliderFloat("Alert volume##EurekaInstanceHunter", ref soundVolumePercent, 0f, 100f, "%.0f%%"))
        {
            configuration.EurekaInstanceIdSoundVolume = soundVolumePercent / 100f;
            ApplyEurekaInstanceIdConfiguration();
            configuration.Save();
        }

        if (ImGui.IsItemDeactivatedAfterEdit() && XAPeepData.ClampSoundEffectId(configuration.EurekaInstanceIdSoundEffectId) > 0)
            PlayEurekaInstanceHunterSoundPreview();

        ImGui.EndDisabled();

        ImGui.Spacing();
        if (ImGui.Button(eurekaHunterHowToUseExpanded ? "How to use [-]##EurekaInstanceHunterHowToUse" : "How to use [+]##EurekaInstanceHunterHowToUse"))
            eurekaHunterHowToUseExpanded = !eurekaHunterHowToUseExpanded;

        if (eurekaHunterHowToUseExpanded)
        {
            ImGui.TextWrapped("The Instance ID toggle is shared with XA Mods and only controls the live Eureka ID for the zone in the server bar DTR entry.");
            ImGui.TextWrapped("Start and Stop buttons control the instance farming loop. When Slave finds a new instance, it updates that zone's baseline to the new ID before stopping.");
            ImGui.TextWrapped("You can edit any row baseline manually or use Use Current Zone to overwrite the currently loaded Eureka row with the live instance.");
            ImGui.TextWrapped("If an enabled row baseline is 0, Slave stores the first observed instance for that zone and keeps scanning instead of treating that first read as a hit.");
            ImGui.TextWrapped("Scan order always follows the enabled rows in order: Anemos -> Pagos -> Pyros -> Hydatos. After each duplicate or baseline capture Slave leaves duty, wait for Kugane to load, then talks to Rodney again.");
            ImGui.TextWrapped("You can select to farm a single or multiple zones.");
        }
    }
}
