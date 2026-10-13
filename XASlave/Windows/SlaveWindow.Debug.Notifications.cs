using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private sealed class DebugNotificationTest(string addon, string description, int requestKind)
    {
        internal readonly string Addon = addon;
        internal readonly string Description = description;
        internal readonly int RequestKind = requestKind;
        internal string Status = "Not inspected.";
    }

    private readonly DebugNotificationTest[] debugNotificationTests =
    [
        new("_Notification", "Normal notification (shared dispatcher)", -1),
        new("_NotificationFcMake", "FC creation invite notification", 14),
        new("_NotificationFcJoin", "FC join notification for an existing FC", 15),
        new("_NotificationFriend", "Friend add notification", 20),
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
        ImGui.TextWrapped("Inspect an incoming notification, then Open it. Choose Yes or No in the game yourself. Opening may also trigger another plugin's automatic response.");
        ImGui.BeginDisabled(debugNotificationPending != null);
        ImGui.Checkbox("Update state##notificationTests", ref debugNotificationUpdateState);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sets the callback updateState flag. Enabled by default; turn off to compare the older FC notification callback.");
        if (ImGui.Button("Inspect all##notificationTests"))
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
            ImGui.TextWrapped(test.Description);
            if (test.RequestKind < 0)
            {
                ImGui.SetNextItemWidth(Scale(110));
                ImGui.InputInt("Request kind", ref debugNotificationNormalKind, 0, 0);
                ImGui.TextWrapped("Enter the kind of the notification to open (0-65535). The dispatcher itself has no single normal-notification kind; -1 leaves Open unavailable.");
            }
            else
                ImGui.TextUnformatted($"Request kind: {test.RequestKind}");
            if (ImGui.Button("Inspect"))
                InspectDebugNotification(test);
            ImGui.SameLine();
            var kind = test.RequestKind < 0 ? debugNotificationNormalKind : test.RequestKind;
            ImGui.BeginDisabled(kind is < 0 or > 65535);
            if (ImGui.Button("Open"))
                OpenDebugNotification(test, kind);
            ImGui.EndDisabled();
            ImGui.TextWrapped(test.Status);
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        ImGui.Separator();
        if (debugNotificationPending != null)
            ImGui.TextWrapped($"Observing {debugNotificationPending.Addon} for up to five seconds. No further callback will be sent.");
        if (ImGui.Button("Copy test log##notificationTests"))
            ImGui.SetClipboardText(GetDebugNotificationLog());
        ImGui.SameLine();
        ImGui.BeginDisabled(debugNotificationPending != null);
        if (ImGui.Button("Clear test log##notificationTests"))
        {
            debugNotificationLog.Clear();
            debugNotificationLogLength = debugNotificationDroppedEntries = 0;
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("Test captures also go to /xllog. The copyable log retains the latest 128K characters.");
        using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("NotificationTestLog", new Vector2(0, Scale(220)), true);
        if (child.Success)
            ImGui.TextUnformatted(GetDebugNotificationLog());
    }

    private void InspectDebugNotification(DebugNotificationTest test)
    {
        var snapshot = NotificationDebugProbe.Capture(test.Addon);
        test.Status = $"Inspected {DateTime.Now:HH:mm:ss}; visible={snapshot.Visible}, ready={snapshot.Ready}, first integer={snapshot.FirstInteger?.ToString() ?? "unavailable"}.";
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
        test.Status = detail;
        LogDebugNotification($"Attempt {debugNotificationAttempt}: {detail}");
        SetDebugResult($"Notification tests: {test.Addon}: {detail}");
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
                test.Status = "Observation stopped: character logged out or changed.";
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
                test.Status = "SelectYesno observed. Check its text in the log against the request; choose Yes/No manually.";
                LogDebugNotification($"Attempt {debugNotificationAttempt}: {test.Status} This observation does not prove acceptance or that the prompt belongs to this request.");
                SetDebugResult("Notification tests: SelectYesno observed; review the prompt.");
            }
            else if (elapsed >= DebugNotificationObservationTimes[^1])
            {
                debugNotificationPending = null;
                test.Status = "Five-second observation ended without a ready SelectYesno. Check the game UI and captured states; opening is unconfirmed.";
                LogDebugNotification($"Attempt {debugNotificationAttempt}: {test.Status}");
                SetDebugResult("Notification tests: observation ended; opening is unconfirmed.");
            }
        }
        catch (Exception ex)
        {
            debugNotificationPending = null;
            test.Status = $"Observation failed: {ex.GetType().Name}: {ex.Message}";
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
