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
            if (ImGui.Button("Ping Aethertek Server Status##xagmanServerStatus"))
                BeginXagmanServerStatusRequest();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Fetch current Aethertek world status and select the first eligible world alphabetically in each data center: online, not congested, and open to character creation. If none qualify, keep the current valid world or use the first world alphabetically as a marked fallback. Every data center stays configured. Available while idle; history is ignored.");
        if (!string.IsNullOrWhiteSpace(xagmanServerStatusMessage))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, xagmanServerStatusWarning
                ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.4f, 0.8f, 1f, 1f));
            ImGui.TextWrapped(xagmanServerStatusMessage);
        }
    }

    private void BeginXagmanServerStatusRequest()
    {
        if (xagmanServerStatusPending || !CanApplyXagmanServerStatus()) return;
        var setupKey = GetXagmanServerStatusSetupKey();
        xagmanServerStatusPending = true;
        xagmanServerStatusWarning = false;
        xagmanServerStatusMessage = "Checking Aethertek server status...";
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
                        xagmanServerStatusMessage = "Run or world selections changed during the check. Ping again while idle to apply fresh choices.";
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
                    StopXagmanTonyAutoSelection("Auto: meet worlds changed. Run Auto again to check coverage.");
                    InvalidateXagmanTradeCapacityForecast();
                    xagmanServerStatusWarning = result.UnavailableDataCenters.Count > 0;
                    xagmanServerStatusMessage = $"Configured {cfg.XagmanServerMeetWorlds.Count} data centers; {result.Worlds.Count} meet the status filters. Status checked {result.CheckedAt.ToLocalTime():HH:mm:ss}.";
                    if (result.UnavailableDataCenters.Count > 0)
                        xagmanServerStatusMessage += $" No eligible world; fallback selections: {string.Join(", ", result.UnavailableDataCenters.Select(dc => $"{dc}: {cfg.XagmanServerMeetWorlds[dc]}"))}.";
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
                    xagmanServerStatusMessage = $"Server status check failed; existing selections kept. {ex.Message}";
                    plugin.TaskRunner.AddLog($"Xagman: {xagmanServerStatusMessage}");
                }).ConfigureAwait(false);
            }
        }))
        {
            xagmanServerStatusPending = false;
            xagmanServerStatusWarning = true;
            xagmanServerStatusMessage = "Could not start the server status check. Existing selections kept.";
        }
    }
}
