using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// Save to XA Database panel - partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // Auto-collection state
    private DateTime? autoCollectScheduledAt;
    private float autoCollectScheduledDelaySeconds;
    private bool autoCollectSkipPending;
    private string autoCollectSkipMessage = string.Empty;
    private bool autoCollectResumeArOnCompletion;
    private string lastIpcResult = string.Empty;
    private DateTime lastIpcResultExpiry = DateTime.MinValue;
    private bool saveToXaDatabaseShowLog;
    private DateTime saveToXaDatabaseStatusExpiry = DateTime.MinValue;
    private bool cachedSaveToXaDatabaseReady;
    private string cachedSaveToXaDatabaseVersion = string.Empty;
    private DateTime? cachedCurrentCharacterLastSyncUtc;
    private int saveToXaDatabaseStatusRefreshRunning;

    // -----------------------------------------------
    //  Task: Save to XA Database
    // -----------------------------------------------
    private void DrawSaveToXaDatabaseTask()
    {
        RefreshSaveToXaDatabaseStatusCache();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Save to XA Database");
        ImGui.TextDisabled("Collects data from game windows and saves to XA Database via IPC.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Connection status --
        if (cachedSaveToXaDatabaseReady)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"XA Database: Connected (v{cachedSaveToXaDatabaseVersion})");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "XA Database: Not available");
            ImGui.TextDisabled("Make sure XA Database plugin is loaded.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Manual actions --
        ImGui.Text("Manual Actions");
        ImGui.Spacing();

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Collecting: {plugin.AutoCollector.StatusText}");
            if (ImGui.Button("Cancel"))
                plugin.AutoCollector.Cancel();
        }
        else
        {
            if (ImGui.Button("Collect Now"))
            {
                AutoOpenTaskLogIfVerbose(ref saveToXaDatabaseShowLog);
                RunAutoCollection();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs the checked Collection Options below, then saves to XA Database via IPC.");

            ImGui.SameLine();
            if (ImGui.Button("IPC Save"))
            {
                AutoOpenTaskLogIfVerbose(ref saveToXaDatabaseShowLog);
                plugin.AutoCollector.AddLog("Manual IPC save requested.");
                if (plugin.SaveToXaDatabaseAndRecordSync())
                    SetIpcResult("Save sent to XA Database");
                else
                    SetIpcResult("Save failed - XA Database not available");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Send a save command to XA Database without opening any game windows.");
        }

        // IPC result feedback
        if (!string.IsNullOrEmpty(lastIpcResult) && DateTime.UtcNow < lastIpcResultExpiry)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), lastIpcResult);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Collection Options");
        ImGui.Spacing();

        var collectArmoury = plugin.Configuration.AutoCollectArmouryChest;
        if (ImGui.Checkbox("Collect Armoury Chest", ref collectArmoury))
        {
            plugin.Configuration.AutoCollectArmouryChest = collectArmoury;
            plugin.Configuration.Save();
        }

        var collectSaddlebag = plugin.Configuration.AutoCollectSaddlebag;
        if (ImGui.Checkbox("Collect Saddlebag", ref collectSaddlebag))
        {
            plugin.Configuration.AutoCollectSaddlebag = collectSaddlebag;
            plugin.Configuration.Save();
        }

        var collectJournal = plugin.Configuration.AutoCollectJournal;
        if (ImGui.Checkbox("Collect Journal", ref collectJournal))
        {
            plugin.Configuration.AutoCollectJournal = collectJournal;
            plugin.Configuration.Save();
        }

        var collectPlot = plugin.Configuration.AutoCollectPersonalPlotInfo;
        if (ImGui.Checkbox("Collect Personal Plot Info", ref collectPlot))
        {
            plugin.Configuration.AutoCollectPersonalPlotInfo = collectPlot;
            plugin.Configuration.Save();
        }

        var collectFc = plugin.Configuration.AutoCollectFc;
        if (ImGui.Checkbox("Collect FC Data (Members, Info, Housing)", ref collectFc))
        {
            plugin.Configuration.AutoCollectFc = collectFc;
            plugin.Configuration.Save();
        }

        ImGui.TextDisabled("Collect Now and Auto-Collection on Login both use these options.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (Plugin.PlayerState.IsLoaded)
        {
            ImGui.TextDisabled(cachedCurrentCharacterLastSyncUtc.HasValue
                ? $"Last synced to XA DB: {cachedCurrentCharacterLastSyncUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "Last synced to XA DB: Never");
            ImGui.Spacing();
        }

        // -- Auto-Collection on Login settings --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Auto-Collection on Login");
        ImGui.Spacing();

        var autoCollect = plugin.Configuration.AutoCollectOnLogin;
        if (ImGui.Checkbox("Enable Auto-Collection on Login", ref autoCollect))
        {
            plugin.Configuration.AutoCollectOnLogin = autoCollect;
            plugin.Configuration.Save();
        }
        ImGui.TextDisabled("Automatically runs the selected Collection Options after login.");

        if (autoCollect)
        {
            ImGui.Spacing();
            var disableOnArMulti = plugin.Configuration.AutoCollectDisableWhenArMultiEnabled;
            if (ImGui.Checkbox("  Disable if AR Multi is enabled", ref disableOnArMulti))
            {
                plugin.Configuration.AutoCollectDisableWhenArMultiEnabled = disableOnArMulti;
                plugin.Configuration.Save();
            }

            var cadenceIndex = GetCheckEveryIndex(plugin.Configuration.AutoCollectCheckEveryHours);
            ImGui.SetNextItemWidth(Scale(180f));
            if (ImGui.SliderInt("Check Every##autoCollectEvery", ref cadenceIndex, 0, CheckEveryHourOptions.Length - 1,
                    FormatCheckEveryHours(CheckEveryHourOptions[cadenceIndex]), ImGuiSliderFlags.AlwaysClamp))
            {
                plugin.Configuration.AutoCollectCheckEveryHours = CheckEveryHourOptions[cadenceIndex];
                plugin.Configuration.SaveDeferred();
            }

            ImGui.Spacing();
            var delay = plugin.Configuration.AutoCollectDelaySeconds;
            ImGui.SetNextItemWidth(Scale(100f));
            if (ImGui.InputFloat("Login Delay (seconds)", ref delay, 1f, 5f, "%.0f"))
            {
                if (delay < 3f) delay = 3f;
                if (delay > 30f) delay = 30f;
                plugin.Configuration.AutoCollectDelaySeconds = delay;
                plugin.Configuration.SaveDeferred();
            }
            ImGui.TextDisabled("Wait time after login before starting collection.");
        }

        // Scheduled status
        if (autoCollectScheduledAt.HasValue)
        {
            var remaining = autoCollectScheduledDelaySeconds - (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
            if (remaining > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Auto-collection starting in ~{remaining:F0}s...");
            }
        }

        DrawTaskLog("saveToXaDatabase", ref saveToXaDatabaseShowLog, plugin.AutoCollector);
    }

    private void RefreshSaveToXaDatabaseStatusCache()
    {
        if (DateTime.UtcNow < saveToXaDatabaseStatusExpiry)
            return;
        if (System.Threading.Interlocked.CompareExchange(ref saveToXaDatabaseStatusRefreshRunning, 1, 0) != 0)
            return;

        saveToXaDatabaseStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        var contentId = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
        if (!RunWorker("XA Database status refresh", async token =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var (ready, version) = await Plugin.RunOnGameThread(() =>
                {
                    var isReady = plugin.IpcClient.IsReady();
                    return (isReady, isReady ? plugin.IpcClient.GetVersion() : string.Empty);
                });
                var lastSyncUtc = contentId == 0
                    ? null
                    : plugin.SlaveDatabase.GetLastSyncedToXaDbUtc(contentId);
                await Plugin.RunOnGameThread(() =>
                {
                    cachedSaveToXaDatabaseReady = ready;
                    cachedSaveToXaDatabaseVersion = version;
                    cachedCurrentCharacterLastSyncUtc = lastSyncUtc;
                });
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref saveToXaDatabaseStatusRefreshRunning, 0);
            }
        }))
        {
            System.Threading.Interlocked.Exchange(ref saveToXaDatabaseStatusRefreshRunning, 0);
        }
    }

    // -----------------------------------------------
    //  Helpers
    // -----------------------------------------------
    private void RunAutoCollection(bool resumeArOnCompletion = false)
    {
        if (plugin.AutoCollector.IsRunning) return;

        var saveAttempted = false;
        var lastSaveOk = false;
        AutoOpenTaskLogIfVerbose(ref saveToXaDatabaseShowLog);
        plugin.AutoCollector.StartCollection(
            plugin.Configuration.AutoCollectArmouryChest,
            plugin.Configuration.AutoCollectSaddlebag,
            plugin.Configuration.AutoCollectJournal,
            plugin.Configuration.AutoCollectPersonalPlotInfo,
            plugin.Configuration.AutoCollectFc,
            () =>
            {
                saveAttempted = true;
                plugin.AutoCollector.AddLog("Sending save to XA Database...");
                lastSaveOk = plugin.SaveToXaDatabaseAndRecordSync();
                plugin.AutoCollector.AddLog(lastSaveOk
                    ? "XA Database save reported success."
                    : "XA Database save reported failure.");
            },
            completed =>
            {
                if (resumeArOnCompletion)
                    plugin.IpcClient.AutoRetainerSetSuppressed(false);

                if (!completed)
                {
                    SetIpcResult(resumeArOnCompletion ? "Collection cancelled - AR resumed." : "Collection cancelled.");
                    return;
                }

                if (!saveAttempted)
                {
                    SetIpcResult("Collection complete.");
                    return;
                }

                SetIpcResult(lastSaveOk
                    ? "Collection complete - saved to XA Database"
                    : "Collection complete - XA Database save failed");
            });

        if (resumeArOnCompletion)
            plugin.AutoCollector.AddLog("AutoRetainer will be resumed after collection.");
    }

    private void SetIpcResult(string message)
    {
        lastIpcResult = message;
        lastIpcResultExpiry = DateTime.UtcNow.AddSeconds(8);
        plugin.AutoCollector.AddLog(message);
    }
}
