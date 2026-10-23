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
    private global::XASlave.Localization.UiMessage? autoCollectSkipMessageUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private bool autoCollectResumeArOnCompletion;
    private string lastIpcResult = string.Empty;
    private global::XASlave.Localization.UiMessage? lastIpcResultMessage;
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

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S0AED21970EED8844"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S613A081ED445687C"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Connection status --
        if (cachedSaveToXaDatabaseReady)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S75A5F65875FB62FB", cachedSaveToXaDatabaseVersion));
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SE01EBEC9914E6284"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S5E95DAD388A7F6A4"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Manual actions --
        ImGui.Text(global::XASlave.Localization.UiLocale.Get("S948121DE103E905A"));
        ImGui.Spacing();

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S2072F33472A7C562", plugin.AutoCollector.LocalizedStatusText));
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3")))
                plugin.AutoCollector.Cancel();
        }
        else
        {
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1BB88EF61843E357")))
            {
                AutoOpenTaskLogIfVerbose(ref saveToXaDatabaseShowLog);
                RunAutoCollection();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S1D8C1740DA1523A6"));

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4A46E0B800579CFE")))
            {
                AutoOpenTaskLogIfVerbose(ref saveToXaDatabaseShowLog);
                plugin.AutoCollector.AddLog("Manual IPC save requested.");
                if (plugin.SaveToXaDatabaseAndRecordSync())
                    SetIpcResult(global::XASlave.Localization.UiMessage.Key("SEECE95D745C5522F"));
                else
                    SetIpcResult(global::XASlave.Localization.UiMessage.Key("S314235448D911BDF"));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S42D2790AF452B523"));
        }

        // IPC result feedback
        if (!string.IsNullOrEmpty(lastIpcResult) && DateTime.UtcNow < lastIpcResultExpiry)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref lastIpcResultMessage, lastIpcResult));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SC15208046CEBA251"));
        ImGui.Spacing();

        var collectArmoury = plugin.Configuration.AutoCollectArmouryChest;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SA23D031A2A2F4E2D"), ref collectArmoury))
        {
            plugin.Configuration.AutoCollectArmouryChest = collectArmoury;
            plugin.Configuration.Save();
        }

        var collectSaddlebag = plugin.Configuration.AutoCollectSaddlebag;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S8FF45013F7B656B8"), ref collectSaddlebag))
        {
            plugin.Configuration.AutoCollectSaddlebag = collectSaddlebag;
            plugin.Configuration.Save();
        }

        var collectJournal = plugin.Configuration.AutoCollectJournal;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SDEB8BF5B1FFBF2D9"), ref collectJournal))
        {
            plugin.Configuration.AutoCollectJournal = collectJournal;
            plugin.Configuration.Save();
        }

        var collectPlot = plugin.Configuration.AutoCollectPersonalPlotInfo;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SFF3C7116AEE8239B"), ref collectPlot))
        {
            plugin.Configuration.AutoCollectPersonalPlotInfo = collectPlot;
            plugin.Configuration.Save();
        }

        var collectFc = plugin.Configuration.AutoCollectFc;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S71A8253EB3D38EC9"), ref collectFc))
        {
            plugin.Configuration.AutoCollectFc = collectFc;
            plugin.Configuration.Save();
        }

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S5D9DFDEFB941CD4C"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (Plugin.PlayerState.IsLoaded)
        {
            ImGui.TextDisabled(cachedCurrentCharacterLastSyncUtc.HasValue
                ? global::XASlave.Localization.UiLocale.FormatKey("SF9F3A8D3D3734D72", cachedCurrentCharacterLastSyncUtc.Value.ToLocalTime())
                : global::XASlave.Localization.UiLocale.Get("SB064842D1302A053"));
            ImGui.Spacing();
        }

        // -- Auto-Collection on Login settings --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SBA40CB42F6BC104A"));
        ImGui.Spacing();

        var autoCollect = plugin.Configuration.AutoCollectOnLogin;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SB85AC9455324B6C2"), ref autoCollect))
        {
            plugin.Configuration.AutoCollectOnLogin = autoCollect;
            plugin.Configuration.Save();
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S0AA484FD6530172D"));

        if (autoCollect)
        {
            ImGui.Spacing();
            var disableOnArMulti = plugin.Configuration.AutoCollectDisableWhenArMultiEnabled;
            if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SF472EE8410FC2EC8"), ref disableOnArMulti))
            {
                plugin.Configuration.AutoCollectDisableWhenArMultiEnabled = disableOnArMulti;
                plugin.Configuration.Save();
            }

            var cadenceIndex = GetCheckEveryIndex(plugin.Configuration.AutoCollectCheckEveryHours);
            ImGui.SetNextItemWidth(Scale(180f));
            if (global::XASlave.Localization.UiNative.SliderInt((global::XASlave.Localization.UiLocale.Native("S4415B9AB79ABEE43") + "##autoCollectEvery"), ref cadenceIndex, 0, CheckEveryHourOptions.Length - 1,
                    FormatCheckEveryHoursMessage(CheckEveryHourOptions[cadenceIndex]).Display, ImGuiSliderFlags.AlwaysClamp))
            {
                plugin.Configuration.AutoCollectCheckEveryHours = CheckEveryHourOptions[cadenceIndex];
                plugin.Configuration.SaveDeferred();
            }

            ImGui.Spacing();
            var delay = plugin.Configuration.AutoCollectDelaySeconds;
            ImGui.SetNextItemWidth(Scale(100f));
            if (global::XASlave.Localization.UiNative.InputFloat(global::XASlave.Localization.UiLocale.Native("S2B9474067408FB11"), ref delay, 1f, 5f, "%.0f"))
            {
                if (delay < 3f) delay = 3f;
                if (delay > 30f) delay = 30f;
                plugin.Configuration.AutoCollectDelaySeconds = delay;
                plugin.Configuration.SaveDeferred();
            }
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S5E64C33D1CAE5799"));
        }

        // Scheduled status
        if (autoCollectScheduledAt.HasValue)
        {
            var remaining = autoCollectScheduledDelaySeconds - (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
            if (remaining > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S9F20E331181A3226", remaining));
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
                    SetIpcResult((resumeArOnCompletion ? global::XASlave.Localization.UiMessage.Key("S5871C71604341E77") : global::XASlave.Localization.UiMessage.Key("S6C4571C7A55263C4")));
                    return;
                }

                if (!saveAttempted)
                {
                    SetIpcResult(global::XASlave.Localization.UiMessage.Key("SA44D4B202013DB1A"));
                    return;
                }

                SetIpcResult((lastSaveOk ? global::XASlave.Localization.UiMessage.Key("SA68889D2AF82E8F9") : global::XASlave.Localization.UiMessage.Key("S2006BA47A86E533F")));
            });

        if (resumeArOnCompletion)
            plugin.AutoCollector.AddLog("AutoRetainer will be resumed after collection.");
    }

    private void SetIpcResult(global::XASlave.Localization.UiMessage presentation)
    {
        var message = presentation.Original;
        lastIpcResult = global::XASlave.Localization.UiMessage.Store(ref lastIpcResultMessage, presentation);
        lastIpcResultExpiry = DateTime.UtcNow.AddSeconds(8);
        plugin.AutoCollector.AddLog(message);
    }
}
