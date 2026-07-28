using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XASlave.Services;
using XASlave.Services.Tasks;
using XASlave.Data;

namespace XASlave.Windows;

/// <summary>
/// Debug / Test Commands panel, partial class split from SlaveWindow.cs.
/// Contains DrawDebugCommands(), SetDebugResult(), IsMounted(), HasFlightUnlocked(), CanMount(), InSanctuary().
/// </summary>
public partial class SlaveWindow
{
    // -----------------------------------------------
    //  Debug / Test Commands
    //  Test buttons for all xafunc-referenced commands
    //  These functions will be used as templates for future tasks
    // -----------------------------------------------
    private string debugResult = string.Empty;
    private string debugTargetPlayerName = string.Empty;
    private string debugCallbackAddonName = "SelectString";
    private string debugCallbackValues = "0";
    private int debugAutoRetainerItemId;
    private int debugAutoRetainerDepositGilKeepAmount = 1_000_000;
    private DateTime debugResultExpiry = DateTime.MinValue;
    private bool debugXaFcChestCheckRunning;
    private bool debugAutoRetainerDepositGilActive;
    private bool debugAutoRetainerDepositGilListTaskRequested;
    private bool debugAutoRetainerDepositGilProcessingRetainer;
    private bool debugAutoRetainerDepositGilSubscribed;
    private bool debugAutoRetainerDepositGilOpeningRetainerList;
    private bool debugAutoRetainerNonAutoInteractActive;
    private int debugAutoRetainerDepositGilProcessedRetainers;
    private long debugAutoRetainerDepositGilRequestedTotal;
    private long debugAutoRetainerDepositGilActualTotal;
    private DateTime debugAutoRetainerDepositGilStartedUtc = DateTime.MinValue;
    private DateTime debugAutoRetainerDepositGilLastActivityUtc = DateTime.MinValue;
    private DateTime debugAutoRetainerNonAutoInteractStartedUtc = DateTime.MinValue;
    private const string DebugAutoRetainerDepositGilPluginName = "XASlave.DepositGilDebug";
    private const int DebugAutoRetainerGilCap = 999_999_999;
    private const string XaAbuseDisplayName = "I Love XA!";
    private const string XaAbuseDefaultOverlayText = " ";
    private const string XaAbuseDefaultTexturePath = "ui/icon/084000/084209_hr1.tex";
    private static readonly Vector4 XaAbuseDefaultOverlayShadowColor = new Vector4(0.32f, 0.02f, 0.14f, 0.88f);
    private static readonly Vector4 XaAbuseDefaultOverlayFillColor = new Vector4(1.0f, 0.92f, 0.96f, 1.0f);
    private bool xaAbuseEnabled;
    private bool xaAbuseAllVisiblePlayers;
    private bool xaAbuseOverlayEnabled;
    private bool xaAbuseOverlayAllVisiblePlayers;
    private bool xaAbuseOverlayUseTexture;
    private string xaAbuseOverlayText = XaAbuseDefaultOverlayText;
    private string xaAbuseOverlayTexturePath = XaAbuseDefaultTexturePath;
    private Vector4 xaAbuseOverlayShadowColor = XaAbuseDefaultOverlayShadowColor;
    private Vector4 xaAbuseOverlayFillColor = XaAbuseDefaultOverlayFillColor;
    private DateTime xaAbuseOverlayEnabledAtUtc = DateTime.MinValue;
    private string xaDbIpcTestReport = string.Empty;
    private readonly record struct DalamudTestNotificationDefinition(
        string ButtonLabel,
        string ResultLabel,
        string Title,
        string Content,
        NotificationType Type);
    private readonly record struct DebugAutoRetainerDepositGilResult(
        long RequestedDeposit,
        long ActualDeposit,
        int RemainingGil,
        bool ShouldStop,
        bool IsFailure,
        string Message);
    private readonly record struct DebugAutoRetainerBellOpenResult(
        bool Opened,
        string Message);
    private enum DebugCallbackValueKind
    {
        Null,
        Int,
        UInt,
        Bool,
    }

    private readonly record struct DebugCallbackValue(
        DebugCallbackValueKind Kind,
        int IntValue,
        uint UIntValue,
        bool BoolValue,
        string Display);

    private static readonly DalamudTestNotificationDefinition[] DalamudTestNotificationDefinitions =
    [
        new("All / None", "general", "XA Test: General Notification", "General Dalamud notification for Hide All testing.", NotificationType.None),
        new("Updates", "updates", "Plugin updates available", "XA test: Dalamud update and plugin updates available.", NotificationType.Info),
        new("Plugin Lifecycle", "plugin lifecycle", "Plugin installed", "XA test: plugin enabled, plugin loaded, and plugin reloaded lifecycle notification.", NotificationType.Info),
        new("Plugin Errors", "plugin errors", "Plugin reload failed", "XA test: plugin load failed and this plugin is creating errors.", NotificationType.Error),
        new("Mod Alerts", "mod alerts", "Penumbra", "One or more mods failed to load. XA test mod manager alert for Penumbra and Glamourer.", NotificationType.Warning),
        new("Success / Info", "success/info", "Success", "XA test success/info notification.", NotificationType.Success),
        new("Warning / Error", "warning/error", "Warning", "XA test warning/error notification.", NotificationType.Warning),
    ];

    private void DrawDebugCommands()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Debug / Test Commands");
        ImGui.TextDisabled("Test individual xafunc commands. These are the building blocks for all tasks.");
        ImGui.TextDisabled("This menu stays visible until /xa debug is typed again.");
        ImGui.Spacing();

        // Status feedback
        if (!string.IsNullOrEmpty(debugResult) && DateTime.UtcNow < debugResultExpiry)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), debugResult);
            ImGui.Spacing();
        }

        // -- Plugin Status (same checker as Monthly Relogger) --
        DrawPluginStatusChecker();

        ImGui.Separator();
        ImGui.Spacing();

        // -- Scrollable test buttons region --
        // Top section (title, results, plugin status) stays pinned.
        // Everything below scrolls independently.
        using var scrollChild = Dalamud.Interface.Utility.Raii.ImRaii.Child("DebugScrollRegion", new Vector2(0, 0), false);
        if (!scrollChild.Success) return;

        // ----------------------------------------------
        // [Movement Functions]                        
        // ----------------------------------------------
        if (ImGui.TreeNode("Movement Functions"))
        {

        // ----------------------------------------------
        //  XA Lazy Movements
        // ----------------------------------------------
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

                            var (ringDist, pathActive) = await Plugin.Framework.Run(() =>
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

                            if (ringDist == float.MinValue) { SetDebugResult("Lost target, aborting"); break; }

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
                                SetDebugResult($"Arrived near {targetName} (ring={ringDist:F1}y, stalled, stopped)");
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled within {ringDist:F1}y of {targetName}, stopping");
                                break;
                            }
                            else if (stalled && jumpAttempts < 5)
                            {
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled at ring={ringDist:F1}y, jump attempt {jumpAttempts + 1}");
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

                            // Negative ring = overlapping hitboxes = very close, interact immediately
                            if (ringDist <= 0)
                            {
                                SetDebugResult($"Overlapping {targetName}: ring={ringDist:F1}y, interacting");
                                await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
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
                else SetDebugResult("Pathfind failed, vnav could not start route");
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
                    // Mount + path simultaneously, mount cast works while running, no need to wait
                    if (shouldMount)
                        await Plugin.Framework.Run(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));

                    var fly = canFly && shouldMount; // Only fly-path if flight is unlocked AND mounted
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

                        var (rd, cd, pathRunning, pathfinding) = await Plugin.Framework.Run(() =>
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

                        if (rd == float.MinValue) { SetDebugResult("Lost target, aborting"); break; }

                        // Negative ring = overlapping hitboxes = very close, interact immediately
                        if (rd <= 0)
                        {
                            plugin.IpcClient.VnavStop();
                            SetDebugResult($"Overlapping {targetName}: ring={rd:F1}y, interacting");
                            await Plugin.Framework.Run(() => AddonHelper.InteractWithTarget());
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

                            // Hard 2s delay after dismount, prevents "Unable to execute command while jumping" error
                            await System.Threading.Tasks.Task.Delay(2000);

                            // Brief ready check, wait up to 1.5s for character to be actionable after dismount
                            for (int sw = 0; sw < 15; sw++)
                            {
                                await System.Threading.Tasks.Task.Delay(100);
                                var charReady = await Plugin.Framework.Run(() =>
                                    MonthlyReloggerTask.IsPlayerAvailable() &&
                                    !Plugin.Condition[ConditionFlag.Casting]);
                                if (charReady) break;
                            }

                            // Re-check distance after dismount, large mounts expand player hitbox
                            // and dismounting may leave us further away than expected
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
                                // Too far after dismount, re-path on foot to close the gap
                                SetDebugResult($"Post-dismount too far: ring={postDismountRd:F1}y, re-pathing on foot");
                                Plugin.Log.Information($"[XASlave] PathSmartThenInteract: post-dismount ring={postDismountRd:F1}y > {interactRange:F1}y, re-pathing");
                                await Plugin.Framework.Run(() =>
                                    plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                                // Wait for re-path to complete
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
            ImGui.SetTooltip("Resets camera (VK_END) + holds W forward for 2 seconds, then auto-releases.\nFully automated, no manual release needed.");

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
            RunDebugMovingCheaterSmart();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mounts if needed + auto-detects flight: uses flyflag if flying unlocked, moveflag otherwise.\nMirrors DoNavFlySequenceXA logic from xafunc (Player.CanFly check).");

        ImGui.SameLine();
        if (ImGui.Button("MovingCheater (Fly)"))
        {
            RunDebugMovingCheaterFly();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fly to flag: mounts if needed + /vnav flyflag. Falls back to ground if flying not unlocked.");

        ImGui.SameLine();
        if (ImGui.Button("MovingCheater (Walk)"))
        {
            RunDebugMovingCheaterWalk();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force ground: mounts if needed + /vnav moveflag. Works everywhere including towns.");

        ImGui.Spacing();

        if (ImGui.Button("PvpMoveTo (Flag)"))
        {
            ChatHelper.SendMessage("/vnav moveflag");
            SetDebugResult("Sent: /vnav moveflag (PvpMoveToXA, no mount, ground pathfind)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ground pathfinds to map flag without mounting (PvpMoveToXA).\nIn full implementation, waits for casting to finish first.");

        ImGui.Spacing();
        } // end XA Lazy Movements

        ImGui.TreePop();
        } // end Movement Functions

        ImGui.Spacing();

        // ----------------------------------------------
        // [Aetheryte Functions]                        
        // ----------------------------------------------
        if (ImGui.TreeNode("Aetheryte Functions"))
        {
            if (ImGui.Button("GetAetherytesCount"))
            {
                var count = AetheryteData.GetAetherytesWithZoneIds().Count;
                SetDebugResult($"Total aetherytes: {count}");
            }
            ImGui.SameLine();
            if (ImGui.Button("GetZoneAetherytes"))
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var aetherytes = AetheryteData.GetAetherytesInZone(zoneId);
                var zoneName = aetherytes.Any() ? AetheryteData.GetAetherytesWithZoneIds()
                    .FirstOrDefault(x => x.ZoneId == zoneId)?.ZoneName ?? "Unknown" : "Unknown";
                SetDebugResult($"Zone [{zoneId}] {zoneName}: {aetherytes.Count} aetherytes");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Gets all aetherytes in the current zone using ZoneID lookup");

            ImGui.Spacing();

            if (ImGui.Button("ListCurrentZoneAetherytes"))
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var aetherytes = AetheryteData.GetAetherytesInZone(zoneId);
                var zoneName = aetherytes.Any() ? AetheryteData.GetAetherytesWithZoneIds()
                    .FirstOrDefault(x => x.ZoneId == zoneId)?.ZoneName ?? "Unknown" : "Unknown";
                
                if (aetherytes.Any())
                {
                    var aetheryteList = string.Join(", ", aetherytes.Take(10));
                    var more = aetherytes.Count > 10 ? $" (+{aetherytes.Count - 10} more)" : "";
                    SetDebugResult($"Zone [{zoneId}] {zoneName}: {aetheryteList}{more}");
                }
                else
                {
                    SetDebugResult($"Zone [{zoneId}] {zoneName}: No aetherytes found");
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Lists up to 10 aetherytes in the current zone");

            ImGui.SameLine();
            if (ImGui.Button("GetAetheryteZoneId"))
            {
                // Example: Get ZoneID for a specific aetheryte
                var testAetheryte = "Limsa Lominsa Aetheryte Plaza";
                var zoneId = AetheryteData.GetZoneIdForAetheryte(testAetheryte);
                var zoneName = AetheryteData.GetZoneNameForAetheryte(testAetheryte);
                SetDebugResult($"{testAetheryte} -> Zone [{zoneId}] {zoneName}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Example: Gets ZoneID for 'Limsa Lominsa Aetheryte Plaza'");

            ImGui.Spacing();

            if (ImGui.Button("ShowAetheryteZoneMapping"))
            {
                var mappings = AetheryteData.GetAetherytesByZoneId()
                    .Take(5) // Show first 5 zones to avoid spam
                    .Select(kvp => {
                        var zoneName = AetheryteData.GetAetherytesWithZoneIds()
                            .FirstOrDefault(x => x.ZoneId == kvp.Key)?.ZoneName ?? "Unknown";
                        var aetheryteCount = kvp.Value.Count;
                        var firstFew = string.Join(", ", kvp.Value.Take(3));
                        var more = kvp.Value.Count > 3 ? $" (+{kvp.Value.Count - 3})" : "";
                        return $"Zone [{kvp.Key}] {zoneName}: {aetheryteCount} aetherytes - {firstFew}{more}";
                    });
                
                var result = string.Join("\n", mappings);
                SetDebugResult($"Aetheryte-Zone mapping (first 5 zones):\n{result}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shows ZoneID -> aetheryte mapping for first 5 zones");

            ImGui.SameLine();
            if (ImGui.Button("DebugTerritoryLookup"))
            {
                var results = new List<string>();
                var testAetherytes = new[] { "Limsa Lominsa Aetheryte Plaza", "New Gridania", "Summerford Farms" };
                
                try
                {
                    var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
                    var territorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
                    
                    if (aetheryteSheet == null || territorySheet == null)
                    {
                        SetDebugResult("Error: Could not load aetheryte or territory sheets");
                        return;
                    }
                    
                    foreach (var testName in testAetherytes)
                    {
                        Aetheryte? foundAetheryte = null;
                        foreach (var aetheryte in aetheryteSheet)
                        {
                            if (!aetheryte.IsAetheryte) continue;
                            var name = aetheryte.PlaceName.ValueNullable?.Name.ToString();
                            if (name?.Equals(testName, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                foundAetheryte = aetheryte;
                                break;
                            }
                        }
                        
                        if (foundAetheryte.HasValue)
                        {
                            var row = foundAetheryte.Value;
                            var zoneId = row.Territory.RowId;
                            var territoryRow = territorySheet.GetRowOrDefault(zoneId);
                            var zoneName = territoryRow?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
                            var territoryExists = territoryRow.HasValue;
                            
                            results.Add($"{testName}:");
                            results.Add($"  Aetheryte RowId: {row.RowId}");
                            results.Add($"  Territory RowId: {zoneId}");
                            results.Add($"  Territory exists: {territoryExists}");
                            results.Add($"  Zone name: {zoneName}");
                            results.Add("");
                        }
                        else
                        {
                            results.Add($"{testName}: Aetheryte not found");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetDebugResult($"Debug error: {ex.Message}");
                    return;
                }
                
                SetDebugResult("Territory lookup details:\n\n" + string.Join("\n", results));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Debug territory lookup for specific aetherytes");

            ImGui.Spacing();

            if (ImGui.Button("ExportAllAetherytes"))
            {
                var aetherytes = AetheryteData.GetAetherytesWithZoneIds();
                var exportLines = aetherytes
                    .OrderBy(x => x.ZoneId)
                    .ThenBy(x => x.Name)
                    .Select(x => $"{x.ZoneId}\t{x.Name}\t{x.ZoneName}");
                
                var header = "ZoneId\tAetheryteName\tZoneName";
                var exportContent = string.Join("\n", new[] { header }.Concat(exportLines));
                
                SetDebugResult($"Exported {aetherytes.Count} aetherytes:\n\n{exportContent}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Exports all aetherytes with ZoneID, name, and zone name for analysis");

            ImGui.TreePop();
        }

        ImGui.Spacing();

        // ----------------------------------------------
        // [Player Checkers]                           
        // ----------------------------------------------
        if (ImGui.TreeNode("Player Checkers"))
        {

        // ----------------------------------------------
        //  Game State Checks (XA)
        // ----------------------------------------------
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
                    var (np, pa, casting, combat, charName) = await Plugin.Framework.Run(() =>
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
                        SetDebugResult($"[{consecutivePasses}/3] OK, {charName} (attempt #{totalAttempts})");
                    }
                    else
                    {
                        if (consecutivePasses > 0)
                            Plugin.Log.Information($"[XASlave] CharacterSafeWait: reset at {consecutivePasses}/3, NP={np} PA={pa} Cast={casting} Combat={combat}");
                        consecutivePasses = 0;
                        SetDebugResult($"[0/3] waiting... NP={np} PA={pa} Cast={casting} Combat={combat} (attempt #{totalAttempts})");
                    }
                }
                var finalName = await Plugin.Framework.Run(() => MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                SetDebugResult($"[3/3] CONFIRMED READY, {finalName}");
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
        if (ImGui.Button("IsInWorkshop"))
        {
            try
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var zoneName = AddonHelper.GetCurrentZoneName();
                var zoneMatch = AddonHelper.ZoneNameLooksLikeWorkshop(zoneName);
                var housingWorkshop = AddonHelper.IsInWorkshopByHousingManager();
                var finalWorkshop = AddonHelper.IsInWorkshop();
                SetDebugResult($"Workshop: final={finalWorkshop}, zoneMatch={zoneMatch}, housingManager={housingWorkshop}, zone=\"{zoneName}\" [{zoneId}]");
            }
            catch (Exception ex) { SetDebugResult($"Workshop check error: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shows the current zone name, whether it matches Company Workshop, the raw HousingManager workshop flag, and the final combined workshop result used by FC chest checks.");
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

        ImGui.TextDisabled("SelectString tester");
        if (ImGui.Button("SelectString: Active?"))
        {
            SetDebugResult(GetSelectStringDebugStatus());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks whether SelectString is visible and ready, then reports how many selectable menu rows XA can currently resolve.");
        ImGui.SameLine();
        if (ImGui.Button("SelectStringMenuItems"))
        {
            var dump = BuildSelectStringMenuDump();
            ImGui.SetClipboardText(dump);
            SetDebugResult($"{dump}\n(copied to clipboard)");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Builds a numbered SelectString option dump, including the raw callback value for each row, and copies it to the clipboard.");

        for (var callbackIndex = 0; callbackIndex < 8; callbackIndex++)
        {
            if (callbackIndex > 0 && callbackIndex % 4 != 0)
                ImGui.SameLine();

            var buttonLabel = $"Select {callbackIndex + 1} [{callbackIndex}]";
            if (ImGui.Button(buttonLabel))
                RunDebugSelectStringOption(callbackIndex);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Fires AddonHelper.FireCallback(\"SelectString\", {callbackIndex}) to test SelectString option {callbackIndex + 1}.");
        }

        } // end Game State Checks

        // ----------------------------------------------
        //  Target Game State Checks
        // ----------------------------------------------
        if (ImGui.CollapsingHeader("Target Checks"))
        {
        ImGui.Spacing();

        ImGui.TextDisabled("TargetByName tester");
        ImGui.SetNextItemWidth(260f);
        var targetEnterPressed = ImGui.InputTextWithHint(
            "##debugTargetPlayerName",
            "Player name, e.g. User N'ame",
            ref debugTargetPlayerName,
            128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Target Player") || targetEnterPressed))
            RunDebugTargetByName();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs AddonHelper.TargetByName with the typed player name. This preserves quoted /target fallback behavior for apostrophe names.");
        ImGui.SameLine();
        if (ImGui.Button("Focus Target"))
            RunDebugFocusCurrentTarget();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sets the current target as focus target through Dalamud TargetManager instead of sending /focustarget.");
        ImGui.SameLine();
        if (ImGui.Button("Clear Focus"))
            RunDebugClearFocusTarget();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears the current focus target through Dalamud TargetManager instead of sending /focustarget with no selected target.");

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

        // ----------------------------------------------
        //  Player State Checks (d)
        // ----------------------------------------------
        if (ImGui.CollapsingHeader("Player State Checks (d)"))
        {
        ImGui.Spacing();

        if (ImGui.Button("IsMounted?"))
        {
            var mounted = IsMounted();
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
                SetDebugResult("Not in a duty, nothing to leave.");
            }
            else
            {
                SetDebugResult("In duty, attempting to leave...");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Wait up to 30s if in combat (might be finishing monsters)
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

                    // Press U to open the Duty Finder menu (ContentsFinderMenu)
                    await Plugin.Framework.Run(() => KeyInputHelper.PressKey(0x55)); // VK_U = 0x55
                    await System.Threading.Tasks.Task.Delay(1000);

                    // Click the Leave button, ContentsFinderMenu NodeList[43]
                    var leaveClicked = await Plugin.Framework.Run(() =>
                        AddonHelper.ClickAddonButton("ContentsFinderMenu", 43));

                    if (leaveClicked)
                    {
                        SetDebugResult("Leave Duty: clicked Leave button, waiting for confirmation...");
                        await System.Threading.Tasks.Task.Delay(500);

                        // Click Yes on the confirmation dialog
                        var yesClicked = await Plugin.Framework.Run(() => AddonHelper.ClickYesNo(true));
                        if (yesClicked)
                            SetDebugResult("Leave Duty: confirmed Yes, leaving instance.");
                        else
                            SetDebugResult("Leave Duty: Leave clicked but SelectYesno not visible, may need manual confirm.");
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
                        var flames = ps->GCRanks[2];
                        var adders = ps->GCRanks[1];
                        var mael = ps->GCRanks[0];
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
            sb.AppendLine($"  Honorific       = {plugin.IpcClient.IsHonorificAvailable()}");
            var result = sb.ToString();
            ImGui.SetClipboardText(result);
            SetDebugResult("IPC status copied to clipboard");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Checks all 12 IPC integrations and copies results to clipboard.\nEquivalent to dfunc GetInternalNamesIPC / GetIPCRegisteredTables");

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
                SetDebugResult(ok ? "Clicked _CharaSelectReturn NodeList[1], exiting to main menu" : "Click failed on _CharaSelectReturn");
            }
            else
            {
                SetDebugResult("_CharaSelectReturn not visible, not on character select screen.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks _CharaSelectReturn NodeList[1] to exit character select\nand return to the main menu.");

        ImGui.Spacing();
        } // end Player State Checks

        // ----------------------------------------------
        //  Character Actions (xafunc equivalents)
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  Player Commands
        // ----------------------------------------------
        if (ImGui.CollapsingHeader("Player Commands"))
        {
        ImGui.Spacing();

        if (ImGui.Button("Interact"))
        {
            var ok = AddonHelper.InteractWithTarget();
            SetDebugResult(ok ? "InteractWithTarget: OK (InteractXA)" : "InteractWithTarget: No target or failed");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses TargetSystem.InteractWithObject, native replacement for SND /interact");

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
            SetDebugResult("Opened Character window, next: Step2 to fire callback");
        }
        ImGui.SameLine();
        if (ImGui.Button("EquipGear Step2: Recommend"))
        {
            var ok = AddonHelper.ClickAddonButton("Character", 74);
            SetDebugResult(ok ? "Clicked Character NodeList[74] (Button #12) †’ RecommendEquip should open" : "Character addon not visible, open it first with Step1");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks Button Component Node #12 at NodeList[74] in Character addon.\nOpens Recommended Gear window (RecommendEquip).\nConfirmed via /xldata Addon Inspector.");

        ImGui.Spacing();

        if (ImGui.Button("EquipGear Step3: Equip"))
        {
            var ok = AddonHelper.ClickAddonButton("RecommendEquip", 3);
            SetDebugResult(ok ? "Clicked RecommendEquip NodeList[3] (Button #11) †’ gear equipped" : "RecommendEquip addon not visible, run Step2 first");
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
            ImGui.SetTooltip("Presses END key to the current FFXIV game window, native replacement for SND /send END");

        ImGui.Spacing();
        } // end Player Commands

        // ----------------------------------------------
        //  XA Database
        // ----------------------------------------------
        if (ImGui.CollapsingHeader("XA Database##playerCheckers"))
        {
        ImGui.Spacing();

        if (ImGui.Button("XA: Save"))
        {
            var ok = plugin.SaveToXaDatabaseAndRecordSync();
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

        if (ImGui.Button("XA: Check FC Chest"))
        {
            RunXaDatabaseFcChestDebugCheck();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Workshop-only debug pass for FC chest gil capture.\nChecks zone name, targets Company Chest, paths to 1.5y, interacts, saves to XA Database, and sends ESC until FreeCompanyChest closes.");

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
        if (ImGui.Button("Housing Command"))
        {
            ChatHelper.SendMessage("/housing");
            SetDebugResult("Sent: /housing");
        }
        ImGui.SameLine();
        if (ImGui.Button("XA: GetVersion"))
        {
            var ver = plugin.IpcClient.GetVersion();
            SetDebugResult($"XA Database Version: {ver}");
        }

        ImGui.Spacing();

        if (ImGui.Button("Housing: Click Estate Menu"))
        {
            var variant = AddonHelper.GetHousingMenuVariant();
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingMenu", "Estate Settings");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingMenu", "Estate Settings");
            SetDebugResult(ok
                ? $"Selected Estate Settings via callback {callbackIndex} ({(string.IsNullOrEmpty(variant) ? "HousingMenu" : variant)})"
                : "HousingMenu not visible or Estate Settings callback not resolved");
        }
        ImGui.SameLine();
        if (ImGui.Button("Housing: Variant"))
        {
            var variant = AddonHelper.GetHousingMenuVariant();
            SetDebugResult(!string.IsNullOrEmpty(variant) ? $"Housing menu: {variant}" : "HousingMenu not visible");
        }
        ImGui.SameLine();
        if (ImGui.Button("Housing: Read Address"))
        {
            var address = AddonHelper.GetAddonTextEntries("HousingSignBoard")
                .FirstOrDefault(text => text.Contains("Ward", StringComparison.OrdinalIgnoreCase) && text.Contains(",", StringComparison.Ordinal));
            SetDebugResult(!string.IsNullOrEmpty(address) ? $"HousingSignBoard: {address}" : "HousingSignBoard not visible or address text not found");
        }

        ImGui.Spacing();

        if (ImGui.Button("Housing: Apartment"))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Apartment");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Apartment");
            SetDebugResult(ok ? $"Selected HousingSelectHouse -> Apartment via callback {callbackIndex}" : "HousingSelectHouse not visible or Apartment callback not resolved");
        }
        ImGui.SameLine();
        if (ImGui.Button("Housing: FC Estate"))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Free Company Estate");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Free Company Estate");
            SetDebugResult(ok ? $"Selected HousingSelectHouse -> Free Company Estate via callback {callbackIndex}" : "HousingSelectHouse not visible or Free Company Estate callback not resolved");
        }
        ImGui.SameLine();
        if (ImGui.Button("Housing: Private Estate"))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Private Estate");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Private Estate");
            SetDebugResult(ok ? $"Selected HousingSelectHouse -> Private Estate via callback {callbackIndex}" : "HousingSelectHouse not visible or Private Estate callback not resolved");
        }
        ImGui.SameLine();
        if (ImGui.Button("Housing: Shared Estate"))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Shared Estate", true);
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Shared Estate", true);
            SetDebugResult(ok ? $"Selected HousingSelectHouse -> Shared Estate via callback {callbackIndex}" : "HousingSelectHouse not visible or Shared Estate callback not resolved");
        }

        ImGui.Spacing();

        if (ImGui.Button("Housing: View Details"))
        {
            var ok = AddonHelper.SelectFirstAddonListText(
                "HousingSubmenu",
                out var callbackIndex,
                out var matchedText,
                ("View Room Details", false),
                ("View Estate Details", false),
                ("Details", true));
            SetDebugResult(ok ? $"Selected HousingSubmenu -> {matchedText} via callback {callbackIndex}" : "HousingSubmenu not visible or no details callback resolved");
        }

        ImGui.Spacing();
        } // end XA Database

        ImGui.TreePop();
        } // end Player Checkers

        if (ImGui.TreeNode("XA Abuse"))
        {

        if (ImGui.CollapsingHeader("PlayerNames##xaAbuse"))
        {
            ImGui.TextDisabled("Testing-build nameplate override + optional floating overlay.");
            ImGui.TextDisabled($"Status: {GetXaAbuseStatusText()}");
            ImGui.Spacing();
            
            if (ImGui.Button("Set Name: I Love XA!"))
            {
                EnableXaAbuse(false);
            }
            ImGui.SameLine();
            if (ImGui.Button("Set ALL Visible Player Names"))
            {
                EnableXaAbuse(true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset Name / Overlay"))
            {
                ResetXaAbuse();
            }

            if (ImGui.Button("Overlay: Me"))
            {
                EnableXaAbuseOverlay(false);
            }
            ImGui.SameLine();
            if (ImGui.Button("Overlay: ALL Visible Heads"))
            {
                EnableXaAbuseOverlay(true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Overlay Off"))
            {
                DisableXaAbuseOverlay();
            }

            var useTextureOverlay = xaAbuseOverlayUseTexture;
            if (ImGui.Checkbox("Use .tex Overlay##xaAbuseUseTex", ref useTextureOverlay))
                xaAbuseOverlayUseTexture = useTextureOverlay;
            ImGui.SameLine();
            if (ImGui.Button("Use Orb .tex Test"))
            {
                xaAbuseOverlayUseTexture = true;
                xaAbuseOverlayTexturePath = XaAbuseDefaultTexturePath;
                SetDebugResult($"XA Abuse: overlay path set to {XaAbuseDefaultTexturePath}");
            }
            ImGui.SameLine();
            if (ImGui.Button("Use Default ™¥"))
            {
                xaAbuseOverlayUseTexture = false;
                xaAbuseOverlayText = XaAbuseDefaultOverlayText;
                SetDebugResult("XA Abuse: overlay text reset to ™¥");
            }

            ImGui.SetNextItemWidth(Scale(220f));
            ImGui.InputText("Overlay Text##xaAbuseOverlayText", ref xaAbuseOverlayText, 64);
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("Overlay .tex Path##xaAbuseOverlayPath", ref xaAbuseOverlayTexturePath, 260);
            ImGui.SetNextItemWidth(Scale(240f));
            ImGui.ColorEdit4("Text Fill Color##xaAbuseOverlayFillColor", ref xaAbuseOverlayFillColor, ImGuiColorEditFlags.AlphaBar);
            ImGui.SetNextItemWidth(Scale(240f));
            ImGui.ColorEdit4("Text Shadow Color##xaAbuseOverlayShadowColor", ref xaAbuseOverlayShadowColor, ImGuiColorEditFlags.AlphaBar);
            if (ImGui.Button("Reset Text Colors##xaAbuseResetTextColors"))
            {
                xaAbuseOverlayFillColor = XaAbuseDefaultOverlayFillColor;
                xaAbuseOverlayShadowColor = XaAbuseDefaultOverlayShadowColor;
            }

            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Lobby Test##xaAbuse"))
        {
            DrawXaAbuseLobbyTest();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Callbacks##xaAbuse"))
        {
            DrawXaAbuseCallbacks();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Dalamud Test Notifications##xaAbuse"))
        {
            DrawDalamudTestNotifications();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("XA Database##xaAbuse"))
        {
            DrawXaAbuseXaDatabaseIpcTests();
            ImGui.Spacing();
        }

        ImGui.Spacing();
        ImGui.TreePop();
        }

        ImGui.Spacing();

        // ----------------------------------------------
        //   [Punish]                                    
        // ----------------------------------------------
        if (ImGui.TreeNode("Punish"))
        {

        // ----------------------------------------------
        //  AutoRetainer
        // ----------------------------------------------
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
        if (ImGui.Button("AR Suppressed?"))
        {
            var suppressed = plugin.IpcClient.AutoRetainerGetSuppressed();
            SetDebugResult($"AutoRetainer.GetSuppressed: {suppressed}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR Available?"))
        {
            var avail = plugin.IpcClient.IsAutoRetainerAvailable();
            SetDebugResult($"AutoRetainer available: {avail}");
        }

        ImGui.Spacing();

        if (ImGui.Button("AR IsBusy?##arPluginStateBusy"))
        {
            var busy = plugin.IpcClient.AutoRetainerPluginStateIsBusy();
            SetDebugResult($"AutoRetainer.PluginState.IsBusy: {busy}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR Retainers Ready?##arPluginStateRetainers"))
        {
            var ready = plugin.IpcClient.AutoRetainerPluginStateAreAnyRetainersAvailableForCurrentChara();
            SetDebugResult($"AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara: {ready}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR Multi Status?##arPluginStateMulti"))
        {
            var enabled = plugin.IpcClient.AutoRetainerPluginStateGetMultiModeStatus();
            SetDebugResult($"AutoRetainer.PluginState.GetMultiModeStatus: {enabled}");
        }

        if (ImGui.Button("AR Can Auto Login?##arPluginStateCanAutoLogin"))
        {
            var canAutoLogin = plugin.IpcClient.AutoRetainerPluginStateCanAutoLogin();
            SetDebugResult($"AutoRetainer.PluginState.CanAutoLogin: {canAutoLogin}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR RetainerSense?##arPluginStateRetainerSense"))
        {
            var retainerSense = plugin.IpcClient.AutoRetainerPluginStateGetOptionRetainerSense();
            SetDebugResult($"AutoRetainer.PluginState.GetOptionRetainerSense: {retainerSense}");
        }

        ImGui.SetNextItemWidth(Scale(120f));
        ImGui.InputInt("Item ID##arPluginStateItemProtectedId", ref debugAutoRetainerItemId);
        ImGui.SameLine();
        if (ImGui.Button("AR Item Protected?##arPluginStateItemProtected"))
        {
            var itemId = Math.Max(0, debugAutoRetainerItemId);
            var protectedItem = plugin.IpcClient.AutoRetainerPluginStateIsItemProtected((uint)itemId);
            SetDebugResult($"AutoRetainer.PluginState.IsItemProtected({itemId}): {protectedItem}");
        }

        var arLocalContentId = Plugin.PlayerState.ContentId;
        if (ImGui.Button("AR Vessels Ready?##arPluginStateVesselsReady"))
        {
            var ready = plugin.IpcClient.AutoRetainerPluginStateAreAnyEnabledVesselsReady(arLocalContentId);
            SetDebugResult($"AutoRetainer.PluginState.AreAnyEnabledVesselsReady({arLocalContentId}): {FormatNullableBool(ready)}");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR Vessels Not Deployed?##arPluginStateVesselsNotDeployed"))
        {
            var notDeployed = plugin.IpcClient.AutoRetainerPluginStateAreAnyEnabledVesselsNotDeployed(arLocalContentId);
            SetDebugResult($"AutoRetainer.PluginState.AreAnyEnabledVesselsNotDeployed({arLocalContentId}): {FormatNullableBool(notDeployed)}");
        }

        ImGui.Spacing();

        if (ImGui.Button("Non-Auto Interact##arNonAutoInteract"))
        {
            StartDebugAutoRetainerNonAutoInteract();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Temporarily suppresses AutoRetainer, interacts with Summoning Bell, disables AR's bell auto-enable state, then releases suppression once RetainerList is active.");
        if (debugAutoRetainerNonAutoInteractActive)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Non-Auto Interact: opening RetainerList under temporary AR suppression");
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(Scale(150f));
        if (ImGui.InputInt("Keep Gil##arDepositGilKeepAmount", ref debugAutoRetainerDepositGilKeepAmount, 100000, 1000000)
            && debugAutoRetainerDepositGilKeepAmount < 0)
        {
            debugAutoRetainerDepositGilKeepAmount = 0;
        }
        ImGui.SameLine();
        if (ImGui.Button("Deposit Gil##arDepositGil"))
        {
            StartDebugAutoRetainerDepositGil();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens Summoning Bell under temporary AutoRetainer suppression if RetainerList is not active, then arms AutoRetainer's retainer-list custom task.");

        if (debugAutoRetainerDepositGilActive)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##arDepositGilCancel"))
            {
                StopDebugAutoRetainerDepositGil("AR deposit gil debug task cancelled.", finishCurrentRetainer: false);
            }

            var state = debugAutoRetainerDepositGilProcessingRetainer
                ? "processing retainer"
                : debugAutoRetainerDepositGilListTaskRequested
                    ? "AR task queued"
                    : debugAutoRetainerDepositGilOpeningRetainerList
                        ? "opening RetainerList"
                    : "waiting for RetainerList";
            ImGui.TextDisabled($"Deposit Gil: {state}, keep {Math.Max(0, debugAutoRetainerDepositGilKeepAmount):N0}, retainers {debugAutoRetainerDepositGilProcessedRetainers}, requested {debugAutoRetainerDepositGilRequestedTotal:N0}, actual {debugAutoRetainerDepositGilActualTotal:N0}");
        }

        ImGui.Spacing();

        if (ImGui.Button("ARDiscard"))
        {
            ChatHelper.SendMessage("/ays discard");
            SetDebugResult("Sent: /ays discard (ARDiscard)");
        }
        ImGui.SameLine();
        if (ImGui.Button("AR ItemSell"))
        {
            ChatHelper.SendMessage("/ays itemsell");
            SetDebugResult("Sent: /ays itemsell (AR ItemSell)");
        }

        ImGui.Spacing();
        } // end AutoRetainer

        // ----------------------------------------------
        //  Lifestream
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  TextAdvance
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  YesAlready
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  Artisan
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  Dropbox
        // ----------------------------------------------
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
        if (ImGui.Button("Enable Auto-Accept Trades"))
        {
            try
            {
                var dropboxPlugin = GetDropboxPlugin();
                if (dropboxPlugin != null)
                {
                    var config = GetPluginConfig(dropboxPlugin);
                    if (config != null)
                    {
                        SetConfigProperty(config, "Active", true);
                        SetDebugResult("Dropbox: Auto-Accept Trades ENABLED");
                    }
                    else
                    {
                        SetDebugResult("Dropbox: Could not access configuration");
                    }
                }
                else
                {
                    SetDebugResult("Dropbox: Plugin not found or not loaded");
                }
            }
            catch (Exception ex)
            {
                SetDebugResult($"Dropbox: Error enabling auto-accept - {ex.Message}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Disable Auto-Accept Trades"))
        {
            try
            {
                var dropboxPlugin = GetDropboxPlugin();
                if (dropboxPlugin != null)
                {
                    var config = GetPluginConfig(dropboxPlugin);
                    if (config != null)
                    {
                        SetConfigProperty(config, "Active", false);
                        SetDebugResult("Dropbox: Auto-Accept Trades DISABLED");
                    }
                    else
                    {
                        SetDebugResult("Dropbox: Could not access configuration");
                    }
                }
                else
                {
                    SetDebugResult("Dropbox: Plugin not found or not loaded");
                }
            }
            catch (Exception ex)
            {
                SetDebugResult($"Dropbox: Error disabling auto-accept - {ex.Message}");
            }
        }

        if (ImGui.Button("Begin Trading"))
        {
            RunDebugDropboxBeginTrading(focusCurrentTargetFirst: false);
        }
        ImGui.SameLine();
        if (ImGui.Button("Focus Target + Begin Trading"))
        {
            RunDebugDropboxBeginTrading(focusCurrentTargetFirst: true);
        }

        if (ImGui.Button("Stop Item Trade Queue"))
        {
            try
            {
                var dropboxPlugin = GetDropboxPlugin();
                if (dropboxPlugin != null)
                {
                    var taskManager = GetDropboxTaskManager(dropboxPlugin);
                    if (taskManager != null)
                    {
                        AbortTaskManager(taskManager);
                        SetDebugResult("Dropbox: Item Trade Queue STOPPED");
                    }
                    else
                    {
                        SetDebugResult("Dropbox: Could not access item trade queue task manager");
                    }
                }
                else
                {
                    SetDebugResult("Dropbox: Plugin not found or not loaded");
                }
            }
            catch (Exception ex)
            {
                SetDebugResult($"Dropbox: Error stopping item trade queue - {ex.Message}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("_TextError Monitor"))
        {
            SetDebugResult(GetTextErrorMonitorResult());
        }

        if (ImGui.Button("Open Item Trade Queue Tab"))
        {
            try
            {
                var dropboxPlugin = GetDropboxPlugin();
                if (dropboxPlugin != null)
                {
                    var uiOpened = TryOpenDropboxUi(dropboxPlugin);
                    var tabRequested = RequestDropboxOpenTabOnce(dropboxPlugin, "Item Trade Queue");
                    if (tabRequested)
                    {
                        SetDebugResult("Dropbox: Item Trade Queue tab OPEN requested once");
                    }
                    else if (uiOpened)
                    {
                        SetDebugResult("Dropbox: Opened UI but could not request Item Trade Queue tab");
                    }
                    else
                    {
                        SetDebugResult("Dropbox: Could not open Item Trade Queue tab");
                    }
                }
                else
                {
                    SetDebugResult("Dropbox: Plugin not found or not loaded");
                }
            }
            catch (Exception ex)
            {
                SetDebugResult($"Dropbox: Error opening Item Trade Queue tab - {ex.Message}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Open Whitelist Tab"))
        {
            try
            {
                var dropboxPlugin = GetDropboxPlugin();
                if (dropboxPlugin != null)
                {
                    var uiOpened = TryOpenDropboxUi(dropboxPlugin);
                    var tabRequested = RequestDropboxOpenTabOnce(dropboxPlugin, "Whitelist");
                    if (tabRequested)
                    {
                        SetDebugResult("Dropbox: Whitelist tab OPEN requested once");
                    }
                    else if (uiOpened)
                    {
                        SetDebugResult("Dropbox: Opened UI but could not request Whitelist tab");
                    }
                    else
                    {
                        SetDebugResult("Dropbox: Could not open Whitelist tab");
                    }
                }
                else
                {
                    SetDebugResult("Dropbox: Plugin not found or not loaded");
                }
            }
            catch (Exception ex)
            {
                SetDebugResult($"Dropbox: Error opening Whitelist tab - {ex.Message}");
            }
        }

        if (ImGui.Button("Accept Trades Only From Whitelisted Characters"))
        {
            try
            {
                var dropboxPlugin = GetDropboxPlugin();
                if (dropboxPlugin != null)
                {
                    var config = GetPluginConfig(dropboxPlugin);
                    if (config != null)
                    {
                        SetConfigProperty(config, "WhitelistMode", true);
                        SetDebugResult("Dropbox: Whitelist-only trade acceptance ENABLED");
                    }
                    else
                    {
                        SetDebugResult("Dropbox: Could not access configuration");
                    }
                }
                else
                {
                    SetDebugResult("Dropbox: Plugin not found or not loaded");
                }
            }
            catch (Exception ex)
            {
                SetDebugResult($"Dropbox: Error enabling whitelist-only trade acceptance - {ex.Message}");
            }
        }

        ImGui.Spacing();
        } // end Dropbox

        // ----------------------------------------------
        //  Pandoras Box
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  Deliveroo
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  Splatoon
        // ----------------------------------------------
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

        // ----------------------------------------------
        //  vnavmesh
        // ----------------------------------------------
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
            ImGui.SetTooltip("Checks if player cannot mount †’ sanctuary.\nEquivalent to dfunc InSanctuary() / !Player.CanMount");

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

        // ----------------------------------------------
        //   [Key Inputs]                                
        // ----------------------------------------------
        if (ImGui.CollapsingHeader("Key Inputs"))
        {
        ImGui.TextDisabled("Win32 game-window key simulation for FFXIV input");
        ImGui.Spacing();

        ImGui.TextDisabled("Methods:");
        ImGui.TextDisabled("  PressKey(vk)                              tap key (down+up)");
        ImGui.TextDisabled("  HoldKey(vk)                                key down only");
        ImGui.TextDisabled("  ReleaseKey(vk)                          key up only");
        ImGui.TextDisabled("  HoldKeyForDuration(vk, ms)   hold + auto-release");
        ImGui.Spacing();

        ImGui.TextDisabled("Available VK Constants:");
        ImGui.TextDisabled("  Movement:  VK_W (0x57)  VK_A (0x41)  VK_S (0x53)  VK_D (0x44)");
        ImGui.TextDisabled("  Special:   VK_END (0x23)  VK_HOME (0x24)  VK_ESCAPE (0x1B)  VK_RETURN (0x0D)");
        ImGui.TextDisabled("             VK_SPACE (0x20)  VK_TAB (0x09)  VK_DELETE (0x2E)  VK_INSERT (0x2D)");
        ImGui.TextDisabled("  Arrow:     VK_LEFT (0x25)  VK_UP (0x26)  VK_RIGHT (0x27)  VK_DOWN (0x28)");
        ImGui.TextDisabled("  Modifier:  VK_SHIFT (0x10)  VK_CONTROL (0x11)  VK_ALT (0x12)");
        ImGui.TextDisabled("  Numpad:    VK_NUMPAD09 (0x60x69)");
        ImGui.TextDisabled("  Function:  VK_F1F12 (0x70x7B)");
        ImGui.TextDisabled("  Letters:   0x410x5A (AZ)    Numbers: 0x300x39 (09)");

        ImGui.Spacing();
        ImGui.TextDisabled("Tap Tests:");

        if (ImGui.Button("Tap ESC"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
            SetDebugResult("KeyInput test: tapped ESC");
        }

        ImGui.SameLine();
        if (ImGui.Button("Tap SPACE"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
            SetDebugResult("KeyInput test: tapped SPACE");
        }

        ImGui.SameLine();
        if (ImGui.Button("Tap U"))
        {
            KeyInputHelper.PressKey(0x55);
            SetDebugResult("KeyInput test: tapped U (0x55)");
        }

        if (ImGui.Button("Tap NUMPAD0"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_NUMPAD0);
            SetDebugResult("KeyInput test: tapped NUMPAD0");
        }

        ImGui.SameLine();
        if (ImGui.Button("Tap CTRL"))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_CONTROL);
            SetDebugResult("KeyInput test: tapped CTRL");
        }

        ImGui.SameLine();
        if (ImGui.Button("Hold CTRL"))
        {
            KeyInputHelper.HoldKey(KeyInputHelper.VK_CONTROL);
            SetDebugResult("KeyInput test: holding CTRL");
        }

        ImGui.SameLine();
        if (ImGui.Button("Release CTRL"))
        {
            KeyInputHelper.ReleaseKey(KeyInputHelper.VK_CONTROL);
            SetDebugResult("KeyInput test: released CTRL");
        }

        ImGui.TextDisabled("Use these to confirm whether tap/hold/release inputs are actually accepted by the game client.");
        ImGui.Spacing();
        } // end Key Inputs

    }

    private void DrawXaAbuseCallbacks()
    {
        ImGui.SetNextItemWidth(Scale(220f));
        ImGui.InputText("Addon##xaAbuseCallbackAddon", ref debugCallbackAddonName, 96);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Values##xaAbuseCallbackValues", ref debugCallbackValues, 256);

        if (ImGui.Button("Fire Callback##xaAbuseCallbackFire"))
            RunDebugRawAddonCallback();

        ImGui.SameLine();
        if (ImGui.Button("Addon Active?##xaAbuseCallbackAddonActive"))
        {
            var addonName = debugCallbackAddonName.Trim();
            if (string.IsNullOrWhiteSpace(addonName))
                SetDebugResult("Callback test: enter an addon name.");
            else
                SetDebugResult($"{addonName}: visible={AddonHelper.IsAddonVisible(addonName)}, ready={AddonHelper.IsAddonReady(addonName)}");
        }

        ImGui.TextDisabled("Examples: -1 true | true -1 | 0 3 | uint:999999999 | null");
    }

    private void DrawDalamudTestNotifications()
    {
        ImGui.TextDisabled("Creates real Dalamud ImGui notifications for testing the UI Mods suppression categories.");
        ImGui.TextDisabled($"Suppressor: {plugin.DalamudNotificationsSuck.StatusText}");

        var buttonSize = new Vector2(Scale(160f), 0f);

        for (var i = 0; i < DalamudTestNotificationDefinitions.Length; i++)
        {
            var definition = DalamudTestNotificationDefinitions[i];
            if (ImGui.Button($"{definition.ButtonLabel}##DalamudTestNotification_{i}", buttonSize))
                SendDalamudTestNotification(definition);

            if (i % 3 != 2 && i < DalamudTestNotificationDefinitions.Length - 1)
                ImGui.SameLine();
        }
    }

    private void SendDalamudTestNotification(DalamudTestNotificationDefinition definition)
    {
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = definition.Title,
            Content = definition.Content,
            MinimizedText = definition.ButtonLabel,
            Type = definition.Type,
            InitialDuration = TimeSpan.FromSeconds(8),
            ExtensionDurationSinceLastInterest = TimeSpan.FromSeconds(8),
            RespectUiHidden = false,
            Minimized = false,
        });

        SetDebugResult($"Dalamud test notification queued: {definition.ResultLabel}");
    }

    private void DrawXaAbuseLobbyTest()
    {
        var monitorStatus = plugin.LobbyErrorAutoClose.GetLobbyMonitorDebugStatus();
        var status = plugin.LobbyErrorAutoClose.GetNoKillPluginPanelDebugStatus();
        var monitorText = monitorStatus.ShouldMonitor ? "true" : "false";
        var activeText = status.IsVisible ? "true" : "false";
        var monitorColor = monitorStatus.ShouldMonitor
            ? new Vector4(0.45f, 1.0f, 0.45f, 1.0f)
            : new Vector4(0.82f, 0.82f, 0.82f, 1.0f);
        var activeColor = status.IsVisible
            ? new Vector4(0.45f, 1.0f, 0.45f, 1.0f)
            : new Vector4(0.82f, 0.82f, 0.82f, 1.0f);

        ImGui.TextDisabled("Close Lobby Errors NoKillPlugin panel diagnostics.");
        ImGui.TextColored(monitorColor, $"Lobby monitor active: {monitorText}");
        ImGui.TextDisabled($"Requested: {monitorStatus.MonitorRequested} | Dialogue listener: {monitorStatus.DialogueListenerSubscribed} | Framework: {monitorStatus.FrameworkSubscribed} | Logged in: {monitorStatus.ClientLoggedIn}");
        ImGui.TextDisabled($"Dialogue visible: {monitorStatus.DialogueVisible} | Ready: {monitorStatus.DialogueReady} | Supported: {monitorStatus.DialogueSupported} | Match: {monitorStatus.DialogueMatch}");
        ImGui.TextDisabled($"10s window active: {monitorStatus.DialogueWindowActive} | Remaining: {monitorStatus.SecondsRemaining:0.0}s | _TitleMenu visible: {monitorStatus.TitleMenuVisible}");
        ImGui.TextWrapped($"Dialogue text: {monitorStatus.DialogueText}");
        ImGui.TextDisabled(monitorStatus.Detail);
        ImGui.TextColored(activeColor, $"Is No Kill plugin window active: {activeText}");
        ImGui.TextDisabled($"Loaded: {status.PluginLoaded} | Instance: {status.RuntimeInstanceResolved} | Window: {status.ConfigWindowResolved} | Visible field: {status.VisibilityMemberResolved}");
        ImGui.TextDisabled(status.Detail);
        ImGui.Spacing();

        if (ImGui.Button("Refresh NoKill Window Status##xaAbuseLobbyStatus"))
            SetDebugResult($"Lobby monitor active: {monitorStatus.ShouldMonitor}. {monitorStatus.Detail} NoKill window active: {status.IsVisible}. {status.Detail}");

        ImGui.SameLine();
        if (ImGui.Button("Close NoKill Plugin Window##xaAbuseLobbyClose"))
        {
            var closed = plugin.LobbyErrorAutoClose.TryCloseNoKillPluginPanelForDebug(out var message);
            var refreshedStatus = plugin.LobbyErrorAutoClose.GetNoKillPluginPanelDebugStatus();
            SetDebugResult($"{(closed ? "NoKill close OK" : "NoKill close failed")}: {message} Active now: {refreshedStatus.IsVisible}. {refreshedStatus.Detail}");
        }
    }

    private string GetXaAbuseStatusText()
    {
        var nameStatus = !xaAbuseEnabled
            ? "names off"
            : xaAbuseAllVisiblePlayers ? "names: all visible" : "names: local";
        var overlayStatus = !xaAbuseOverlayEnabled
            ? "overlay off"
            : xaAbuseOverlayAllVisiblePlayers ? "overlay: all visible" : "overlay: local";

        if (xaAbuseOverlayEnabled)
            overlayStatus += xaAbuseOverlayUseTexture ? " (.tex)" : " (text)";

        return $"{nameStatus} | {overlayStatus}";
    }

    private void EnableXaAbuse(bool allVisiblePlayers)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null)
        {
            SetDebugResult("XA Abuse: local player not available");
            return;
        }

        xaAbuseEnabled = true;
        xaAbuseAllVisiblePlayers = allVisiblePlayers;
        Plugin.NamePlateGui.RequestRedraw();
        SetDebugResult(allVisiblePlayers
            ? "XA Abuse enabled, all visible player nameplates now say I Love XA!"
            : "XA Abuse enabled, local nameplate now says I Love XA!");
    }

    private void EnableXaAbuseOverlay(bool allVisiblePlayers)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null)
        {
            SetDebugResult("XA Abuse overlay: local player not available");
            return;
        }

        xaAbuseOverlayEnabled = true;
        xaAbuseOverlayAllVisiblePlayers = allVisiblePlayers;
        xaAbuseOverlayEnabledAtUtc = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(xaAbuseOverlayText))
            xaAbuseOverlayText = XaAbuseDefaultOverlayText;
        if (string.IsNullOrWhiteSpace(xaAbuseOverlayTexturePath))
            xaAbuseOverlayTexturePath = XaAbuseDefaultTexturePath;

        SetDebugResult(allVisiblePlayers
            ? $"XA Abuse overlay enabled, all visible player heads now show {(xaAbuseOverlayUseTexture ? ".tex" : "text")} overlay"
            : $"XA Abuse overlay enabled, local player now shows {(xaAbuseOverlayUseTexture ? ".tex" : "text")} overlay");
    }

    private void DisableXaAbuseOverlay()
    {
        var wasEnabled = xaAbuseOverlayEnabled;
        xaAbuseOverlayEnabled = false;
        xaAbuseOverlayAllVisiblePlayers = false;
        xaAbuseOverlayEnabledAtUtc = DateTime.MinValue;
        SetDebugResult(wasEnabled ? "XA Abuse overlay disabled" : "XA Abuse overlay already off");
    }

    private void ResetXaAbuse()
    {
        var wasEnabled = xaAbuseEnabled || xaAbuseOverlayEnabled;
        xaAbuseEnabled = false;
        xaAbuseAllVisiblePlayers = false;
        xaAbuseOverlayEnabled = false;
        xaAbuseOverlayAllVisiblePlayers = false;
        xaAbuseOverlayEnabledAtUtc = DateTime.MinValue;
        Plugin.NamePlateGui.RequestRedraw();
        SetDebugResult(wasEnabled ? "XA Abuse reset, restored normal nameplate / overlay" : "XA Abuse already reset");
    }

    private void DrawXaAbuseXaDatabaseIpcTests()
    {
        ImGui.TextDisabled("Pulls every XA.Database.* IPC channel in one pass and copies the results to the clipboard.");
        ImGui.TextDisabled("Covers all 21 channels from the XA Database [?] IPC reference tab (2 actions + 19 queries).");
        ImGui.Spacing();

        if (ImGui.Button("Run All IPC Tests + Copy Results##xaAbuseDbIpcRun"))
        {
            RunXaDatabaseIpcClipboardTest();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs IsReady/GetVersion/GetDbPath, then Refresh + Save (fresh snapshot),\nthen every remaining query including the item-search channels,\nand copies the full result report to the clipboard.");

        if (!string.IsNullOrEmpty(xaDbIpcTestReport))
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy Last Report##xaAbuseDbIpcCopy"))
            {
                ImGui.SetClipboardText(xaDbIpcTestReport);
                SetDebugResult("XA Database IPC report copied to clipboard again");
            }

            ImGui.Spacing();
            ImGui.BeginChild("##xaAbuseDbIpcReport", new Vector2(0f, Scale(220f)), true);
            ImGui.TextUnformatted(xaDbIpcTestReport);
            ImGui.EndChild();
        }
    }

    private void RunXaDatabaseIpcClipboardTest()
    {
        const string SearchQuery = "potion";
        var ipc = plugin.IpcClient;
        var report = new StringBuilder();
        report.AppendLine("XA Database IPC Test Report");
        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (!ipc.IsXaDatabaseAvailable())
        {
            report.AppendLine("[FAIL] XA Database IPC unavailable - is the XA Database plugin installed and loaded?");
            xaDbIpcTestReport = report.ToString();
            ImGui.SetClipboardText(xaDbIpcTestReport);
            SetDebugResult("XA Database IPC unavailable, failure report copied to clipboard");
            return;
        }

        var tested = 0;
        var returnedData = 0;

        void Append(string channel, string type, string value, bool hasData)
        {
            tested++;
            if (hasData)
                returnedData++;

            if (value.Contains('\n'))
            {
                report.AppendLine($"{channel} ({type}):");
                var lines = value.Split('\n');
                const int MaxLines = 25;
                for (var i = 0; i < lines.Length && i < MaxLines; i++)
                    report.AppendLine($"    {lines[i].TrimEnd('\r')}");
                if (lines.Length > MaxLines)
                    report.AppendLine($"    ... (+{lines.Length - MaxLines} more lines)");
            }
            else
            {
                report.AppendLine($"{channel} ({type}): {(string.IsNullOrEmpty(value) ? "(empty)" : value)}");
            }
        }

        report.AppendLine($"XA Database version: {ipc.GetVersion()}");
        report.AppendLine(new string('-', 72));

        Append("XA.Database.IsReady", "bool", ipc.IsReady().ToString(), true);
        Append("XA.Database.GetVersion", "string", ipc.GetVersion(), !string.IsNullOrEmpty(ipc.GetVersion()));

        var dbPath = ipc.GetDbPath();
        Append("XA.Database.GetDbPath", "string", dbPath, !string.IsNullOrEmpty(dbPath));

        // Actions run before the remaining queries so their results reflect a fresh snapshot.
        var refreshOk = ipc.Refresh();
        Append("XA.Database.Refresh", "action", refreshOk ? "invoked OK" : "FAILED", refreshOk);
        var saveOk = plugin.SaveToXaDatabaseAndRecordSync();
        Append("XA.Database.Save", "action", saveOk ? "invoked OK (snapshot saved + sync recorded)" : "FAILED", saveOk);

        var characterName = ipc.GetCharacterName();
        Append("XA.Database.GetCharacterName", "string", characterName, !string.IsNullOrEmpty(characterName));
        Append("XA.Database.GetGil", "int", $"{ipc.GetGil():N0}", true);
        Append("XA.Database.GetRetainerGil", "long", $"{ipc.GetRetainerGil():N0}", true);

        var fcInfo = ipc.GetFcInfo();
        Append("XA.Database.GetFcInfo", "string", fcInfo, !string.IsNullOrEmpty(fcInfo));
        var fcName = ipc.GetFcName();
        Append("XA.Database.GetFcName", "string", fcName, !string.IsNullOrEmpty(fcName));
        var fcTag = ipc.GetFcTag();
        Append("XA.Database.GetFcTag", "string", fcTag, !string.IsNullOrEmpty(fcTag));
        Append("XA.Database.GetFcPoints", "int", $"{ipc.GetFcPoints():N0}", true);

        var plotInfo = ipc.GetPlotInfo();
        Append("XA.Database.GetPlotInfo", "string", plotInfo, !string.IsNullOrEmpty(plotInfo));
        var personalPlotInfo = ipc.GetPersonalPlotInfo();
        Append("XA.Database.GetPersonalPlotInfo", "string", personalPlotInfo, !string.IsNullOrEmpty(personalPlotInfo));
        var apartment = ipc.GetApartment();
        Append("XA.Database.GetApartment", "string", apartment, !string.IsNullOrEmpty(apartment));

        var summaryJson = ipc.GetCharacterSummaryJson();
        Append("XA.Database.GetCharacterSummaryJson", "string", summaryJson, !string.IsNullOrEmpty(summaryJson));
        var accountRosterJson = ipc.GetAccountCharacterListJson();
        Append("XA.Database.GetAccountCharacterListJson", "string", accountRosterJson, !string.IsNullOrEmpty(accountRosterJson));
        var snapshotJson = ipc.GetLastSnapshotResultJson();
        Append("XA.Database.GetLastSnapshotResultJson", "string", snapshotJson, !string.IsNullOrEmpty(snapshotJson));

        var searchRaw = ipc.SearchItems(SearchQuery);
        Append($"XA.Database.SearchItems(\"{SearchQuery}\")", "string", searchRaw, !string.IsNullOrEmpty(searchRaw));

        // Derive itemId:isHq keys from the search results so the match channel gets a realistic payload.
        var itemKeys = (searchRaw ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length >= 7 && uint.TryParse(parts[4], out _) && bool.TryParse(parts[6], out _))
            .Select(parts => $"{parts[4]}:{(bool.Parse(parts[6]) ? 1 : 0)}")
            .Distinct()
            .Take(3)
            .ToList();
        var itemKeysPayload = itemKeys.Count > 0 ? string.Join(",", itemKeys) : "1:0";
        var matchOk = ipc.TryGetMatchingCharactersForItems(itemKeysPayload, out var matchRaw);
        Append($"XA.Database.GetMatchingCharactersForItems(\"{itemKeysPayload}\")", "string",
            matchOk ? matchRaw : "FAILED", matchOk && !string.IsNullOrEmpty(matchRaw));

        var currentCharJson = ipc.SearchCurrentCharacterItemsJson(SearchQuery);
        Append($"XA.Database.SearchCurrentCharacterItemsJson(\"{SearchQuery}\")", "string", currentCharJson, !string.IsNullOrEmpty(currentCharJson));

        report.AppendLine(new string('-', 72));
        report.AppendLine($"Tested {tested} IPC channels, {returnedData} returned data. Empty values can be legitimate (no FC, no plot, no search hits).");

        xaDbIpcTestReport = report.ToString();
        ImGui.SetClipboardText(xaDbIpcTestReport);
        SetDebugResult($"XA Database IPC test: {tested} channels tested, {returnedData} returned data, report copied to clipboard");
    }

    private void OnXaAbuseNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!xaAbuseEnabled)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        foreach (var handler in handlers)
        {
            if (xaAbuseAllVisiblePlayers)
            {
                if (handler.PlayerCharacter == null)
                    continue;
            }
            else if (handler.GameObjectId != localPlayer.GameObjectId)
                continue;

            handler.Name = new SeStringBuilder()
                .AddText(XaAbuseDisplayName)
                .Build();

            if (!xaAbuseAllVisiblePlayers)
                break;
        }
    }

    private void DrawXaAbuseOverlay()
    {
        if (!xaAbuseOverlayEnabled || !Plugin.PlayerState.IsLoaded)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var elapsed = xaAbuseOverlayEnabledAtUtc == DateTime.MinValue
            ? 0f
            : (float)(DateTime.UtcNow - xaAbuseOverlayEnabledAtUtc).TotalSeconds;
        var drawList = ImGui.GetForegroundDrawList();

        if (xaAbuseOverlayAllVisiblePlayers)
        {
            foreach (var player in Plugin.ObjectTable.OfType<IPlayerCharacter>())
                DrawXaAbuseOverlayForPlayer(drawList, player, elapsed);
            return;
        }

        DrawXaAbuseOverlayForPlayer(drawList, localPlayer, elapsed);
    }

    private void DrawXaAbuseOverlayForPlayer(ImDrawListPtr drawList, IPlayerCharacter player, float elapsed)
    {
        var phase = (float)((player.GameObjectId & 255UL) * 0.04f);
        if (!Plugin.GameGui.WorldToScreen(player.Position + new Vector3(0f, 2.3f, 0f), out var screenPos))
            return;

        var sway = MathF.Sin((elapsed * 2.2f) + phase) * 10f;
        var bob = MathF.Sin((elapsed * 3.4f) + phase) * 6f;
        var pulse = 1f + (MathF.Sin((elapsed * 5.0f) + phase) * 0.1f);
        var center = new Vector2(screenPos.X + sway, screenPos.Y - 72f + bob);

        if (xaAbuseOverlayUseTexture && DrawXaAbuseTextureOverlay(drawList, center, pulse))
            return;

        DrawXaAbuseTextOverlay(drawList, center, pulse);
    }

    private void DrawXaAbuseTextOverlay(ImDrawListPtr drawList, Vector2 center, float pulse)
    {
        var overlayText = string.IsNullOrWhiteSpace(xaAbuseOverlayText) ? XaAbuseDefaultOverlayText : xaAbuseOverlayText;
        var font = ImGui.GetFont();
        var baseFontSize = font.FontSize <= 0f ? 13f : font.FontSize;
        var overlayFontSize = (overlayText.Length <= 2 ? 62f : overlayText.Length <= 4 ? 48f : 36f) * pulse;
        var textScale = overlayFontSize / baseFontSize;
        var textSize = ImGui.CalcTextSize(overlayText) * textScale;
        var textPos = center - (textSize * 0.5f);
        var shadowColor = ImGui.GetColorU32(xaAbuseOverlayShadowColor);
        var fillColor = ImGui.GetColorU32(xaAbuseOverlayFillColor);

        ImGui.AddText(drawList, font, overlayFontSize, textPos + new Vector2(2f, 2f), shadowColor, overlayText);
        ImGui.AddText(drawList, font, overlayFontSize, textPos, fillColor, overlayText);
    }

    private bool DrawXaAbuseTextureOverlay(ImDrawListPtr drawList, Vector2 center, float pulse)
    {
        var texturePath = string.IsNullOrWhiteSpace(xaAbuseOverlayTexturePath) ? XaAbuseDefaultTexturePath : xaAbuseOverlayTexturePath.Trim();
        var sharedTexture = Plugin.TextureProvider.GetFromGame(texturePath);
        if (!sharedTexture.TryGetWrap(out var wrap, out _))
            return false;

        var height = 76f * pulse;
        var scale = height / MathF.Max(1f, (float)wrap.Height);
        var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
        var min = center - (size * 0.5f);
        var max = min + size;
        drawList.AddImage(wrap.Handle, min, max);
        return true;
    }

    private void StartDebugAutoRetainerDepositGil()
    {
        if (debugAutoRetainerDepositGilActive)
        {
            SetDebugResult("AR deposit gil debug task is already armed.");
            return;
        }

        if (!Plugin.PlayerState.IsLoaded)
        {
            SetDebugResult("AR deposit gil: character is not loaded.");
            return;
        }

        if (!plugin.IpcClient.IsAutoRetainerAvailable())
        {
            SetDebugResult("AR deposit gil: AutoRetainer IPC is not available.");
            return;
        }

        debugAutoRetainerDepositGilKeepAmount = Math.Max(0, debugAutoRetainerDepositGilKeepAmount);
        var currentGil = GetDebugCurrentCharacterGil();
        if (currentGil <= debugAutoRetainerDepositGilKeepAmount)
        {
            SetDebugResult($"AR deposit gil: current gil {currentGil:N0} is at or below keep amount {debugAutoRetainerDepositGilKeepAmount:N0}.");
            return;
        }

        debugAutoRetainerDepositGilActive = true;
        debugAutoRetainerDepositGilListTaskRequested = false;
        debugAutoRetainerDepositGilProcessingRetainer = false;
        debugAutoRetainerDepositGilOpeningRetainerList = false;
        debugAutoRetainerDepositGilProcessedRetainers = 0;
        debugAutoRetainerDepositGilRequestedTotal = 0;
        debugAutoRetainerDepositGilActualTotal = 0;
        debugAutoRetainerDepositGilStartedUtc = DateTime.UtcNow;
        debugAutoRetainerDepositGilLastActivityUtc = debugAutoRetainerDepositGilStartedUtc;

        try
        {
            plugin.IpcClient.AutoRetainerSubscribeRetainerListTaskButtonsDraw(OnDebugAutoRetainerDepositGilListTaskButtonsDraw);
            plugin.IpcClient.AutoRetainerSubscribeRetainerAdditionalTask(OnDebugAutoRetainerDepositGilAdditionalTask);
            plugin.IpcClient.AutoRetainerSubscribeRetainerPostProcess(OnDebugAutoRetainerDepositGilReadyForPostprocess);
            debugAutoRetainerDepositGilSubscribed = true;
        }
        catch (Exception ex)
        {
            StopDebugAutoRetainerDepositGil($"AR deposit gil: failed to subscribe to AutoRetainer hooks - {ex.Message}.", finishCurrentRetainer: false);
            return;
        }

        StartDebugAutoRetainerDepositGilMonitor(debugAutoRetainerDepositGilStartedUtc);
        StartDebugAutoRetainerDepositGilRetainerListOpenTask(debugAutoRetainerDepositGilStartedUtc);

        SetDebugResult(AddonHelper.IsAddonVisible("RetainerList")
            ? $"AR deposit gil armed: keep {debugAutoRetainerDepositGilKeepAmount:N0}; requesting custom retainer-list task."
            : $"AR deposit gil armed: keep {debugAutoRetainerDepositGilKeepAmount:N0}; targeting Summoning Bell.");
    }

    private void StartDebugAutoRetainerNonAutoInteract()
    {
        if (debugAutoRetainerNonAutoInteractActive)
        {
            SetDebugResult("AR non-auto interact is already opening RetainerList.");
            return;
        }

        if (!Plugin.PlayerState.IsLoaded)
        {
            SetDebugResult("AR non-auto interact: character is not loaded.");
            return;
        }

        if (!plugin.IpcClient.IsAutoRetainerAvailable())
        {
            SetDebugResult("AR non-auto interact: AutoRetainer IPC is not available.");
            return;
        }

        debugAutoRetainerNonAutoInteractActive = true;
        debugAutoRetainerNonAutoInteractStartedUtc = DateTime.UtcNow;
        var runStartedUtc = debugAutoRetainerNonAutoInteractStartedUtc;

        System.Threading.Tasks.Task.Run(async () =>
        {
            var result = await OpenDebugRetainerListWithoutAutoRetainerEnableAsync(
                "AR non-auto interact",
                () => debugAutoRetainerNonAutoInteractActive && debugAutoRetainerNonAutoInteractStartedUtc == runStartedUtc);

            if (debugAutoRetainerNonAutoInteractStartedUtc != runStartedUtc)
                return;

            debugAutoRetainerNonAutoInteractActive = false;
            SetDebugResult(result.Message);
        });

        SetDebugResult("AR non-auto interact: suppressing AutoRetainer and opening Summoning Bell.");
    }

    private void StopDebugAutoRetainerDepositGil(string message, bool finishCurrentRetainer)
    {
        if (debugAutoRetainerDepositGilSubscribed)
        {
            try { plugin.IpcClient.AutoRetainerUnsubscribeRetainerListTaskButtonsDraw(OnDebugAutoRetainerDepositGilListTaskButtonsDraw); } catch { }
            try { plugin.IpcClient.AutoRetainerUnsubscribeRetainerAdditionalTask(OnDebugAutoRetainerDepositGilAdditionalTask); } catch { }
            try { plugin.IpcClient.AutoRetainerUnsubscribeRetainerPostProcess(OnDebugAutoRetainerDepositGilReadyForPostprocess); } catch { }
            debugAutoRetainerDepositGilSubscribed = false;
        }

        if (finishCurrentRetainer && debugAutoRetainerDepositGilProcessingRetainer)
        {
            plugin.IpcClient.AutoRetainerFinishRetainerPostProcess();
        }

        debugAutoRetainerDepositGilActive = false;
        debugAutoRetainerDepositGilListTaskRequested = false;
        debugAutoRetainerDepositGilProcessingRetainer = false;
        debugAutoRetainerDepositGilOpeningRetainerList = false;
        SetDebugResult(message);
    }

    private void CompleteDebugAutoRetainerDepositGil(string message, bool abortArTasks, bool closeRetainerList)
    {
        if (debugAutoRetainerDepositGilSubscribed)
        {
            try { plugin.IpcClient.AutoRetainerUnsubscribeRetainerListTaskButtonsDraw(OnDebugAutoRetainerDepositGilListTaskButtonsDraw); } catch { }
            try { plugin.IpcClient.AutoRetainerUnsubscribeRetainerAdditionalTask(OnDebugAutoRetainerDepositGilAdditionalTask); } catch { }
            try { plugin.IpcClient.AutoRetainerUnsubscribeRetainerPostProcess(OnDebugAutoRetainerDepositGilReadyForPostprocess); } catch { }
            debugAutoRetainerDepositGilSubscribed = false;
        }

        if (abortArTasks)
            plugin.IpcClient.AutoRetainerPluginStateAbortAllTasks();

        debugAutoRetainerDepositGilActive = false;
        debugAutoRetainerDepositGilListTaskRequested = false;
        debugAutoRetainerDepositGilProcessingRetainer = false;
        debugAutoRetainerDepositGilOpeningRetainerList = false;

        if (closeRetainerList)
        {
            StartDebugRetainerListCancelRecovery(message);
            SetDebugResult($"{message} Waiting 1s before RetainerList cancel check.");
        }
        else
        {
            SetDebugResult(message);
        }
    }

    private void StartDebugRetainerListCancelRecovery(string completionMessage)
    {
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1000);

                const int maxAttempts = 8;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    var state = await Plugin.Framework.Run(GetDebugRetainerListCancelState);
                    if (state.CharacterSafeWaitReady)
                    {
                        await SetDebugResultOnFrameworkAsync($"{completionMessage} RetainerList cleared; CharacterSafeWait is ready.");
                        return;
                    }

                    if (state.RetainerListVisible)
                    {
                        var cancelled = await Plugin.Framework.Run(CancelDebugRetainerList);
                        await System.Threading.Tasks.Task.Delay(1000);

                        state = await Plugin.Framework.Run(GetDebugRetainerListCancelState);
                        if (state.CharacterSafeWaitReady)
                        {
                            await SetDebugResultOnFrameworkAsync($"{completionMessage} RetainerList cancelled; CharacterSafeWait is ready.");
                            return;
                        }

                        Plugin.Log.Information($"[XASlave] AR deposit gil: RetainerList cancel attempt {attempt}/{maxAttempts}, callback={cancelled}, visible={state.RetainerListVisible}, ready={state.RetainerListReady}, characterSafeWait={state.CharacterSafeWaitReady}.");
                        continue;
                    }

                    if (attempt >= 3)
                    {
                        await Plugin.Framework.Run(() =>
                        {
                            KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                            return true;
                        });
                        Plugin.Log.Information($"[XASlave] AR deposit gil: CharacterSafeWait still false and RetainerList not visible; pressed ESC on recovery attempt {attempt}/{maxAttempts}.");
                    }

                    await System.Threading.Tasks.Task.Delay(1000);
                }

                var finalState = await Plugin.Framework.Run(GetDebugRetainerListCancelState);
                if (!finalState.CharacterSafeWaitReady && finalState.RetainerListVisible)
                {
                    await Plugin.Framework.Run(CancelDebugRetainerList);
                    await System.Threading.Tasks.Task.Delay(1000);
                    finalState = await Plugin.Framework.Run(GetDebugRetainerListCancelState);
                }

                await SetDebugResultOnFrameworkAsync(finalState.CharacterSafeWaitReady
                    ? $"{completionMessage} RetainerList recovery completed; CharacterSafeWait is ready."
                    : $"{completionMessage} RetainerList recovery timed out; visible={finalState.RetainerListVisible}, ready={finalState.RetainerListReady}, CharacterSafeWait={finalState.CharacterSafeWaitReady}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[XASlave] AR deposit gil RetainerList cancel recovery failed.");
                await SetDebugResultOnFrameworkAsync($"{completionMessage} RetainerList recovery failed - {ex.Message}");
            }
        });
    }

    private static (bool CharacterSafeWaitReady, bool RetainerListVisible, bool RetainerListReady) GetDebugRetainerListCancelState()
    {
        return (
            CharacterSafetyHelper.IsCharacterSafeWaitReady(),
            AddonHelper.IsAddonVisible("RetainerList"),
            IsDebugAddonReady("RetainerList"));
    }

    private async System.Threading.Tasks.Task SetDebugResultOnFrameworkAsync(string message)
    {
        await Plugin.Framework.Run(() =>
        {
            SetDebugResult(message);
            return true;
        });
    }

    private static bool CancelDebugRetainerList()
    {
        if (AddonHelper.FireCallback("RetainerList", -1))
        {
            Plugin.Log.Information("[XASlave] AR deposit gil: sent RetainerList -1 cancel callback.");
            return true;
        }

        Plugin.Log.Warning("[XASlave] AR deposit gil: failed to send RetainerList -1 cancel callback.");
        return false;
    }

    private void OnDebugAutoRetainerDepositGilListTaskButtonsDraw()
    {
        if (!debugAutoRetainerDepositGilActive || debugAutoRetainerDepositGilListTaskRequested)
            return;

        var keepAmount = Math.Max(0, debugAutoRetainerDepositGilKeepAmount);
        var currentGil = GetDebugCurrentCharacterGil();
        if (currentGil <= keepAmount)
        {
            CompleteDebugAutoRetainerDepositGil(
                $"AR deposit gil complete: current gil {currentGil:N0} is at or below keep {keepAmount:N0}.",
                abortArTasks: true,
                closeRetainerList: true);
            return;
        }

        if (!plugin.IpcClient.AutoRetainerRequestRetainerListCustomTask(DebugAutoRetainerDepositGilPluginName))
        {
            StopDebugAutoRetainerDepositGil("AR deposit gil: failed to request AutoRetainer retainer-list custom task.", finishCurrentRetainer: false);
            return;
        }

        debugAutoRetainerDepositGilListTaskRequested = true;
        debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;
        SetDebugResult("AR deposit gil: AutoRetainer custom retainer-list task queued.");
    }

    private void OnDebugAutoRetainerDepositGilAdditionalTask(string retainerName)
    {
        if (!debugAutoRetainerDepositGilActive || !debugAutoRetainerDepositGilListTaskRequested)
            return;

        var keepAmount = Math.Max(0, debugAutoRetainerDepositGilKeepAmount);
        var currentGil = GetDebugCurrentCharacterGil();
        if (currentGil <= keepAmount)
        {
            CompleteDebugAutoRetainerDepositGil(
                $"AR deposit gil complete before {retainerName}: current gil {currentGil:N0} is at or below keep {keepAmount:N0}.",
                abortArTasks: true,
                closeRetainerList: true);
            return;
        }

        if (plugin.IpcClient.AutoRetainerRequestRetainerPostProcess(DebugAutoRetainerDepositGilPluginName))
        {
            debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;
            Plugin.Log.Information($"[XASlave] AR deposit gil: requested postprocess for retainer '{retainerName}'.");
        }
    }

    private void OnDebugAutoRetainerDepositGilReadyForPostprocess(string pluginName, string retainerName)
    {
        if (!pluginName.Equals(DebugAutoRetainerDepositGilPluginName, StringComparison.Ordinal))
            return;

        if (!debugAutoRetainerDepositGilActive)
        {
            plugin.IpcClient.AutoRetainerFinishRetainerPostProcess();
            return;
        }

        if (debugAutoRetainerDepositGilProcessingRetainer)
        {
            Plugin.Log.Warning($"[XASlave] AR deposit gil: received overlapping postprocess for '{retainerName}'.");
            return;
        }

        debugAutoRetainerDepositGilProcessingRetainer = true;
        debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;
        var keepAmount = Math.Max(0, debugAutoRetainerDepositGilKeepAmount);

        System.Threading.Tasks.Task.Run(async () =>
        {
            var stopAfterCurrentRetainer = false;
            var stopMessage = string.Empty;
            try
            {
                var result = await RunDebugAutoRetainerDepositGilForRetainerAsync(retainerName, keepAmount);
                debugAutoRetainerDepositGilProcessedRetainers++;
                debugAutoRetainerDepositGilRequestedTotal += result.RequestedDeposit;
                debugAutoRetainerDepositGilActualTotal += result.ActualDeposit;
                SetDebugResult(result.Message);
                stopAfterCurrentRetainer = result.ShouldStop;
                stopMessage = result.Message;
            }
            catch (Exception ex)
            {
                stopAfterCurrentRetainer = true;
                stopMessage = $"AR deposit gil: {retainerName} failed - {ex.Message}";
                SetDebugResult(stopMessage);
                Plugin.Log.Error(ex, $"[XASlave] AR deposit gil failed for retainer '{retainerName}'.");
            }
            finally
            {
                plugin.IpcClient.AutoRetainerFinishRetainerPostProcess();
                debugAutoRetainerDepositGilProcessingRetainer = false;
                debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;

                if (stopAfterCurrentRetainer)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                    await Plugin.Framework.Run(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil(
                            $"{stopMessage} Closing RetainerList.",
                            abortArTasks: true,
                            closeRetainerList: true);
                        return true;
                    });
                }
            }
        });
    }

    private void StartDebugAutoRetainerDepositGilRetainerListOpenTask(DateTime runStartedUtc)
    {
        if (IsDebugAddonReady("RetainerList"))
            return;

        debugAutoRetainerDepositGilOpeningRetainerList = true;

        System.Threading.Tasks.Task.Run(async () =>
        {
            var result = await OpenDebugRetainerListWithoutAutoRetainerEnableAsync(
                "AR deposit gil",
                () => debugAutoRetainerDepositGilActive && debugAutoRetainerDepositGilStartedUtc == runStartedUtc);

            if (!debugAutoRetainerDepositGilActive || debugAutoRetainerDepositGilStartedUtc != runStartedUtc)
                return;

            debugAutoRetainerDepositGilOpeningRetainerList = false;
            if (result.Opened)
            {
                debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;
                SetDebugResult("AR deposit gil: RetainerList active through non-auto interact; waiting for AutoRetainer custom-task hook.");
                return;
            }

            StopDebugAutoRetainerDepositGil(result.Message, finishCurrentRetainer: false);
        });
    }

    private async System.Threading.Tasks.Task<DebugAutoRetainerBellOpenResult> OpenDebugRetainerListWithoutAutoRetainerEnableAsync(string operationName, Func<bool> shouldContinue)
    {
        if (await Plugin.Framework.Run(() => IsDebugAddonReady("RetainerList")))
        {
            var alreadySuppressed = await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerGetSuppressed());
            if (alreadySuppressed)
            {
                var released = await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerSetSuppressed(false));
                if (!released)
                    return new(false, $"{operationName}: RetainerList is already active but failed to release AutoRetainer suppression.");

                await System.Threading.Tasks.Task.Delay(250);
                return new(true, $"{operationName}: RetainerList is already active and ready; AutoRetainer suppression released for custom-task processing.");
            }

            return new(true, $"{operationName}: RetainerList is already active and ready.");
        }

        var suppressedByTask = false;
        var wasSuppressedBeforeOpen = false;
        var releasedSuppression = false;
        try
        {
            var alreadySuppressed = await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerGetSuppressed());
            wasSuppressedBeforeOpen = alreadySuppressed;
            if (!alreadySuppressed)
            {
                var suppressed = await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerSetSuppressed(true));
                if (!suppressed)
                    return new(false, $"{operationName}: failed to suppress AutoRetainer before Summoning Bell interaction.");

                suppressedByTask = true;
                await System.Threading.Tasks.Task.Delay(250);
            }

            var started = Environment.TickCount64;
            var interacted = false;
            while (shouldContinue() && Environment.TickCount64 - started < 20000)
            {
                var opened = await Plugin.Framework.Run(() =>
                {
                    if (IsDebugAddonReady("RetainerList"))
                        return true;

                    if (AddonHelper.IsAddonVisible("RetainerList"))
                        return false;

                    var target = Plugin.TargetManager.Target;
                    var targetName = target?.Name.TextValue ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(targetName)
                        || !targetName.Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase))
                    {
                        AddonHelper.TargetByName("Summoning Bell");
                    }
                    else
                    {
                        AddonHelper.InteractWithTarget();
                        interacted = true;
                    }

                    return IsDebugAddonReady("RetainerList");
                });

                if (opened)
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    if (suppressedByTask || wasSuppressedBeforeOpen)
                    {
                        var released = await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerSetSuppressed(false));
                        if (!released)
                            return new(false, $"{operationName}: RetainerList active but failed to release AutoRetainer suppression.");

                        releasedSuppression = true;
                        await System.Threading.Tasks.Task.Delay(250);
                    }

                    var suffix = (suppressedByTask || wasSuppressedBeforeOpen)
                        ? "AutoRetainer suppression released for custom-task processing"
                        : "AutoRetainer was not suppressed";
                    return new(true, $"{operationName}: RetainerList active; {suffix}.");
                }

                await System.Threading.Tasks.Task.Delay(interacted ? 750 : 500);
            }

            return new(false, $"{operationName}: could not open RetainerList from Summoning Bell.");
        }
        catch (Exception ex)
        {
            return new(false, $"{operationName}: failed while opening RetainerList - {ex.Message}");
        }
        finally
        {
            if (suppressedByTask && !releasedSuppression)
            {
                try { await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerSetSuppressed(false)); } catch { }
            }
        }
    }

    private void StartDebugAutoRetainerDepositGilMonitor(DateTime runStartedUtc)
    {
        System.Threading.Tasks.Task.Run(async () =>
        {
            while (debugAutoRetainerDepositGilActive && debugAutoRetainerDepositGilStartedUtc == runStartedUtc)
            {
                await System.Threading.Tasks.Task.Delay(1000);

                if (!debugAutoRetainerDepositGilActive || debugAutoRetainerDepositGilStartedUtc != runStartedUtc)
                    return;

                var now = DateTime.UtcNow;
                var elapsed = now - runStartedUtc;
                var idleFor = now - debugAutoRetainerDepositGilLastActivityUtc;

                if (!debugAutoRetainerDepositGilListTaskRequested && elapsed > TimeSpan.FromMinutes(2))
                {
                    await Plugin.Framework.Run(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil("AR deposit gil: timed out waiting for RetainerList.", abortArTasks: true, closeRetainerList: true);
                        return true;
                    });
                    return;
                }

                if (elapsed > TimeSpan.FromMinutes(15))
                {
                    await Plugin.Framework.Run(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil("AR deposit gil: timed out after 15 minutes.", abortArTasks: true, closeRetainerList: true);
                        return true;
                    });
                    return;
                }

                if (!debugAutoRetainerDepositGilListTaskRequested || debugAutoRetainerDepositGilProcessingRetainer)
                    continue;

                if (debugAutoRetainerDepositGilProcessedRetainers == 0 && idleFor < TimeSpan.FromSeconds(10))
                    continue;

                var arBusy = await Plugin.Framework.Run(() => plugin.IpcClient.AutoRetainerPluginStateIsBusy());
                if (!arBusy && idleFor > TimeSpan.FromSeconds(3))
                {
                    var remainingGil = await Plugin.Framework.Run(GetDebugCurrentCharacterGil);
                    await Plugin.Framework.Run(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil(
                            $"AR deposit gil stopped: retainers {debugAutoRetainerDepositGilProcessedRetainers}, requested {debugAutoRetainerDepositGilRequestedTotal:N0}, actual {debugAutoRetainerDepositGilActualTotal:N0}, remaining {remainingGil:N0}.",
                            abortArTasks: false,
                            closeRetainerList: true);
                        return true;
                    });
                    return;
                }
            }
        });
    }

    private async System.Threading.Tasks.Task<DebugAutoRetainerDepositGilResult> RunDebugAutoRetainerDepositGilForRetainerAsync(string retainerName, int keepAmount)
    {
        var beforeGil = await Plugin.Framework.Run(GetDebugCurrentCharacterGil);
        var requestedDeposit = Math.Max(0, beforeGil - keepAmount);
        if (requestedDeposit <= 0)
        {
            return new(0, 0, beforeGil, true, false, $"AR deposit gil: {retainerName} skipped; current gil {beforeGil:N0} is at or below keep {keepAmount:N0}.");
        }

        if (!await WaitForDebugFrameworkConditionAsync(() => IsDebugAddonReady("SelectString"), 5000))
            return new(0, 0, beforeGil, true, true, $"AR deposit gil: {retainerName} failed; SelectString did not become ready.");

        if (!await Plugin.Framework.Run(SelectDebugAutoRetainerGilMenu))
            return new(0, 0, beforeGil, true, true, $"AR deposit gil: {retainerName} failed; could not select gil menu.");

        if (!await WaitForDebugFrameworkConditionAsync(() => IsDebugAddonReady("Bank"), 5000))
            return new(0, 0, beforeGil, true, true, $"AR deposit gil: {retainerName} failed; Bank did not become ready.");

        await System.Threading.Tasks.Task.Delay(500);

        if (!await Plugin.Framework.Run(() => FireDebugBankSwapDepositMode()))
            return new(0, 0, beforeGil, true, true, $"AR deposit gil: {retainerName} failed; could not switch Bank to deposit mode.");

        await System.Threading.Tasks.Task.Delay(500);

        beforeGil = await Plugin.Framework.Run(GetDebugCurrentCharacterGil);
        requestedDeposit = Math.Max(0, beforeGil - keepAmount);
        if (requestedDeposit <= 0)
        {
            await Plugin.Framework.Run(() => FireDebugBankProcessOrCancel(forceCancel: true));
            return new(0, 0, beforeGil, true, false, $"AR deposit gil: {retainerName} skipped after bank opened; gil {beforeGil:N0}, keep {keepAmount:N0}.");
        }

        var depositAmount = (uint)Math.Min(requestedDeposit, uint.MaxValue);
        if (!await Plugin.Framework.Run(() => FireDebugBankSetDepositAmount(depositAmount)))
            return new(requestedDeposit, 0, beforeGil, true, true, $"AR deposit gil: {retainerName} failed; could not set deposit amount {requestedDeposit:N0}.");

        await System.Threading.Tasks.Task.Delay(250);

        if (!await Plugin.Framework.Run(() => FireDebugBankProcessOrCancel(forceCancel: false)))
            return new(requestedDeposit, 0, beforeGil, true, true, $"AR deposit gil: {retainerName} failed; could not process Bank deposit.");

        await System.Threading.Tasks.Task.Delay(1000);

        var afterGil = await Plugin.Framework.Run(GetDebugCurrentCharacterGil);
        var actualDeposit = Math.Max(0, beforeGil - afterGil);
        var shouldStop = afterGil <= keepAmount;
        var capDetail = !shouldStop && actualDeposit < requestedDeposit
            ? $" {retainerName} may be at/near the {DebugAutoRetainerGilCap:N0} gil cap; continuing to next retainer."
            : string.Empty;
        return new(
            requestedDeposit,
            actualDeposit,
            afterGil,
            shouldStop,
            false,
            $"AR deposit gil: {retainerName} requested {requestedDeposit:N0}, actual {actualDeposit:N0}, remaining {afterGil:N0}.{capDetail}");
    }

    private static async System.Threading.Tasks.Task<bool> WaitForDebugFrameworkConditionAsync(Func<bool> condition, int timeoutMs, int pollMs = 100)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (await Plugin.Framework.Run(condition))
                return true;

            await System.Threading.Tasks.Task.Delay(pollMs);
        }

        return false;
    }

    private static bool IsDebugAddonReady(string addonName)
    {
        unsafe { return AddonHelper.IsAddonReady(addonName); }
    }

    private static int GetDebugCurrentCharacterGil()
    {
        unsafe
        {
            var inventoryManager = InventoryManager.Instance();
            return inventoryManager == null ? 0 : inventoryManager->GetInventoryItemCount(1);
        }
    }

    private static bool SelectDebugAutoRetainerGilMenu()
    {
        unsafe
        {
            var localizedText = GetDebugAutoRetainerGilMenuText();
            if (string.IsNullOrWhiteSpace(localizedText))
            {
                return AddonHelper.SelectFirstAddonListText(
                    "SelectString",
                    out _,
                    out _,
                    ("Entrust or withdraw gil", true));
            }

            return AddonHelper.SelectFirstAddonListText(
                "SelectString",
                out _,
                out _,
                (localizedText, true),
                ("Entrust or withdraw gil", true));
        }
    }

    private static string GetDebugAutoRetainerGilMenuText()
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Addon>().GetRow(2379).Text.ToString();
        }
        catch
        {
            return "Entrust or withdraw gil.";
        }
    }

    private static unsafe bool FireDebugBankSwapDepositMode()
    {
        var addon = AddonHelper.GetAddon("Bank");
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var values = stackalloc AtkValue[2];
        values[0] = default;
        values[0].Type = AtkValueType.Int;
        values[0].Int = 2;
        values[1] = default;
        addon->FireCallback(2, values);
        Plugin.Log.Information("[XASlave] AR deposit gil: Bank swapped to deposit mode.");
        return true;
    }

    private static unsafe bool FireDebugBankSetDepositAmount(uint amount)
    {
        var addon = AddonHelper.GetAddon("Bank");
        if (addon == null || !addon->IsVisible || !addon->IsReady || amount == 0)
            return false;

        var values = stackalloc AtkValue[2];
        values[0] = default;
        values[0].Type = AtkValueType.Int;
        values[0].Int = 3;
        values[1] = default;
        values[1].Type = AtkValueType.UInt;
        values[1].UInt = amount;
        addon->FireCallback(2, values);
        Plugin.Log.Information($"[XASlave] AR deposit gil: Bank deposit amount set to {amount:N0}.");
        return true;
    }

    private static unsafe bool FireDebugBankProcessOrCancel(bool forceCancel)
    {
        var addon = AddonHelper.GetAddon("Bank");
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var nodeIndex = forceCancel ? 2 : 3;
        if (addon->UldManager.NodeListCount <= nodeIndex)
            return false;

        var node = addon->UldManager.NodeList[nodeIndex];
        if (node == null || !node->IsVisible())
            return false;

        var button = (AtkComponentButton*)node->GetComponent();
        if (button == null || !button->IsEnabled)
            return false;

        var values = stackalloc AtkValue[2];
        values[0] = default;
        values[0].Type = AtkValueType.Int;
        values[0].Int = forceCancel ? 1 : 0;
        values[1] = default;
        addon->FireCallback(2, values);
        addon->Close(true);
        Plugin.Log.Information(forceCancel
            ? "[XASlave] AR deposit gil: Bank cancelled."
            : "[XASlave] AR deposit gil: Bank deposit processed.");
        return true;
    }

    private void SetDebugResult(string msg)
    {
        debugResult = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        debugResultExpiry = DateTime.UtcNow.AddSeconds(15);
        Plugin.Log.Information($"[XASlave] Debug: {msg}");
    }

    private static string FormatNullableBool(bool? value)
        => value.HasValue ? value.Value ? "true" : "false" : "null";

    private void RunDebugRawAddonCallback()
    {
        var addonName = debugCallbackAddonName.Trim();
        if (string.IsNullOrWhiteSpace(addonName))
        {
            SetDebugResult("Callback test: enter an addon name.");
            return;
        }

        if (!TryParseDebugCallbackValues(debugCallbackValues, out var values, out var error))
        {
            SetDebugResult($"Callback test parse error: {error}");
            return;
        }

        var ok = FireDebugRawAddonCallback(addonName, values);
        SetDebugResult(ok
            ? $"Callback test: fired {addonName}({string.Join(", ", values.Select(value => value.Display))})."
            : $"Callback test: failed to fire {addonName}; addon may be hidden or not ready.");
    }

    private static bool TryParseDebugCallbackValues(string rawValues, out List<DebugCallbackValue> values, out string error)
    {
        values = new List<DebugCallbackValue>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValues))
            return true;

        var tokens = rawValues.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                values.Add(new DebugCallbackValue(DebugCallbackValueKind.Null, 0, 0, false, "null"));
                continue;
            }

            if (bool.TryParse(token, out var boolValue))
            {
                values.Add(new DebugCallbackValue(DebugCallbackValueKind.Bool, boolValue ? 1 : 0, 0, boolValue, boolValue ? "true" : "false"));
                continue;
            }

            if (token.StartsWith("uint:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
            {
                var separatorIndex = token.IndexOf(':');
                var uintText = separatorIndex >= 0 ? token[(separatorIndex + 1)..] : string.Empty;
                if (uint.TryParse(uintText, out var uintValue))
                {
                    values.Add(new DebugCallbackValue(DebugCallbackValueKind.UInt, 0, uintValue, false, $"uint:{uintValue}"));
                    continue;
                }

                error = $"'{token}' is not a valid uint token.";
                return false;
            }

            if (int.TryParse(token, out var intValue))
            {
                values.Add(new DebugCallbackValue(DebugCallbackValueKind.Int, intValue, 0, false, intValue.ToString()));
                continue;
            }

            error = $"Unsupported token '{token}'. Use signed integers, true/false, null, or uint:<value>.";
            return false;
        }

        return true;
    }

    private static unsafe bool FireDebugRawAddonCallback(string addonName, IReadOnlyList<DebugCallbackValue> values)
    {
        var addon = AddonHelper.GetAddon(addonName);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        try
        {
            if (values.Count == 0)
            {
                addon->FireCallback(0, null);
                Plugin.Log.Information($"[XASlave] Callback test: fired {addonName} with no values.");
                return true;
            }

            var atkValues = stackalloc AtkValue[values.Count];
            for (var index = 0; index < values.Count; index++)
            {
                atkValues[index] = default;
                var value = values[index];
                switch (value.Kind)
                {
                    case DebugCallbackValueKind.Bool:
                        atkValues[index].Type = AtkValueType.Bool;
                        atkValues[index].Int = value.BoolValue ? 1 : 0;
                        break;
                    case DebugCallbackValueKind.UInt:
                        atkValues[index].Type = AtkValueType.UInt;
                        atkValues[index].UInt = value.UIntValue;
                        break;
                    case DebugCallbackValueKind.Int:
                        atkValues[index].Type = AtkValueType.Int;
                        atkValues[index].Int = value.IntValue;
                        break;
                    default:
                        atkValues[index].Type = 0;
                        break;
                }
            }

            addon->FireCallback((uint)values.Count, atkValues);
            Plugin.Log.Information($"[XASlave] Callback test: fired {addonName} with [{string.Join(", ", values.Select(value => value.Display))}].");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[XASlave] Callback test failed for '{addonName}'.");
            return false;
        }
    }

    private void RunDebugSelectStringOption(int callbackIndex)
    {
        const string addonName = "SelectString";
        var optionNumber = callbackIndex + 1;
        if (!AddonHelper.IsAddonVisible(addonName))
        {
            SetDebugResult($"{addonName}: option {optionNumber} [value {callbackIndex}] failed - addon not visible.");
            return;
        }

        if (!AddonHelper.IsAddonReady(addonName))
        {
            SetDebugResult($"{addonName}: option {optionNumber} [value {callbackIndex}] blocked - addon visible but not ready.");
            return;
        }

        var ok = AddonHelper.FireCallback(addonName, callbackIndex);
        SetDebugResult(ok
            ? $"{addonName}: fired option {optionNumber} with raw value {callbackIndex}."
            : $"{addonName}: raw callback {callbackIndex} failed.");
    }

    private void RunDebugTargetByName()
    {
        var targetName = debugTargetPlayerName.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            SetDebugResult("TargetByName: enter a player name first.");
            return;
        }

        AddonHelper.TargetByName(targetName);
        SetDebugResult($"TargetByName requested \"{targetName}\"; fallback command remains /target \"{targetName}\".");
    }

    private void RunDebugFocusCurrentTarget()
    {
        var target = Plugin.TargetManager.Target;
        if (target == null)
        {
            SetDebugResult("FocusTarget: no current target selected.");
            return;
        }

        Plugin.TargetManager.FocusTarget = target;
        SetDebugResult($"FocusTarget set through Dalamud TargetManager: {target.Name}");
    }

    private void RunDebugClearFocusTarget()
    {
        var previous = Plugin.TargetManager.FocusTarget;
        Plugin.TargetManager.FocusTarget = null;
        SetDebugResult(previous == null
            ? "FocusTarget was already clear."
            : $"FocusTarget cleared through Dalamud TargetManager: {previous.Name}");
    }

    // Dropbox's BeginTradingQueue IPC silently ignores the call unless its TaskManager is idle AND the
    // focus target is a player; ItemQueueUI.BeginTrading() then trades the persisted item queue with them.
    private void RunDebugDropboxBeginTrading(bool focusCurrentTargetFirst)
    {
        if (!plugin.IpcClient.IsDropboxAvailable())
        {
            SetDebugResult("Dropbox Begin Trading: Dropbox is not available.");
            return;
        }

        if (plugin.IpcClient.DropboxIsBusy())
        {
            SetDebugResult("Dropbox Begin Trading: Dropbox is busy; it ignores BeginTradingQueue while a task runs.");
            return;
        }

        if (focusCurrentTargetFirst && Plugin.TargetManager.FocusTarget is not IPlayerCharacter)
        {
            if (Plugin.TargetManager.Target is not IPlayerCharacter target)
            {
                SetDebugResult("Dropbox Begin Trading: no player focus target and the current target is not a player; target the trade partner first.");
                return;
            }

            Plugin.TargetManager.FocusTarget = target;
        }

        if (Plugin.TargetManager.FocusTarget is not IPlayerCharacter focus)
        {
            SetDebugResult("Dropbox Begin Trading: no player focus target; Dropbox ignores BeginTradingQueue without one. Use \"Focus Target + Begin Trading\" or /focustarget the partner.");
            return;
        }

        var invoked = plugin.IpcClient.DropboxBeginTrading();
        var busyAfter = plugin.IpcClient.DropboxIsBusy();
        SetDebugResult($"Dropbox Begin Trading: IPC invoked={invoked}, focus={focus.Name}, busy after={busyAfter}" +
                       (busyAfter ? " - trading started." : " - nothing started (is the Dropbox item queue empty?)."));
    }

    private string GetSelectStringDebugStatus()
    {
        const string addonName = "SelectString";
        var visible = AddonHelper.IsAddonVisible(addonName);
        var ready = AddonHelper.IsAddonReady(addonName);
        var options = GetResolvedAddonListEntries(addonName);
        return options.Count == 0
            ? $"{addonName}: visible={visible}, ready={ready}, resolvedOptions=0"
            : $"{addonName}: visible={visible}, ready={ready}, resolvedOptions={options.Count}, first=Option {options[0].CallbackIndex + 1} -> value {options[0].CallbackIndex}: {options[0].Text}";
    }

    private string BuildSelectStringMenuDump()
    {
        const string addonName = "SelectString";
        var visible = AddonHelper.IsAddonVisible(addonName);
        var ready = AddonHelper.IsAddonReady(addonName);
        var allEntries = AddonHelper.GetAddonTextEntries(addonName)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = GetResolvedAddonListEntries(addonName);
        var lines = new List<string>
        {
            $"{addonName}: visible={visible}, ready={ready}, textEntries={allEntries.Count}, resolvedOptions={options.Count}",
        };

        if (options.Count == 0)
        {
            lines.Add(allEntries.Count == 0
                ? "No SelectString text entries found."
                : $"No selectable menu rows resolved. Raw text: {string.Join(" | ", allEntries)}");
            return string.Join("\n", lines);
        }

        foreach (var option in options)
            lines.Add($"Option {option.CallbackIndex + 1} -> value {option.CallbackIndex}: {option.Text}");

        var optionTexts = new HashSet<string>(options.Select(option => option.Text), StringComparer.OrdinalIgnoreCase);
        var otherText = allEntries
            .Where(text => !optionTexts.Contains(text))
            .ToList();
        if (otherText.Count > 0)
            lines.Add($"Other text: {string.Join(" | ", otherText)}");

        return string.Join("\n", lines);
    }

    private static List<(int CallbackIndex, string Text)> GetResolvedAddonListEntries(string addonName)
    {
        return AddonHelper.GetAddonTextEntries(addonName)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(text => new
            {
                Text = text,
                CallbackIndex = AddonHelper.GetAddonListTextCallbackIndex(addonName, text),
            })
            .Where(entry => entry.CallbackIndex >= 0)
            .GroupBy(entry => entry.CallbackIndex)
            .Select(group => group
                .OrderBy(entry => entry.Text.Length)
                .ThenBy(entry => entry.Text, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(entry => entry.CallbackIndex)
            .Select(entry => (entry.CallbackIndex, entry.Text))
            .ToList();
    }

    private void RunXaDatabaseFcChestDebugCheck()
    {
        if (debugXaFcChestCheckRunning)
        {
            SetDebugResult("XA FC Chest check already running.");
            return;
        }

        debugXaFcChestCheckRunning = true;
        SetDebugResult("XA FC Chest: starting debug run...");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (zoneName, zoneMatch, xaDbReady, vnavReady) = await Plugin.Framework.Run(() =>
                {
                    var currentZoneName = AddonHelper.GetCurrentZoneName();
                    return (
                        currentZoneName,
                        AddonHelper.ZoneNameLooksLikeWorkshop(currentZoneName),
                        plugin.IpcClient.IsXaDatabaseAvailable(),
                        plugin.IpcClient.VnavIsReady());
                });

                if (!xaDbReady)
                {
                    SetDebugResult("XA FC Chest: XA Database not available.");
                    return;
                }

                if (!zoneMatch)
                {
                    SetDebugResult($"XA FC Chest: zone '{zoneName}' is not a Company Workshop.");
                    return;
                }

                if (!vnavReady)
                {
                    SetDebugResult("XA FC Chest: vnavmesh not ready.");
                    return;
                }

                SetDebugResult($"XA FC Chest: zone '{zoneName}' OK - targeting Company Chest...");
                await Plugin.Framework.Run(() => AddonHelper.TargetByName("Company Chest"));

                if (!await WaitForDebugTargetMatchAsync("Company Chest", 3000))
                {
                    SetDebugResult("XA FC Chest: failed to target Company Chest.");
                    return;
                }

                SetDebugResult("XA FC Chest: pathing to Company Chest (1.5y stop)...");
                var pathStarted = await Plugin.Framework.Run(() => AddonHelper.TryPathToCurrentTarget(1.5f));
                if (!pathStarted)
                {
                    SetDebugResult("XA FC Chest: could not start path to Company Chest.");
                    return;
                }

                if (!await WaitForDebugTargetInRangeAsync("Company Chest", 1.5f, 20000))
                {
                    plugin.IpcClient.VnavStop();
                    SetDebugResult("XA FC Chest: path to Company Chest timed out.");
                    return;
                }

                if (!await TryOpenFcChestWithRecoveryAsync())
                {
                    return;
                }

                await System.Threading.Tasks.Task.Delay(500);
                SetDebugResult("XA FC Chest: saving to XA Database...");
                var saveOk = await Plugin.Framework.Run(() => plugin.SaveToXaDatabaseAndRecordSync());

                if (await CloseDebugAddonWithEscAsync("FreeCompanyChest", 12000))
                {
                    SetDebugResult(saveOk
                        ? "XA FC Chest: complete - saved to XA Database and closed FreeCompanyChest."
                        : "XA FC Chest: complete - XA Database save failed, but FreeCompanyChest closed.");
                }
                else
                {
                    SetDebugResult(saveOk
                        ? "XA FC Chest: saved to XA Database, but FreeCompanyChest stayed open after ESC retries."
                        : "XA FC Chest: XA Database save failed and FreeCompanyChest stayed open after ESC retries.");
                }
            }
            catch (Exception ex)
            {
                plugin.IpcClient.VnavStop();
                SetDebugResult($"XA FC Chest: error - {ex.Message}");
            }
            finally
            {
                debugXaFcChestCheckRunning = false;
            }
        });
    }

    private async System.Threading.Tasks.Task<bool> WaitForDebugTargetMatchAsync(string targetName, int timeoutMs, int pollMs = 100)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs);
            elapsed += pollMs;

            var matched = await Plugin.Framework.Run(() => AddonHelper.CurrentTargetMatches(targetName));
            if (matched)
                return true;
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> WaitForDebugTargetInRangeAsync(string targetName, float stopDistance, int timeoutMs, int pollMs = 200)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs);
            elapsed += pollMs;

            var inRange = await Plugin.Framework.Run(() =>
                AddonHelper.IsCurrentTargetWithinStopDistanceAndStopped(targetName, stopDistance));
            if (inRange)
                return true;
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> TryOpenFcChestWithRecoveryAsync()
    {
        const string targetName = "Company Chest";
        const string addonName = "FreeCompanyChest";
        const int maxRecoveryAttempts = 2;

        for (var recoveryAttempt = 0; recoveryAttempt <= maxRecoveryAttempts; recoveryAttempt++)
        {
            SetDebugResult($"XA FC Chest: interacting with {targetName}...");
            var interactOk = await Plugin.Framework.Run(() =>
            {
                AddonHelper.DismissTextError();
                return AddonHelper.InteractWithTarget();
            });
            if (!interactOk)
            {
                SetDebugResult("XA FC Chest: interaction failed.");
                return false;
            }

            var elapsed = 0;
            const int pollMs = 100;
            const int interactTimeoutMs = 5000;

            while (elapsed < interactTimeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(pollMs);
                elapsed += pollMs;

                var (addonVisible, matchedText) = await Plugin.Framework.Run(() =>
                {
                    var opened = AddonHelper.IsAddonVisible(addonName);
                    var text = AddonHelper.TryGetCannotSeeTargetTextError(out var matched)
                        ? matched
                        : string.Empty;
                    return (opened, text);
                });

                if (addonVisible)
                    return true;

                if (!string.IsNullOrWhiteSpace(matchedText))
                {
                    if (recoveryAttempt >= maxRecoveryAttempts)
                    {
                        await Plugin.Framework.Run(() => AddonHelper.DismissTextError());
                        SetDebugResult($"XA FC Chest: _TextError '{matchedText}' persisted after {maxRecoveryAttempts} recovery attempts.");
                        return false;
                    }

                    SetDebugResult($"XA FC Chest: _TextError '{matchedText}' - re-pathing for 0.5s, stopping vnav, and resetting camera ({recoveryAttempt + 1}/{maxRecoveryAttempts})...");
                    await Plugin.Framework.Run(() =>
                    {
                        AddonHelper.DismissTextError();
                        AddonHelper.TryPathToCurrentTarget(1.5f);
                    });

                    await System.Threading.Tasks.Task.Delay(500);
                    plugin.IpcClient.VnavStop();
                    await Plugin.Framework.Run(() =>
                    {
                        AddonHelper.ResetCamera();
                        AddonHelper.DismissTextError();
                        AddonHelper.TargetByName(targetName);
                    });
                    await System.Threading.Tasks.Task.Delay(250);
                    break;
                }
            }

            if (elapsed >= interactTimeoutMs)
            {
                SetDebugResult("XA FC Chest: FreeCompanyChest did not open.");
                return false;
            }
        }

        SetDebugResult("XA FC Chest: FreeCompanyChest did not open after visibility recovery.");
        return false;
    }

    private async System.Threading.Tasks.Task<bool> WaitForDebugAddonVisibleAsync(string addonName, int timeoutMs, int pollMs = 100)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs);
            elapsed += pollMs;

            var visible = await Plugin.Framework.Run(() => AddonHelper.IsAddonVisible(addonName));
            if (visible)
                return true;
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> CloseDebugAddonWithEscAsync(string addonName, int timeoutMs, int pollMs = 100, int escIntervalMs = 1000)
    {
        int elapsed = 0;
        int closedChecks = 0;
        int lastEscAtMs = -escIntervalMs;

        SetDebugResult($"XA FC Chest: closing {addonName} with ESC...");

        while (elapsed < timeoutMs)
        {
            var visible = await Plugin.Framework.Run(() => AddonHelper.IsAddonVisible(addonName));
            if (!visible)
            {
                closedChecks++;
                if (closedChecks >= 2)
                    return true;
            }
            else
            {
                closedChecks = 0;
                if (elapsed - lastEscAtMs >= escIntervalMs)
                {
                    lastEscAtMs = elapsed;
                    await Plugin.Framework.Run(() => KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE));
                }
            }

            await System.Threading.Tasks.Task.Delay(pollMs);
            elapsed += pollMs;
        }

        return false;
    }

    /// <summary>
    /// Checks if the player is mounted or riding pillion.
    /// </summary>
    private static bool IsMounted()
    {
        return Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];
    }

    /// <summary>
    /// Checks if flying is unlocked in the current zone.
    /// Uses PlayerState.CanFly field (offset 0x601), set during zone loading.
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
    /// Checks if the player is in a sanctuary, equivalent to dfunc InSanctuary().
    /// Returns true when the player CANNOT mount (inverse of CanMount).
    /// Matches SND's Player.CanMount logic: if CanMount == false then InSanctuary.
    /// </summary>
    private static unsafe bool InSanctuary()
    {
        return !CanMount();
    }

    // -------------------------------------------------------
    //  Movement State Helpers (vnavmesh IPC)
    //  Used to ensure movement is complete before sending new commands.
    //  Mirrors xafunc MoveTo Completed pattern.
    // -------------------------------------------------------

    /// <summary>
    /// Checks if the navmesh is built and ready for pathfinding.
    /// If not ready, triggers a rebuild via vnavmesh.Nav.Rebuild.
    /// Returns true if ready, false if rebuild was triggered (caller should wait).
    /// </summary>
    private bool EnsureNavReady()
    {
        if (plugin.IpcClient.VnavIsReady())
            return true;

        Plugin.Log.Debug("[XASlave] Nav not ready, triggering rebuild");
        plugin.IpcClient.VnavRebuild();
        return false;
    }

    /// <summary>
    /// Checks if movement is idle (not pathfinding and not running).
    /// Use before sending a new movement command to avoid overlapping paths.
    ///
    /// States:
    ///   Nav.IsReady == false             †’ navmesh not built, need rebuild
    ///   PathfindInProgress == true       †’ calculating path, not yet moving
    ///   Path.IsRunning == true           †’ actively moving along path
    ///   Both false + IsReady == true     †’ idle, safe to send new movement
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

            var idle = await Plugin.Framework.Run(() => IsMovementIdle());
            if (idle) return true;
        }
        return false;
    }

    private unsafe string GetTextErrorMonitorResult()
    {
        const string addonName = "_TextError";
        var trackedSummary = string.Join(" | ", xagmanTradeFailureTexts.Select(entry => entry.Text));

        var addon = AddonHelper.GetAddon(addonName);
        if (addon == null)
        {
            return $"{addonName}: visible=False, ready=False, tracked=\"{trackedSummary}\", matches=0";
        }

        var textEntries = AddonHelper.GetAddonTextEntries(addonName)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matches = GetXagmanTradeFailureMatches(textEntries);
        var matchSummary = matches.Count == 0
            ? "<none>"
            : string.Join(" | ", matches.Select(entry => $"{entry.Kind}: {entry.Text}"));

        if (matchSummary.Length > 220)
            matchSummary = matchSummary.Substring(0, 217) + "...";

        return $"{addonName}: visible={addon->IsVisible}, ready={addon->IsReady}, nodeCount={addon->UldManager.NodeListCount}, textEntries={textEntries.Count}, tracked=\"{trackedSummary}\", matches={matches.Count}, text=\"{matchSummary}\"";
    }

    private static object? GetDropboxPlugin()
    {
        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly
                .GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            
            if (pluginManagerServiceType == null || pluginManagerType == null) return null;
            
            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);

            if (pluginManager == null) return null;

            var installedPlugins = pluginManager.GetType()
                .GetProperty("InstalledPlugins")
                ?.GetValue(pluginManager) as System.Collections.IList;

            if (installedPlugins == null) return null;

            foreach (var plugin in installedPlugins)
            {
                if (plugin == null) continue;
                
                var internalNameProperty = plugin.GetType()
                    .GetProperty("InternalName");
                
                if (internalNameProperty == null) continue;
                
                var internalName = internalNameProperty
                    .GetValue(plugin)?.ToString();

                if (internalName == "Dropbox")
                {
                    var pluginType = plugin.GetType().Name == "LocalDevPlugin" 
                        ? plugin.GetType().BaseType 
                        : plugin.GetType();

                    if (pluginType == null) continue;

                    var instanceField = pluginType.GetField("instance", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    return instanceField?.GetValue(plugin);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error getting Dropbox plugin: {ex.Message}");
        }

        return null;
    }

    private static object? GetPluginConfig(object pluginInstance)
    {
        try
        {
            if (pluginInstance == null) return null;
            
            var configFieldNames = new[] { "C", "Config", "configuration", "Configuration" };
            var pluginType = pluginInstance.GetType();
            var bindingFlags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static;
            
            foreach (var fieldName in configFieldNames)
            {
                var field = pluginType.GetField(fieldName, 
                    bindingFlags);

                if (field != null)
                {
                    var config = field.GetValue(field.IsStatic ? null : pluginInstance);
                    if (config != null) return config;
                }
            }

            foreach (var propName in configFieldNames)
            {
                var property = pluginType.GetProperty(propName, 
                    bindingFlags);

                if (property != null)
                {
                    var getter = property.GetGetMethod(true);
                    var config = property.GetValue(getter != null && getter.IsStatic ? null : pluginInstance);
                    if (config != null) return config;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error getting plugin config: {ex.Message}");
        }

        return null;
    }

    private static bool TryOpenDropboxUi(object pluginInstance)
    {
        try
        {
            if (pluginInstance == null) return false;

            if (TryInvokeParameterlessMethod(pluginInstance, "OpenUI"))
            {
                return true;
            }

            return OpenDropboxConfigWindow(pluginInstance);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error opening Dropbox UI: {ex.Message}");
        }

        return false;
    }

    private static bool OpenDropboxConfigWindow(object pluginInstance)
    {
        try
        {
            if (pluginInstance == null) return false;

            var loadContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(pluginInstance.GetType().Assembly);
            if (loadContext == null) return false;

            var bindingFlags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static;

            foreach (var assembly in loadContext.Assemblies)
            {
                var ezConfigGuiType = assembly.GetType("ECommons.SimpleGui.EzConfigGui");
                if (ezConfigGuiType == null) continue;

                var openMethod = ezConfigGuiType.GetMethod("Open",
                    bindingFlags,
                    null,
                    System.Type.EmptyTypes,
                    null);

                if (openMethod != null)
                {
                    openMethod.Invoke(null, null);
                    return true;
                }

                var openWithArgsMethod = ezConfigGuiType.GetMethod("Open",
                    bindingFlags,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);

                if (openWithArgsMethod != null)
                {
                    openWithArgsMethod.Invoke(null, new object?[] { null, null });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error opening Dropbox config window: {ex.Message}");
        }

        return false;
    }

    private static bool RequestDropboxOpenTabOnce(object pluginInstance, string tabName)
    {
        try
        {
            if (pluginInstance == null || string.IsNullOrWhiteSpace(tabName)) return false;

            var requested = TrySetObjectMemberValue(pluginInstance, "OpenTabName", tabName);
            if (!requested)
            {
                return false;
            }

            QueueDropboxOpenTabReset(pluginInstance);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error requesting Dropbox tab '{tabName}': {ex.Message}");
        }

        return false;
    }

    private static void QueueDropboxOpenTabReset(object pluginInstance, int delayMs = 250)
    {
        _ = ClearDropboxOpenTabRequestAsync(pluginInstance, delayMs);
    }

    private static async System.Threading.Tasks.Task ClearDropboxOpenTabRequestAsync(object pluginInstance, int delayMs)
    {
        try
        {
            if (pluginInstance == null) return;

            await System.Threading.Tasks.Task.Delay(delayMs);
            await Plugin.Framework.Run(() =>
                TrySetObjectMemberValue(pluginInstance, "OpenTabName", string.Empty));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error clearing Dropbox tab request: {ex.Message}");
        }
    }

    private static bool TrySetObjectMemberValue(object target, string memberName, object value)
    {
        try
        {
            if (target == null) return false;

            var targetType = target.GetType();
            var bindingFlags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static;

            var field = targetType.GetField(memberName, bindingFlags);
            if (field != null)
            {
                field.SetValue(field.IsStatic ? null : target, value);
                return true;
            }

            var property = targetType.GetProperty(memberName, bindingFlags);
            if (property != null && property.CanWrite)
            {
                var setter = property.GetSetMethod(true);
                property.SetValue(setter != null && setter.IsStatic ? null : target, value);
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error setting object member {memberName}: {ex.Message}");
        }

        return false;
    }

    private static bool TryInvokeParameterlessMethod(object target, string methodName)
    {
        try
        {
            if (target == null) return false;

            var bindingFlags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static;

            var method = target.GetType().GetMethod(
                methodName,
                bindingFlags,
                null,
                System.Type.EmptyTypes,
                null);

            if (method == null)
            {
                return false;
            }

            method.Invoke(method.IsStatic ? null : target, null);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error invoking method {methodName}: {ex.Message}");
        }

        return false;
    }

    private static object? GetDropboxTaskManager(object pluginInstance)
    {
        try
        {
            if (pluginInstance == null) return null;

            var pluginType = pluginInstance.GetType();
            var bindingFlags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static;

            var field = pluginType.GetField("TaskManager", bindingFlags);
            if (field != null)
            {
                var taskManager = field.GetValue(field.IsStatic ? null : pluginInstance);
                if (taskManager != null) return taskManager;
            }

            var property = pluginType.GetProperty("TaskManager", bindingFlags);
            if (property != null)
            {
                var getter = property.GetGetMethod(true);
                var taskManager = property.GetValue(getter != null && getter.IsStatic ? null : pluginInstance);
                if (taskManager != null) return taskManager;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error getting Dropbox task manager: {ex.Message}");
        }

        return null;
    }

    private static void AbortTaskManager(object taskManager)
    {
        try
        {
            var abortMethod = taskManager.GetType().GetMethod(
                "Abort",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static,
                null,
                System.Type.EmptyTypes,
                null);

            if (abortMethod == null)
            {
                throw new InvalidOperationException("Method 'Abort' not found on task manager");
            }

            abortMethod.Invoke(abortMethod.IsStatic ? null : taskManager, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error aborting task manager: {ex.Message}");
            throw;
        }
    }

    private static void SetConfigProperty(object config, string propertyName, object value)
    {
        try
        {
            var configType = config.GetType();

            // Try to set as a field first
            var field = configType.GetField(propertyName, 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                field.SetValue(config, value);
                return;
            }

            // Try to set as a property
            var property = configType.GetProperty(propertyName, 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Instance);

            if (property != null && property.CanWrite)
            {
                property.SetValue(config, value);
                return;
            }

            throw new InvalidOperationException($"Property '{propertyName}' not found on config object");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Reflection] Error setting config property {propertyName}: {ex.Message}");
            throw;
        }
    }
}
