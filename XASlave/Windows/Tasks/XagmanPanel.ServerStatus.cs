using System;
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
            && plugin.Configuration.XagmanServerMatchingEnabled && !plugin.Configuration.XagmanOutsideNetworkHelper;

    private string GetXagmanServerStatusSetupKey()
        => JsonSerializer.Serialize(plugin.Configuration.XagmanServerMeetWorlds.OrderBy(pair => pair.Key).ToArray());

    private void DrawXagmanServerStatus()
    {
        using (ImRaii.Disabled(xagmanServerStatusPending || !CanApplyXagmanServerStatus()))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S93D63FA3A4D5ABA9") + "##xagmanServerStatus")))
                BeginXagmanServerStatusRequest();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S5BE5C0ECB9214380"));
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
                    var previousWorlds = cfg.XagmanServerMeetWorlds;
                    cfg.XagmanServerMeetWorlds = AethertekServerStatus.CompleteSelections(result.Worlds, previousWorlds);
                    try
                    {
                        cfg.Save();
                    }
                    catch
                    {
                        cfg.XagmanServerMeetWorlds = previousWorlds;
                        throw;
                    }
                    StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("SA63D8F44280EEA54"));
                    InvalidateXagmanTradeCapacityForecast();
                    xagmanServerStatusWarning = result.UnavailableDataCenters.Count > 0;
                    xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Format("S4A5338EC8808717F", cfg.XagmanServerMeetWorlds.Count, result.Worlds.Count, result.CheckedAt.ToLocalTime()));
                    if (result.UnavailableDataCenters.Count > 0)
                        xagmanServerStatusMessage = global::XASlave.Localization.UiMessage.Store(ref xagmanServerStatusMessageUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Capture(ref xagmanServerStatusMessageUiMessage, xagmanServerStatusMessage), global::XASlave.Localization.UiMessage.Format("SEA155AC43E3DF353", string.Join(", ", result.UnavailableDataCenters.Select(dc => $"{dc}: {cfg.XagmanServerMeetWorlds[dc]}")))));
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
