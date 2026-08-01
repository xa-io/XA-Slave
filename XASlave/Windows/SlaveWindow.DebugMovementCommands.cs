using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private const int DebugLeaveDutyQuickMenuAttempts = 8;
    private const int DebugLeaveDutyQuickPromptAttempts = 12;
    private const int DebugLeaveDutyQuickRetryDelayMilliseconds = 250;
    private int debugLeaveDutyQuickRunning;

    public bool TryExecuteXaMovementCommand(string subcommand, string arguments, out string message, out bool handled)
    {
        handled = true;
        var normalized = subcommand.Trim();

        if (normalized.Equals("leaveduty", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arguments))
        {
            handled = false;
            message = string.Empty;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            switch (normalized.ToLowerInvariant())
            {
                case "movingcheatersmart":
                case "movingcheaterfly":
                case "movingcheaterwalk":
                case "interact":
                case "recommendedgear":
                case "stopmovement":
                case "pathtotargetinteract":
                case "pathsmartinteract":
                    message = $"Usage: /xa {normalized.ToLowerInvariant()}.";
                    return false;
            }
        }

        switch (normalized.ToLowerInvariant())
        {
            case "movingcheatersmart":
                RunDebugMovingCheaterSmart();
                message = "Triggered MovingCheater Smart.";
                return true;
            case "movingcheaterfly":
                RunDebugMovingCheaterFly();
                message = "Triggered MovingCheater Fly.";
                return true;
            case "movingcheaterwalk":
                RunDebugMovingCheaterWalk();
                message = "Triggered MovingCheater Walk.";
                return true;
            case "interact":
                RunDebugInteract();
                message = "Triggered Interact.";
                return true;
            case "leaveduty":
                RunDebugLeaveDuty();
                message = "Triggered direct Leave Duty.";
                return true;
            case "recommendedgear":
                RunDebugRecommendedGear();
                message = "Triggered Recommended Gear.";
                return true;
            case "stopmovement":
                RunDebugVnavStop();
                message = "Triggered vnav stop.";
                return true;
            case "pathtotargetinteract":
                RunDebugPathToTargetThenInteract();
                message = "Triggered PathToTargetThenInteract.";
                return true;
            case "pathsmartinteract":
                RunDebugPathSmartThenInteract();
                message = "Triggered PathSmartThenInteract.";
                return true;
            default:
                handled = false;
                message = string.Empty;
                return false;
        }
    }

    private void RunDebugInteract()
    {
        var ok = AddonHelper.InteractWithTarget();
        SetDebugResult(ok ? "InteractWithTarget: OK" : "No target or interaction failed");
    }

    private void RunDebugVnavStop()
    {
        plugin.IpcClient.VnavStop();
        SetDebugResult("Sent: vnavmesh.Path.Stop()");
    }

    private void RunDebugMovingCheaterSmart()
    {
        try
        {
            if (!TryStartMovingCheaterMountStep(out var alreadyMounted))
                return;

            var canFly = HasFlightUnlocked();
            if (canFly)
            {
                ChatHelper.SendMessage("/vnav flyflag");
                SetDebugResult(alreadyMounted
                    ? "Smart: Already mounted + /vnav flyflag (flying unlocked in zone)"
                    : "Smart: Mount + /vnav flyflag (flying unlocked in zone)");
            }
            else
            {
                ChatHelper.SendMessage("/vnav moveflag");
                SetDebugResult(alreadyMounted
                    ? "Smart: Already mounted + /vnav moveflag (flying NOT unlocked, ground pathfind)"
                    : "Smart: Mount + /vnav moveflag (flying NOT unlocked, ground pathfind)");
            }
        }
        catch (Exception ex)
        {
            SetDebugResult($"MovingCheater error: {ex.Message}");
        }
    }

    private void RunDebugMovingCheaterFly()
    {
        try
        {
            if (!TryStartMovingCheaterMountStep(out var alreadyMounted))
                return;

            var canFly = HasFlightUnlocked();
            if (canFly)
            {
                ChatHelper.SendMessage("/vnav flyflag");
                SetDebugResult(alreadyMounted
                    ? "Sent: already mounted + /vnav flyflag (flying unlocked)"
                    : "Sent: Mount + /vnav flyflag (flying unlocked)");
            }
            else
            {
                ChatHelper.SendMessage("/vnav moveflag");
                SetDebugResult(alreadyMounted
                    ? "Sent: already mounted + /vnav moveflag (flight NOT unlocked, fallback to ground)"
                    : "Sent: Mount + /vnav moveflag (flight NOT unlocked, fallback to ground)");
            }
        }
        catch (Exception ex)
        {
            SetDebugResult($"MovingCheater error: {ex.Message}");
        }
    }

    private void RunDebugMovingCheaterWalk()
    {
        try
        {
            if (!TryStartMovingCheaterMountStep(out var alreadyMounted))
                return;

            ChatHelper.SendMessage("/vnav moveflag");
            SetDebugResult(alreadyMounted
                ? "Sent: already mounted + /vnav moveflag (force ground)"
                : "Sent: Mount + /vnav moveflag (force ground)");
        }
        catch (Exception ex)
        {
            SetDebugResult($"MovingCheater error: {ex.Message}");
        }
    }

    private bool TryStartMovingCheaterMountStep(out bool alreadyMounted)
    {
        alreadyMounted = false;
        if (!plugin.IpcClient.VnavIsReady())
        {
            SetDebugResult("vnavmesh not ready, cannot navigate");
            return false;
        }

        alreadyMounted = IsMounted();
        if (!alreadyMounted)
            ChatHelper.SendMessage("/gaction \"Mount Roulette\"");

        return true;
    }

    private void RunDebugPathToTargetThenInteract()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        var target = local?.TargetObject;
        if (target == null)
        {
            SetDebugResult("No target selected");
            return;
        }

        if (local == null || !plugin.IpcClient.VnavIsReady())
        {
            SetDebugResult("vnavmesh not ready");
            return;
        }

        var targetPos = target.Position;
        var targetName = target.Name.ToString();
        var targetHitbox = target.HitboxRadius;
        const float stopDist = 0.5f;
        const float interactRange = 1.0f;

        var ok = plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist);
        if (!ok)
        {
            SetDebugResult("Pathfind failed, vnav could not start route");
            return;
        }

        SetDebugResult($"Pathing to {targetName} (stop={stopDist:F1}y, interact<={interactRange:F1}y ring)");
        Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: {targetName} hitbox={targetHitbox:F1} stopDist={stopDist:F1} interactRange={interactRange:F1}");
        System.Threading.Tasks.Task.Run(async () =>
        {
            var distSamples = new System.Collections.Generic.List<float>();
            const int maxSamples = 7;
            const float stallThreshold = 0.3f;
            const int pollMs = 300;
            const int maxTimeoutMs = 60000;
            int elapsed = 0;
            bool interacted = false;
            int jumpAttempts = 0;

            await System.Threading.Tasks.Task.Delay(600);
            elapsed += 600;

            while (elapsed < maxTimeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(pollMs);
                elapsed += pollMs;

                var (ringDist, centerDist, pathRunning, pathfinding) = await Plugin.Framework.Run(() =>
                {
                    var lp = Plugin.ObjectTable.LocalPlayer;
                    var tgt = lp?.TargetObject;
                    if (lp == null || tgt == null) return (float.MinValue, -1f, false, false);
                    var pp = lp.Position;
                    var tp = tgt.Position;
                    var dx = tp.X - pp.X; var dy = tp.Y - pp.Y; var dz = tp.Z - pp.Z;
                    var cd = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    var rd = cd - lp.HitboxRadius - tgt.HitboxRadius;
                    return (rd, cd, plugin.IpcClient.VnavPathIsRunning(), plugin.IpcClient.VnavSimpleMovePathfindInProgress());
                });

                if (ringDist == float.MinValue) { SetDebugResult("Lost target, aborting"); break; }

                if (ringDist <= 0)
                {
                    SetDebugResult($"Overlapping {targetName}: ring={ringDist:F1}y, interacting");
                    await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
                    plugin.IpcClient.VnavStop();
                    Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: overlapping interact with {targetName} (ring={ringDist:F1}y)");
                    interacted = true;
                    break;
                }

                var pathActive = pathRunning || pathfinding;

                distSamples.Add(ringDist);
                if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                var stalled = false;
                if (distSamples.Count >= maxSamples)
                {
                    float maxD = distSamples[0], minD = distSamples[0];
                    foreach (var sample in distSamples) { if (sample > maxD) maxD = sample; if (sample < minD) minD = sample; }
                    stalled = (maxD - minD) < stallThreshold;
                }

                if (ringDist <= interactRange)
                {
                    SetDebugResult($"In range of {targetName}: ring={ringDist:F1}y, interacting");
                    await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
                    plugin.IpcClient.VnavStop();
                    Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: interacted with {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                    interacted = true;
                    break;
                }
                else if (stalled && ringDist < 20.0f)
                {
                    SetDebugResult($"Stalled near {targetName}: ring={ringDist:F1}y, jumping to unstuck");
                    Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: stalled at ring={ringDist:F1}y, jump attempt {jumpAttempts + 1}");
                    if (jumpAttempts < 5)
                    {
                        KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
                        jumpAttempts++;
                        distSamples.Clear();
                        await System.Threading.Tasks.Task.Delay(800);
                        elapsed += 800;
                    }
                }
                else if (!pathActive)
                {
                    SetDebugResult($"Path ended at ring={ringDist:F1}y, vnav routing done");
                    Plugin.Log.Warning($"[XASlave] PathToTargetThenInteract: path ended for {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                    if (ringDist <= interactRange * 3)
                    {
                        await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
                        SetDebugResult($"Path ended, attempted interact at ring={ringDist:F1}y");
                        interacted = true;
                    }
                    break;
                }
                else if (elapsed % 3000 < pollMs)
                {
                    SetDebugResult($"Pathing to {targetName}: ring={ringDist:F1}y");
                }
            }

            if (!interacted && elapsed >= maxTimeoutMs)
            {
                plugin.IpcClient.VnavStop();
                SetDebugResult($"PathToTargetThenInteract timeout (60s) for {targetName}");
            }
        });
    }

    private void RunDebugPathSmartThenInteract()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        var target = local?.TargetObject;
        if (target == null)
        {
            SetDebugResult("No target selected");
            return;
        }

        if (local == null || !plugin.IpcClient.VnavIsReady())
        {
            SetDebugResult("vnavmesh not ready");
            return;
        }

        var targetPos = target.Position;
        var targetName = target.Name.ToString();
        var targetHitbox = target.HitboxRadius;
        var playerHitbox = local.HitboxRadius;
        const float stopDist = 0.5f;
        const float interactRange = 1.0f;

        var lp0 = local.Position;
        var dx0 = targetPos.X - lp0.X; var dy0 = targetPos.Y - lp0.Y; var dz0 = targetPos.Z - lp0.Z;
        var ringDist0 = (float)Math.Sqrt(dx0 * dx0 + dy0 * dy0 + dz0 * dz0) - playerHitbox - targetHitbox;
        var canFly = HasFlightUnlocked();
        var shouldMount = ringDist0 > 20.0f;

        SetDebugResult($"PathSmart to {targetName}: ring={ringDist0:F0}y, fly={canFly}, mount={shouldMount}");
        Plugin.Log.Information($"[XASlave] PathSmartThenInteract: {targetName} ring={ringDist0:F1}y fly={canFly} mount={shouldMount} stop={stopDist:F1}");

        System.Threading.Tasks.Task.Run(async () =>
        {
            if (shouldMount)
                await Plugin.Framework.Run(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));

            var fly = canFly && shouldMount;
            var pathOk = await Plugin.Framework.Run(() =>
                plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, fly, stopDist));
            if (!pathOk) { SetDebugResult("Pathfind failed"); return; }

            var distSamples = new System.Collections.Generic.List<float>();
            const int maxSamples = 7;
            const float stallThreshold = 0.3f;
            const int pollMs = 100;
            const int maxTimeoutMs = 60000;
            int elapsed = 0;
            bool interacted = false;
            int jumpAttempts = 0;

            await System.Threading.Tasks.Task.Delay(200);
            elapsed += 200;

            while (elapsed < maxTimeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(pollMs);
                elapsed += pollMs;

                var (rd, pathRunning, pathfinding) = await Plugin.Framework.Run(() =>
                {
                    var lp2 = Plugin.ObjectTable.LocalPlayer;
                    var tgt = lp2?.TargetObject;
                    if (lp2 == null || tgt == null) return (float.MinValue, false, false);
                    var pp = lp2.Position; var tp = tgt.Position;
                    var ddx = tp.X - pp.X; var ddy = tp.Y - pp.Y; var ddz = tp.Z - pp.Z;
                    var c = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                    var r = c - lp2.HitboxRadius - tgt.HitboxRadius;
                    return (r, plugin.IpcClient.VnavPathIsRunning(), plugin.IpcClient.VnavSimpleMovePathfindInProgress());
                });

                if (rd == float.MinValue) { SetDebugResult("Lost target, aborting"); break; }

                if (rd <= 0)
                {
                    plugin.IpcClient.VnavStop();
                    SetDebugResult($"Overlapping {targetName}: ring={rd:F1}y, interacting");
                    await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
                    Plugin.Log.Information($"[XASlave] PathSmartThenInteract: overlapping interact with {targetName} (ring={rd:F1}y)");
                    interacted = true;
                    break;
                }

                var pathActive = pathRunning || pathfinding;

                distSamples.Add(rd);
                if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                var stalled = false;
                if (distSamples.Count >= maxSamples)
                {
                    float maxD = distSamples[0], minD = distSamples[0];
                    foreach (var sample in distSamples) { if (sample > maxD) maxD = sample; if (sample < minD) minD = sample; }
                    stalled = (maxD - minD) < stallThreshold;
                }

                if (rd <= interactRange)
                {
                    plugin.IpcClient.VnavStop();
                    var isMounted = await Plugin.Framework.Run(() =>
                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                    if (isMounted)
                    {
                        SetDebugResult($"In range of {targetName}: ring={rd:F1}y, dismounting...");
                        await Plugin.Framework.Run(() => ChatHelper.SendMessage("/mount"));
                        for (int w = 0; w < 30; w++)
                        {
                            await System.Threading.Tasks.Task.Delay(100);
                            isMounted = await Plugin.Framework.Run(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (!isMounted) break;
                        }
                    }

                    await System.Threading.Tasks.Task.Delay(2000);

                    for (int sw = 0; sw < 15; sw++)
                    {
                        await System.Threading.Tasks.Task.Delay(100);
                        var charReady = await Plugin.Framework.Run(() =>
                            MonthlyReloggerTask.IsPlayerAvailable() &&
                            !Plugin.Condition[ConditionFlag.Casting]);
                        if (charReady) break;
                    }

                    var postDismountRd = await Plugin.Framework.Run(() =>
                    {
                        var lp3 = Plugin.ObjectTable.LocalPlayer;
                        var tgt3 = lp3?.TargetObject;
                        if (lp3 == null || tgt3 == null) return float.MinValue;
                        var pp3 = lp3.Position; var tp3 = tgt3.Position;
                        var ddx3 = tp3.X - pp3.X; var ddy3 = tp3.Y - pp3.Y; var ddz3 = tp3.Z - pp3.Z;
                        return (float)Math.Sqrt(ddx3 * ddx3 + ddy3 * ddy3 + ddz3 * ddz3) - lp3.HitboxRadius - tgt3.HitboxRadius;
                    });

                    if (postDismountRd != float.MinValue && postDismountRd > interactRange)
                    {
                        SetDebugResult($"Post-dismount too far: ring={postDismountRd:F1}y, re-pathing on foot");
                        Plugin.Log.Information($"[XASlave] PathSmartThenInteract: post-dismount ring={postDismountRd:F1}y > {interactRange:F1}y, re-pathing");
                        await Plugin.Framework.Run(() =>
                            plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                        for (int rp = 0; rp < 100; rp++)
                        {
                            await System.Threading.Tasks.Task.Delay(200);
                            var (rpRd, rpIdle) = await Plugin.Framework.Run(() =>
                            {
                                var lp4 = Plugin.ObjectTable.LocalPlayer;
                                var tgt4 = lp4?.TargetObject;
                                if (lp4 == null || tgt4 == null) return (float.MinValue, true);
                                var pp4 = lp4.Position; var tp4 = tgt4.Position;
                                var ddx4 = tp4.X - pp4.X; var ddy4 = tp4.Y - pp4.Y; var ddz4 = tp4.Z - pp4.Z;
                                var r4 = (float)Math.Sqrt(ddx4 * ddx4 + ddy4 * ddy4 + ddz4 * ddz4) - lp4.HitboxRadius - tgt4.HitboxRadius;
                                var idle = !plugin.IpcClient.VnavPathIsRunning() && !plugin.IpcClient.VnavSimpleMovePathfindInProgress();
                                return (r4, idle);
                            });
                            if (rpRd <= interactRange || rpRd <= 0 || rpIdle) break;
                        }
                    }

                    SetDebugResult($"In range of {targetName}: ring={postDismountRd:F1}y, interacting");
                    await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
                    plugin.IpcClient.VnavStop();
                    Plugin.Log.Information($"[XASlave] PathSmartThenInteract: interacted with {targetName} (ring={postDismountRd:F1}y)");
                    interacted = true;
                    break;
                }
                else if (stalled && rd < 20.0f)
                {
                    var isMounted = await Plugin.Framework.Run(() =>
                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                    if (isMounted)
                    {
                        plugin.IpcClient.VnavStop();
                        await Plugin.Framework.Run(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                        for (int w = 0; w < 50; w++)
                        {
                            await System.Threading.Tasks.Task.Delay(100);
                            isMounted = await Plugin.Framework.Run(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (!isMounted) break;
                        }
                        await Plugin.Framework.Run(() =>
                            plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                        distSamples.Clear();
                        await System.Threading.Tasks.Task.Delay(600);
                        elapsed += 600;
                    }
                    else
                    {
                        SetDebugResult($"Stalled near {targetName}: ring={rd:F1}y, jumping");
                        if (jumpAttempts < 5)
                        {
                            KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
                            jumpAttempts++;
                            distSamples.Clear();
                            await System.Threading.Tasks.Task.Delay(800);
                            elapsed += 800;
                        }
                    }
                }
                else if (!pathActive)
                {
                    SetDebugResult($"Path ended at ring={rd:F1}y, routing done");
                    Plugin.Log.Warning($"[XASlave] PathSmartThenInteract: path ended for {targetName} (ring={rd:F1}y)");
                    if (rd <= interactRange * 3)
                    {
                        var isMounted = await Plugin.Framework.Run(() =>
                            Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                        if (isMounted)
                        {
                            await Plugin.Framework.Run(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                            for (int w = 0; w < 50; w++)
                            {
                                await System.Threading.Tasks.Task.Delay(100);
                                isMounted = await Plugin.Framework.Run(() =>
                                    Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                if (!isMounted) break;
                            }
                        }
                        await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
                        SetDebugResult($"Path ended, attempted interact at ring={rd:F1}y");
                        interacted = true;
                    }
                    break;
                }
                else if (elapsed % 3000 < pollMs)
                {
                    SetDebugResult($"PathSmart to {targetName}: ring={rd:F1}y");
                }
            }

            if (!interacted && elapsed >= maxTimeoutMs)
            {
                plugin.IpcClient.VnavStop();
                SetDebugResult($"PathSmartThenInteract timeout (60s) for {targetName}");
            }
        });
    }

    private void RunDebugLeaveDuty()
    {
        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
        if (!inDuty)
        {
            SetDebugResult("Not in a duty, nothing to leave.");
            return;
        }

        SetDebugResult("In duty, attempting to leave...");
        System.Threading.Tasks.Task.Run(async () =>
        {
            var inCombat = await Plugin.Framework.Run(() => Plugin.Condition[ConditionFlag.InCombat]);
            if (inCombat)
            {
                SetDebugResult("In combat, waiting up to 30s for combat to end...");
                for (int w = 0; w < 60; w++)
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    inCombat = await Plugin.Framework.Run(() => Plugin.Condition[ConditionFlag.InCombat]);
                    if (!inCombat) break;
                }
                if (inCombat)
                {
                    SetDebugResult("Still in combat after 30s, cannot leave duty.");
                    return;
                }
                SetDebugResult("Combat ended, leaving duty...");
            }

            await Plugin.Framework.Run(() => KeyInputHelper.PressKey(0x55));
            await System.Threading.Tasks.Task.Delay(1000);

            var leaveClicked = await Plugin.Framework.Run(() =>
                AddonHelper.ClickAddonButton("ContentsFinderMenu", 43));

            if (leaveClicked)
            {
                SetDebugResult("Leave Duty: clicked Leave button, waiting for confirmation...");
                await System.Threading.Tasks.Task.Delay(500);

                var yesClicked = await Plugin.Framework.Run(() => AddonHelper.ClickYesNo(true));
                SetDebugResult(yesClicked
                    ? "Leave Duty: confirmed Yes, leaving instance."
                    : "Leave Duty: Leave clicked but SelectYesno not visible, may need manual confirm.");
            }
            else
            {
                SetDebugResult("Leave Duty: ContentsFinderMenu not visible or Leave button not found.");
            }
        });
    }

    private void RunDebugLeaveDutyQuick()
    {
        if (!Plugin.Condition[ConditionFlag.BoundByDuty])
        {
            SetDebugResult("Leave Duty Quick: not in a duty, nothing to leave.");
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            SetDebugResult("Leave Duty Quick blocked: combat is active.");
            return;
        }

        if (TryGetDebugLeaveDutyQuickBlocker(out var blocker))
        {
            SetDebugResult($"Leave Duty Quick blocked: {blocker}.");
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref debugLeaveDutyQuickRunning, 1, 0) != 0)
        {
            SetDebugResult("Leave Duty Quick is already running.");
            return;
        }

        SetDebugResult("Leave Duty Quick: opening the game-owned duty menu without pressing U...");
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var existingPromptState = await Plugin.Framework.Run(() =>
                {
                    if (!AddonHelper.IsAddonVisible("SelectYesno"))
                        return 0;

                    return AddonHelper.IsAddonReady("SelectYesno") &&
                           AddonHelper.IsLeaveDutyConfirmationPrompt()
                        ? 1
                        : -1;
                });
                if (existingPromptState < 0)
                {
                    SetDebugResult("Leave Duty Quick stopped: an unrelated or unreadable SelectYesno is already open.");
                    return;
                }

                if (existingPromptState > 0)
                {
                    var confirmed = await Plugin.Framework.Run(() => AddonHelper.ClickYesNo(true));
                    SetDebugResult(confirmed
                        ? "Leave Duty Quick: confirmed the existing validated leave-duty prompt; waiting for zone-out."
                        : "Leave Duty Quick: validated the existing leave-duty prompt, but the Yes callback failed.");
                    return;
                }

                var callbacksSent = false;
                for (var attempt = 1; attempt <= DebugLeaveDutyQuickMenuAttempts; attempt++)
                {
                    if (!await Plugin.Framework.Run(() => Plugin.Condition[ConditionFlag.BoundByDuty]))
                    {
                        SetDebugResult("Leave Duty Quick: duty exit already started.");
                        return;
                    }

                    var menuReady = await Plugin.Framework.Run(() =>
                        AddonHelper.IsAddonReady("ContentsFinderMenu"));
                    if (!menuReady)
                    {
                        var agentShown = await Plugin.Framework.Run(() =>
                            AddonHelper.ShowAgent(AgentId.ContentsFinderMenu));
                        if (!agentShown && attempt == DebugLeaveDutyQuickMenuAttempts)
                        {
                            SetDebugResult("Leave Duty Quick: ContentsFinderMenu agent was unavailable.");
                            return;
                        }

                        await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds);
                        continue;
                    }

                    callbacksSent = await Plugin.Framework.Run(
                        AddonHelper.TryRequestLeaveDutyFromContentsFinderMenu);
                    if (callbacksSent)
                        break;

                    await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds);
                }

                if (!callbacksSent)
                {
                    SetDebugResult("Leave Duty Quick: the duty menu opened, but its Leave Duty callbacks failed.");
                    return;
                }

                SetDebugResult("Leave Duty Quick: leave callbacks sent; waiting for the validated confirmation...");
                for (var attempt = 1; attempt <= DebugLeaveDutyQuickPromptAttempts; attempt++)
                {
                    if (!await Plugin.Framework.Run(() => Plugin.Condition[ConditionFlag.BoundByDuty]))
                    {
                        SetDebugResult("Leave Duty Quick: duty exit started.");
                        return;
                    }

                    var selectYesnoVisible = await Plugin.Framework.Run(() =>
                        AddonHelper.IsAddonVisible("SelectYesno"));
                    if (selectYesnoVisible)
                    {
                        var selectYesnoReady = await Plugin.Framework.Run(() =>
                            AddonHelper.IsAddonReady("SelectYesno"));
                        if (!selectYesnoReady)
                        {
                            await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds);
                            continue;
                        }

                        var isLeavePrompt = await Plugin.Framework.Run(
                            AddonHelper.IsLeaveDutyConfirmationPrompt);
                        if (!isLeavePrompt)
                        {
                            SetDebugResult("Leave Duty Quick stopped: SelectYesno is not a readable leave-duty prompt.");
                            return;
                        }

                        var confirmed = await Plugin.Framework.Run(() => AddonHelper.ClickYesNo(true));
                        SetDebugResult(confirmed
                            ? "Leave Duty Quick: confirmed the validated leave-duty prompt; waiting for zone-out."
                            : "Leave Duty Quick: validated the leave-duty prompt, but the Yes callback failed.");
                        return;
                    }

                    if (attempt == DebugLeaveDutyQuickPromptAttempts / 2)
                    {
                        await Plugin.Framework.Run(() =>
                        {
                            if (!AddonHelper.IsAddonReady("ContentsFinderMenu"))
                                return;

                            AddonHelper.TryRequestLeaveDutyFromContentsFinderMenu();
                        });
                    }

                    await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds);
                }

                SetDebugResult("Leave Duty Quick: no leave-duty confirmation appeared within 3 seconds.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[XASlave] Leave Duty Quick failed.");
                SetDebugResult($"Leave Duty Quick error: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref debugLeaveDutyQuickRunning, 0);
            }
        });
    }

    private static bool TryGetDebugLeaveDutyQuickBlocker(out string blocker)
    {
        if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            blocker = "area transition is active";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            blocker = "cutscene is active";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39])
        {
            blocker = "occupied transition is active";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private void RunDebugRecommendedGear()
    {
        SetDebugResult("Recommended Gear: starting Step1/2/3/close sequence.");
        System.Threading.Tasks.Task.Run(async () =>
        {
            await Plugin.Framework.Run(() => RunDebugRecommendedGearStep1());
            if (!await WaitForDebugAddonVisibleAsync("Character", 3000))
            {
                SetDebugResult("Recommended Gear: Character addon did not open.");
                return;
            }

            var recommendClicked = await Plugin.Framework.Run(() => RunDebugRecommendedGearStep2());
            if (!recommendClicked)
                return;

            if (!await WaitForDebugAddonVisibleAsync("RecommendEquip", 3000))
            {
                SetDebugResult("Recommended Gear: RecommendEquip addon did not open.");
                return;
            }

            var equipClicked = await Plugin.Framework.Run(() => RunDebugRecommendedGearStep3());
            await System.Threading.Tasks.Task.Delay(500);
            await Plugin.Framework.Run(() => RunDebugRecommendedGearClose());
            if (equipClicked)
                SetDebugResult("Recommended Gear: Step1/2/3/close complete.");
        });
    }

    private void RunDebugRecommendedGearStep1()
    {
        ChatHelper.SendMessage("/character");
        SetDebugResult("Opened Character window, next: Step2 to fire callback");
    }

    private bool RunDebugRecommendedGearStep2()
    {
        var ok = AddonHelper.ClickAddonButton("Character", 74);
        SetDebugResult(ok
            ? "Clicked Character NodeList[74] (Button #12) -> RecommendEquip should open"
            : "Character addon not visible, open it first with Step1");
        return ok;
    }

    private bool RunDebugRecommendedGearStep3()
    {
        var ok = AddonHelper.ClickAddonButton("RecommendEquip", 3);
        SetDebugResult(ok
            ? "Clicked RecommendEquip NodeList[3] (Button #11) -> gear equipped"
            : "RecommendEquip addon not visible, run Step2 first");
        return ok;
    }

    private void RunDebugRecommendedGearClose()
    {
        AddonHelper.CloseAddon("RecommendEquip");
        AddonHelper.CloseAddon("Character");
        SetDebugResult("Closed RecommendEquip + Character addons");
    }
}
