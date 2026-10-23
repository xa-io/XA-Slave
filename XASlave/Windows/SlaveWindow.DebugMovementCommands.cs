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

    public bool TryExecuteXaMovementCommand(string subcommand, string arguments, out string message, out bool handled) => TryExecuteXaMovementCommandLocalizedCore(out _, subcommand, arguments, out message, out handled);
    internal bool TryExecuteXaMovementCommandMessage(string subcommand, string arguments, out global::XASlave.Localization.UiMessage message, out bool handled)
    {
        var success = TryExecuteXaMovementCommandLocalizedCore(out var presentation, subcommand, arguments, out var originalReply, out handled);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool TryExecuteXaMovementCommandLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, string subcommand, string arguments, out string message, out bool handled)
    {
        replyPresentation = null;
        handled = true;
        var normalized = subcommand.Trim();

        if (normalized.Equals("leaveduty", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arguments))
        {
            handled = false;
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Raw(string.Empty));
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
                    message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S20EA4C8DF7D58B90", normalized.ToLowerInvariant()));
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
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Format("S905A12A28BF0E892", normalizedCommand));
            SetDebugResult(global::XASlave.Localization.UiMessage.Capture(ref replyPresentation, message));
            return false;
        }

        switch (normalizedCommand)
        {
            case "movingcheatersmart":
                RunDebugMovingCheaterSmart();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SF383D8F8AC15EC67"));
                return true;
            case "movingcheaterfly":
                RunDebugMovingCheaterFly();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S9D9ABC17F0C4FA53"));
                return true;
            case "movingcheaterwalk":
                RunDebugMovingCheaterWalk();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S71B0C31A668F7DAC"));
                return true;
            case "interact":
                RunDebugInteract();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S61C62D8FF363B419"));
                return true;
            case "leaveduty":
                RunDebugLeaveDuty();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SD9B1D3C78E6358DE"));
                return true;
            case "recommendedgear":
                RunDebugRecommendedGear();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SDACFB0CF655229F4"));
                return true;
            case "stopmovement":
                RunDebugVnavStop();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SD055D283714989B1"));
                return true;
            case "pathtotargetinteract":
                RunDebugPathToTargetThenInteract();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("SBB60179249761A0C"));
                return true;
            case "pathsmartinteract":
                RunDebugPathSmartThenInteract();
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S76966C02C3BF6F1F"));
                return true;
            default:
                handled = false;
                message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Raw(string.Empty));
                return false;
        }
    }

    private void RunDebugInteract()
    {
        RunWorker("Interact", async token =>
        {
            token.ThrowIfCancellationRequested();
            var ok = await Plugin.RunOnGameThread(AddonHelper.InteractWithTarget);
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Key("S2EAB5B5CB4CE3A25") : global::XASlave.Localization.UiMessage.Key("S14EEF80FC742EEB5")));
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S17CFF3C86F7F809A"));
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
                SetDebugResult((canFly ? (alreadyMounted ? global::XASlave.Localization.UiMessage.Key("S106B99C03B486527") : global::XASlave.Localization.UiMessage.Key("SF3BC5B65D4FCC981")) : (alreadyMounted ? global::XASlave.Localization.UiMessage.Key("S933AEE2452DF8C19") : global::XASlave.Localization.UiMessage.Key("S497CFA0F191ACE4E"))));
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
                SetDebugResult((canFly ? (alreadyMounted ? global::XASlave.Localization.UiMessage.Key("S84B80D52B02E83CC") : global::XASlave.Localization.UiMessage.Key("SF2B9551F66550620")) : (alreadyMounted ? global::XASlave.Localization.UiMessage.Key("S5638ED37F076B095") : global::XASlave.Localization.UiMessage.Key("SA272D6B5E40B0476"))));
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
                SetDebugResult((alreadyMounted ? global::XASlave.Localization.UiMessage.Key("S877308520625A029") : global::XASlave.Localization.UiMessage.Key("SB43D6835E6C691D3")));
            });
        }, exclusive: true);
    }

    private bool TryStartMovingCheaterMountStep(out bool alreadyMounted)
    {
        alreadyMounted = false;
        if (!plugin.IpcClient.VnavIsReady())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S027810E57B428737"));
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
            return;
        }

        if (local == null || !plugin.IpcClient.VnavIsReady())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6E7B4A556C347411"));
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4D77D79F828C32C8"));
            return;
        }

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE52123C6DC9C71FB", targetName, stopDist, interactRange));
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

                if (ringDist == float.MinValue) { SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90BBB6E6F1BAF")); break; }

                if (ringDist <= 0)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S19A3FEE3105458CA", targetName, ringDist));
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S4B08E1291CA212CA", targetName, ringDist));
                    await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    Plugin.Log.Information($"[XASlave] PathToTargetThenInteract: interacted with {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                    interacted = true;
                    break;
                }
                else if (stalled && ringDist < 20.0f)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S793578DEFF36CA49", targetName, ringDist));
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("SFE19EEBCC77A6562", ringDist));
                    Plugin.Log.Warning($"[XASlave] PathToTargetThenInteract: path ended for {targetName} (ring={ringDist:F1}y center={centerDist:F1}y)");
                    if (ringDist <= interactRange * 3)
                    {
                        await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S9B61E7BEB8AA0707", ringDist));
                        interacted = true;
                    }
                    break;
                }
                else if (elapsed % 3000 < pollMs)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S5B7A61E9F75ADEEC", targetName, ringDist));
                }
            }

            if (!interacted && elapsed >= maxTimeoutMs)
            {
                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6757A7A90F506A16", targetName));
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
            return;
        }

        if (local == null || !plugin.IpcClient.VnavIsReady())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6E7B4A556C347411"));
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

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6D427FE9886F3A9E", targetName, ringDist0, canFly, shouldMount));
        Plugin.Log.Information($"[XASlave] PathSmartThenInteract: {targetName} ring={ringDist0:F1}y fly={canFly} mount={shouldMount} stop={stopDist:F1}");

        RunWorker("PathSmartThenInteract", async token =>
        {
            if (shouldMount)
                await Plugin.RunOnGameThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));

            var fly = canFly && shouldMount;
            var pathOk = await Plugin.RunOnGameThread(() =>
                plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, fly, stopDist));
            if (!pathOk) { SetDebugResult(global::XASlave.Localization.UiMessage.Key("S298904A962E111A9")); return; }

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

                if (rd == float.MinValue) { SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90BBB6E6F1BAF")); break; }

                if (rd <= 0)
                {
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S19A3FEE3105458CA", targetName, rd));
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S7B22DEC4B65C87B0", targetName, rd));
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S41004770A9885670", postDismountRd));
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

                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S4B08E1291CA212CA", targetName, postDismountRd));
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE92C4552FB23B1F3", targetName, rd));
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S333FDCF0B0DB732F", rd));
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S9B61E7BEB8AA0707", rd));
                        interacted = true;
                    }
                    break;
                }
                else if (elapsed % 3000 < pollMs)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S4F87AB1CACB590AD", targetName, rd));
                }
            }

            if (!interacted && elapsed >= maxTimeoutMs)
            {
                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S289B7ABC7D660C71", targetName));
            }
        }, exclusive: true);
    }

    private void RunDebugLeaveDuty()
    {
        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
        if (!inDuty)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SB0E55651B808D075"));
            return;
        }

        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SD41BA9F009CB7227"));
        RunWorker("Leave Duty", async token =>
        {
            var inCombat = await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.InCombat]);
            if (inCombat)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S34CCB72B78B8B39D"));
                for (int w = 0; w < 60; w++)
                {
                    await System.Threading.Tasks.Task.Delay(500, token);
                    inCombat = await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.InCombat]);
                    if (!inCombat) break;
                }
                if (inCombat)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5EF03CF68E05E5E0"));
                    return;
                }
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SCAA26D1C6277112B"));
            }

            await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(0x55));
            await System.Threading.Tasks.Task.Delay(1000, token);

            var leaveClicked = await Plugin.RunOnGameThread(() =>
                AddonHelper.ClickAddonButton("ContentsFinderMenu", AddonNodes.ContentsFinderMenuLeave));

            if (leaveClicked)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6FF534E0169308ED"));
                await System.Threading.Tasks.Task.Delay(500, token);

                var yesClicked = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                SetDebugResult((yesClicked ? global::XASlave.Localization.UiMessage.Key("S76130C7163E94B28") : global::XASlave.Localization.UiMessage.Key("S938D82F98BCFE1DA")));
            }
            else
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4F6FB59F662DCEEF"));
            }
        }, exclusive: true);
    }

    private void RunDebugLeaveDutyQuick()
    {
        if (!Plugin.Condition[ConditionFlag.BoundByDuty])
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SE9DAAE607FDE508E"));
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SE36ED0AFE3EF08D4"));
            return;
        }

        if (TryGetDebugLeaveDutyQuickBlocker(out var blocker))
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8960F99885809609", blocker));
            return;
        }

        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SF69C39861987D64B"));
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6CA5CE55BDC87E41"));
                    return;
                }

                if (existingPromptState > 0)
                {
                    var confirmed = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                    SetDebugResult((confirmed ? global::XASlave.Localization.UiMessage.Key("S6D6F56E388BD4BE8") : global::XASlave.Localization.UiMessage.Key("S14B17F7EF2B57FC6")));
                    return;
                }

                var callbacksSent = false;
                for (var attempt = 1; attempt <= DebugLeaveDutyQuickMenuAttempts; attempt++)
                {
                    if (!await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.BoundByDuty]))
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SA5687DC1A55B2C75"));
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
                            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC9B3C84BAC900DCA"));
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S79798ECC36BAB1DA"));
                    return;
                }

                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SA91AAC7E07EE1D91"));
                for (var attempt = 1; attempt <= DebugLeaveDutyQuickPromptAttempts; attempt++)
                {
                    if (!await Plugin.RunOnGameThread(() => Plugin.Condition[ConditionFlag.BoundByDuty]))
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SA49CBF5A60D7A27D"));
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
                            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S01789992C0C3292B"));
                            return;
                        }

                        var confirmed = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                        SetDebugResult((confirmed ? global::XASlave.Localization.UiMessage.Key("S4A7255EAF31D904E") : global::XASlave.Localization.UiMessage.Key("S0D3CA611480F316C")));
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

                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4979AF9BD8CE77F4"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[XASlave] Leave Duty Quick failed.");
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6969C1B81AA5CB08", ex.Message));
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
        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC40A74BE1BAEB48F"));
        RunWorker("Recommended Gear", async token =>
        {
            await Plugin.RunOnGameThread(() => RunDebugRecommendedGearStep1());
            if (!await WaitForDebugAddonVisibleAsync("Character", 3000, token))
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S628D04FCBA4C3E88"));
                return;
            }

            var recommendClicked = await Plugin.RunOnGameThread(() => RunDebugRecommendedGearStep2());
            if (!recommendClicked)
                return;

            if (!await WaitForDebugAddonVisibleAsync("RecommendEquip", 3000, token))
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S1D916C4F365B9506"));
                return;
            }

            var equipClicked = await Plugin.RunOnGameThread(() => RunDebugRecommendedGearStep3());
            await System.Threading.Tasks.Task.Delay(500, token);
            await Plugin.RunOnGameThread(() => RunDebugRecommendedGearClose());
            if (equipClicked)
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SF1DAAF29922088C2"));
        }, exclusive: true);
    }

    private void RunDebugRecommendedGearStep1()
    {
        ChatHelper.SendMessage("/character");
        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S81AD4F4DDA3FBB7F"));
    }

    private bool RunDebugRecommendedGearStep2()
    {
        var addonReady = AddonHelper.IsAddonReady("Character");
        var ok = addonReady && AddonHelper.ClickAddonButton("Character", AddonNodes.CharacterRecommendEquip);
        SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S39A900F2F42B92DB", AddonNodes.CharacterRecommendEquip) : (addonReady ? global::XASlave.Localization.UiMessage.Format("S5EAF06873B8860C4", AddonNodes.CharacterRecommendEquip) : global::XASlave.Localization.UiMessage.Key("S2EC325F5B43A6024"))));
        return ok;
    }

    private bool RunDebugRecommendedGearStep3()
    {
        var addonReady = AddonHelper.IsAddonReady("RecommendEquip");
        var ok = addonReady && AddonHelper.ClickAddonButton("RecommendEquip", AddonNodes.RecommendEquipConfirm);
        SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S49451160C7E0CC9C", AddonNodes.RecommendEquipConfirm) : (addonReady ? global::XASlave.Localization.UiMessage.Format("SD66B39D66CB9369B", AddonNodes.RecommendEquipConfirm) : global::XASlave.Localization.UiMessage.Key("SE9C9F14DF2EE5DF0"))));
        return ok;
    }

    private void RunDebugRecommendedGearClose()
    {
        AddonHelper.CloseAddon("RecommendEquip");
        AddonHelper.CloseAddon("Character");
        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S2C55C73ED15B0C58"));
    }
}
