using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Auto-Accept FC Invites/Leave - monitors for FC creation and join invitations,
/// automatically accepts them, waits a configurable time, then leaves the FC.
/// Designed for single character use (FC floater assist).
///
/// Flow:
///   1. Monitor for _NotificationFcMake (FC creation invite) and _NotificationFcJoin (FC join invite)
///   2. When detected, fire callback to open invite, then accept via SelectYesno
///   3. Wait configurable time after joining
///   4. Leave the FC via FC window → Info tab → Leave button + confirm
///   5. Loop back to monitoring
///   6. Timeout after configurable idle time with no invite
///
/// Uses: AddonHelper for addon detection and callbacks, ChatHelper for commands
/// </summary>
public partial class SlaveWindow
{
    // -- FC Floater state --
    private bool fcFloaterRunning;
    private bool fcFloaterShowLog;
    private float fcFloaterCheckInterval = 1.0f;
    private float fcFloaterDialogTimeout = 5.0f;
    private float fcFloaterWaitAfterJoin = 15.0f;
    private float fcFloaterTimeoutMinutes = 10.0f;
    private bool fcFloaterOptionsInitialized;
    private DateTime fcFloaterStartTime;
    private DateTime fcFloaterLastCheck = DateTime.MinValue;
    private int fcFloaterInvitesProcessed;

    // FC invite callback constants
    private const int InviteTypeCreate = 14;
    private const int InviteTypeJoin = 15;

    private void DrawAutoAcceptFcInviteTask()
    {
        EnsureFcFloaterOptionsInitialized();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S561FC262F8C8D0F9"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S6E9723C65EA6A2AB"));
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
            global::XASlave.Localization.UiLocale.Get("S343D00CC761CCB2A"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Configuration
        ImGui.Text(global::XASlave.Localization.UiLocale.Get("SEF75F631B332CA1D"));
        ImGui.Spacing();

        ImGui.SetNextItemWidth(Scale(80f));
        var checkInt = fcFloaterCheckInterval;
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("S450DA347330F7030") + "##fcFloaterCheck"), ref checkInt, 0.5f, 1.0f, "%.1f"))
        {
            if (checkInt < 0.5f) checkInt = 0.5f;
            if (checkInt > 10f) checkInt = 10f;
            fcFloaterCheckInterval = checkInt;
            plugin.Configuration.FcFloaterCheckIntervalSeconds = checkInt;
            plugin.Configuration.SaveDeferred();
        }

        ImGui.SetNextItemWidth(Scale(80f));
        var dlgTimeout = fcFloaterDialogTimeout;
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("S4369F1FAD55DD134") + "##fcFloaterDlg"), ref dlgTimeout, 1f, 1f, "%.0f"))
        {
            if (dlgTimeout < 1f) dlgTimeout = 1f;
            if (dlgTimeout > 30f) dlgTimeout = 30f;
            fcFloaterDialogTimeout = dlgTimeout;
            plugin.Configuration.FcFloaterDialogTimeoutSeconds = dlgTimeout;
            plugin.Configuration.SaveDeferred();
        }

        ImGui.SetNextItemWidth(Scale(80f));
        var waitJoin = fcFloaterWaitAfterJoin;
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("SBE7F3D59078C57AD") + "##fcFloaterWait"), ref waitJoin, 1f, 5f, "%.0f"))
        {
            if (waitJoin < 1f) waitJoin = 1f;
            if (waitJoin > 300f) waitJoin = 300f;
            fcFloaterWaitAfterJoin = waitJoin;
            plugin.Configuration.FcFloaterWaitAfterJoinSeconds = waitJoin;
            plugin.Configuration.SaveDeferred();
        }

        ImGui.SetNextItemWidth(Scale(80f));
        var timeout = fcFloaterTimeoutMinutes;
        if (global::XASlave.Localization.UiNative.InputFloat((global::XASlave.Localization.UiLocale.Native("S3CE6463A541ED987") + "##fcFloaterTimeout"), ref timeout, 1f, 5f, "%.0f"))
        {
            if (timeout < 1f) timeout = 1f;
            if (timeout > 120f) timeout = 120f;
            fcFloaterTimeoutMinutes = timeout;
            plugin.Configuration.FcFloaterIdleTimeoutMinutes = timeout;
            plugin.Configuration.SaveDeferred();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Start/Stop
        var started = DrawPriorityTaskActionButton(
            SlaveTask.AutoAcceptFcInvite,
            (global::XASlave.Localization.UiLocale.Native("S7A52B03A4B3E7385") + "##fcFloaterStart"),
            !plugin.TaskRunner.IsRunning,
            StartAutoAcceptFcInviteTask,
            global::XASlave.Localization.UiLocale.Get("SFFA14D5B20D4D1E9"));
        if (started)
            AutoOpenTaskLogIfVerbose(ref fcFloaterShowLog);

        if (fcFloaterRunning)
        {
            ImGui.SameLine();

            var elapsed = (DateTime.UtcNow - fcFloaterStartTime).TotalSeconds;
            var remaining = (fcFloaterTimeoutMinutes * 60) - elapsed;
            if (remaining > 0)
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                    global::XASlave.Localization.UiLocale.FormatKey("S0D9ACC058FAE2871", remaining, fcFloaterInvitesProcessed));
            }
            else
            {
                fcFloaterRunning = false;
                plugin.TaskRunner.AddLog($"[FC Floater] Timeout reached ({fcFloaterTimeoutMinutes} minutes). Stopping.");
            }
        }
        else if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var activeTask) && activeTask == SlaveTask.AutoAcceptFcInvite)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SE435DD8232E22011"));
        }

        DrawTaskLog("fcFloater", ref fcFloaterShowLog, plugin.TaskRunner);
    }

    private void EnsureFcFloaterOptionsInitialized()
    {
        if (fcFloaterOptionsInitialized)
            return;

        var cfg = plugin.Configuration;
        fcFloaterCheckInterval = Math.Clamp(cfg.FcFloaterCheckIntervalSeconds, 0.5f, 10f);
        fcFloaterDialogTimeout = Math.Clamp(cfg.FcFloaterDialogTimeoutSeconds, 1f, 30f);
        fcFloaterWaitAfterJoin = Math.Clamp(cfg.FcFloaterWaitAfterJoinSeconds, 1f, 300f);
        fcFloaterTimeoutMinutes = Math.Clamp(cfg.FcFloaterIdleTimeoutMinutes, 1f, 120f);
        fcFloaterOptionsInitialized = true;
    }

    private void StartAutoAcceptFcInviteTask()
    {
        EnsureFcFloaterOptionsInitialized();
        if (fcFloaterRunning)
            return;

        fcFloaterRunning = true;
        fcFloaterStartTime = DateTime.UtcNow;
        fcFloaterLastCheck = DateTime.MinValue;
        fcFloaterInvitesProcessed = 0;
        plugin.TaskRunner.AddLog("[FC Floater] Started monitoring for FC invitations...");
        plugin.TaskRunner.AddLog($"[FC Floater] Timeout: {fcFloaterTimeoutMinutes} minutes, Wait after join: {fcFloaterWaitAfterJoin}s");
        UpdatePriorityTaskExternalStatus();
    }

    private void StopAutoAcceptFcInviteTask()
    {
        var stoppedMonitor = false;
        if (fcFloaterRunning)
        {
            fcFloaterRunning = false;
            stoppedMonitor = true;
        }

        if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var activeTask) && activeTask == SlaveTask.AutoAcceptFcInvite)
            plugin.TaskRunner.Cancel();

        if (stoppedMonitor)
            plugin.TaskRunner.AddLog("[FC Floater] Stopped.");

        UpdatePriorityTaskExternalStatus();
    }

    /// <summary>
    /// Poll for FC invitations and handle the accept/wait/leave flow.
    /// Called on the framework draw thread at the configured interval.
    /// </summary>
    private void PollFcInvitations()
    {
        try
        {
            // Check for FC creation invitation
            if (AddonHelper.IsAddonReady("_NotificationFcMake"))
            {
                plugin.TaskRunner.AddLog("[FC Floater] FC creation invitation detected! Accepting...");
                ProcessFcInvitation(InviteTypeCreate, "FC creation");
                return;
            }

            // Check for FC join invitation
            if (AddonHelper.IsAddonReady("_NotificationFcJoin"))
            {
                plugin.TaskRunner.AddLog("[FC Floater] FC join invitation detected! Accepting...");
                ProcessFcInvitation(InviteTypeJoin, "FC join");
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] FC Floater poll error: {ex.Message}");
        }
    }

    /// <summary>
    /// Process an FC invitation: open notification, accept SelectYesno, wait, leave FC.
    /// Uses a step-based approach via TaskRunner to avoid blocking the framework thread.
    /// </summary>
    private void ProcessFcInvitation(int invitationType, string invitationName)
    {
        fcFloaterRunning = false; // Pause monitoring while processing

        var steps = new System.Collections.Generic.List<TaskStep>();
        var runner = plugin.TaskRunner;
        var flowFailed = false;
        var notificationDispatched = false;
        var invitationConfirmationDispatched = false;
        var leaveConfirmationDispatched = false;
        var guardedStart = steps.Count;

        void FailFlow(string message)
        {
            if (flowFailed)
                return;

            flowFailed = true;
            runner.AddLog($"[FC Floater] {message} Refusing to continue this invitation flow.");
        }

        // Open the notification dialog
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SFBDA825082410AF1", invitationName),
            OnEnter = () =>
            {
                runner.AddVerboseLog($"Opening {invitationName} notification...");
                notificationDispatched = AddonHelper.FireCallback("_Notification", 0, invitationType);
            },
            IsComplete = () => notificationDispatched,
            TimeoutSec = 2f,
            MaxRetries = 1,
            OnTimeout = () => FailFlow($"Could not open the {invitationName} notification."),
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S8E9EE6ECC51BD7E5"), 1.0f));

        // Wait for SelectYesno and accept
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SB2071868AE62CA8D", invitationName),
            OnEnter = () => { },
            IsComplete = () => IsExpectedFcInvitationPrompt(invitationType, out _),
            TimeoutSec = fcFloaterDialogTimeout,
            OnTimeout = () =>
            {
                AddonHelper.TryGetSelectYesnoText(out var prompt);
                FailFlow($"The confirmation prompt did not match the expected {invitationName} invitation (visible text: '{prompt}').");
            },
        });

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S5E6A65CFCB8C1BCD"),
            OnEnter = () =>
            {
                if (IsExpectedFcInvitationPrompt(invitationType, out var prompt))
                {
                    runner.AddLog($"Accepting the verified {invitationName} invitation prompt: {prompt}");
                    invitationConfirmationDispatched = AddonHelper.ClickYesNo(true);
                }
                else
                {
                    FailFlow($"The {invitationName} prompt changed before confirmation.");
                }
            },
            IsComplete = () => flowFailed || invitationConfirmationDispatched && !AddonHelper.IsAddonReady("SelectYesno"),
            TimeoutSec = 5f,
            OnTimeout = () => FailFlow($"The {invitationName} confirmation did not resolve."),
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S8BAED153236C308D"), 3.0f));

        // Wait after joining
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SD8944314461EBAB6"),
            OnEnter = () =>
            {
                runner.AddLog($"Waiting {fcFloaterWaitAfterJoin}s before leaving FC...");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SEE5362E1D92FCFB6"), fcFloaterWaitAfterJoin));

        // Leave FC - open FC window, navigate to Info tab, click Leave button
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SA714EFE3E2061AA3"),
            OnEnter = () =>
            {
                runner.AddVerboseLog("Opening FC window to leave...");
                ChatHelper.SendMessage("/freecompanycmd");
            },
            IsComplete = () => AddonHelper.IsAddonVisible("FreeCompany"),
            TimeoutSec = 5f,
            OnTimeout = () => FailFlow("The Free Company window did not open after joining."),
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S1F24D56E71AEF278"), 1.0f));

        // Navigate to Info tab - callback "FreeCompany false 0 5"
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S3D2380200C880B5C"),
            OnEnter = () =>
            {
                runner.AddVerboseLog("Navigating to FC Info tab...");
                if (AddonHelper.IsAddonReady("FreeCompany"))
                    AddonHelper.FireCallback("FreeCompany", 0, 5);
            },
            IsComplete = () => AddonHelper.IsAddonVisible("FreeCompanyStatus"),
            TimeoutSec = 5f,
            OnTimeout = () => FailFlow("The Free Company status page did not open."),
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S0416EA98AC11CDA5"), 1.0f));

        // Click Leave FC - callback "FreeCompanyStatus true 3"
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SEC55BE45E290DD4C"),
            OnEnter = () =>
            {
                runner.AddVerboseLog("Clicking Leave Free Company...");
                if (AddonHelper.IsAddonReady("FreeCompanyStatus"))
                    AddonHelper.FireCallback("FreeCompanyStatus", 3);
            },
            IsComplete = () => IsExpectedFcLeavePrompt(out _),
            TimeoutSec = 5f,
            OnTimeout = () =>
            {
                AddonHelper.TryGetSelectYesnoText(out var prompt);
                FailFlow($"The leave confirmation prompt was not positively identified (visible text: '{prompt}').");
            },
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SC0701DAB536AB3F5"), 0.5f));

        // Confirm leave
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S847B5DE7CD9B125E"),
            OnEnter = () =>
            {
                if (IsExpectedFcLeavePrompt(out var prompt))
                {
                    runner.AddVerboseLog($"Confirming verified FC leave prompt: {prompt}");
                    leaveConfirmationDispatched = AddonHelper.ClickYesNo(true);
                }
                else
                    FailFlow("The Free Company leave prompt changed before confirmation.");
            },
            IsComplete = () => flowFailed || leaveConfirmationDispatched && !AddonHelper.IsAddonReady("SelectYesno"),
            TimeoutSec = 5f,
            OnTimeout = () => FailFlow("The Free Company leave confirmation did not resolve."),
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("S0B249B535798A783"), 2.0f));

        // Close remaining FC windows
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SB4507E5D85C5C6B7"),
            OnEnter = () =>
            {
                AddonHelper.CloseAddon("FreeCompanyStatus");
                AddonHelper.CloseAddon("FreeCompany");
            },
            IsComplete = () => !AddonHelper.IsAddonVisible("FreeCompany"),
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Key("SB38A53DDD9AEFEE3"), 1.0f));

        for (var index = guardedStart; index < steps.Count; index++)
            steps[index] = MonthlyReloggerTask.WithSkip(steps[index], () => flowFailed);

        // Resume monitoring
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SE035502A6485EF6B"),
            OnEnter = () =>
            {
                if (!flowFailed)
                {
                    fcFloaterInvitesProcessed++;
                    runner.AddLog($"[FC Floater] Invitation processed. Total: {fcFloaterInvitesProcessed}. Ready for next...");
                }
                else
                {
                    runner.AddLog("[FC Floater] Invitation was not processed because a required UI state could not be verified. Monitoring resumed.");
                }
                fcFloaterRunning = true; // Resume monitoring
                fcFloaterStartTime = DateTime.UtcNow; // Reset timeout
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        if (!runner.Start(global::XASlave.Localization.UiMessage.Key("SE2B2059260E2D1A3"), steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        }))
        {
            fcFloaterRunning = true;
            fcFloaterStartTime = DateTime.UtcNow;
            runner.AddLog($"[FC Floater] Could not process the {invitationName} invitation because '{runner.CurrentTaskName}' owns the task runner. Monitoring resumed.");
            return;
        }

        UpdatePriorityTaskExternalStatus();
    }

    private static bool IsExpectedFcInvitationPrompt(int invitationType, out string prompt)
    {
        if (!AddonHelper.TryGetSelectYesnoText(out prompt) ||
            !prompt.Contains("free company", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return invitationType == InviteTypeCreate
            ? prompt.Contains("form", StringComparison.OrdinalIgnoreCase) ||
              prompt.Contains("create", StringComparison.OrdinalIgnoreCase) ||
              prompt.Contains("establish", StringComparison.OrdinalIgnoreCase)
            : prompt.Contains("join", StringComparison.OrdinalIgnoreCase) ||
              prompt.Contains("invite", StringComparison.OrdinalIgnoreCase) ||
              prompt.Contains("invitation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedFcLeavePrompt(out string prompt)
    {
        return AddonHelper.TryGetSelectYesnoText(out prompt) &&
               prompt.Contains("free company", StringComparison.OrdinalIgnoreCase) &&
               (prompt.Contains("leave", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("resign", StringComparison.OrdinalIgnoreCase));
    }
}
