using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Debug / Test Commands panel — partial class split from SlaveWindow.cs.
/// Contains DrawDebugCommands(), SetDebugResult(), HasFlightUnlocked(), CanMount(), InSanctuary().
/// </summary>
public partial class SlaveWindow
{
    // ───────────────────────────────────────────────
    //  Debug / Test Commands
    //  Test buttons for all xafunc-referenced commands
    //  These functions will be used as templates for future tasks
    // ───────────────────────────────────────────────
    private string debugResult = string.Empty;
    private DateTime debugResultExpiry = DateTime.MinValue;

    private void DrawDebugCommands()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Debug / Test Commands");
        ImGui.TextDisabled("Test individual xafunc commands. These are the building blocks for all tasks.");
        ImGui.Spacing();

        // Status feedback
        if (!string.IsNullOrEmpty(debugResult) && DateTime.UtcNow < debugResultExpiry)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), debugResult);
            ImGui.Spacing();
        }

        // ── Plugin Status (same checker as Monthly Relogger) ──
        DrawPluginStatusChecker();

        ImGui.Separator();
        ImGui.Spacing();

        // ── Scrollable test buttons region ──
        // Top section (title, results, plugin status) stays pinned.
        // Everything below scrolls independently.
        using var scrollChild = Dalamud.Interface.Utility.Raii.ImRaii.Child("DebugScrollRegion", new Vector2(0, 0), false);
        if (!scrollChild.Success) return;

        // ╔══════════════════════════════════════════════╗
        // ║  [Movement Functions]                        ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.TreeNode("Movement Functions"))
        {

        // ══════════════════════════════════════════════
        //  XA Lazy Movements
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("XA Lazy Movements"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Interact"))
        {
            var ok = AddonHelper.InteractWithTarget();
            SetDebugResult(ok ? "InteractWithTarget: OK" : "No target or interaction failed");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: Stop"))
        {
            plugin.IpcClient.VnavStop();
            SetDebugResult("Sent: vnavmesh.Path.Stop()");
        }
        ImGui.SameLine();
        if (ImGui.Button("PathToTarget"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null && plugin.IpcClient.VnavIsReady())
            {
                var targetPos = target.Position;
                var targetName = target.Name.ToString();
                var stopDist = 0.5f;
                var ok = plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist);
                if (ok)
                {
                    SetDebugResult($"Pathing to {targetName} (stop={stopDist:F1}y, no auto-interact)");
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        var distSamples = new System.Collections.Generic.List<float>();
                        const int maxSamples = 7;
                        const float stallThreshold = 0.3f;
                        const float closeEnough = 10.0f;
                        const int pollMs = 300;
                        const int maxTimeoutMs = 60000;
                        int elapsed = 0;
                        int jumpAttempts = 0;

                        await System.Threading.Tasks.Task.Delay(600);
                        elapsed += 600;

                        while (elapsed < maxTimeoutMs)
                        {
                            await System.Threading.Tasks.Task.Delay(pollMs);
                            elapsed += pollMs;

                            var (ringDist, pathActive) = await Plugin.Framework.RunOnFrameworkThread(() =>
                            {
                                var lp = Plugin.ObjectTable.LocalPlayer;
                                var tgt = lp?.TargetObject;
                                if (lp == null || tgt == null) return (float.MinValue, false);
                                var pp = lp.Position; var tp = tgt.Position;
                                var dx = tp.X - pp.X; var dy = tp.Y - pp.Y; var dz = tp.Z - pp.Z;
                                var cd = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                var rd = cd - lp.HitboxRadius - tgt.HitboxRadius;
                                var active = plugin.IpcClient.VnavPathIsRunning() || plugin.IpcClient.VnavSimpleMovePathfindInProgress();
                                return (rd, active);
                            });

                            if (ringDist == float.MinValue) { SetDebugResult("Lost target — aborting"); break; }

                            // Negative ring = overlapping hitboxes = very close, treat as arrived
                            if (ringDist <= 0)
                            {
                                plugin.IpcClient.VnavStop();
                                SetDebugResult($"Arrived at {targetName} (ring={ringDist:F1}y, overlapping)");
                                break;
                            }

                            distSamples.Add(ringDist);
                            if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                            bool stalled = false;
                            if (distSamples.Count >= maxSamples)
                            {
                                float maxD = distSamples[0], minD = distSamples[0];
                                foreach (var s in distSamples) { if (s > maxD) maxD = s; if (s < minD) minD = s; }
                                stalled = (maxD - minD) < stallThreshold;
                            }

                            if (!pathActive)
                            {
                                SetDebugResult($"Arrived near {targetName} (ring={ringDist:F1}y)");
                                break;
                            }
                            else if (stalled && ringDist <= closeEnough)
                            {
                                plugin.IpcClient.VnavStop();
                                SetDebugResult($"Arrived near {targetName} (ring={ringDist:F1}y, stalled — stopped)");
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled within {ringDist:F1}y of {targetName} — stopping");
                                break;
                            }
                            else if (stalled && jumpAttempts < 5)
                            {
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled at ring={ringDist:F1}y — jump attempt {jumpAttempts + 1}");
                                KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
                                jumpAttempts++;
                                distSamples.Clear();
                                await System.Threading.Tasks.Task.Delay(800);
                                elapsed += 800;
                            }
                            else
                            {
                                if (elapsed % 3000 < pollMs)
                                    SetDebugResult($"Pathing to {targetName}: ring={ringDist:F1}y");
                            }
                        }

                        if (elapsed >= maxTimeoutMs)
                        {
                            plugin.IpcClient.VnavStop();
                            SetDebugResult($"PathToTarget timeout (60s) for {targetName}");
                        }
                    });
                }
                else SetDebugResult("Pathfind failed");
            }
            else if (target == null) SetDebugResult("No target selected");
            else SetDebugResult("vnavmesh not ready");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ground pathfind to target, no auto-interact.\nStop distance = 2y + target hitbox radius.");

        ImGui.Spacing();

        if (ImGui.Button("PathToTargetThenInteract"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null && plugin.IpcClient.VnavIsReady())
            {
                var targetPos = target.Position;
                var targetName = target.Name.ToString();
                var targetHitbox = target.HitboxRadius;
                var playerHitbox = local.HitboxRadius;
                var stopDist = 0.5f;
                var interactRange = 1.0f; // Interact when within 1.0y ring distance

                var ok = plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist);
                if (ok)
                {
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

                            var (ringDist, centerDist, pathRunning, pathfinding) = await Plugin.Framework.RunOnFrameworkThread(() =>
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

                            if (ringDist == float.MinValue) { SetDebugResult("Lost target — aborting"); break; }

                            // Negative ring = overlapping hitboxes = very close, interact immediately
                            if (ringDist <= 0)
                            {
                                SetDebugResult($"Overlapping {targetName}: ring={ringDist:F1}y — interacting");
                                await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                plugin.IpcClient.VnavStop();
                                Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: overlapping interact with {targetName} (ring={ringDist:F1}y)");
                                interacted = true;
                                break;
                            }

                            bool pathActive = pathRunning || pathfinding;

                            distSamples.Add(ringDist);
                            if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                            bool stalled = false;
                            if (distSamples.Count >= maxSamples)
                            {
                                float maxD = distSamples[0], minD = distSamples[0];
                                foreach (var s in distSamples) { if (s > maxD) maxD = s; if (s < minD) minD = s; }
                                stalled = (maxD - minD) < stallThreshold;
                            }

                            if (ringDist <= interactRange)
                            {
                                SetDebugResult($"In range of {targetName}: ring={ringDist:F1}y — interacting");
                                await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                plugin.IpcClient.VnavStop();
                                Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: interacted with {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                                interacted = true;
                                break;
                            }
                            else if (stalled && ringDist < 20.0f)
                            {
                                SetDebugResult($"Stalled near {targetName}: ring={ringDist:F1}y — jumping to unstuck");
                                Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: stalled at ring={ringDist:F1}y — jump attempt {jumpAttempts + 1}");
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
                                SetDebugResult($"Path ended at ring={ringDist:F1}y — vnav routing done");
                                Plugin.Log.Warning($"[XASlave] PathToTargetThenInteract: path ended for {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                                if (ringDist <= interactRange * 3)
                                {
                                    await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                    SetDebugResult($"Path ended — attempted interact at ring={ringDist:F1}y");
                                    interacted = true;
                                }
                                break;
                            }
                            else
                            {
                                if (elapsed % 3000 < pollMs)
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
                else SetDebugResult("Pathfind failed — vnav could not start route");
            }
            else if (target == null)
                SetDebugResult("No target selected");
            else
                SetDebugResult("vnavmesh not ready");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smart pathfind to target with auto-interact.\n" +
                "Stop distance = 2y + target hitbox (adapts to target size).\n" +
                "Attempts interact once within ring range.\n" +
                "Jumps to unstuck if stalled and interact fails.\n" +
                "Cancels movement on successful interaction.");

        ImGui.SameLine();
        if (ImGui.Button("PathSmartThenInteract"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null && plugin.IpcClient.VnavIsReady())
            {
                var targetPos = target.Position;
                var targetName = target.Name.ToString();
                var targetHitbox = target.HitboxRadius;
                var playerHitbox = local.HitboxRadius;
                var stopDist = 0.5f;
                var interactRange = 1.0f; // Interact when within 1.0y ring distance

                var lp0 = local.Position;
                var dx0 = targetPos.X - lp0.X; var dy0 = targetPos.Y - lp0.Y; var dz0 = targetPos.Z - lp0.Z;
                var ringDist0 = (float)Math.Sqrt(dx0 * dx0 + dy0 * dy0 + dz0 * dz0) - playerHitbox - targetHitbox;
                var canFly = HasFlightUnlocked();
                var shouldMount = ringDist0 > 20.0f; // Mount for any distance > 20y (ground or fly)

                SetDebugResult($"PathSmart to {targetName}: ring={ringDist0:F0}y, fly={canFly}, mount={shouldMount}");
                Plugin.Log.Information($"[XASlave] PathSmartThenInteract: {targetName} ring={ringDist0:F1}y fly={canFly} mount={shouldMount} stop={stopDist:F1}");

                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Mount + path simultaneously — mount cast works while running, no need to wait
                    if (shouldMount)
                        await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));

                    var fly = canFly && shouldMount; // Only fly-path if flight is unlocked AND mounted
                    var pathOk = await Plugin.Framework.RunOnFrameworkThread(() =>
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

                        var (rd, cd, pathRunning, pathfinding) = await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            var lp2 = Plugin.ObjectTable.LocalPlayer;
                            var tgt = lp2?.TargetObject;
                            if (lp2 == null || tgt == null) return (float.MinValue, -1f, false, false);
                            var pp = lp2.Position; var tp = tgt.Position;
                            var ddx = tp.X - pp.X; var ddy = tp.Y - pp.Y; var ddz = tp.Z - pp.Z;
                            var c = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                            var r = c - lp2.HitboxRadius - tgt.HitboxRadius;
                            return (r, c, plugin.IpcClient.VnavPathIsRunning(), plugin.IpcClient.VnavSimpleMovePathfindInProgress());
                        });

                        if (rd == float.MinValue) { SetDebugResult("Lost target — aborting"); break; }

                        // Negative ring = overlapping hitboxes = very close, interact immediately
                        if (rd <= 0)
                        {
                            plugin.IpcClient.VnavStop();
                            SetDebugResult($"Overlapping {targetName}: ring={rd:F1}y — interacting");
                            await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                            Plugin.Log.Information($"[XASlave] PathSmartThenInteract: overlapping interact with {targetName} (ring={rd:F1}y)");
                            interacted = true;
                            break;
                        }

                        bool pathActive = pathRunning || pathfinding;

                        distSamples.Add(rd);
                        if (distSamples.Count > maxSamples) distSamples.RemoveAt(0);

                        bool stalled = false;
                        if (distSamples.Count >= maxSamples)
                        {
                            float maxD = distSamples[0], minD = distSamples[0];
                            foreach (var s in distSamples) { if (s > maxD) maxD = s; if (s < minD) minD = s; }
                            stalled = (maxD - minD) < stallThreshold;
                        }

                        if (rd <= interactRange)
                        {
                            plugin.IpcClient.VnavStop();
                            var isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (isMounted)
                            {
                                SetDebugResult($"In range of {targetName}: ring={rd:F1}y — dismounting...");
                                await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/mount"));
                                for (int w = 0; w < 30; w++)
                                {
                                    await System.Threading.Tasks.Task.Delay(100);
                                    isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                    if (!isMounted) break;
                                }
                            }

                            // Hard 2s delay after dismount — prevents "Unable to execute command while jumping" error
                            await System.Threading.Tasks.Task.Delay(2000);

                            // Brief ready check — wait up to 1.5s for character to be actionable after dismount
                            for (int sw = 0; sw < 15; sw++)
                            {
                                await System.Threading.Tasks.Task.Delay(100);
                                var charReady = await Plugin.Framework.RunOnFrameworkThread(() =>
                                    MonthlyReloggerTask.IsPlayerAvailable() &&
                                    !Plugin.Condition[ConditionFlag.Casting]);
                                if (charReady) break;
                            }

                            // Re-check distance after dismount — large mounts expand player hitbox
                            // and dismounting may leave us further away than expected
                            var postDismountRd = await Plugin.Framework.RunOnFrameworkThread(() =>
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
                                // Too far after dismount — re-path on foot to close the gap
                                SetDebugResult($"Post-dismount too far: ring={postDismountRd:F1}y — re-pathing on foot");
                                Plugin.Log.Information($"[XASlave] PathSmartThenInteract: post-dismount ring={postDismountRd:F1}y > {interactRange:F1}y — re-pathing");
                                await Plugin.Framework.RunOnFrameworkThread(() =>
                                    plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                                // Wait for re-path to complete
                                for (int rp = 0; rp < 100; rp++)
                                {
                                    await System.Threading.Tasks.Task.Delay(200);
                                    var (rpRd, rpIdle) = await Plugin.Framework.RunOnFrameworkThread(() =>
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

                            SetDebugResult($"In range of {targetName}: ring={postDismountRd:F1}y — interacting");
                            await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                            plugin.IpcClient.VnavStop();
                            Plugin.Log.Information($"[XASlave] PathSmartThenInteract: interacted with {targetName} (ring={postDismountRd:F1}y)");
                            interacted = true;
                            break;
                        }
                        else if (stalled && rd < 20.0f)
                        {
                            var isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                            if (isMounted)
                            {
                                plugin.IpcClient.VnavStop();
                                await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                                for (int w = 0; w < 50; w++)
                                {
                                    await System.Threading.Tasks.Task.Delay(100);
                                    isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                        Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                    if (!isMounted) break;
                                }
                                await Plugin.Framework.RunOnFrameworkThread(() =>
                                    plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                                distSamples.Clear();
                                await System.Threading.Tasks.Task.Delay(600);
                                elapsed += 600;
                            }
                            else
                            {
                                SetDebugResult($"Stalled near {targetName}: ring={rd:F1}y — jumping");
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
                            SetDebugResult($"Path ended at ring={rd:F1}y — routing done");
                            Plugin.Log.Warning($"[XASlave] PathSmartThenInteract: path ended for {targetName} (ring={rd:F1}y)");
                            if (rd <= interactRange * 3)
                            {
                                var isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                    Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                if (isMounted)
                                {
                                    await Plugin.Framework.RunOnFrameworkThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));
                                    for (int w = 0; w < 50; w++)
                                    {
                                        await System.Threading.Tasks.Task.Delay(100);
                                        isMounted = await Plugin.Framework.RunOnFrameworkThread(() =>
                                            Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion]);
                                        if (!isMounted) break;
                                    }
                                }
                                await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.InteractWithTarget());
                                SetDebugResult($"Path ended — attempted interact at ring={rd:F1}y");
                                interacted = true;
                            }
                            break;
                        }
                        else
                        {
                            if (elapsed % 3000 < pollMs)
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
            else if (target == null) SetDebugResult("No target selected");
            else SetDebugResult("vnavmesh not ready");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smart pathfind: mounts if flying unlocked and target > 50y.\n" +
                "Flies to target, dismounts on arrival, then interacts.\n" +
                "Falls back to ground pathfind + interact if close or can't fly.");

        ImGui.Spacing();

        if (ImGui.Button("WalkThroughDottedWall"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_END);
            KeyInputHelper.HoldKeyForDuration(KeyInputHelper.VK_W, 2000);
            SetDebugResult("KeyInput: END (reset camera) + Hold W for 2s then auto-release (WalkThroughDottedWallXA)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Resets camera (VK_END) + holds W forward for 2 seconds, then auto-releases.\nFully automated — no manual release needed.");

        ImGui.SameLine();
        if (ImGui.Button("Release W (Emergency)"))
        {
            KeyInputHelper.ReleaseKey(KeyInputHelper.VK_W);
            SetDebugResult("KeyInput: Emergency released W key");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Emergency release if W key gets stuck. Normally not needed.");

        ImGui.Spacing();

        if (ImGui.Button("MovingCheater (Smart)"))
        {
            try
            {
                if (plugin.IpcClient.VnavIsReady())
                {
                    ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
                    var canFly = HasFlightUnlocked();
                    if (canFly)
                    {
                        ChatHelper.SendMessage("/vnav flyflag");
                        SetDebugResult("Smart: Mount + /vnav flyflag (flying unlocked in zone)");
                    }
                    else
                    {
                        ChatHelper.SendMessage("/vnav moveflag");
                        SetDebugResult("Smart: Mount + /vnav moveflag (flying NOT unlocked — ground pathfind)");
                    }
                }
                else SetDebugResult("vnavmesh not ready — cannot navigate");
            }
            catch (Exception ex) { SetDebugResult($"MovingCheater error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mounts + auto-detects flight: uses flyflag if flying unlocked, moveflag otherwise.\nMirrors DoNavFlySequenceXA logic from xafunc.lua (Player.CanFly check).");

        ImGui.SameLine();
        if (ImGui.Button("MovingCheater (Fly)"))
        {
            try
            {
                if (plugin.IpcClient.VnavIsReady())
                {
                    ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
                    var canFly = HasFlightUnlocked();
                    if (canFly)
                    {
                        ChatHelper.SendMessage("/vnav flyflag");
                        SetDebugResult("Sent: Mount + /vnav flyflag (flying unlocked)");
                    }
                    else
                    {
                        ChatHelper.SendMessage("/vnav moveflag");
                        SetDebugResult("Sent: Mount + /vnav moveflag (flight NOT unlocked — fallback to ground)");
                    }
                }
                else SetDebugResult("vnavmesh not ready — cannot navigate");
            }
            catch (Exception ex) { SetDebugResult($"MovingCheater error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fly to flag: Mounts + /vnav flyflag. Falls back to ground if flying not unlocked.");

        ImGui.SameLine();
        if (ImGui.Button("MovingCheater (Walk)"))
        {
            try
            {
                if (plugin.IpcClient.VnavIsReady())
                {
                    ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
                    ChatHelper.SendMessage("/vnav moveflag");
                    SetDebugResult("Sent: Mount + /vnav moveflag (force ground)");
                }
                else SetDebugResult("vnavmesh not ready — cannot navigate");
            }
            catch (Exception ex) { SetDebugResult($"MovingCheater error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force ground: Mounts + /vnav moveflag. Works everywhere including towns.");

        ImGui.Spacing();

        if (ImGui.Button("PvpMoveTo (Flag)"))
        {
            ChatHelper.SendMessage("/vnav moveflag");
            SetDebugResult("Sent: /vnav moveflag (PvpMoveToXA — no mount, ground pathfind)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ground pathfinds to map flag without mounting (PvpMoveToXA).\nIn full implementation, waits for casting to finish first.");

        ImGui.Spacing();
        } // end XA Lazy Movements

        ImGui.TreePop();
        } // end Movement Functions

        ImGui.Separator();
        ImGui.Spacing();

        // ╔══════════════════════════════════════════════╗
        // ║  [Player Checkers]                           ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.TreeNode("Player Checkers"))
        {

        // ══════════════════════════════════════════════
        //  Game State Checks (XA)
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Game State Checks (XA)"))
        {
        ImGui.Spacing();

        if (ImGui.Button("CharacterSafeWait"))
        {
            SetDebugResult("CharacterSafeWait: checking...");
            System.Threading.Tasks.Task.Run(async () =>
            {
                int consecutivePasses = 0;
                int totalAttempts = 0;
                while (consecutivePasses < 3)
                {
                    await System.Threading.Tasks.Task.Delay(1000);
                    totalAttempts++;
                    var (np, pa, casting, combat, charName) = await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        return (MonthlyReloggerTask.IsNamePlateReady(),
                                MonthlyReloggerTask.IsPlayerAvailable(),
                                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting],
                                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
                                MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                    });
                    bool ready = np && pa && !casting && !combat;
                    if (ready)
                    {
                        consecutivePasses++;
                        SetDebugResult($"[{consecutivePasses}/3] OK — {charName} (attempt #{totalAttempts})");
                    }
                    else
                    {
                        if (consecutivePasses > 0)
                            Plugin.Log.Information($"[XASlave] CharacterSafeWait: reset at {consecutivePasses}/3 — NP={np} PA={pa} Cast={casting} Combat={combat}");
                        consecutivePasses = 0;
                        SetDebugResult($"[0/3] waiting... NP={np} PA={pa} Cast={casting} Combat={combat} (attempt #{totalAttempts})");
                    }
                }
                var finalName = await Plugin.Framework.RunOnFrameworkThread(() => MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                SetDebugResult($"[3/3] CONFIRMED READY — {finalName}");
            });
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Waits for 3 consecutive passes (1s apart) of:\nNamePlate ready + PlayerAvailable + not casting + not in combat.");

        ImGui.SameLine();
        if (ImGui.Button("GetLevel"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            SetDebugResult(lp != null ? $"Level: {lp.Level}" : "Player not available");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetVnavCoords"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var p = lp.Position;
                var coordStr = $"{p.X:F3}, {p.Y:F3}, {p.Z:F3}";
                ImGui.SetClipboardText(coordStr);
                SetDebugResult($"Coords: {coordStr} (copied)");
            }
            else SetDebugResult("Player not available");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Gets player X,Y,Z coordinates and copies to clipboard (GetVnavCoordsXA)");

        ImGui.Spacing();

        if (ImGui.Button("GetZoneID"))
        {
            var zoneId = Plugin.ClientState.TerritoryType;
            SetDebugResult($"Zone ID: {zoneId}");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetZoneName"))
        {
            try
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
                var row = sheet?.GetRowOrDefault(zoneId);
                var zoneName = row?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
                SetDebugResult($"Zone: {zoneName} [{zoneId}]");
            }
            catch (Exception ex) { SetDebugResult($"Zone lookup error: {ex.Message}"); }
        }
        ImGui.SameLine();
        if (ImGui.Button("GetWorldName"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var worldName = lp.CurrentWorld.ValueNullable?.Name.ToString() ?? "Unknown";
                var worldId = lp.CurrentWorld.RowId;
                SetDebugResult($"World: {worldName} [{worldId}]");
            }
            else SetDebugResult("Player not available");
        }

        ImGui.Spacing();

        if (ImGui.Button("GetPlayerName"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            SetDebugResult(lp != null ? $"Player: {lp.Name}" : "Player not available");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetPlayerNameAndWorld"))
        {
            var name = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
            SetDebugResult($"Character: {name}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsInFreeCompany"))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                // CompanyTag is empty string if not in FC
                var fcTag = lp.CompanyTag.ToString();
                var inFc = !string.IsNullOrEmpty(fcTag);
                SetDebugResult($"IsInFC: {inFc} (tag: \"{fcTag}\")");
            }
            else SetDebugResult("Player not available");
        }

        ImGui.Spacing();

        if (ImGui.Button("IsInFCResults"))
        {
            ChatHelper.SendMessage("/freecompanycmd");
            var fcInfo = plugin.IpcClient.GetFcInfo();
            var plotInfo = plugin.IpcClient.GetPlotInfo();
            SetDebugResult($"FC: {fcInfo} | Plot: {plotInfo}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens FC window + reads XA Database FC/Plot data (IsInFreeCompanyResultsXA)");

        ImGui.SameLine();
        if (ImGui.Button("IsInParty"))
        {
            var count = Plugin.PartyList.Length;
            SetDebugResult($"IsInParty: {count > 0} (members: {count})");
        }
        ImGui.SameLine();
        if (ImGui.Button("PartyDisband"))
        {
            ChatHelper.SendMessage("/partycmd disband");
            SetDebugResult("Sent: /partycmd disband (PartyDisbandXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("PartyLeave"))
        {
            ChatHelper.SendMessage("/partycmd leave");
            SetDebugResult("Sent: /partycmd leave (PartyLeaveXA)");
        }

        ImGui.Spacing();

        if (ImGui.Button("SelectYesNo: Yes"))
        {
            var ok = AddonHelper.ClickYesNo(true);
            SetDebugResult(ok ? "SelectYesno: Clicked Yes" : "SelectYesno not visible");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fires SelectYesno callback to click Yes.\nUse after PartyDisband, PartyLeave, Logout, etc.");
        ImGui.SameLine();
        if (ImGui.Button("SelectYesNo: No"))
        {
            var ok = AddonHelper.ClickYesNo(false);
            SetDebugResult(ok ? "SelectYesno: Clicked No" : "SelectYesno not visible");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fires SelectYesno callback to click No.");

        ImGui.Spacing();
        } // end Game State Checks

        // ══════════════════════════════════════════════
        //  Target Game State Checks
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Target Checks"))
        {
        ImGui.Spacing();

        if (ImGui.Button("GetTargetName"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            SetDebugResult(target != null ? $"Target: {target.Name} (ID: {target.GameObjectId:X})" : "No target selected");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetTargetCoords"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target != null)
            {
                var p = target.Position;
                var coordStr = $"{p.X:F3}, {p.Y:F3}, {p.Z:F3}";
                ImGui.SetClipboardText(coordStr);
                SetDebugResult($"Target Coords: {coordStr} (copied)");
            }
            else SetDebugResult("No target selected");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetTargetDistance"))
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            var target = local?.TargetObject;
            if (local != null && target != null)
            {
                var lp = local.Position;
                var tp = target.Position;
                var dx = tp.X - lp.X;
                var dy = tp.Y - lp.Y;
                var dz = tp.Z - lp.Z;
                var centerDist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var playerHitbox = local.HitboxRadius;
                var targetHitbox = target.HitboxRadius;
                var ringDist = centerDist - playerHitbox - targetHitbox;
                SetDebugResult($"Distance to {target.Name}: ring={ringDist:F2}y center={centerDist:F2}y (hitbox: player={playerHitbox:F2} target={targetHitbox:F2})");
            }
            else SetDebugResult("No target or player not available");
        }

        ImGui.Spacing();

        if (ImGui.Button("GetTargetKind"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target != null)
                SetDebugResult($"Target: {target.Name} | Kind: {target.ObjectKind} | BaseId: {target.BaseId}");
            else
                SetDebugResult("No target selected");
        }
        ImGui.SameLine();
        if (ImGui.Button("GetTargetHP"))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target is Dalamud.Game.ClientState.Objects.Types.IBattleChara bc)
                SetDebugResult($"Target HP: {bc.CurrentHp}/{bc.MaxHp} ({(bc.MaxHp > 0 ? (100.0 * bc.CurrentHp / bc.MaxHp) : 0):F1}%)");
            else if (target != null)
                SetDebugResult($"Target '{target.Name}' is not a battle character (Kind: {target.ObjectKind})");
            else
                SetDebugResult("No target selected");
        }

        ImGui.Spacing();
        } // end Target Checks

        // ══════════════════════════════════════════════
        //  Player State Checks (d)
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Player State Checks (d)"))
        {
        ImGui.Spacing();

        if (ImGui.Button("IsMounted?"))
        {
            var mounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];
            SetDebugResult($"IsMounted: {mounted}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsInCombat?"))
        {
            var combat = Plugin.Condition[ConditionFlag.InCombat];
            SetDebugResult($"IsInCombat: {combat}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsCasting?"))
        {
            var casting = Plugin.Condition[ConditionFlag.Casting];
            SetDebugResult($"IsCasting: {casting}");
        }
        ImGui.SameLine();
        if (ImGui.Button("IsFlying?"))
        {
            var flying = Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Diving];
            SetDebugResult($"IsFlying/Diving: {flying}");
        }

        ImGui.Spacing();

        if (ImGui.Button("InDuty?"))
        {
            var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
            var inCombat = Plugin.Condition[ConditionFlag.InCombat];
            SetDebugResult($"BoundByDuty: {inDuty}, InCombat: {inCombat}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks ConditionFlag.BoundByDuty (in a duty instance).\nAlso shows combat status.");
        ImGui.SameLine();
        if (ImGui.Button("Leave Duty"))
        {
            var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
            if (!inDuty)
            {
                SetDebugResult("Not in a duty — nothing to leave.");
            }
            else
            {
                SetDebugResult("In duty — attempting to leave...");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Wait up to 30s if in combat (might be finishing monsters)
                    var inCombat = await Plugin.Framework.RunOnFrameworkThread(() => Plugin.Condition[ConditionFlag.InCombat]);
                    if (inCombat)
                    {
                        SetDebugResult("In combat — waiting up to 30s for combat to end...");
                        for (int w = 0; w < 60; w++)
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            inCombat = await Plugin.Framework.RunOnFrameworkThread(() => Plugin.Condition[ConditionFlag.InCombat]);
                            if (!inCombat) break;
                        }
                        if (inCombat)
                        {
                            SetDebugResult("Still in combat after 30s — cannot leave duty.");
                            return;
                        }
                        SetDebugResult("Combat ended — leaving duty...");
                    }

                    // Press U to open the Duty Finder menu (ContentsFinderMenu)
                    await Plugin.Framework.RunOnFrameworkThread(() => KeyInputHelper.PressKey(0x55)); // VK_U = 0x55
                    await System.Threading.Tasks.Task.Delay(1000);

                    // Click the Leave button — ContentsFinderMenu NodeList[43]
                    var leaveClicked = await Plugin.Framework.RunOnFrameworkThread(() =>
                        AddonHelper.ClickAddonButton("ContentsFinderMenu", 43));

                    if (leaveClicked)
                    {
                        SetDebugResult("Leave Duty: clicked Leave button — waiting for confirmation...");
                        await System.Threading.Tasks.Task.Delay(500);

                        // Click Yes on the confirmation dialog
                        var yesClicked = await Plugin.Framework.RunOnFrameworkThread(() => AddonHelper.ClickYesNo(true));
                        if (yesClicked)
                            SetDebugResult("Leave Duty: confirmed Yes — leaving instance.");
                        else
                            SetDebugResult("Leave Duty: Leave clicked but SelectYesno not visible — may need manual confirm.");
                    }
                    else
                    {
                        SetDebugResult("Leave Duty: ContentsFinderMenu not visible or Leave button not found.");
                    }
                });
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Attempts to leave the current duty.\nWaits up to 30s if in combat, then sends /leaveDuty + confirms Yes.");

        ImGui.Spacing();

        if (ImGui.Button("GetGCRank"))
        {
            try
            {
                unsafe
                {
                    var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                    if (ps != null)
                    {
                        var flames = ps->GCRankImmortalFlames;
                        var adders = ps->GCRankTwinAdders;
                        var mael = ps->GCRankMaelstrom;
                        var highest = Math.Max(flames, Math.Max(adders, mael));
                        SetDebugResult($"GC Ranks: Flames={flames}, Adders={adders}, Mael={mael} (highest={highest})");
                    }
                    else SetDebugResult("PlayerState not available");
                }
            }
            catch (Exception ex) { SetDebugResult($"GCRank error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads all three GC ranks.\nEquivalent to dfunc GetGCRank/GetFlamesGCRank/GetAddersGCRank/GetMaelstromGCRank");

        ImGui.SameLine();
        if (ImGui.Button("PartyMemberCount"))
        {
            var count = Plugin.PartyList.Length;
            var members = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var m = Plugin.PartyList[i];
                if (m != null) members.Append($"{m.Name} (HP:{m.CurrentHP}/{m.MaxHP}), ");
            }
            SetDebugResult($"Party: {count} members. {members}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists party members with HP.\nEquivalent to dfunc BroCheck/GetPartyMemberName");

        ImGui.Spacing();

        if (ImGui.Button("Check All IPC"))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("IPC Availability:");
            sb.AppendLine($"  AutoRetainer    = {plugin.IpcClient.IsAutoRetainerAvailable()}");
            sb.AppendLine($"  Lifestream      = {plugin.IpcClient.IsLifestreamAvailable()}");
            sb.AppendLine($"  TextAdvance     = {plugin.IpcClient.IsTextAdvanceAvailable()}");
            sb.AppendLine($"  vnavmesh        = {plugin.IpcClient.IsVnavAvailable()}");
            sb.AppendLine($"  XA Database     = {plugin.IpcClient.IsXaDatabaseAvailable()}");
            sb.AppendLine($"  YesAlready      = {plugin.IpcClient.IsYesAlreadyAvailable()}");
            sb.AppendLine($"  PandorasBox     = {plugin.IpcClient.IsPandorasBoxAvailable()}");
            sb.AppendLine($"  Deliveroo       = {plugin.IpcClient.IsDeliverooAvailable()}");
            sb.AppendLine($"  Artisan         = {plugin.IpcClient.IsArtisanAvailable()}");
            sb.AppendLine($"  Dropbox         = {plugin.IpcClient.IsDropboxAvailable()}");
            sb.AppendLine($"  Splatoon        = {plugin.IpcClient.IsSplatoonAvailable()}");
            var result = sb.ToString();
            ImGui.SetClipboardText(result);
            SetDebugResult("IPC status copied to clipboard");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks all 11 IPC integrations and copies results to clipboard.\nEquivalent to dfunc GetInternalNamesIPC / GetIPCRegisteredTables");

        ImGui.SameLine();
        if (ImGui.Button("Installed Plugins"))
        {
            try
            {
                var installedPlugins = Plugin.PluginInterface.InstalledPlugins;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Loaded Dalamud Plugins:");
                foreach (var p in installedPlugins.Where(p => p.IsLoaded).OrderBy(p => p.InternalName))
                    sb.AppendLine($"  {p.InternalName}");
                sb.AppendLine($"\nTotal loaded: {installedPlugins.Count(p => p.IsLoaded)}");
                var result = sb.ToString();
                ImGui.SetClipboardText(result);
                SetDebugResult($"Plugin list ({installedPlugins.Count(p => p.IsLoaded)}) copied to clipboard");
            }
            catch (Exception ex) { SetDebugResult($"Plugin list error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists all loaded Dalamud plugins and copies to clipboard.\nEquivalent to dfunc GetInternalNamesIPC");

        ImGui.Spacing();

        if (ImGui.Button("List All Addons"))
        {
            try
            {
                unsafe
                {
                    var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
                    var unitMgr = stage->RaptureAtkUnitManager;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Visible Addon Windows:");
                    int count = 0;
                    for (int ai = 0; ai < unitMgr->AtkUnitManager.AllLoadedUnitsList.Count; ai++)
                    {
                        var entry = unitMgr->AtkUnitManager.AllLoadedUnitsList.Entries[ai];
                        if (entry.Value == null) continue;
                        var addon = entry.Value;
                        var addonName = addon->NameString;
                        var visible = addon->IsVisible;
                        var ready = addon->IsReady;
                        var nodeCount = addon->UldManager.NodeListCount;
                        if (visible)
                        {
                            sb.AppendLine($"  {addonName} (visible, ready={ready}, nodes={nodeCount})");
                            count++;
                        }
                    }
                    sb.AppendLine($"\nTotal visible: {count}");
                    var result = sb.ToString();
                    ImGui.SetClipboardText(result);
                    SetDebugResult($"Addon list ({count} visible) copied to clipboard");
                }
            }
            catch (Exception ex) { SetDebugResult($"Addon list error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists all currently visible addon windows and copies to clipboard.\nIncludes node count and ready state for debugging.");

        ImGui.SameLine();
        if (ImGui.Button("List All Conditions"))
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Active Condition Flags:");
                int count = 0;
                foreach (ConditionFlag flag in Enum.GetValues(typeof(ConditionFlag)))
                {
                    if (Plugin.Condition[flag])
                    {
                        sb.AppendLine($"  [{(int)flag}] {flag}");
                        count++;
                    }
                }
                if (count == 0)
                    sb.AppendLine("  (none active)");
                sb.AppendLine($"\nTotal active: {count}");
                var result = sb.ToString();
                ImGui.SetClipboardText(result);
                SetDebugResult($"Condition flags ({count} active) copied to clipboard");
            }
            catch (Exception ex) { SetDebugResult($"Condition list error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists all currently active ConditionFlags and copies to clipboard.\nUseful for debugging player state issues.");

        ImGui.SameLine();
        if (ImGui.Button("Exit CharaSelect"))
        {
            var visible = AddonHelper.IsAddonVisible("_CharaSelectReturn");
            if (visible)
            {
                var ok = AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
                SetDebugResult(ok ? "Clicked _CharaSelectReturn NodeList[1] — exiting to main menu" : "Click failed on _CharaSelectReturn");
            }
            else
            {
                SetDebugResult("_CharaSelectReturn not visible — not on character select screen.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks _CharaSelectReturn NodeList[1] to exit character select\nand return to the main menu.");

        ImGui.Spacing();
        } // end Player State Checks

        // ══════════════════════════════════════════════
        //  Character Actions (xafunc equivalents)
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Character Actions"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Remove Sprout"))
        {
            ChatHelper.SendMessage("/nastatus off");
            SetDebugResult("Sent: /nastatus off (RemoveSproutXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Mount Roulette"))
        {
            ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
            SetDebugResult("Sent: /gaction \"Mount Roulette\" (MountUpXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dismount"))
        {
            ChatHelper.SendMessage("/mount");
            SetDebugResult("Sent: /mount (DismountXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Logout"))
        {
            ChatHelper.SendMessage("/logout");
            SetDebugResult("Sent: /logout");
        }

        ImGui.Spacing();

        if (ImGui.Button("Open Inventory"))
        {
            ChatHelper.SendMessage("/inventory");
            SetDebugResult("Sent: /inventory (OpenInventoryXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open Armoury"))
        {
            ChatHelper.SendMessage("/armourychest");
            SetDebugResult("Sent: /armourychest (OpenArmouryChestXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open Saddlebags"))
        {
            ChatHelper.SendMessage("/saddlebag");
            SetDebugResult("Sent: /saddlebag (OpenSaddlebagsXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open FC Window"))
        {
            ChatHelper.SendMessage("/freecompanycmd");
            SetDebugResult("Sent: /freecompanycmd (FreeCompanyCmdXA)");
        }

        ImGui.Spacing();
        } // end Character Actions

        // ══════════════════════════════════════════════
        //  Player Commands
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Player Commands"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Interact"))
        {
            var ok = AddonHelper.InteractWithTarget();
            SetDebugResult(ok ? "InteractWithTarget: OK (InteractXA)" : "InteractWithTarget: No target or failed");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses TargetSystem.InteractWithObject — native replacement for SND /interact");

        ImGui.SameLine();
        if (ImGui.Button("EquipGear (SimpleTweaks)"))
        {
            ChatHelper.SendMessage("/equiprecommended");
            SetDebugResult("Sent: /equiprecommended (SimpleTweaks EquipRecommendedGearCmdXA)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses SimpleTweaks /equiprecommended command. Primary method.");

        ImGui.SameLine();
        if (ImGui.Button("EquipGear Step1: Open"))
        {
            ChatHelper.SendMessage("/character");
            SetDebugResult("Opened Character window — next: Step2 to fire callback");
        }
        ImGui.SameLine();
        if (ImGui.Button("EquipGear Step2: Recommend"))
        {
            var ok = AddonHelper.ClickAddonButton("Character", 74);
            SetDebugResult(ok ? "Clicked Character NodeList[74] (Button #12) → RecommendEquip should open" : "Character addon not visible — open it first with Step1");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks Button Component Node #12 at NodeList[74] in Character addon.\nOpens Recommended Gear window (RecommendEquip).\nConfirmed via /xldata Addon Inspector.");

        ImGui.Spacing();

        if (ImGui.Button("EquipGear Step3: Equip"))
        {
            var ok = AddonHelper.ClickAddonButton("RecommendEquip", 3);
            SetDebugResult(ok ? "Clicked RecommendEquip NodeList[3] (Button #11) → gear equipped" : "RecommendEquip addon not visible — run Step2 first");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks Button Component Node #11 at NodeList[3] in RecommendEquip addon.\nEquips recommended gear.\nConfirmed via /xldata Addon Inspector.");
        ImGui.SameLine();
        if (ImGui.Button("EquipGear: Close"))
        {
            AddonHelper.CloseAddon("RecommendEquip");
            AddonHelper.CloseAddon("Character");
            SetDebugResult("Closed RecommendEquip + Character addons");
        }

        ImGui.Spacing();

        if (ImGui.Button("Reset Camera"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_END);
            SetDebugResult("Sent: VK_END key press (ResetCameraXA) via KeyInputHelper");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Presses END key via Win32 keybd_event — native replacement for SND /send END");

        ImGui.Spacing();
        } // end Player Commands

        // ══════════════════════════════════════════════
        //  XA Database
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("XA Database##playerCheckers"))
        {
        ImGui.Spacing();

        if (ImGui.Button("XA: Save"))
        {
            var ok = plugin.IpcClient.Save();
            SetDebugResult($"XA.Database.Save: {(ok ? "OK" : "FAILED")}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: Refresh"))
        {
            var ok = plugin.IpcClient.Refresh();
            SetDebugResult($"XA.Database.Refresh: {(ok ? "OK" : "FAILED")}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: IsReady?"))
        {
            var ready = plugin.IpcClient.IsReady();
            SetDebugResult($"XA.Database.IsReady: {ready}");
        }

        ImGui.Spacing();

        if (ImGui.Button("XA: GetGil"))
        {
            var gil = plugin.IpcClient.GetGil();
            SetDebugResult($"Gil: {gil:N0}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetRetainerGil"))
        {
            var gil = plugin.IpcClient.GetRetainerGil();
            SetDebugResult($"Retainer Gil: {gil:N0}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetFcInfo"))
        {
            var info = plugin.IpcClient.GetFcInfo();
            SetDebugResult($"FC: {info}");
        }

        ImGui.Spacing();

        if (ImGui.Button("XA: GetPlotInfo"))
        {
            var info = plugin.IpcClient.GetPlotInfo();
            SetDebugResult($"Plot: {info}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetPersonalPlot"))
        {
            var info = plugin.IpcClient.GetPersonalPlotInfo();
            SetDebugResult($"Personal Plot: {info}");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetVersion"))
        {
            var ver = plugin.IpcClient.GetVersion();
            SetDebugResult($"XA Database Version: {ver}");
        }

        ImGui.Spacing();
        } // end XA Database

        ImGui.TreePop();
        } // end Player Checkers

        // ╔══════════════════════════════════════════════╗
        // ║  [Punish]                                    ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.TreeNode("Punish"))
        {

        // ══════════════════════════════════════════════
        //  AutoRetainer
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("AutoRetainer##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Disable AR Multi"))
        {
            plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            SetDebugResult("Sent: AR Multi disabled (DisableARMultiXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Enable AR Multi"))
        {
            plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            SetDebugResult("Sent: AR Multi enabled (EnableARMultiXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR IsBusy?"))
        {
            var suppressed = plugin.IpcClient.AutoRetainerGetSuppressed();
            SetDebugResult($"AR Suppressed/Busy: {suppressed}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR Available?"))
        {
            var avail = plugin.IpcClient.IsAutoRetainerAvailable();
            SetDebugResult($"AutoRetainer available: {avail}");
        }

        ImGui.Spacing();

        if (ImGui.Button("ARDiscard"))
        {
            ChatHelper.SendMessage("/ays discard");
            SetDebugResult("Sent: /ays discard (ARDiscard)");
        }

        ImGui.Spacing();
        } // end AutoRetainer

        // ══════════════════════════════════════════════
        //  Lifestream
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Lifestream"))
        {
        ImGui.Spacing();

        if (ImGui.Button("LS: Teleport Home"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("home");
            SetDebugResult("Sent: /li home (return_to_homeXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Teleport FC"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("fc");
            SetDebugResult("Sent: /li fc (return_to_fcXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Home GC"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("hc");
            SetDebugResult("Sent: /li hc (RunToHomeGCXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Abort"))
        {
            plugin.IpcClient.LifestreamAbort();
            SetDebugResult("Sent: Lifestream.Abort()");
        }

        ImGui.Spacing();

        if (ImGui.Button("LS: Homeworld"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("");
            SetDebugResult("Sent: /li (return_to_homeworldXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: Auto"))
        {
            plugin.IpcClient.LifestreamExecuteCommand("auto");
            SetDebugResult("Sent: /li auto (return_to_autoXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("LS: IsBusy?"))
        {
            try
            {
                var busy = plugin.IpcClient.LifestreamIsBusy();
                SetDebugResult($"Lifestream IsBusy: {busy}");
            }
            catch (Exception ex) { SetDebugResult($"Lifestream error: {ex.Message}"); }
        }
        ImGui.SameLine();
        if (ImGui.Button("LS Available?"))
        {
            var avail = plugin.IpcClient.IsLifestreamAvailable();
            var busy = avail && plugin.IpcClient.LifestreamIsBusy();
            SetDebugResult($"Lifestream: available={avail}, busy={busy}");
        }

        ImGui.Spacing();
        } // end Lifestream

        // ══════════════════════════════════════════════
        //  TextAdvance
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("TextAdvance##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Enable TextAdvance"))
        {
            ChatHelper.SendMessage("/at y");
            SetDebugResult("Sent: /at y (EnableTextAdvanceXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Disable TextAdvance"))
        {
            ChatHelper.SendMessage("/at n");
            SetDebugResult("Sent: /at n (DisableTextAdvanceXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("TA Available?"))
        {
            var avail = plugin.IpcClient.IsTextAdvanceAvailable();
            SetDebugResult($"TextAdvance available: {avail}");
        }

        ImGui.Spacing();
        } // end TextAdvance

        // ══════════════════════════════════════════════
        //  YesAlready
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("YesAlready##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("YA: Enable"))
        {
            plugin.IpcClient.YesAlreadySetEnabled(true);
            SetDebugResult("YesAlready: Enabled");
        }
        ImGui.SameLine();
        if (ImGui.Button("YA: Disable"))
        {
            plugin.IpcClient.YesAlreadySetEnabled(false);
            SetDebugResult("YesAlready: Disabled");
        }
        ImGui.SameLine();
        if (ImGui.Button("YA: IsEnabled?"))
        {
            var enabled = plugin.IpcClient.YesAlreadyIsEnabled();
            SetDebugResult($"YesAlready IsEnabled: {enabled}");
        }
        ImGui.SameLine();
        if (ImGui.Button("YA: Pause 5s"))
        {
            plugin.IpcClient.YesAlreadyPause(5000);
            SetDebugResult("YesAlready: Paused for 5 seconds");
        }

        ImGui.SameLine();
        if (ImGui.Button("YA Available?"))
        {
            var avail = plugin.IpcClient.IsYesAlreadyAvailable();
            SetDebugResult($"YesAlready available: {avail}");
        }

        ImGui.Spacing();
        } // end YesAlready

        // ══════════════════════════════════════════════
        //  Artisan
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Artisan##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Art: Enable"))
        {
            ChatHelper.SendMessage("/xlenableprofile Artisan");
            SetDebugResult("Sent: /xlenableprofile Artisan (EnableArtisanXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: Disable"))
        {
            ChatHelper.SendMessage("/xldisableprofile Artisan");
            SetDebugResult("Sent: /xldisableprofile Artisan (DisableArtisanXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: IsBusy?"))
        {
            var avail = plugin.IpcClient.IsArtisanAvailable();
            var busy = avail && plugin.IpcClient.ArtisanIsBusy();
            SetDebugResult($"Artisan: avail={avail}, busy={busy}");
        }

        ImGui.Spacing();

        if (ImGui.Button("Art: GetEndurance"))
        {
            var status = plugin.IpcClient.ArtisanGetEnduranceStatus();
            SetDebugResult($"Artisan Endurance: {status}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: EnduranceOn"))
        {
            plugin.IpcClient.ArtisanSetEnduranceStatus(true);
            SetDebugResult("Artisan Endurance: ON");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: EnduranceOff"))
        {
            plugin.IpcClient.ArtisanSetEnduranceStatus(false);
            SetDebugResult("Artisan Endurance: OFF");
        }

        ImGui.Spacing();

        if (ImGui.Button("Art: IsListRunning?"))
        {
            var running = plugin.IpcClient.ArtisanIsListRunning();
            SetDebugResult($"Artisan ListRunning: {running}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: IsListPaused?"))
        {
            var paused = plugin.IpcClient.ArtisanIsListPaused();
            SetDebugResult($"Artisan ListPaused: {paused}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: PauseList"))
        {
            plugin.IpcClient.ArtisanSetListPause(true);
            SetDebugResult("Artisan List: Paused");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: ResumeList"))
        {
            plugin.IpcClient.ArtisanSetListPause(false);
            SetDebugResult("Artisan List: Resumed");
        }

        ImGui.Spacing();

        if (ImGui.Button("Art: GetStopReq"))
        {
            var stop = plugin.IpcClient.ArtisanGetStopRequest();
            SetDebugResult($"Artisan StopRequest: {stop}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: SetStop"))
        {
            plugin.IpcClient.ArtisanSetStopRequest(true);
            SetDebugResult("Artisan StopRequest: true");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art: ClearStop"))
        {
            plugin.IpcClient.ArtisanSetStopRequest(false);
            SetDebugResult("Artisan StopRequest: false (cleared)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Art Available?"))
        {
            var avail = plugin.IpcClient.IsArtisanAvailable();
            SetDebugResult($"Artisan available: {avail}");
        }

        ImGui.Spacing();
        } // end Artisan

        // ══════════════════════════════════════════════
        //  Dropbox
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Dropbox"))
        {
        ImGui.Spacing();

        if (ImGui.Button("OpenDropbox"))
        {
            ChatHelper.SendMessage("/dropbox");
            ChatHelper.SendMessage("/dropbox OpenTradeTab");
            SetDebugResult("Sent: /dropbox + /dropbox OpenTradeTab (OpenDropboxXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dropbox IsBusy?"))
        {
            var busy = plugin.IpcClient.DropboxIsBusy();
            SetDebugResult($"Dropbox IsBusy: {busy}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dropbox Available?"))
        {
            var avail = plugin.IpcClient.IsDropboxAvailable();
            var busy = avail && plugin.IpcClient.DropboxIsBusy();
            SetDebugResult($"Dropbox: available={avail}, busy={busy}");
        }

        ImGui.Spacing();
        } // end Dropbox

        // ══════════════════════════════════════════════
        //  Pandoras Box
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Pandoras Box"))
        {
        ImGui.Spacing();

        if (ImGui.Button("EnableSprintInTown"))
        {
            var ok = plugin.IpcClient.PandoraSetFeatureEnabled("Auto-Sprint in Sanctuaries", true);
            SetDebugResult($"PandorasBox Auto-Sprint enabled: {ok} (EnableSprintingInTownXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("DisableSprintInTown"))
        {
            var ok = plugin.IpcClient.PandoraSetFeatureEnabled("Auto-Sprint in Sanctuaries", false);
            SetDebugResult($"PandorasBox Auto-Sprint disabled: {ok} (DisableSprintingInTownXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("PandorasBox?"))
        {
            var avail = plugin.IpcClient.IsPandorasBoxAvailable();
            SetDebugResult($"PandorasBox available: {avail}");
        }

        ImGui.Spacing();
        } // end Pandoras Box

        // ══════════════════════════════════════════════
        //  Deliveroo
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Deliveroo"))
        {
        ImGui.Spacing();

        if (ImGui.Button("EnableDeliveroo"))
        {
            ChatHelper.SendMessage("/deliveroo enable");
            SetDebugResult("Sent: /deliveroo enable (EnableDeliverooXA)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Deliveroo Running?"))
        {
            var running = plugin.IpcClient.DeliverooIsTurnInRunning();
            SetDebugResult($"Deliveroo turn-in running: {running}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Deliveroo?"))
        {
            var avail = plugin.IpcClient.IsDeliverooAvailable();
            SetDebugResult($"Deliveroo available: {avail}");
        }

        ImGui.Spacing();
        } // end Deliveroo

        // ══════════════════════════════════════════════
        //  Splatoon
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("Splatoon"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Splatoon?"))
        {
            var avail = plugin.IpcClient.IsSplatoonAvailable();
            SetDebugResult($"Splatoon available: {avail}");
        }

        ImGui.Spacing();
        } // end Splatoon

        // ══════════════════════════════════════════════
        //  vnavmesh
        // ══════════════════════════════════════════════
        if (ImGui.CollapsingHeader("vnavmesh##punish"))
        {
        ImGui.Spacing();

        if (ImGui.Button("vnav: IsReady?"))
        {
            var ready = plugin.IpcClient.VnavIsReady();
            SetDebugResult($"vnavmesh IsReady: {ready}");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: PathRunning?"))
        {
            var running = plugin.IpcClient.VnavPathIsRunning();
            SetDebugResult($"vnavmesh PathIsRunning: {running}");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: Stop"))
        {
            plugin.IpcClient.VnavStop();
            SetDebugResult("Sent: vnavmesh.Path.Stop()");
        }
        ImGui.SameLine();
        if (ImGui.Button("vnav: Rebuild"))
        {
            plugin.IpcClient.VnavRebuild();
            SetDebugResult("Sent: vnavmesh.Nav.Rebuild()");
        }

        ImGui.Spacing();

        if (ImGui.Button("HasFlightUnlocked?"))
        {
            var canFly = HasFlightUnlocked();
            SetDebugResult($"HasFlightUnlocked: {canFly} (zone {Plugin.ClientState.TerritoryType})");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses PlayerState.IsAetherCurrentZoneComplete.\nDirect equivalent of dfunc HasFlightUnlocked() / Player.CanFly.");

        ImGui.SameLine();
        if (ImGui.Button("InSanctuary?"))
        {
            var inSanc = InSanctuary();
            SetDebugResult($"InSanctuary: {inSanc} (CanMount: {!inSanc})");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks if player cannot mount → sanctuary.\nEquivalent to dfunc InSanctuary() / !Player.CanMount");

        ImGui.SameLine();
        if (ImGui.Button("vnav Available?"))
        {
            var avail = plugin.IpcClient.IsVnavAvailable();
            SetDebugResult($"vnavmesh available: {avail}");
        }

        ImGui.Spacing();
        } // end vnavmesh

        ImGui.TreePop();
        } // end Punish

        // ╔══════════════════════════════════════════════╗
        // ║  [Key Inputs]                                ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.CollapsingHeader("Key Inputs"))
        {
        ImGui.TextDisabled("Win32 keybd_event key simulation for FFXIV input");
        ImGui.Spacing();

        ImGui.TextDisabled("Methods:");
        ImGui.TextDisabled("  PressKey(vk)                — tap key (down+up)");
        ImGui.TextDisabled("  HoldKey(vk)                 — key down only");
        ImGui.TextDisabled("  ReleaseKey(vk)              — key up only");
        ImGui.TextDisabled("  HoldKeyForDuration(vk, ms)  — hold + auto-release");
        ImGui.Spacing();

        ImGui.TextDisabled("Available VK Constants:");
        ImGui.TextDisabled("  Movement:  VK_W (0x57)  VK_A (0x41)  VK_S (0x53)  VK_D (0x44)");
        ImGui.TextDisabled("  Special:   VK_END (0x23)  VK_HOME (0x24)  VK_ESCAPE (0x1B)  VK_RETURN (0x0D)");
        ImGui.TextDisabled("             VK_SPACE (0x20)  VK_TAB (0x09)  VK_DELETE (0x2E)  VK_INSERT (0x2D)");
        ImGui.TextDisabled("  Arrow:     VK_LEFT (0x25)  VK_UP (0x26)  VK_RIGHT (0x27)  VK_DOWN (0x28)");
        ImGui.TextDisabled("  Modifier:  VK_SHIFT (0x10)  VK_CONTROL (0x11)  VK_ALT (0x12)");
        ImGui.TextDisabled("  Numpad:    VK_NUMPAD0–9 (0x60–0x69)");
        ImGui.TextDisabled("  Function:  VK_F1–F12 (0x70–0x7B)");
        ImGui.TextDisabled("  Letters:   0x41–0x5A (A–Z)    Numbers: 0x30–0x39 (0–9)");

        ImGui.Spacing();
        } // end Key Inputs

        // ╔══════════════════════════════════════════════╗
        // ║  [Braindead Functions]                        ║
        // ╚══════════════════════════════════════════════╝
        if (ImGui.CollapsingHeader("Braindead Functions"))
        {
        ImGui.TextDisabled("Multi-step scripted sequences. These will be implemented as full task chains.");
        ImGui.Spacing();

        ImGui.TextDisabled("• FreshLimsaToSummer — Complete Limsa intro → Summerford Farms");
        ImGui.TextDisabled("• FreshLimsaToMist — Limsa intro → Summerford → Mist housing");
        ImGui.TextDisabled("• FreshUldahToHorizon — Ul'dah intro → Horizon");
        ImGui.TextDisabled("• FreshUldahToGoblet — Ul'dah intro → Horizon → Goblet housing");
        ImGui.TextDisabled("• FreshGridaniaToBentbranch — Gridania intro → Bentbranch Meadows");
        ImGui.TextDisabled("• FreshGridaniaToBeds — Gridania intro → Bentbranch → Lavender Beds");
        ImGui.TextDisabled("• ImNotNewbStopWatching — Remove sprout + enable TA + set camera");
        ImGui.TextDisabled("• EnterHousingWardFromMenu — Navigate housing ward selection");
        } // end Braindead Functions
    }

    private void SetDebugResult(string msg)
    {
        debugResult = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        debugResultExpiry = DateTime.UtcNow.AddSeconds(15);
        Plugin.Log.Information($"[XASlave] Debug: {msg}");
    }

    /// <summary>
    /// Checks if flying is unlocked in the current zone.
    /// Uses PlayerState.CanFly field (offset 0x601) — set during zone loading.
    /// This is the direct equivalent of SND's Player.CanFly / dfunc HasFlightUnlocked().
    /// </summary>
    private static unsafe bool HasFlightUnlocked()
    {
        try
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps == null)
            {
                Plugin.Log.Warning("[XASlave] HasFlightUnlocked: PlayerState.Instance() returned null");
                return false;
            }
            var territory = Plugin.ClientState.TerritoryType;
            var canFly = ps->CanFly;
            Plugin.Log.Debug($"[XASlave] HasFlightUnlocked: territory={territory}, CanFly={canFly}");
            return canFly;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] HasFlightUnlocked error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if the player can mount in the current location.
    /// Equivalent to dfunc Player.CanMount (inverse of InSanctuary for mount-blocked zones).
    /// Uses ActionManager to check if Mount Roulette (GeneralAction 24) is usable.
    /// </summary>
    private static unsafe bool CanMount()
    {
        try
        {
            return FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 24) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks if the player is in a sanctuary — equivalent to dfunc InSanctuary().
    /// Returns true when the player CANNOT mount (inverse of CanMount).
    /// Matches SND's Player.CanMount logic: if CanMount == false then InSanctuary.
    /// </summary>
    private static unsafe bool InSanctuary()
    {
        return !CanMount();
    }

    // ═══════════════════════════════════════════════════════
    //  Movement State Helpers (vnavmesh IPC)
    //  Used to ensure movement is complete before sending new commands.
    //  Mirrors xafunc MoveTo Completed pattern.
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Checks if the navmesh is built and ready for pathfinding.
    /// If not ready, triggers a rebuild via vnavmesh.Nav.Rebuild.
    /// Returns true if ready, false if rebuild was triggered (caller should wait).
    /// </summary>
    private bool EnsureNavReady()
    {
        if (plugin.IpcClient.VnavIsReady())
            return true;

        Plugin.Log.Debug("[XASlave] Nav not ready — triggering rebuild");
        plugin.IpcClient.VnavRebuild();
        return false;
    }

    /// <summary>
    /// Checks if movement is idle (not pathfinding and not running).
    /// Use before sending a new movement command to avoid overlapping paths.
    ///
    /// States:
    ///   Nav.IsReady == false             → navmesh not built, need rebuild
    ///   PathfindInProgress == true       → calculating path, not yet moving
    ///   Path.IsRunning == true           → actively moving along path
    ///   Both false + IsReady == true     → idle, safe to send new movement
    /// </summary>
    private bool IsMovementIdle()
    {
        if (!plugin.IpcClient.VnavIsReady()) return false;
        if (plugin.IpcClient.VnavSimpleMovePathfindInProgress()) return false;
        if (plugin.IpcClient.VnavPathIsRunning()) return false;
        return true;
    }

    /// <summary>
    /// Async helper: waits until movement is complete (not pathfinding and not running).
    /// Returns true if movement completed, false if timed out.
    /// Equivalent to xafunc "MoveTo Completed" wait pattern.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> WaitForMovementComplete(int timeoutMs = 60000, int pollMs = 200)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs);
            elapsed += pollMs;

            var idle = await Plugin.Framework.RunOnFrameworkThread(() => IsMovementIdle());
            if (idle) return true;
        }
        return false;
    }
}
