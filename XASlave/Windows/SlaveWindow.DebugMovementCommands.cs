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

        var normalizedCommand = normalized.ToLowerInvariant();
        var isExclusiveCommand = normalizedCommand is
            "movingcheatersmart" or
            "movingcheaterfly" or
            "movingcheaterwalk" or
            "interact" or
            "leaveduty" or
            "recommendedgear" or
            "pathtotargetinteract" or
            "pathsmartinteract";
        if (isExclusiveCommand && System.Threading.Volatile.Read(ref exclusiveWorkerRunning) != 0)
        {
            message = $"'{normalizedCommand}' rejected - another movement command is already running.";
            SetDebugResult(message);
            return false;
        }

        switch (normalizedCommand)
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
        RunWorker("Interact", async token =>
        {
            token.ThrowIfCancellationRequested();
            var ok = await Plugin.RunOnGameThread(AddonHelper.InteractWithTarget);
            SetDebugResult(ok ? "InteractWithTarget: OK" : "No target or interaction failed");
        }, exclusive: true);
    }

    private async System.Threading.Tasks.Task<bool> WaitForDebugAddonVisibleAsync(
        string addonName,
        int timeoutMs,
        System.Threading.CancellationToken token,
        int pollMs = 100)
    {
        var elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs, token);
            elapsed += pollMs;

            var visible = await Plugin.RunOnGameThread(() => AddonHelper.IsAddonVisible(addonName));
            if (visible)
                return true;
        }

        return false;
    }

    private static bool IsMounted()
        => Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];

    private static unsafe bool HasFlightUnlocked()
    {
        try
        {
            var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (playerState == null)
            {
                Plugin.Log.Warning("[XASlave] HasFlightUnlocked: PlayerState.Instance() returned null");
                return false;
            }

            var territory = Plugin.ClientState.TerritoryType;
            var canFly = playerState->CanFly;
            Plugin.Log.Debug($"[XASlave] HasFlightUnlocked: territory={territory}, CanFly={canFly}");
            return canFly;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] HasFlightUnlocked error: {ex.Message}");
            return false;
        }
    }

    private void RunDebugVnavStop()
    {
        RunWorker("Stop Movement", async token =>
        {
            token.ThrowIfCancellationRequested();
            await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
            SetDebugResult("Sent: vnavmesh.Path.Stop()");
        });
    }

    private void RunDebugMovingCheaterSmart()
    {
        RunWorker("MovingCheater Smart", async token =>
        {
            token.ThrowIfCancellationRequested();
            await Plugin.RunOnGameThread(() =>
            {
                if (!TryStartMovingCheaterMountStep(out var alreadyMounted))
                    return;

                var canFly = HasFlightUnlocked();
                ChatHelper.SendMessage(canFly ? "/vnav flyflag" : "/vnav moveflag");
                SetDebugResult(canFly
                    ? alreadyMounted
                        ? "Smart: Already mounted + /vnav flyflag (flying unlocked in zone)"
                        : "Smart: Mount + /vnav flyflag (flying unlocked in zone)"
                    : alreadyMounted
                        ? "Smart: Already mounted + /vnav moveflag (flying NOT unlocked, ground pathfind)"
                        : "Smart: Mount + /vnav moveflag (flying NOT unlocked, ground pathfind)");
            });
        }, exclusive: true);
    }

    private void RunDebugMovingCheaterFly()
    {
        RunWorker("MovingCheater Fly", async token =>
        {
            token.ThrowIfCancellationRequested();
            await Plugin.RunOnGameThread(() =>
            {
                if (!TryStartMovingCheaterMountStep(out var alreadyMounted))
                    return;

                var canFly = HasFlightUnlocked();
                ChatHelper.SendMessage(canFly ? "/vnav flyflag" : "/vnav moveflag");
                SetDebugResult(canFly
                    ? alreadyMounted
                        ? "Sent: already mounted + /vnav flyflag (flying unlocked)"
                        : "Sent: Mount + /vnav flyflag (flying unlocked)"
                    : alreadyMounted
                        ? "Sent: already mounted + /vnav moveflag (flight NOT unlocked, fallback to ground)"
                        : "Sent: Mount + /vnav moveflag (flight NOT unlocked, fallback to ground)");
            });
        }, exclusive: true);
    }

    private void RunDebugMovingCheaterWalk()
    {
        RunWorker("MovingCheater Walk", async token =>
        {
            token.ThrowIfCancellationRequested();
            await Plugin.RunOnGameThread(() =>
            {
                if (!TryStartMovingCheaterMountStep(out var alreadyMounted))
                    return;

                ChatHelper.SendMessage("/vnav moveflag");
                SetDebugResult(alreadyMounted
                    ? "Sent: already mounted + /vnav moveflag (force ground)"
                    : "Sent: Mount + /vnav moveflag (force ground)");
            });
        }, exclusive: true);
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
        if (RejectIfExclusiveWorkerBusy("PathToTargetThenInteract"))
            return;

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
        RunWorker("PathToTargetThenInteract", async token =>
        {
            var distSamples = new System.Collections.Generic.List<float>();
            const int maxSamples = 7;
            const float stallThreshold = 0.3f;
            const int pollMs = 300;
            const int maxTimeoutMs = 60000;
            int elapsed = 0;
            bool interacted = false;
            int jumpAttempts = 0;

            await System.Threading.Tasks.Task.Delay(600, token);
            elapsed += 600;

            while (elapsed < maxTimeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(pollMs, token);
                elapsed += pollMs;

                var (ringDist, centerDist, pathRunning, pathfinding) = await Plugin.RunOnGameThread(() =>
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
                    await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
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
                    await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
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
                        await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE));
                        jumpAttempts++;
                        distSamples.Clear();
                        await System.Threading.Tasks.Task.Delay(800, token);
                        elapsed += 800;
                    }
                }
                else if (!pathActive)
                {
                    SetDebugResult($"Path ended at ring={ringDist:F1}y, vnav routing done");
                    Plugin.Log.Warning($"[XASlave] PathToTargetThenInteract: path ended for {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                    if (ringDist <= interactRange * 3)
                    {
                        await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
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
                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                SetDebugResult($"PathToTargetThenInteract timeout (60s) for {targetName}");
            }
        }, exclusive: true);
    }

    private void RunDebugPathSmartThenInteract()
    {
        if (RejectIfExclusiveWorkerBusy("PathSmartThenInteract"))
            return;

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

        RunWorker("PathSmartThenInteract", async token =>
        {
            if (shouldMount)
                await Plugin.RunOnGameThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));

            var fly = canFly && shouldMount;
            var pathOk = await Plugin.RunOnGameThread(() =>
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

            await System.Threading.Tasks.Task.Delay(200, token);
            elapsed += 200;

            while (elapsed < maxTimeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(pollMs, token);
                elapsed += pollMs;

                var (rd, pathRunning, pathfinding) = await Plugin.RunOnGameThread(() =>
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
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    SetDebugResult($"Overlapping {targetName}: ring={rd:F1}y, interacting");
                    await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
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
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    var isMounted = await Plugin.RunOnGameThread(() =>
                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                    if (isMounted)
                    {
                        SetDebugResult($"In range of {targetName}: ring={rd:F1}y, dismounting...");
                        await Plugin.RunOnGameThread(() => ChatHelper.SendMessage("/mount"));
                        for (int w = 0; w < 30; w++)
                        {
                            await System.Threading.Tasks.Task.Delay(100, token);
                            isMounted = await Plugin.RunOnGameThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (!isMounted) break;
                        }
                    }

                    await System.Threading.Tasks.Task.Delay(2000, token);

                    for (int sw = 0; sw < 15; sw++)
                    {
                        await System.Threading.Tasks.Task.Delay(100, token);
                        var charReady = await Plugin.RunOnGameThread(() =>
                            MonthlyReloggerTask.IsPlayerAvailable() &&
                            !Plugin.Condition[ConditionFlag.Casting]);
                        if (charReady) break;
                    }

                    var postDismountRd = await Plugin.RunOnGameThread(() =>
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
                        await Plugin.RunOnGameThread(() =>
                            plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                        for (int rp = 0; rp < 100; rp++)
                        {
                            await System.Threading.Tasks.Task.Delay(200, token);
                            var (rpRd, rpIdle) = await Plugin.RunOnGameThread(() =>
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
                    await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    Plugin.Log.Information($"[XASlave] PathSmartThenInteract: interacted with {targetName} (ring={postDismountRd:F1}y)");
                    interacted = true;
                    break;
                }
                else if (stalled && rd < 20.0f)
                {
                    var isMounted = await Plugin.RunOnGameThread(() =>
                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                    if (isMounted)
                    {
                        await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                        await Plugin.RunOnGameThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                        for (int w = 0; w < 50; w++)
                        {
                            await System.Threading.Tasks.Task.Delay(100, token);
                            isMounted = await Plugin.RunOnGameThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (!isMounted) break;
                        }
                        await Plugin.RunOnGameThread(() =>
                            plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                        distSamples.Clear();
                        await System.Threading.Tasks.Task.Delay(600, token);
                        elapsed += 600;
                    }
                    else
                    {
                        SetDebugResult($"Stalled near {targetName}: ring={rd:F1}y, jumping");
                        if (jumpAttempts < 5)
                        {
                            await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE));
                            jumpAttempts++;
                            distSamples.Clear();
                            await System.Threading.Tasks.Task.Delay(800, token);
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
                        var isMounted = await Plugin.RunOnGameThread(() =>
                            Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                        if (isMounted)
                        {
                            await Plugin.RunOnGameThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                            for (int w = 0; w < 50; w++)
                            {
                                await System.Threading.Tasks.Task.Delay(100, token);
                                isMounted = await Plugin.RunOnGameThread(() =>
                                    Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                if (!isMounted) break;
                            }
                        }
                        await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
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
                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                SetDebugResult($"PathSmartThenInteract timeout (60s) for {targetName}");
            }
        }, exclusive: true);
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
        RunWorker("Leave Duty", async token =>
        {
            var inCombat = await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.InCombat]);
            if (inCombat)
            {
                SetDebugResult("In combat, waiting up to 30s for combat to end...");
                for (int w = 0; w < 60; w++)
                {
                    await System.Threading.Tasks.Task.Delay(500, token);
                    inCombat = await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.InCombat]);
                    if (!inCombat) break;
                }
                if (inCombat)
                {
                    SetDebugResult("Still in combat after 30s, cannot leave duty.");
                    return;
                }
                SetDebugResult("Combat ended, leaving duty...");
            }

            await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(0x55));
            await System.Threading.Tasks.Task.Delay(1000, token);

            var leaveClicked = await Plugin.RunOnGameThread(() =>
                AddonHelper.ClickAddonButton("ContentsFinderMenu", AddonNodes.ContentsFinderMenuLeave));

            if (leaveClicked)
            {
                SetDebugResult("Leave Duty: clicked Leave button, waiting for confirmation...");
                await System.Threading.Tasks.Task.Delay(500, token);

                var yesClicked = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                SetDebugResult(yesClicked
                    ? "Leave Duty: confirmed Yes, leaving instance."
                    : "Leave Duty: Leave clicked but SelectYesno not visible, may need manual confirm.");
            }
            else
            {
                SetDebugResult("Leave Duty: ContentsFinderMenu not visible or Leave button not found.");
            }
        }, exclusive: true);
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

        SetDebugResult("Leave Duty Quick: opening the game-owned duty menu without pressing U...");
        RunWorker("Leave Duty Quick", async token =>
        {
            try
            {
                var existingPromptState = await Plugin.RunOnGameThread(() =>
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
                    var confirmed = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                    SetDebugResult(confirmed
                        ? "Leave Duty Quick: confirmed the existing validated leave-duty prompt; waiting for zone-out."
                        : "Leave Duty Quick: validated the existing leave-duty prompt, but the Yes callback failed.");
                    return;
                }

                var callbacksSent = false;
                for (var attempt = 1; attempt <= DebugLeaveDutyQuickMenuAttempts; attempt++)
                {
                    if (!await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.BoundByDuty]))
                    {
                        SetDebugResult("Leave Duty Quick: duty exit already started.");
                        return;
                    }

                    var menuReady = await Plugin.RunOnGameThread(() =>
                        AddonHelper.IsAddonReady("ContentsFinderMenu"));
                    if (!menuReady)
                    {
                        var agentShown = await Plugin.RunOnGameThread(() =>
                            AddonHelper.ShowAgent(AgentId.ContentsFinderMenu));
                        if (!agentShown && attempt == DebugLeaveDutyQuickMenuAttempts)
                        {
                            SetDebugResult("Leave Duty Quick: ContentsFinderMenu agent was unavailable.");
                            return;
                        }

                        await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds, token);
                        continue;
                    }

                    callbacksSent = await Plugin.RunOnGameThread(
                        AddonHelper.TryRequestLeaveDutyFromContentsFinderMenu);
                    if (callbacksSent)
                        break;

                    await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds, token);
                }

                if (!callbacksSent)
                {
                    SetDebugResult("Leave Duty Quick: the duty menu opened, but its Leave Duty callbacks failed.");
                    return;
                }

                SetDebugResult("Leave Duty Quick: leave callbacks sent; waiting for the validated confirmation...");
                for (var attempt = 1; attempt <= DebugLeaveDutyQuickPromptAttempts; attempt++)
                {
                    if (!await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.BoundByDuty]))
                    {
                        SetDebugResult("Leave Duty Quick: duty exit started.");
                        return;
                    }

                    var selectYesnoVisible = await Plugin.RunOnGameThread(() =>
                        AddonHelper.IsAddonVisible("SelectYesno"));
                    if (selectYesnoVisible)
                    {
                        var selectYesnoReady = await Plugin.RunOnGameThread(() =>
                            AddonHelper.IsAddonReady("SelectYesno"));
                        if (!selectYesnoReady)
                        {
                            await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds, token);
                            continue;
                        }

                        var isLeavePrompt = await Plugin.RunOnGameThread(
                            AddonHelper.IsLeaveDutyConfirmationPrompt);
                        if (!isLeavePrompt)
                        {
                            SetDebugResult("Leave Duty Quick stopped: SelectYesno is not a readable leave-duty prompt.");
                            return;
                        }

                        var confirmed = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                        SetDebugResult(confirmed
                            ? "Leave Duty Quick: confirmed the validated leave-duty prompt; waiting for zone-out."
                            : "Leave Duty Quick: validated the leave-duty prompt, but the Yes callback failed.");
                        return;
                    }

                    if (attempt == DebugLeaveDutyQuickPromptAttempts / 2)
                    {
                        await Plugin.RunOnGameThread(() =>
                        {
                            if (!AddonHelper.IsAddonReady("ContentsFinderMenu"))
                                return;

                            AddonHelper.TryRequestLeaveDutyFromContentsFinderMenu();
                        });
                    }

                    await System.Threading.Tasks.Task.Delay(DebugLeaveDutyQuickRetryDelayMilliseconds, token);
                }

                SetDebugResult("Leave Duty Quick: no leave-duty confirmation appeared within 3 seconds.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[XASlave] Leave Duty Quick failed.");
                SetDebugResult($"Leave Duty Quick error: {ex.Message}");
            }
        }, exclusive: true);
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
        RunWorker("Recommended Gear", async token =>
        {
            await Plugin.RunOnGameThread(() => RunDebugRecommendedGearStep1());
            if (!await WaitForDebugAddonVisibleAsync("Character", 3000, token))
            {
                SetDebugResult("Recommended Gear: Character addon did not open.");
                return;
            }

            var recommendClicked = await Plugin.RunOnGameThread(() => RunDebugRecommendedGearStep2());
            if (!recommendClicked)
                return;

            if (!await WaitForDebugAddonVisibleAsync("RecommendEquip", 3000, token))
            {
                SetDebugResult("Recommended Gear: RecommendEquip addon did not open.");
                return;
            }

            var equipClicked = await Plugin.RunOnGameThread(() => RunDebugRecommendedGearStep3());
            await System.Threading.Tasks.Task.Delay(500, token);
            await Plugin.RunOnGameThread(() => RunDebugRecommendedGearClose());
            if (equipClicked)
                SetDebugResult("Recommended Gear: Step1/2/3/close complete.");
        }, exclusive: true);
    }

    private void RunDebugRecommendedGearStep1()
    {
        ChatHelper.SendMessage("/character");
        SetDebugResult("Opened Character window, next: Step2 to fire callback");
    }

    private bool RunDebugRecommendedGearStep2()
    {
        var addonReady = AddonHelper.IsAddonReady("Character");
        var ok = addonReady && AddonHelper.ClickAddonButton("Character", AddonNodes.CharacterRecommendEquip);
        SetDebugResult(ok
            ? $"Clicked Character NodeList[{AddonNodes.CharacterRecommendEquip}] -> RecommendEquip should open"
            : addonReady
                ? $"Character is ready, but recommend node {AddonNodes.CharacterRecommendEquip} is unavailable; verify the current addon layout."
                : "Character addon is not ready; open it first with Step1.");
        return ok;
    }

    private bool RunDebugRecommendedGearStep3()
    {
        var addonReady = AddonHelper.IsAddonReady("RecommendEquip");
        var ok = addonReady && AddonHelper.ClickAddonButton("RecommendEquip", AddonNodes.RecommendEquipConfirm);
        SetDebugResult(ok
            ? $"Clicked RecommendEquip NodeList[{AddonNodes.RecommendEquipConfirm}] -> gear equipped"
            : addonReady
                ? $"RecommendEquip is ready, but confirm node {AddonNodes.RecommendEquipConfirm} is unavailable; verify the current addon layout."
                : "RecommendEquip addon is not ready; run Step2 first.");
        return ok;
    }

    private void RunDebugRecommendedGearClose()
    {
        AddonHelper.CloseAddon("RecommendEquip");
        AddonHelper.CloseAddon("Character");
        SetDebugResult("Closed RecommendEquip + Character addons");
    }
}
