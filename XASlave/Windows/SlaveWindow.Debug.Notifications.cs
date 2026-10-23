using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class DebugNotificationTest(string addon, global::XASlave.Localization.UiMessage description, int requestKind)
    {
        internal readonly string Addon = addon;
        internal readonly global::XASlave.Localization.UiMessage Description = description;
        internal readonly int RequestKind = requestKind;
        internal global::XASlave.Localization.UiMessage StatusMessage = global::XASlave.Localization.UiMessage.Key("S2A0EF071E7826BA1");
        internal string Status => StatusMessage.Original;
    }

    private readonly DebugNotificationTest[] debugNotificationTests =
    [
        new("_Notification", global::XASlave.Localization.UiMessage.Key("SA423D05BAC8F3355"), -1),
        new("_NotificationFcMake", global::XASlave.Localization.UiMessage.Key("SC1AD1C8D41590CB9"), 14),
        new("_NotificationFcJoin", global::XASlave.Localization.UiMessage.Key("SE2258C33DFC4FF9E"), 15),
        new("_NotificationFriend", global::XASlave.Localization.UiMessage.Key("S73F1DBDA93480DC9"), 20),
    ];
    private readonly Queue<string> debugNotificationLog = new();
    private int debugNotificationLogLength;
    private int debugNotificationDroppedEntries;
    private int debugNotificationNormalKind = -1;
    private bool debugNotificationUpdateState = true;
    private int debugNotificationAttempt;
    private DebugNotificationTest? debugNotificationPending;
    private ulong debugNotificationContentId;
    private long debugNotificationStarted;
    private int debugNotificationObservation;
    private static readonly int[] DebugNotificationObservationTimes = [250, 1000, 3000, 5000];
    private const int DebugNotificationLogLimit = 131072;

    private void DrawXaAbuseNotificationTests()
    {
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SC259A81E2CDCC435"));
        ImGui.BeginDisabled(debugNotificationPending != null);
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SBFE906AD00C2BFE4") + "##notificationTests"), ref debugNotificationUpdateState);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S39888D3B749B504B"));
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SAC7B551D5D4D42A2") + "##notificationTests")))
        {
            LogDebugNotification("Inspect all requested.");
            foreach (var test in debugNotificationTests)
                InspectDebugNotification(test);
        }
        foreach (var test in debugNotificationTests)
        {
            ImGui.PushID(test.Addon);
            ImGui.Separator();
            ImGui.TextUnformatted(test.Addon);
            ImGui.TextWrapped(test.Description.Display);
            if (test.RequestKind < 0)
            {
                ImGui.SetNextItemWidth(Scale(110));
                global::XASlave.Localization.UiNative.InputInt(global::XASlave.Localization.UiLocale.Native("S1804E55FD1416AE3"), ref debugNotificationNormalKind, 0, 0);
                ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S3E200CCDEFA1FD90"));
            }
            else
                ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S41F393A13E408AE4", test.RequestKind));
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE0723A86A5B9408A")))
                InspectDebugNotification(test);
            ImGui.SameLine();
            var kind = test.RequestKind < 0 ? debugNotificationNormalKind : test.RequestKind;
            ImGui.BeginDisabled(kind is < 0 or > 65535);
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SED077F3D8125D60D")))
                OpenDebugNotification(test, kind);
            ImGui.EndDisabled();
            ImGui.TextWrapped(test.StatusMessage.Display);
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        ImGui.Separator();
        if (debugNotificationPending != null)
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SA43D74441B1D49B5", debugNotificationPending.Addon));
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S15A0FA02160925DB") + "##notificationTests")))
            ImGui.SetClipboardText(GetDebugNotificationLog());
        ImGui.SameLine();
        ImGui.BeginDisabled(debugNotificationPending != null);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9FA34EA2946B0745") + "##notificationTests")))
        {
            debugNotificationLog.Clear();
            debugNotificationLogLength = debugNotificationDroppedEntries = 0;
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S669C7BAD2C67010D"));
        using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("NotificationTestLog", new Vector2(0, Scale(220)), true);
        if (child.Success)
            ImGui.TextUnformatted(GetDebugNotificationLog());
    }

    private void InspectDebugNotification(DebugNotificationTest test)
    {
        var snapshot = NotificationDebugProbe.Capture(test.Addon);
        test.StatusMessage = global::XASlave.Localization.UiMessage.Format("S6C097655A93929C4", DateTime.Now, snapshot.Visible, snapshot.Ready,
            snapshot.FirstInteger.HasValue ? global::XASlave.Localization.UiMessage.Value(snapshot.FirstInteger.Value) : global::XASlave.Localization.UiMessage.Key("SBA691BA042BCEDD9"));
        LogDebugNotification($"Inspect {test.Addon}\n{snapshot.Report}");
    }

    private void OpenDebugNotification(DebugNotificationTest test, int kind)
    {
        if (debugNotificationPending != null || isDisposed)
            return;
        ++debugNotificationAttempt;
        debugNotificationContentId = Plugin.PlayerState.ContentId;
        LogDebugNotification($"Attempt {debugNotificationAttempt}: Open requested; target={test.Addon}, host=_Notification, Int arguments=[0, {kind}], updateState={debugNotificationUpdateState}; character={debugNotificationContentId}.");
        var before = NotificationDebugProbe.Capture(test.Addon);
        LogDebugNotification("Before target\n" + before.Report);
        if (test.Addon != "_Notification")
            LogDebugNotification("Before host\n" + NotificationDebugProbe.Capture("_Notification").Report);
        LogDebugNotification("Before prompt\n" + NotificationDebugProbe.Capture("SelectYesno").Report);
        var dispatched = NotificationDebugProbe.TryOpen(before, kind, debugNotificationUpdateState, out var detail);
        test.StatusMessage = global::XASlave.Localization.UiMessage.Raw(detail);
        LogDebugNotification($"Attempt {debugNotificationAttempt}: {detail}");
        SetDebugResult(global::XASlave.Localization.UiMessage.Format("SA1505B1EC8309432", test.Addon, detail));
        if (!dispatched)
            return;
        // Only managed values survive dispatch. Native addons can be destroyed by the callback.
        debugNotificationStarted = Environment.TickCount64;
        debugNotificationObservation = 0;
        debugNotificationPending = test;
    }

    private void UpdateDebugNotificationTests()
    {
        var test = debugNotificationPending;
        if (test == null || isDisposed)
            return;
        try
        {
            if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != debugNotificationContentId)
            {
                debugNotificationPending = null;
                test.StatusMessage = global::XASlave.Localization.UiMessage.Key("S76C6256FF74C5C26");
                LogDebugNotification(test.Status);
                return;
            }
            var elapsed = Environment.TickCount64 - debugNotificationStarted;
            if (elapsed < DebugNotificationObservationTimes[debugNotificationObservation])
                return;
            ++debugNotificationObservation;
            LogDebugNotification($"Attempt {debugNotificationAttempt}: after {elapsed} ms\n" + NotificationDebugProbe.Capture(test.Addon).Report);
            if (test.Addon != "_Notification")
                LogDebugNotification("After host\n" + NotificationDebugProbe.Capture("_Notification").Report);
            var prompt = NotificationDebugProbe.Capture("SelectYesno");
            LogDebugNotification("After prompt\n" + prompt.Report);
            if (prompt.Available)
            {
                debugNotificationPending = null;
                test.StatusMessage = global::XASlave.Localization.UiMessage.Key("SA7B2F4864A47F742");
                LogDebugNotification($"Attempt {debugNotificationAttempt}: {test.Status} This observation does not prove acceptance or that the prompt belongs to this request.");
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6A07F5FE150736C9"));
            }
            else if (elapsed >= DebugNotificationObservationTimes[^1])
            {
                debugNotificationPending = null;
                test.StatusMessage = global::XASlave.Localization.UiMessage.Key("S5F4BAE7CD3189FF4");
                LogDebugNotification($"Attempt {debugNotificationAttempt}: {test.Status}");
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SF9A64372B9AEF2E0"));
            }
        }
        catch (Exception ex)
        {
            debugNotificationPending = null;
            test.StatusMessage = global::XASlave.Localization.UiMessage.Format("SEA9DDE8583E59EE9", ex.GetType().Name, ex.Message);
            LogDebugNotification(test.Status);
        }
    }

    private void LogDebugNotification(string message)
    {
        var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
        // Native captures are bounded too; retain an explicit truncation marker if a future capture grows.
        if (entry.Length > DebugNotificationLogLimit / 2)
            entry = entry[..(DebugNotificationLogLimit / 2)] + "\n[capture truncated]";
        while (debugNotificationLog.Count > 0 && debugNotificationLogLength + entry.Length > DebugNotificationLogLimit)
        {
            debugNotificationLogLength -= debugNotificationLog.Dequeue().Length;
            ++debugNotificationDroppedEntries;
        }
        debugNotificationLog.Enqueue(entry);
        debugNotificationLogLength += entry.Length;
        Plugin.Log.Information($"[XASlave] [Notification tests] {entry}");
    }

    private string GetDebugNotificationLog() =>
        "XA Slave - Notification tests (manual open only)\n" +
        (debugNotificationDroppedEntries == 0 ? string.Empty : $"[{debugNotificationDroppedEntries} older log entries dropped; /xllog may retain them]\n") +
        string.Join("\n\n", debugNotificationLog);
}
