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
    private global::XASlave.Localization.UiMessage? eurekaHunterStatusUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
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

    private global::XASlave.Localization.UiMessage BuildEnabledEurekaInstanceIdZoneSummary()
    {
        var enabledZones = new System.Collections.Generic.List<string>();
        foreach (var zone in EurekaZoneOptions)
        {
            if (GetEurekaInstanceIdZoneEnabled(zone))
                enabledZones.Add(EurekaInstanceIdService.GetZoneLabel(zone));
        }

        return enabledZones.Count == 0
            ? global::XASlave.Localization.UiMessage.Key("S59DB20B35F330517")
            : global::XASlave.Localization.UiMessage.Raw(string.Join(", ", enabledZones));
    }

    private void DrawEurekaInstanceIdSharedDisplayOptions(string idSuffix)
    {
        var configuration = plugin.Configuration;
        var fieldEnabled = configuration.FieldOperationsInstanceDisplayEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SDC6048FDB55CF1EA") + $"##{idSuffix}"), ref fieldEnabled))
        {
            configuration.FieldOperationsInstanceDisplayEnabled = fieldEnabled;
            plugin.FieldOperationsInstanceDisplay.ApplyConfiguration(); configuration.SaveDeferred();
        }
        var fieldDtr = configuration.FieldOperationsInstanceDisplayShowInDtr;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S56136AAE41846A10") + $"##{idSuffix}"), ref fieldDtr))
        {
            configuration.FieldOperationsInstanceDisplayShowInDtr = fieldDtr;
            plugin.FieldOperationsInstanceDisplay.ApplyConfiguration(); configuration.SaveDeferred();
        }
        var fieldChat = configuration.FieldOperationsInstanceDisplayChat;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S5F0272767E255D6B") + $"##{idSuffix}"), ref fieldChat))
        {
            configuration.FieldOperationsInstanceDisplayChat = fieldChat;
            plugin.FieldOperationsInstanceDisplay.ApplyConfiguration(); configuration.SaveDeferred();
        }
        ImGui.TextWrapped(plugin.FieldOperationsInstanceDisplay.LocalizedStatusText);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1D2EA963CDECE235"));
        var showInDtr = configuration.EurekaInstanceIdShowInDtr;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S262A4A0D72C008E6") + $"##{idSuffix}"), ref showInDtr))
        {
            configuration.EurekaInstanceIdShowInDtr = showInDtr;
            ApplyEurekaInstanceIdConfiguration();
            configuration.SaveDeferred();
        }
    }

    private void SetEurekaInstanceHunterStatus(global::XASlave.Localization.UiMessage messagePresentation, bool isError = false)
    {
        var message = messagePresentation.Original;
        eurekaHunterStatus = global::XASlave.Localization.UiMessage.Store(ref eurekaHunterStatusUiMessage, messagePresentation);
        eurekaHunterStatusIsError = isError;
        eurekaHunterStatusExpiryUtc = DateTime.UtcNow.AddSeconds(8);
    }

    private void PlayEurekaInstanceHunterSoundPreview()
        => Plugin.ScheduleOnGameThread(PlayEurekaInstanceHunterSoundPreviewOnFramework);

    private unsafe void PlayEurekaInstanceHunterSoundPreviewOnFramework()
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

        ImGui.TextColored(new Vector4(0.82f, 1.0f, 0.55f, 1.0f), global::XASlave.Localization.UiLocale.Get("S5578C356136F70AA"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S570B57F704C4A056"));
        ImGui.Spacing();

        var enabled = configuration.EurekaInstanceIdEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S0DC49FB52EF24934") + "##EurekaInstanceHunterEnabled"), ref enabled))
        {
            configuration.EurekaInstanceIdEnabled = plugin.EurekaInstanceId.SetEnabled(enabled);
            configuration.SaveDeferred();
            SetEurekaInstanceHunterStatus(
                (configuration.EurekaInstanceIdEnabled ? global::XASlave.Localization.UiMessage.Key("SE8D21CA573B146E2") : global::XASlave.Localization.UiMessage.Key("SD9C9FDB4345A538C")));
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
        ImGui.TextColored(stateColor, global::XASlave.Localization.UiLocale.FormatKey("S390591EA8DC1971A", stateLabel));

        using (ImRaii.TextWrapPos(0f))
        {
            ImGui.TextUnformatted(plugin.EurekaInstanceId.LocalizedStatusText);
        }

        if (!string.IsNullOrEmpty(eurekaHunterStatus) && DateTime.UtcNow < eurekaHunterStatusExpiryUtc)
            ImGui.TextColored(eurekaHunterStatusIsError ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f) : new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref eurekaHunterStatusUiMessage, eurekaHunterStatus));

        ImGui.Spacing();
        using (var imguiScope215 = ImRaii.Table(
                "EurekaInstanceHunterZones",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        if (imguiScope215)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SA8A06E4A5695553C"), ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SC36D819E7BC6D2B7"), ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCC9EA7F7C64E772C"), ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

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
                    configuration.SaveDeferred();
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.SetNextItemWidth(Scale(90f));
                var baselineInstanceId = GetEurekaInstanceIdZoneBaseline(zone);
                if (ImGui.InputInt($"##EurekaInstanceHunterBaseline{zone}", ref baselineInstanceId))
                {
                    SetEurekaInstanceIdZoneBaseline(zone, baselineInstanceId);
                    ApplyEurekaInstanceIdConfiguration();
                    configuration.SaveDeferred();
                }
            }


        }

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3604E68D3953A4C5") + "##EurekaInstanceHunter")))
        {
            if (plugin.EurekaInstanceId.RequestUseCurrentInstanceMessage((success, currentZone, currentInstanceId, resultMessage) =>
            {
                if (!success)
                {
                    SetEurekaInstanceHunterStatus(resultMessage, true);
                    return;
                }

                configuration.EurekaInstanceIdZone = (int)currentZone;
                configuration.EurekaInstanceIdBaselineInstanceId = currentInstanceId;
                SetEurekaInstanceIdZoneBaseline(currentZone, currentInstanceId);
                ApplyEurekaInstanceIdConfiguration();
                configuration.SaveDeferred();
                Plugin.ChatGui.Print($"[XASlave] {EurekaInstanceIdService.GetZoneLabel(currentZone)} Instance: {currentInstanceId}");
                SetEurekaInstanceHunterStatus(resultMessage);
            }, out var requestMessage))
            {
                SetEurekaInstanceHunterStatus(requestMessage);
            }
            else
                SetEurekaInstanceHunterStatus(requestMessage, true);
        }

        ImGui.SameLine();
        var startColor = GetEurekaHunterStartButtonColor(isScanning);
        using (ImRaii.PushColor(ImGuiCol.Button, startColor))
        {
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, BrightenButtonColor(startColor, 0.08f)))
            {
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, BrightenButtonColor(startColor, 0.16f)))
                {
                    if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SE4BB9F1ECE9AF926") + "##EurekaInstanceHunter")))
                    {
                        if (isScanning)
                        {
                            SetEurekaInstanceHunterStatus(global::XASlave.Localization.UiMessage.Key("S93F7881C1498AA1B"));
                        }
                        else
                        {
                            ApplyEurekaInstanceIdConfiguration();
                            if (!configuration.EurekaInstanceIdEnabled)
                            {
                                configuration.EurekaInstanceIdEnabled = plugin.EurekaInstanceId.SetEnabled(true);
                                configuration.SaveDeferred();
                            }

                            if (GetEnabledEurekaInstanceIdZoneCount() <= 0)
                            {
                                SetEurekaInstanceHunterStatus(global::XASlave.Localization.UiMessage.Key("S98957C786565E712"), true);
                            }
                            else
                            {
                                var applied = plugin.EurekaInstanceId.StartScanning();
                                SetEurekaInstanceHunterStatus(
                                    (applied ? global::XASlave.Localization.UiMessage.Format("SBEF0165C4DD69269", BuildEnabledEurekaInstanceIdZoneSummary()) : plugin.EurekaInstanceId.StatusTextMessage),
                                    !applied);
                            }
                        }
                    }

                }
            }
        }
        ImGui.SameLine();
        var stopColor = GetEurekaHunterStopButtonColor();
        using (ImRaii.PushColor(ImGuiCol.Button, stopColor))
        {
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, BrightenButtonColor(stopColor, 0.08f)))
            {
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, BrightenButtonColor(stopColor, 0.16f)))
                {
                    if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SCAE7D57BC067A514") + "##EurekaInstanceHunter")))
                    {
                        plugin.EurekaInstanceId.StopScanning();
                        SetEurekaInstanceHunterStatus(
                            (configuration.EurekaInstanceIdEnabled ? global::XASlave.Localization.UiMessage.Key("S9DDDFD8314AFF7A6") : global::XASlave.Localization.UiMessage.Key("S45A982666E032799")));
                    }

                }
            }
        }

        ImGui.Spacing();
        DrawEurekaInstanceIdSharedDisplayOptions("EurekaInstanceHunter");

        var leaveDutyDelaySeconds = configuration.EurekaInstanceIdLeaveDutyDelaySeconds;
        if (global::XASlave.Localization.UiNative.SliderInt(
                (global::XASlave.Localization.UiLocale.Native("SEEB77B78CFD41517") + "##EurekaInstanceHunter"),
                ref leaveDutyDelaySeconds,
                EurekaInstanceIdService.LeaveDutyDelaySecondsMinimum,
                EurekaInstanceIdService.LeaveDutyDelaySecondsMaximum,
                "%d sec",
                ImGuiSliderFlags.AlwaysClamp))
        {
            configuration.EurekaInstanceIdLeaveDutyDelaySeconds = leaveDutyDelaySeconds;
            ApplyEurekaInstanceIdConfiguration();
            configuration.SaveDeferred();
        }

        var playSound = configuration.EurekaInstanceIdPlaySound;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S87CF7C0A97033581") + "##EurekaInstanceHunter"), ref playSound))
        {
            configuration.EurekaInstanceIdPlaySound = playSound;
            ApplyEurekaInstanceIdConfiguration();
            configuration.SaveDeferred();
        }

        using (ImRaii.Disabled(!configuration.EurekaInstanceIdPlaySound))
        {
            var soundEffectId = configuration.EurekaInstanceIdSoundEffectId;
            using (var imguiScope360 = global::XASlave.Localization.UiNative.ComboScope((global::XASlave.Localization.UiLocale.Native("S46124E023603420F") + "##EurekaInstanceHunter"), XAPeepData.GetSoundEffectLabelMessage(soundEffectId).Display))
            if (imguiScope360)
            {
                for (var id = 0; id <= XAPeepData.MaxSoundEffectId; id++)
                {
                    var isSelected = soundEffectId == id;
                    if (global::XASlave.Localization.UiNative.Selectable(XAPeepData.GetSoundEffectLabel(id), isSelected, display: XAPeepData.GetSoundEffectLabelMessage(id).Display))
                    {
                        configuration.EurekaInstanceIdSoundEffectId = id;
                        ApplyEurekaInstanceIdConfiguration();
                        configuration.SaveDeferred();
                        if (id > 0)
                            PlayEurekaInstanceHunterSoundPreview();

                        soundEffectId = id;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }


            }

            var soundVolumePercent = configuration.EurekaInstanceIdSoundVolume * 100f;
            if (global::XASlave.Localization.UiNative.SliderFloat((global::XASlave.Localization.UiLocale.Native("S1525C9B4ECFB8C7F") + "##EurekaInstanceHunter"), ref soundVolumePercent, 0f, 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.EurekaInstanceIdSoundVolume = soundVolumePercent / 100f;
                ApplyEurekaInstanceIdConfiguration();
                configuration.SaveDeferred();
            }

            if (ImGui.IsItemDeactivatedAfterEdit() && XAPeepData.ClampSoundEffectId(configuration.EurekaInstanceIdSoundEffectId) > 0)
                PlayEurekaInstanceHunterSoundPreview();

        }

        ImGui.Spacing();
        if (global::XASlave.Localization.UiNative.Button(eurekaHunterHowToUseExpanded ? (global::XASlave.Localization.UiLocale.Native("S6A4DC3ABD4C219BA") + "##EurekaInstanceHunterHowToUse") : (global::XASlave.Localization.UiLocale.Native("S91F1A47CACCC47B8") + "##EurekaInstanceHunterHowToUse")))
            eurekaHunterHowToUseExpanded = !eurekaHunterHowToUseExpanded;

        if (eurekaHunterHowToUseExpanded)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S63FD6B945E54C926"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SA39FA2335C01B6B3"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF85C4EE810AE852F"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S0049AE60B5E6BBBA"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S8C24AD117521F9BA"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S7F2B17797E4DE7D1"));
        }
    }
}
