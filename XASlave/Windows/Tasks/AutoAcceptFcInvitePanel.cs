using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Auto-Accept FC Invites/Leave — monitors for FC creation and join invitations,
/// automatically accepts them, waits a configurable time, then leaves the FC.
/// Designed for single character use (FC floater assist).
///
/// Converted from: 7.35 XA FC Floater Assist.lua
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
    // ── FC Floater state ──
    private bool fcFloaterRunning;
    private bool fcFloaterShowLog;
    private float fcFloaterCheckInterval = 1.0f;
    private float fcFloaterDialogTimeout = 5.0f;
    private float fcFloaterWaitAfterJoin = 15.0f;
    private float fcFloaterTimeoutMinutes = 10.0f;
    private DateTime fcFloaterStartTime;
    private DateTime fcFloaterLastCheck = DateTime.MinValue;
    private int fcFloaterInvitesProcessed;

    // FC invite callback constants (from Lua script)
    private const int InviteTypeCreate = 14;
    private const int InviteTypeJoin = 15;

    private void DrawAutoAcceptFcInviteTask()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Auto-Accept FC Invites/Leave");
        ImGui.TextDisabled("Monitors for FC invitations, accepts them, waits, then leaves.");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
            "WARNING: This WILL leave the FC your character is in. Do not run if you don't want to leave any FC.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Configuration
        ImGui.Text("Configuration:");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(80);
        var checkInt = fcFloaterCheckInterval;
        if (ImGui.InputFloat("Check Interval (sec)##fcFloaterCheck", ref checkInt, 0.5f, 1.0f, "%.1f"))
        {
            if (checkInt < 0.5f) checkInt = 0.5f;
            if (checkInt > 10f) checkInt = 10f;
            fcFloaterCheckInterval = checkInt;
        }

        ImGui.SetNextItemWidth(80);
        var dlgTimeout = fcFloaterDialogTimeout;
        if (ImGui.InputFloat("Dialog Timeout (sec)##fcFloaterDlg", ref dlgTimeout, 1f, 1f, "%.0f"))
        {
            if (dlgTimeout < 1f) dlgTimeout = 1f;
            if (dlgTimeout > 30f) dlgTimeout = 30f;
            fcFloaterDialogTimeout = dlgTimeout;
        }

        ImGui.SetNextItemWidth(80);
        var waitJoin = fcFloaterWaitAfterJoin;
        if (ImGui.InputFloat("Wait After Join (sec)##fcFloaterWait", ref waitJoin, 1f, 5f, "%.0f"))
        {
            if (waitJoin < 1f) waitJoin = 1f;
            if (waitJoin > 300f) waitJoin = 300f;
            fcFloaterWaitAfterJoin = waitJoin;
        }

        ImGui.SetNextItemWidth(80);
        var timeout = fcFloaterTimeoutMinutes;
        if (ImGui.InputFloat("Idle Timeout (min)##fcFloaterTimeout", ref timeout, 1f, 5f, "%.0f"))
        {
            if (timeout < 1f) timeout = 1f;
            if (timeout > 120f) timeout = 120f;
            fcFloaterTimeoutMinutes = timeout;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Start/Stop
        if (!fcFloaterRunning)
        {
            if (plugin.TaskRunner.IsRunning) ImGui.BeginDisabled();
            if (ImGui.Button("Start FC Floater##fcFloaterStart"))
            {
                fcFloaterRunning = true;
                fcFloaterStartTime = DateTime.UtcNow;
                fcFloaterLastCheck = DateTime.MinValue;
                fcFloaterInvitesProcessed = 0;
                plugin.TaskRunner.AddLog("[FC Floater] Started monitoring for FC invitations...");
                plugin.TaskRunner.AddLog($"[FC Floater] Timeout: {fcFloaterTimeoutMinutes} minutes, Wait after join: {fcFloaterWaitAfterJoin}s");
            }
            if (plugin.TaskRunner.IsRunning) ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button("Stop FC Floater##fcFloaterStop"))
            {
                fcFloaterRunning = false;
                plugin.TaskRunner.AddLog("[FC Floater] Stopped.");
            }
            ImGui.SameLine();

            var elapsed = (DateTime.UtcNow - fcFloaterStartTime).TotalSeconds;
            var remaining = (fcFloaterTimeoutMinutes * 60) - elapsed;
            if (remaining > 0)
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                    $"Monitoring... ({remaining:F0}s until timeout) | Invites processed: {fcFloaterInvitesProcessed}");
            }
            else
            {
                fcFloaterRunning = false;
                plugin.TaskRunner.AddLog($"[FC Floater] Timeout reached ({fcFloaterTimeoutMinutes} minutes). Stopping.");
            }

            // Polling logic
            if (fcFloaterRunning && (DateTime.UtcNow - fcFloaterLastCheck).TotalSeconds >= fcFloaterCheckInterval)
            {
                fcFloaterLastCheck = DateTime.UtcNow;
                PollFcInvitations();
            }
        }

        DrawTaskLog("fcFloater", ref fcFloaterShowLog, plugin.TaskRunner);
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

        // Open the notification dialog
        steps.Add(new TaskStep
        {
            Name = $"FC Floater: Open {invitationName}",
            OnEnter = () =>
            {
                runner.AddLog($"Opening {invitationName} notification...");
                AddonHelper.FireCallback("_Notification", 0, invitationType);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: Wait Dialog", 1.0f));

        // Wait for SelectYesno and accept
        steps.Add(new TaskStep
        {
            Name = $"FC Floater: Accept {invitationName}",
            OnEnter = () => { },
            IsComplete = () => AddonHelper.IsAddonReady("SelectYesno"),
            TimeoutSec = fcFloaterDialogTimeout,
        });

        steps.Add(new TaskStep
        {
            Name = "FC Floater: Click Yes",
            OnEnter = () =>
            {
                if (AddonHelper.IsAddonReady("SelectYesno"))
                {
                    runner.AddLog($"Accepting {invitationName} invitation...");
                    AddonHelper.ClickYesNo(true);
                }
                else
                {
                    runner.AddLog($"SelectYesno didn't appear for {invitationName}, skipping...");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: Post-Accept", 3.0f));

        // Wait after joining
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Wait After Join",
            OnEnter = () =>
            {
                runner.AddLog($"Waiting {fcFloaterWaitAfterJoin}s before leaving FC...");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: Join Wait", fcFloaterWaitAfterJoin));

        // Leave FC — open FC window, navigate to Info tab, click Leave button
        // Reference: .xafunc.lua LeaveFreeCompanyXA()
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Open FC Window",
            OnEnter = () =>
            {
                runner.AddLog("Opening FC window to leave...");
                ChatHelper.SendMessage("/freecompanycmd");
            },
            IsComplete = () => AddonHelper.IsAddonVisible("FreeCompany"),
            TimeoutSec = 5f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: FC Load", 1.0f));

        // Navigate to Info tab — callback "FreeCompany false 0 5"
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Info Tab",
            OnEnter = () =>
            {
                runner.AddLog("Navigating to FC Info tab...");
                if (AddonHelper.IsAddonReady("FreeCompany"))
                    AddonHelper.FireCallback("FreeCompany", 0, 5);
            },
            IsComplete = () => AddonHelper.IsAddonVisible("FreeCompanyStatus"),
            TimeoutSec = 5f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: Info Load", 1.0f));

        // Click Leave FC — callback "FreeCompanyStatus true 3"
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Click Leave",
            OnEnter = () =>
            {
                runner.AddLog("Clicking Leave Free Company...");
                if (AddonHelper.IsAddonReady("FreeCompanyStatus"))
                    AddonHelper.FireCallback("FreeCompanyStatus", 3);
            },
            IsComplete = () => AddonHelper.IsAddonReady("SelectYesno"),
            TimeoutSec = 5f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: Leave Dialog", 0.5f));

        // Confirm leave
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Confirm Leave",
            OnEnter = () =>
            {
                if (AddonHelper.IsAddonReady("SelectYesno"))
                {
                    runner.AddLog("Confirming FC leave...");
                    AddonHelper.ClickYesNo(true);
                }
            },
            IsComplete = () => !AddonHelper.IsAddonReady("SelectYesno"),
            TimeoutSec = 5f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: Post-Leave", 2.0f));

        // Close remaining FC windows
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Close FC Windows",
            OnEnter = () =>
            {
                AddonHelper.CloseAddon("FreeCompanyStatus");
                AddonHelper.CloseAddon("FreeCompany");
            },
            IsComplete = () => !AddonHelper.IsAddonVisible("FreeCompany"),
            TimeoutSec = 3f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay("FC Floater: FC Close", 1.0f));

        // Resume monitoring
        steps.Add(new TaskStep
        {
            Name = "FC Floater: Resume",
            OnEnter = () =>
            {
                fcFloaterInvitesProcessed++;
                runner.AddLog($"[FC Floater] Invitation processed. Total: {fcFloaterInvitesProcessed}. Ready for next...");
                fcFloaterRunning = true; // Resume monitoring
                fcFloaterStartTime = DateTime.UtcNow; // Reset timeout
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        runner.Start("FC Floater: Process Invite", steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });
    }
}
