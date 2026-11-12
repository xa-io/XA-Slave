using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private bool xagmanServerStatusPending;
    private string xagmanServerStatusMessage = string.Empty;
    private global::XASlave.Localization.UiMessage? xagmanServerStatusMessageUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private bool xagmanServerStatusWarning;

    private bool CanApplyXagmanServerStatus()
        => !isDisposed && !xagmanRunning && !plugin.TaskRunner.IsRunning && !xagmanTradeSafetySessionActive
            && plugin.Configuration.XagmanRole == XagmanRole.Tony
            && plugin.Configuration.XagmanServerMatchingEnabled;

    private string GetXagmanServerStatusSetupKey()
        => JsonSerializer.Serialize((plugin.Configuration.XagmanRegionMeetWorlds ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key).ToArray());

    private void DrawXagmanServerStatus()
    {
        using (ImRaii.Disabled(xagmanServerStatusPending || !CanApplyXagmanServerStatus()))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S93D63FA3A4D5ABA9") + "##xagmanServerStatus")))
                BeginXagmanServerStatusRequest();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get(global::XASlave.Localization.UiLocale.Key("Select one available meeting world per region from current server status. Review the choices before starting; ONH partners must use the same worlds.")));
        if (!string.IsNullOrWhiteSpace(xagmanServerStatusMessage))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, xagmanServerStatusWarning
                ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.4f, 0.8f, 1f, 1f));
            ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref xagmanServerStatusMessageUiMessage, xagmanServerStatusMessage));
        }
    }

    private void BeginXagmanServerStatusRequest()
    {
        if (xagmanServerStatusPending || !CanApplyXagmanServerStatus()) return;
        var setupKey = GetXagmanServerStatusSetupKey();
        xagmanServerStatusPending = true;
        xagmanServerStatusWarning = false;
        xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Key("SB65CDB4C841A9FAD"));
        if (!RunWorker("Xagman Aethertek server status", async cancellationToken =>
        {
            try
            {
                var result = await AethertekServerStatus.FetchAsync(cancellationToken).ConfigureAwait(false);
                await Plugin.RunOnGameThread(() =>
                {
                    if (isDisposed || cancellationToken.IsCancellationRequested) return;
                    xagmanServerStatusPending = false;
                    if (!CanApplyXagmanServerStatus() || GetXagmanServerStatusSetupKey() != setupKey)
                    {
                        xagmanServerStatusWarning = true;
                        xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Key("S18C83A541EDBFFB6"));
                        return;
                    }
                    var cfg = plugin.Configuration;
                    var eligible = AethertekServerStatus.SelectRegionalWorlds(result.Worlds);
                    var unavailable = WorldData.RegionOrder.Where(region => !eligible.ContainsKey(region)).ToList();
                    var previousWorlds = cfg.XagmanRegionMeetWorlds;
                    cfg.XagmanRegionMeetWorlds = AethertekServerStatus.CompleteRegionalSelections(eligible, previousWorlds);
                    try
                    {
                        cfg.Save();
                    }
                    catch
                    {
                        cfg.XagmanRegionMeetWorlds = previousWorlds;
                        throw;
                    }
                    StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("SA63D8F44280EEA54"));
                    InvalidateXagmanTradeCapacityForecast();
                    xagmanServerStatusWarning = unavailable.Count > 0;
                    xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Format(global::XASlave.Localization.UiLocale.Key("Selected meeting worlds for {0} regions; {1} have an available world. Status checked {2:g}."), cfg.XagmanRegionMeetWorlds.Count, eligible.Count, result.CheckedAt.ToLocalTime()));
                    if (unavailable.Count > 0)
                        xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref xagmanServerStatusMessageUiMessage, xagmanServerStatusMessage), global::XASlave.Localization.UiMessage.Format(global::XASlave.Localization.UiLocale.Key(" No available world was found for these regions; review the retained or fallback choices: {0}."), string.Join(", ", unavailable.Select(region => $"{region}: {cfg.XagmanRegionMeetWorlds[region]}")))));
                    plugin.TaskRunner.AddLog($"Xagman: Aethertek server status: {xagmanServerStatusMessage}");
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Window teardown owns cancellation; never publish after disposal.
            }
            catch (Exception ex)
            {
                await Plugin.RunOnGameThread(() =>
                {
                    if (isDisposed || cancellationToken.IsCancellationRequested) return;
                    xagmanServerStatusPending = false;
                    xagmanServerStatusWarning = true;
                    xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Format("S87EA370F2252E86E", ex.Message));
                    plugin.TaskRunner.AddLog($"Xagman: {xagmanServerStatusMessage}");
                }).ConfigureAwait(false);
            }
        }))
        {
            xagmanServerStatusPending = false;
            xagmanServerStatusWarning = true;
            xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Key("SE4DC3BCBB1763DF0"));
        }
    }
}
