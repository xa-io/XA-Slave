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
/// Contains DrawDebugCommands(), SetDebugResult(), CanMount(), and InSanctuary().
/// </summary>
public partial class SlaveWindow
{
    // -----------------------------------------------
    //  Debug / Test Commands
    //  Test buttons for all xafunc-referenced commands
    //  These functions will be used as templates for future tasks
    // -----------------------------------------------
    private string debugTargetPlayerName = string.Empty;
    private string debugCallbackAddonName = "SelectString";
    private string debugCallbackValues = "0";
    private int debugAutoRetainerItemId;
    private int debugAutoRetainerDepositGilKeepAmount = 1_000_000;
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
    private int xaDbIpcTestAetheryteId = 8;
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
        global::XASlave.Localization.UiMessage Message);
    private readonly record struct DebugAutoRetainerBellOpenResult(
        bool Opened,
        global::XASlave.Localization.UiMessage Message);
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
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S818ED636AE0021B7"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8095FBAD4E972402"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1C770244EBF0CC48"));
        ImGui.Spacing();

        // Status feedback
        if (!string.IsNullOrEmpty(debugResult) && DateTime.UtcNow < debugResultExpiry)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref debugResultUiMessage, debugResult));
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
        using (var imguiScope167 = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("S46BE6927BD82F81D")))
        if (imguiScope167)
        {

        // ----------------------------------------------
        //  XA Lazy Movements
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S3FD0DCA0875382A1")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S04ADDA4AC0C788B8")))
            RunDebugInteract();
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S26DE6C6BA4D67830")))
            RunDebugVnavStop();
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2B8E0351D4A5EA5A")))
        {
            if (RejectIfExclusiveWorkerBusy("PathToTarget"))
                return;

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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("SCE0E7AD6517284DD", targetName, stopDist));
                    RunWorker("PathToTarget", async token =>
                    {
                        var distSamples = new System.Collections.Generic.List<float>();
                        const int maxSamples = 7;
                        const float stallThreshold = 0.3f;
                        const float closeEnough = 10.0f;
                        const int pollMs = 300;
                        const int maxTimeoutMs = 60000;
                        int elapsed = 0;
                        int jumpAttempts = 0;

                        await System.Threading.Tasks.Task.Delay(600, token);
                        elapsed += 600;

                        while (elapsed < maxTimeoutMs)
                        {
                            await System.Threading.Tasks.Task.Delay(pollMs, token);
                            elapsed += pollMs;

                            var (ringDist, pathActive) = await Plugin.RunOnGameThread(() =>
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

                            if (ringDist == float.MinValue) { SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90BBB6E6F1BAF")); break; }

                            // Negative ring = overlapping hitboxes = very close, treat as arrived
                            if (ringDist <= 0)
                            {
                                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S1D3FEF35D48AEDDE", targetName, ringDist));
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
                                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SB087CBB6192186F6", targetName, ringDist));
                                break;
                            }
                            else if (stalled && ringDist <= closeEnough)
                            {
                                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SDF9015210C18D8D9", targetName, ringDist));
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled within {ringDist:F1}y of {targetName}, stopping");
                                break;
                            }
                            else if (stalled && jumpAttempts < 5)
                            {
                                Plugin.Log.Information($"[XASlave] PathToTarget: stalled at ring={ringDist:F1}y, jump attempt {jumpAttempts + 1}");
                                await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE));
                                jumpAttempts++;
                                distSamples.Clear();
                                await System.Threading.Tasks.Task.Delay(800, token);
                                elapsed += 800;
                            }
                            else
                            {
                                if (elapsed % 3000 < pollMs)
                                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S5B7A61E9F75ADEEC", targetName, ringDist));
                            }
                        }

                        if (elapsed >= maxTimeoutMs)
                        {
                            await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S31BCA1450C8B0037", targetName));
                        }
                    }, exclusive: true);
                }
                else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S298904A962E111A9"));
            }
            else if (target == null) SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6E7B4A556C347411"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SE4E4ACA6B77E88D2"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC2989CB52A425DE3")))
        {
            if (RejectIfExclusiveWorkerBusy("PathToTargetThenInteract"))
                return;

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

                            // Negative ring = overlapping hitboxes = very close, interact immediately
                            if (ringDist <= 0)
                            {
                                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S19A3FEE3105458CA", targetName, ringDist));
                                await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
                                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
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
                            else
                            {
                                if (elapsed % 3000 < pollMs)
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
                else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4D77D79F828C32C8"));
            }
            else if (target == null)
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
            else
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6E7B4A556C347411"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S9405841469006AB7"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7ACFE54C7BF50ED7")))
        {
            if (RejectIfExclusiveWorkerBusy("PathSmartThenInteract"))
                return;

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

                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6D427FE9886F3A9E", targetName, ringDist0, canFly, shouldMount));
                Plugin.Log.Information($"[XASlave] PathSmartThenInteract: {targetName} ring={ringDist0:F1}y fly={canFly} mount={shouldMount} stop={stopDist:F1}");

                RunWorker("PathSmartThenInteract", async token =>
                {
                    // Mount + path simultaneously, mount cast works while running, no need to wait
                    if (shouldMount)
                        await Plugin.RunOnGameThread(() => ChatHelper.SendMessage("/gaction \"Mount Roulette\""));

                    var fly = canFly && shouldMount; // Only fly-path if flight is unlocked AND mounted
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

                        var (rd, cd, pathRunning, pathfinding) = await Plugin.RunOnGameThread(() =>
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

                        if (rd == float.MinValue) { SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90BBB6E6F1BAF")); break; }

                        // Negative ring = overlapping hitboxes = very close, interact immediately
                        if (rd <= 0)
                        {
                            await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S19A3FEE3105458CA", targetName, rd));
                            await Plugin.RunOnGameThread(() => AddonHelper.InteractWithTarget());
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

                            // Hard 2s delay after dismount, prevents "Unable to execute command while jumping" error
                            await System.Threading.Tasks.Task.Delay(2000, token);

                            // Brief ready check, wait up to 1.5s for character to be actionable after dismount
                            for (int sw = 0; sw < 15; sw++)
                            {
                                await System.Threading.Tasks.Task.Delay(100, token);
                                var charReady = await Plugin.RunOnGameThread(() =>
                                    MonthlyReloggerTask.IsPlayerAvailable() &&
                                    !Plugin.Condition[ConditionFlag.Casting]);
                                if (charReady) break;
                            }

                            // Re-check distance after dismount, large mounts expand player hitbox
                            // and dismounting may leave us further away than expected
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
                                // Too far after dismount, re-path on foot to close the gap
                                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S41004770A9885670", postDismountRd));
                                Plugin.Log.Information($"[XASlave] PathSmartThenInteract: post-dismount ring={postDismountRd:F1}y > {interactRange:F1}y, re-pathing");
                                await Plugin.RunOnGameThread(() =>
                                    plugin.IpcClient.VnavPathfindAndMoveCloseTo(targetPos, false, stopDist));
                                // Wait for re-path to complete
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
                        else
                        {
                            if (elapsed % 3000 < pollMs)
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
            else if (target == null) SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6E7B4A556C347411"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S2529825FCF2F0C23"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S647EC4226E5CB188")))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_END);
            KeyInputHelper.HoldKeyForDuration(KeyInputHelper.VK_W, 2000);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S9A64C72100183FB5"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S0650A4EC63927786"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S473A973B6F4370A0")))
        {
            KeyInputHelper.ReleaseKey(KeyInputHelper.VK_W);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S128693B8BB83DE97"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S1A41EC46AFBFD7EC"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S60FD65DD94AE091E")))
        {
            RunDebugMovingCheaterSmart();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SECE57D062BBC234F"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA2D692624294BD96")))
        {
            RunDebugMovingCheaterFly();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SAEA8C1557D83D3F0"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S02F1B0572564C7D7")))
        {
            RunDebugMovingCheaterWalk();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S92E911C47352CB12"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S18BF236337F21386")))
        {
            ChatHelper.SendMessage("/vnav moveflag");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S702F6689614DED34"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SCAC65357BCA5704C"));

        ImGui.Spacing();
        } // end XA Lazy Movements


        } // end Movement Functions

        ImGui.Spacing();

        // ----------------------------------------------
        // [Aetheryte Functions]
        // ----------------------------------------------
        using (var imguiScope753 = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("SCB583245B8F066EF")))
        if (imguiScope753)
        {
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S98DF981D713A7D3F")))
            {
                var count = AetheryteData.GetAetherytesWithZoneIds().Count;
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S104D14E1B9AFE896", count));
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S0B8FA9DFB6953363")))
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var aetherytes = AetheryteData.GetAetherytesInZone(zoneId);
                var zoneName = aetherytes.Any() ? AetheryteData.GetAetherytesWithZoneIds()
                    .FirstOrDefault(x => x.ZoneId == zoneId)?.ZoneName ?? "Unknown" : "Unknown";
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S244FF84D2E8F400F", zoneId, zoneName, aetherytes.Count));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SDEADF3F49B5611CB"));

            ImGui.Spacing();

            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9121DBD0E83623F3")))
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var aetherytes = AetheryteData.GetAetherytesInZone(zoneId);
                var zoneName = aetherytes.Any() ? AetheryteData.GetAetherytesWithZoneIds()
                    .FirstOrDefault(x => x.ZoneId == zoneId)?.ZoneName ?? "Unknown" : "Unknown";
                
                if (aetherytes.Any())
                {
                    var aetheryteList = string.Join(", ", aetherytes.Take(10));
                    var more = aetherytes.Count > 10 ? $" (+{aetherytes.Count - 10} more)" : "";
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S01353ADBFFDBD2A6", zoneId, zoneName, aetheryteList, more));
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("SD80730631AC35989", zoneId, zoneName));
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SC065405235F9E1F9"));

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SFFD4FCB1ABF2FC16")))
            {
                // Example: Get ZoneID for a specific aetheryte
                var testAetheryte = "Limsa Lominsa Aetheryte Plaza";
                var zoneId = AetheryteData.GetZoneIdForAetheryte(testAetheryte);
                var zoneName = AetheryteData.GetZoneNameForAetheryte(testAetheryte);
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S566FB038ED837BCA", testAetheryte, zoneId, zoneName));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S119FE90F987B6B04"));

            ImGui.Spacing();

            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S42B519363EFE9A64")))
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
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S210F3DE175E10F33", result));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SBCCEC8B2ACF84CC5"));

            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S5490FD26AEC70B0D")))
            {
                var results = new List<global::XASlave.Localization.UiMessage>();
                var testAetherytes = new[] { "Limsa Lominsa Aetheryte Plaza", "New Gridania", "Summerford Farms" };
                
                try
                {
                    var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
                    var territorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
                    
                    if (aetheryteSheet == null || territorySheet == null)
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S86706C52D751B3D4"));
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
                            var zoneName = territoryRow?.PlaceName.ValueNullable?.Name.ToString() is { } gameZoneName ? global::XASlave.Localization.UiMessage.Raw(gameZoneName) : global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374");
                            var territoryExists = territoryRow.HasValue;
                            
                            results.Add(global::XASlave.Localization.UiMessage.Format("S14159DD9D0974E6A", testName));
                            results.Add(global::XASlave.Localization.UiMessage.Format("S6E128830BA313A39", row.RowId));
                            results.Add(global::XASlave.Localization.UiMessage.Format("S316049DDA40B5448", zoneId));
                            results.Add(global::XASlave.Localization.UiMessage.Format("S223BF448F4405262", territoryExists));
                            results.Add(global::XASlave.Localization.UiMessage.Format("SB51986068A537AB1", zoneName));
                            results.Add(global::XASlave.Localization.UiMessage.Raw(""));
                        }
                        else
                        {
                            results.Add(global::XASlave.Localization.UiMessage.Format("S64D81A3169D41CFF", testName));
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("SAB02030D1504843F", global::XASlave.Localization.UiMessage.FromException(ex)));
                    return;
                }
                
                SetDebugResult(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S081965356D714DDA"), global::XASlave.Localization.UiMessage.Join("\n", results)));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SF90EE1891A19C5F1"));

            ImGui.Spacing();

            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD6F20AE09EF83BDD")))
            {
                var aetherytes = AetheryteData.GetAetherytesWithZoneIds();
                var exportLines = aetherytes
                    .OrderBy(x => x.ZoneId)
                    .ThenBy(x => x.Name)
                    .Select(x => $"{x.ZoneId}\t{x.Name}\t{x.ZoneName}");

                var header = "ZoneId\tAetheryteName\tZoneName";
                var exportContent = string.Join("\n", new[] { header }.Concat(exportLines));

                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SF023E66611F894AB", aetherytes.Count, exportContent));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SBD91417199C51090"));


        }

        ImGui.Spacing();

        // ----------------------------------------------
        // [Player Checkers]
        // ----------------------------------------------
        using (var imguiScope917 = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("S3970C8745A6FCDF3")))
        if (imguiScope917)
        {

        // ----------------------------------------------
        //  Game State Checks (XA)
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S997D8176A364144F")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8D3B5F87BEBA267F")))
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S21C2A1C4119FE37E"));
            RunWorker("CharacterSafeWait", async token =>
            {
                int consecutivePasses = 0;
                int totalAttempts = 0;
                const int maxAttempts = 120;
                while (consecutivePasses < 3 && totalAttempts < maxAttempts)
                {
                    await System.Threading.Tasks.Task.Delay(1000, token);
                    totalAttempts++;
                    var (np, pa, casting, combat, charName) = await Plugin.RunOnGameThread(() =>
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2DCB5C3BBCBE907E", consecutivePasses, charName, totalAttempts));
                    }
                    else
                    {
                        if (consecutivePasses > 0)
                            Plugin.Log.Information($"[XASlave] CharacterSafeWait: reset at {consecutivePasses}/3, NP={np} PA={pa} Cast={casting} Combat={combat}");
                        consecutivePasses = 0;
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S66B4A2350669AE8E", np, pa, casting, combat, totalAttempts));
                    }
                }

                if (consecutivePasses < 3)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S9C5EFD4DFD79219A"));
                    return;
                }

                var finalName = await Plugin.RunOnGameThread(() => MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S574BD08EE0CAC667", finalName));
            });
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SD24D89BED563CA3D"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4C29C6D15ED8AC0D")))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            SetDebugResult((lp != null ? global::XASlave.Localization.UiMessage.Format("SCF6D4FE7FB495393", lp.Level) : global::XASlave.Localization.UiMessage.Key("SEE932B5A8AEDE2EC")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S46A1ACEC970B6E6A")))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var p = lp.Position;
                var coordStr = $"{p.X:F3}, {p.Y:F3}, {p.Z:F3}";
                ImGui.SetClipboardText(coordStr);
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SD763D27D4590A8D1", coordStr));
            }
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEE932B5A8AEDE2EC"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S14F6CA37AA0BBFC2"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9E6F2E6F9BBCF6F5")))
        {
            var zoneId = Plugin.ClientState.TerritoryType;
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SBAF8ED046F5FE126", zoneId));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF4EE848B67B1641D")))
        {
            try
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
                var row = sheet?.GetRowOrDefault(zoneId);
                var zoneName = row?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SA99731CAE51F6043", zoneName, zoneId));
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("S1474F86C019321DF", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF8B9D8EA9660FFF2")))
        {
            try
            {
                var zoneId = Plugin.ClientState.TerritoryType;
                var zoneName = AddonHelper.GetCurrentZoneName();
                var zoneMatch = AddonHelper.ZoneNameLooksLikeWorkshop(zoneName);
                var housingWorkshop = AddonHelper.IsInWorkshopByHousingManager();
                var finalWorkshop = AddonHelper.IsInWorkshop();
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE3B5A27D2EC1469F", finalWorkshop, zoneMatch, housingWorkshop, zoneName, zoneId));
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("SBCE62B31FA53F90E", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S4F0735A1A2A721B4"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2E366039B99945E9")))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var worldName = lp.CurrentWorld.ValueNullable?.Name.ToString() ?? "Unknown";
                var worldId = lp.CurrentWorld.RowId;
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S39C7E88CA7BCAB5D", worldName, worldId));
            }
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEE932B5A8AEDE2EC"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S5EA79AA818746FA5")))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            SetDebugResult((lp != null ? global::XASlave.Localization.UiMessage.Format("SE98599D98F7CDF63", lp.Name) : global::XASlave.Localization.UiMessage.Key("SEE932B5A8AEDE2EC")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S83EB8A29CCD872A2")))
        {
            var name = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SDAF26450D6C8CF6D", name));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6B5E52287266737B")))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                // CompanyTag is empty string if not in FC
                var fcTag = lp.CompanyTag.ToString();
                var inFc = !string.IsNullOrEmpty(fcTag);
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SD13D5114CB94F90A", inFc, fcTag));
            }
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEE932B5A8AEDE2EC"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S703843FF8CD3B480")))
        {
            ChatHelper.SendMessage("/freecompanycmd");
            var fcInfo = plugin.IpcClient.GetFcInfo();
            var plotInfo = plugin.IpcClient.GetPlotInfo();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S22E8BE01182568F4", fcInfo, plotInfo));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SC7E8CE4B49E55D9F"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SAB2D0636953A44BF")))
        {
            var count = Plugin.PartyList.Length;
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6592406ACCF9DF55", count > 0, count));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4231F1150E251187")))
        {
            ChatHelper.SendMessage("/partycmd disband");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S98DA32F1C19AFB9A"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE6A48FD6DDC0C350")))
        {
            ChatHelper.SendMessage("/partycmd leave");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S92E067FD1614D7A5"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF12DE22670685363")))
        {
            var ok = AddonHelper.ClickYesNo(true);
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Key("S0ABF2935A9526BC7") : global::XASlave.Localization.UiMessage.Key("SC17A451B2A68F972")));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S0AC7C5D61F15C465"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD21A762BE4EC0687")))
        {
            var ok = AddonHelper.ClickYesNo(false);
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Key("S65556B0CE092B3D6") : global::XASlave.Localization.UiMessage.Key("SC17A451B2A68F972")));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SE145EEB980EF8FCA"));

        ImGui.Spacing();

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S08218BF8A08AC00C"));
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S796B15BC165A6609")))
        {
            SetDebugResult(GetSelectStringDebugStatus());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S5E6F4E2C41A336BE"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD1CFC876E92B34E7")))
        {
            var dump = BuildSelectStringMenuDump();
            ImGui.SetClipboardText(dump);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S201A9F5FD6180A5D", dump));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SEB98ABEDEE51EC21"));

        for (var callbackIndex = 0; callbackIndex < 8; callbackIndex++)
        {
            if (callbackIndex > 0 && callbackIndex % 4 != 0)
                ImGui.SameLine();

            var buttonLabel = $"Select {callbackIndex + 1} [{callbackIndex}]";
            if (ImGui.Button(buttonLabel))
                RunDebugSelectStringOption(callbackIndex);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S5FD00D1A35CE44AF", callbackIndex, callbackIndex + 1));
        }

        } // end Game State Checks

        // ----------------------------------------------
        //  Target Game State Checks
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S26B0E6DDE5A6CBE5")))
        {
        ImGui.Spacing();

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SDAD690BD1D497E10"));
        ImGui.SetNextItemWidth(260f);
        var targetEnterPressed = ImGui.InputTextWithHint(
            "##debugTargetPlayerName",
            global::XASlave.Localization.UiLocale.Get("SA236E85AB7AFA171"),
            ref debugTargetPlayerName,
            128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S3F3B7B06C0D49A7B")) || targetEnterPressed))
            RunDebugTargetByName();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S1E16DF71ECC3CD59"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S576AC2E7FC1CA699")))
            RunDebugFocusCurrentTarget();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S813B0CD618800A6F"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6CC607635C282771")))
            RunDebugClearFocusTarget();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SB1910CD7650D031A"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE2A2CF0B1723EDE7")))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            SetDebugResult((target != null ? global::XASlave.Localization.UiMessage.Format("S97471607A89C4FDF", target.Name, target.GameObjectId) : global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC1A57D1BAEEDD8ED")))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target != null)
            {
                var p = target.Position;
                var coordStr = $"{p.X:F3}, {p.Y:F3}, {p.Z:F3}";
                ImGui.SetClipboardText(coordStr);
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SA02CCE62C5B78346", coordStr));
            }
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SBB7C0051991F8001")))
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
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SFABACDC4BB5148C1", target.Name, ringDist, centerDist, playerHitbox, targetHitbox));
            }
            else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S3521F65DE0C1673B"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7E1A5B287C42AA04")))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target != null)
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE137E046416AF2C9", target.Name, target.ObjectKind, target.BaseId));
            else
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S870B6F5E3CFDCED5")))
        {
            var target = Plugin.ObjectTable.LocalPlayer?.TargetObject;
            if (target is Dalamud.Game.ClientState.Objects.Types.IBattleChara bc)
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S4A7CC942BB86665B", bc.CurrentHp, bc.MaxHp, (bc.MaxHp > 0 ? (100.0 * bc.CurrentHp / bc.MaxHp) : 0)));
            else if (target != null)
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S526E3147A6A17F18", target.Name, target.ObjectKind));
            else
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S70E90A9A75A8FED3"));
        }

        ImGui.Spacing();
        } // end Target Checks

        // ----------------------------------------------
        //  Player State Checks (d)
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S626556967E0E48FB")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE6895E88AC6B66E6")))
        {
            var mounted = IsMounted();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SD871BBC585EE3558", mounted));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S644796F641E67496")))
        {
            var combat = Plugin.Condition[ConditionFlag.InCombat];
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S57E427A991099C5B", combat));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB61A049BEFCF0555")))
        {
            var casting = Plugin.Condition[ConditionFlag.Casting];
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S5BC4F29430A2445A", casting));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S58191FE8476D3C7D")))
        {
            var flying = Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Diving];
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S665D4D05281A27E9", flying));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB1354A6D0ADC9671")))
        {
            var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
            var inCombat = Plugin.Condition[ConditionFlag.InCombat];
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SB4EB5923739CA96B", inDuty, inCombat));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S3F5BA5E40368DCD4"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA8F98F06F191AB60")))
        {
            var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
            if (!inDuty)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SB0E55651B808D075"));
            }
            else
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SD41BA9F009CB7227"));
                RunWorker("Leave Duty", async token =>
                {
                    // Wait up to 30s if in combat (might be finishing monsters)
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

                    // Press U to open the Duty Finder menu (ContentsFinderMenu)
                    await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(0x55)); // VK_U = 0x55
                    await System.Threading.Tasks.Task.Delay(1000, token);

                    // Click the named, patch-sensitive ContentsFinderMenu Leave node.
                    var leaveClicked = await Plugin.RunOnGameThread(() =>
                        AddonHelper.ClickAddonButton("ContentsFinderMenu", AddonNodes.ContentsFinderMenuLeave));

                    if (leaveClicked)
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6FF534E0169308ED"));
                        await System.Threading.Tasks.Task.Delay(500, token);

                        // Click Yes on the confirmation dialog
                        var yesClicked = await Plugin.RunOnGameThread(() => AddonHelper.ClickYesNo(true));
                        if (yesClicked)
                            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S76130C7163E94B28"));
                        else
                            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S938D82F98BCFE1DA"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4F6FB59F662DCEEF"));
                    }
                }, exclusive: true);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S4465070507908E2B"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S177E0859467C0EFE")))
            RunDebugLeaveDutyQuick();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S8C2B570DF518E7A1"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S75036F1D86320D37")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S4F0255EEB5E9B327", flames, adders, mael, highest));
                    }
                    else SetDebugResult(global::XASlave.Localization.UiMessage.Key("S196C6942F80702C4"));
                }
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("S5F7DE41B126899B6", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SA2CB1C645197DA54"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SAC4C8959E1284AD8")))
        {
            var count = Plugin.PartyList.Length;
            var members = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var m = Plugin.PartyList[i];
                if (m != null) members.Append($"{m.Name} (HP:{m.CurrentHP}/{m.MaxHP}), ");
            }
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SB3D509A4358888F2", count, members));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SA82FAF84232253CB"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SEDE0071FC56624A9")))
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SDCDCF317FCCF915D"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S9113641B77E54322"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S15C826069645C629")))
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
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S5EDDF9ADA0491640", installedPlugins.Count(p => p.IsLoaded)));
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("S428DB9CFF0E1B697", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S2E91A5921EAF0745"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6B96A808B6E7CCE6")))
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE6D0EFF8B1CD5E1B", count));
                }
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("S9F76EA117662B8A0", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SCE27AA0B438C5BDD"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S94C5847A5B783BCD")))
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
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S41C10786AAF227C4", count));
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("S7F4E6C1B759CF175", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S33BBFC38E8EE8EF6"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S0460362856B525BA")))
        {
            var visible = AddonHelper.IsAddonVisible("_CharaSelectReturn");
            if (visible)
            {
                var ok = AddonHelper.ClickAddonButton("_CharaSelectReturn", 1);
                SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Key("SA20062CF122E4F21") : global::XASlave.Localization.UiMessage.Key("S85BE347DC50D37EC")));
            }
            else
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEFCA72AA94C2BE73"));
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S867029FA8F7BC9A4"));

        ImGui.Spacing();
        } // end Player State Checks

        // ----------------------------------------------
        //  Character Actions (xafunc equivalents)
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("SC332DB3D80D1725A")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S738F6706366C8B8B")))
        {
            ChatHelper.SendMessage("/nastatus off");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S0F0E03E023C1D17D"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S58E74428E14B62DB")))
        {
            ChatHelper.SendMessage("/gaction \"Mount Roulette\"");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S88FC335475396EF8"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB94639B25E146169")))
        {
            ChatHelper.SendMessage("/mount");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S73A6ECA2433A38FC"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD0527E4B3D658351")))
        {
            ChatHelper.SendMessage("/logout");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S49FA47B962DC6601"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S166DBD737506990A")))
        {
            ChatHelper.SendMessage("/inventory");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S19FBE2480EB91FA9"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC53EA9F878436B1D")))
        {
            ChatHelper.SendMessage("/armourychest");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S85F051844586C21C"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S04135E2D90F4F408")))
        {
            ChatHelper.SendMessage("/saddlebag");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S38985E6917625DEB"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB062952FD2A35F98")))
        {
            ChatHelper.SendMessage("/freecompanycmd");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S3B977370AC558613"));
        }

        ImGui.Spacing();
        } // end Character Actions

        // ----------------------------------------------
        //  Player Commands
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S2964E7AF86AECA75")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S04ADDA4AC0C788B8")))
        {
            var ok = AddonHelper.InteractWithTarget();
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Key("SB28B851F29E69261") : global::XASlave.Localization.UiMessage.Key("S6DBC889337B32F73")));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SD8EA4B1973C3132C"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF18124CABAD09F25")))
        {
            ChatHelper.SendMessage("/equiprecommended");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEA202B4D61F8327F"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SA7565F362293680B"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S3B583F83D6D30F35")))
        {
            ChatHelper.SendMessage("/character");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S81AD4F4DDA3FBB7F"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SFE67650F344DEF60")))
        {
            var addonReady = AddonHelper.IsAddonReady("Character");
            var ok = addonReady && AddonHelper.ClickAddonButton("Character", AddonNodes.CharacterRecommendEquip);
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S39A900F2F42B92DB", AddonNodes.CharacterRecommendEquip) : (addonReady ? global::XASlave.Localization.UiMessage.Format("S5EAF06873B8860C4", AddonNodes.CharacterRecommendEquip) : global::XASlave.Localization.UiMessage.Key("S2EC325F5B43A6024"))));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SE5F7A354560854C9"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA4E40E036B65C3DA")))
        {
            var addonReady = AddonHelper.IsAddonReady("RecommendEquip");
            var ok = addonReady && AddonHelper.ClickAddonButton("RecommendEquip", AddonNodes.RecommendEquipConfirm);
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S49451160C7E0CC9C", AddonNodes.RecommendEquipConfirm) : (addonReady ? global::XASlave.Localization.UiMessage.Format("SD66B39D66CB9369B", AddonNodes.RecommendEquipConfirm) : global::XASlave.Localization.UiMessage.Key("SE9C9F14DF2EE5DF0"))));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S31A43D0DE12E3EE0"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SBDE7AD89B226B1B0")))
        {
            AddonHelper.CloseAddon("RecommendEquip");
            AddonHelper.CloseAddon("Character");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S2C55C73ED15B0C58"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD0ACB8FD5F779C00")))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_END);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SF73DD5CEC242424E"));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S905138FB37B283FB"));

        ImGui.Spacing();
        } // end Player Commands

        // ----------------------------------------------
        //  XA Database
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S8B20C8AE60A98CFE") + "##playerCheckers")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S98F9CDA2567ADD9A")))
        {
            var ok = plugin.SaveToXaDatabaseAndRecordSync();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SCA62ADA14B29D171", ((ok ? global::XASlave.Localization.UiMessage.Key("S565339BC4D33D728") : global::XASlave.Localization.UiMessage.Key("S02BD349299173F7F")))));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB5C7E00F092FB784")))
        {
            var ok = plugin.IpcClient.Refresh();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SDACD59C64980DDD7", ((ok ? global::XASlave.Localization.UiMessage.Key("S565339BC4D33D728") : global::XASlave.Localization.UiMessage.Key("S02BD349299173F7F")))));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9E0DC77D837FB85A")))
        {
            var ready = plugin.IpcClient.IsReady();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SDCCCB06EC3E881F0", ready));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S5DB038D0288A3FF9")))
        {
            RunXaDatabaseFcChestDebugCheck();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S084DD575B3C0FD11"));

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB2CA9710B0BDF1ED")))
        {
            var gil = plugin.IpcClient.GetGil();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S1362AE4BE63316A7", gil));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S35C6D3B5B92D3952")))
        {
            var gil = plugin.IpcClient.GetRetainerGil();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S40A323321AF56787", gil));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF076089688AFF4F6")))
        {
            var info = plugin.IpcClient.GetFcInfo();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S83823F76B1EDDF94", info));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S82F248833064F142")))
        {
            var info = plugin.IpcClient.GetPlotInfo();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S14474DF60CFB71DC", info));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC148CD2332C8A099")))
        {
            var info = plugin.IpcClient.GetPersonalPlotInfo();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SFEEB96845D51F932", info));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9E9956EC588B7691")))
        {
            ChatHelper.SendMessage("/housing");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S3252CCEADEE0AF25"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA8BF08429E3BC81C")))
        {
            var ver = plugin.IpcClient.GetVersion();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2A55898C52048851", ver));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S0DD09F96DE1F72CB")))
        {
            var variant = AddonHelper.GetHousingMenuVariant();
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingMenu", "Estate Settings");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingMenu", "Estate Settings");
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S6CA30F8AFE62C0E7", callbackIndex, ((string.IsNullOrEmpty(variant) ? global::XASlave.Localization.UiMessage.Key("S7B74467362B0949F") : global::XASlave.Localization.UiMessage.Value(variant)))) : global::XASlave.Localization.UiMessage.Key("S1B34AD3290F86A7C")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8DD40B1D134F8161")))
        {
            var variant = AddonHelper.GetHousingMenuVariant();
            SetDebugResult((!string.IsNullOrEmpty(variant) ? global::XASlave.Localization.UiMessage.Format("S4665C110908439E5", variant) : global::XASlave.Localization.UiMessage.Key("SA9FE2CD99885969F")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB6098D7F61AB2CB8")))
        {
            var address = AddonHelper.GetAddonTextEntries("HousingSignBoard")
                .FirstOrDefault(text => text.Contains("Ward", StringComparison.OrdinalIgnoreCase) && text.Contains(",", StringComparison.Ordinal));
            SetDebugResult((!string.IsNullOrEmpty(address) ? global::XASlave.Localization.UiMessage.Format("S9C29FB88E3BD218C", address) : global::XASlave.Localization.UiMessage.Key("SD340A3702CB3D3AA")));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9DC63A62DA0A7206")))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Apartment");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Apartment");
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S84E4D91D34B41753", callbackIndex) : global::XASlave.Localization.UiMessage.Key("SFE7D34C0F59ABE74")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S029BECD799283C40")))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Free Company Estate");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Free Company Estate");
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("SE786B96AA1518ABD", callbackIndex) : global::XASlave.Localization.UiMessage.Key("S5033993018B74A60")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SFD034ECB677FEAB0")))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Private Estate");
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Private Estate");
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("SD70B5B6DEA6174F1", callbackIndex) : global::XASlave.Localization.UiMessage.Key("S7A8D08679639EBE8")));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB26F91CE6B97EE48")))
        {
            var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("HousingSelectHouse", "Shared Estate", true);
            var ok = callbackIndex >= 0 && AddonHelper.SelectAddonListText("HousingSelectHouse", "Shared Estate", true);
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("SEC280A5790757757", callbackIndex) : global::XASlave.Localization.UiMessage.Key("S89AB277CCD28C3C7")));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S72629F180FED1776")))
        {
            var ok = AddonHelper.SelectFirstAddonListText(
                "HousingSubmenu",
                out var callbackIndex,
                out var matchedText,
                ("View Room Details", false),
                ("View Estate Details", false),
                ("Details", true));
            SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S3AE9E61D513DD61B", matchedText, callbackIndex) : global::XASlave.Localization.UiMessage.Key("S6B107E9E9EC6934C")));
        }

        ImGui.Spacing();
        } // end XA Database


        } // end Player Checkers

        using (var imguiScope1827 = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("S9E25DA5C15CA0FB3")))
        if (imguiScope1827)
        {

        DrawPluginInspector();

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SA8D85371E852AE36") + "##xaAbuse")))
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S939AF4DA41F114ED"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S390591EA8DC1971A", GetXaAbuseStatusText().Display));
            ImGui.Spacing();
            
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6249A15E45F8A456")))
            {
                EnableXaAbuse(false);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7D870FECD219BA64")))
            {
                EnableXaAbuse(true);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S97A7810E3E3DDDE9")))
            {
                ResetXaAbuse();
            }

            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6832171D5F230932")))
            {
                EnableXaAbuseOverlay(false);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S49244BF4CCCCF7FE")))
            {
                EnableXaAbuseOverlay(true);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2BA3DD85EC8659A1")))
            {
                DisableXaAbuseOverlay();
            }

            var useTextureOverlay = xaAbuseOverlayUseTexture;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF85F014C3851C097") + "##xaAbuseUseTex"), ref useTextureOverlay))
                xaAbuseOverlayUseTexture = useTextureOverlay;
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S330C15E9318C2164")))
            {
                xaAbuseOverlayUseTexture = true;
                xaAbuseOverlayTexturePath = XaAbuseDefaultTexturePath;
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SB012E455F26116D9", XaAbuseDefaultTexturePath));
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF8D314F4E7D1EC12")))
            {
                xaAbuseOverlayUseTexture = false;
                xaAbuseOverlayText = XaAbuseDefaultOverlayText;
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S56A89BEE37E2A0A8"));
            }

            ImGui.SetNextItemWidth(Scale(220f));
            global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("SFC966D6FBFFFDD67") + "##xaAbuseOverlayText"), ref xaAbuseOverlayText, 64);
            ImGui.SetNextItemWidth(-1f);
            global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("SCB1770E1896563EB") + "##xaAbuseOverlayPath"), ref xaAbuseOverlayTexturePath, 260);
            ImGui.SetNextItemWidth(Scale(240f));
            global::XASlave.Localization.UiNative.ColorEdit4((global::XASlave.Localization.UiLocale.Native("S4770ECE45AE17DE6") + "##xaAbuseOverlayFillColor"), ref xaAbuseOverlayFillColor, ImGuiColorEditFlags.AlphaBar);
            ImGui.SetNextItemWidth(Scale(240f));
            global::XASlave.Localization.UiNative.ColorEdit4((global::XASlave.Localization.UiLocale.Native("SC44E8C1F46AA5B8C") + "##xaAbuseOverlayShadowColor"), ref xaAbuseOverlayShadowColor, ImGuiColorEditFlags.AlphaBar);
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S86B91A8783746583") + "##xaAbuseResetTextColors")))
            {
                xaAbuseOverlayFillColor = XaAbuseDefaultOverlayFillColor;
                xaAbuseOverlayShadowColor = XaAbuseDefaultOverlayShadowColor;
            }

            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SC1088FAE2C530913") + "##xaAbuse")))
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S77BF352086EEF120"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S5490A670ACC5C1B1"));
            ImGui.Spacing();

            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S6E7BE685B22C6C4D") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetAllCharacterHeaders(
                    AutoRetainerUiSection.Retainers,
                    true);
                SetDebugResult(result.MessagePresentation);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S42AE95B7272ABE81") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetAllCharacterHeaders(
                    AutoRetainerUiSection.Retainers,
                    false);
                SetDebugResult(result.MessagePresentation);
            }

            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SD0083F7088226319") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetAllCharacterHeaders(
                    AutoRetainerUiSection.Deployables,
                    true);
                SetDebugResult(result.MessagePresentation);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3BC34D46F8253922") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetAllCharacterHeaders(
                    AutoRetainerUiSection.Deployables,
                    false);
                SetDebugResult(result.MessagePresentation);
            }

            ImGui.Spacing();
            var attentionFilterEnabled = AutoRetainerUiReflectionService.DeployablesAttentionFilterEnabled;
            var attentionFilterButtonText = (attentionFilterEnabled ? global::XASlave.Localization.UiMessage.Key("S8C6FA7A730D70F2C") : global::XASlave.Localization.UiMessage.Key("SA3A586FEE49B6752"));
            if (global::XASlave.Localization.UiNative.Button($"{attentionFilterButtonText.Original}##xaAbuseAutoRetainer", display: attentionFilterButtonText.Display))
            {
                var result = AutoRetainerUiReflectionService.SetDeployablesAttentionFilter(true);
                SetDebugResult(result.MessagePresentation);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SD2D8D4B70001E0BC") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetCharacterFilter(
                    AutoRetainerCharacterFilterMode.None);
                SetDebugResult(result.MessagePresentation);
            }

            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8799A44C1FCB6F19") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetCharacterFilter(
                    AutoRetainerCharacterFilterMode.MultiEnabled);
                SetDebugResult(result.MessagePresentation);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0FC4425898DD714F") + "##xaAbuseAutoRetainer")))
            {
                var result = AutoRetainerUiReflectionService.SetCharacterFilter(
                    AutoRetainerCharacterFilterMode.MultiDisabled);
                SetDebugResult(result.MessagePresentation);
            }

            var missingFcAddressFilterEnabled = AutoRetainerUiReflectionService.CharacterFilterMode
                == AutoRetainerCharacterFilterMode.MissingLifestreamFcAddress;
            var missingFcAddressButtonText = (missingFcAddressFilterEnabled ? global::XASlave.Localization.UiMessage.Key("SE2459C5DDD0E1623") : global::XASlave.Localization.UiMessage.Key("S9A97EA97AFFE41F7"));
            if (global::XASlave.Localization.UiNative.Button($"{missingFcAddressButtonText.Original}##xaAbuseAutoRetainer", display: missingFcAddressButtonText.Display))
            {
                var result = AutoRetainerUiReflectionService.SetCharacterFilter(
                    AutoRetainerCharacterFilterMode.MissingLifestreamFcAddress);
                SetDebugResult(result.MessagePresentation);
            }

            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S4FEE9401EF7B76C2"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1C29A11FC0161662"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S77857C8A278E8235"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SDDA9A5C1AFDCA61E"));

            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SC4B006AB186FE022") + "##xaAbuse")))
        {
            var readable = plugin.IpcClient.TryGetLifestreamBusy(out var busy);
            ImGui.TextUnformatted(readable ? global::XASlave.Localization.UiLocale.FormatKey("S693B7479351F3790", busy) : global::XASlave.Localization.UiLocale.Get("S7A26BA8210FC0B94"));
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SBAF083B029CB149C") + "##XaAbuseLifestream")))
            {
                CancelLifestreamTaskMessage(out var message);
                SetDebugResult(message);
            }
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SA89E8A11E3E6EF7C") + "##xaAbuse")))
        {
            DrawXaAbuseLobbyTest();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S997F656AB5725738") + "##xaAbuse")))
        {
            DrawXaAbuseDalamud();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S4252C720AF443307") + "##xaAbuse")))
        {
            DrawXaAbuseDalamudDllBypassChecker();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S9EA80739025FFA3B") + "##xaAbuse")))
        {
            DrawXaAbuseInputWindows();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S34C59B4CADD6ADD7") + "##xaAbuse")))
        {
            DrawXaAbuseFreeCompany();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S55CAC01855A1896C") + "##xaAbuse")))
        {
            DrawXaAbuseCallbacks();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SFCF7749ED05DD1C3") + "##xaAbuse")))
        {
            DrawXaAbuseNotificationTests();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SB94FB11966DC68BC") + "##xaAbuse")))
        {
            DrawDalamudTestNotifications();
            ImGui.Spacing();
        }

        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S8B20C8AE60A98CFE") + "##xaAbuse")))
        {
            DrawXaAbuseXaDatabaseIpcTests();
            ImGui.Spacing();
        }

        ImGui.Spacing();

        }

        ImGui.Spacing();

        // ----------------------------------------------
        //   [Punish]
        // ----------------------------------------------
        using (var imguiScope1940 = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("SF1FC8ACEFDC3B4C6")))
        if (imguiScope1940)
        {

        // ----------------------------------------------
        //  AutoRetainer
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("SC1088FAE2C530913") + "##punish")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9B795D2E7629A961")))
        {
            plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SB8D102F8F4CD2D4A"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6000BA680FDBE7B1")))
        {
            plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC4DF3A3158A2D80A"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S38B5B0620C225E8D")))
        {
            var suppressed = plugin.IpcClient.AutoRetainerGetSuppressed();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S77503A3ED3782C68", suppressed));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S80AA5CBE48334299")))
        {
            var avail = plugin.IpcClient.IsAutoRetainerAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8DFD73B885F83D3F", avail));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SEC2E3907AA28E60A") + "##arPluginStateBusy")))
        {
            var busy = plugin.IpcClient.AutoRetainerPluginStateIsBusy();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SCFC410992941CE38", busy));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SB7B37BA7FC05BC42") + "##arPluginStateRetainers")))
        {
            var ready = plugin.IpcClient.AutoRetainerPluginStateAreAnyRetainersAvailableForCurrentChara();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8D4FAD3FA6FFBEFB", ready));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SF785F5DA3D3AFAB3") + "##arPluginStateMulti")))
        {
            var enabled = plugin.IpcClient.AutoRetainerPluginStateGetMultiModeStatus();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S87A328354946A852", enabled));
        }

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S5C3D7613A0425E7A") + "##arPluginStateCanAutoLogin")))
        {
            var canAutoLogin = plugin.IpcClient.AutoRetainerPluginStateCanAutoLogin();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S650BF4558CC2E2F8", canAutoLogin));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S5E549C02777725CB") + "##arPluginStateRetainerSense")))
        {
            var retainerSense = plugin.IpcClient.AutoRetainerPluginStateGetOptionRetainerSense();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE4DA7CF7DEB445B2", retainerSense));
        }

        ImGui.SetNextItemWidth(Scale(120f));
        global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("S2E5FFE2CA6851003") + "##arPluginStateItemProtectedId"), ref debugAutoRetainerItemId);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9BC8D8AE72EADCCA") + "##arPluginStateItemProtected")))
        {
            var itemId = Math.Max(0, debugAutoRetainerItemId);
            var protectedItem = plugin.IpcClient.AutoRetainerPluginStateIsItemProtected((uint)itemId);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SEA641603A64D60C6", itemId, protectedItem));
        }

        var arLocalContentId = Plugin.PlayerState.ContentId;
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S4547E07618754191") + "##arPluginStateVesselsReady")))
        {
            var ready = plugin.IpcClient.AutoRetainerPluginStateAreAnyEnabledVesselsReady(arLocalContentId);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S48E6387D9B5AAE79", arLocalContentId, FormatNullableBool(ready)));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0D66749D169BE130") + "##arPluginStateVesselsNotDeployed")))
        {
            var notDeployed = plugin.IpcClient.AutoRetainerPluginStateAreAnyEnabledVesselsNotDeployed(arLocalContentId);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S9E99F14F7A13C5B5", arLocalContentId, FormatNullableBool(notDeployed)));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S38203848AC943D8C") + "##arNonAutoInteract")))
        {
            StartDebugAutoRetainerNonAutoInteract();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S0805618BD693F312"));
        if (debugAutoRetainerNonAutoInteractActive)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S0BFBAE737718DB8E"));
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(Scale(150f));
        if (global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("SE85DC5631C413D6E") + "##arDepositGilKeepAmount"), ref debugAutoRetainerDepositGilKeepAmount, 100000, 1000000)
            && debugAutoRetainerDepositGilKeepAmount < 0)
        {
            debugAutoRetainerDepositGilKeepAmount = 0;
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S6EBD68E8BD901FDE") + "##arDepositGil")))
        {
            StartDebugAutoRetainerDepositGil();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SC17DB1A4A6E4AB0B"));

        if (debugAutoRetainerDepositGilActive)
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + "##arDepositGilCancel")))
            {
                StopDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage.Key("S15F3727F81D1CB0F"), finishCurrentRetainer: false);
            }

            var state = ((debugAutoRetainerDepositGilProcessingRetainer ? global::XASlave.Localization.UiMessage.Key("S6348A23E431E3A76") : (debugAutoRetainerDepositGilListTaskRequested ? global::XASlave.Localization.UiMessage.Key("S39029A7DBC076302") : (debugAutoRetainerDepositGilOpeningRetainerList ? global::XASlave.Localization.UiMessage.Key("S892F1D6B7436CE37") : global::XASlave.Localization.UiMessage.Key("S12DE2B0AD9848FC3"))))).Display;
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SA7DCFB093E24E907", state, Math.Max(0, debugAutoRetainerDepositGilKeepAmount), debugAutoRetainerDepositGilProcessedRetainers, debugAutoRetainerDepositGilRequestedTotal, debugAutoRetainerDepositGilActualTotal));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF9AC0F62F1067BD1")))
        {
            ChatHelper.SendMessage("/ays discard");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEB451DCC9066CC48"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA168380E152B8E2F")))
        {
            ChatHelper.SendMessage("/ays itemsell");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SA88CA87018DD5DA5"));
        }

        ImGui.Spacing();
        } // end AutoRetainer

        // ----------------------------------------------
        //  Lifestream
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("SC4B006AB186FE022")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S47D2600BF6B9E23C")))
        {
            plugin.IpcClient.LifestreamExecuteCommand("home");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S78B0B121F2CA1D0F"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9F0356594434A8B4")))
        {
            plugin.IpcClient.LifestreamExecuteCommand("fc");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S83DF3240650DCDF0"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S81FDED66870CC42F")))
        {
            plugin.IpcClient.LifestreamExecuteCommand("hc");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S0DED8626CC0B88B9"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4735CB42FEFA1493")))
        {
            plugin.IpcClient.LifestreamAbort();
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S494F7B68ED0928AD"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF001AE51CF20514D")))
        {
            plugin.IpcClient.LifestreamExecuteCommand("");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S96E59B7FE5FC1D50"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2E9F710C1FD0E145")))
        {
            plugin.IpcClient.LifestreamExecuteCommand("auto");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SB2869BCBAA163C13"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB96050E129FDB894")))
        {
            try
            {
                var busy = plugin.IpcClient.LifestreamIsBusy();
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S7CFA644E8806EF95", busy));
            }
            catch (Exception ex) { SetDebugResult(global::XASlave.Localization.UiMessage.Format("SC7D38D472BC80803", global::XASlave.Localization.UiMessage.FromException(ex))); }
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB560E504F185B7E6")))
        {
            var avail = plugin.IpcClient.IsLifestreamAvailable();
            var busy = avail && plugin.IpcClient.LifestreamIsBusy();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8BFA17BA9E8B0C3E", avail, busy));
        }

        ImGui.Spacing();
        } // end Lifestream

        // ----------------------------------------------
        //  TextAdvance
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S61B7437CA7FB7CC6") + "##punish")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA99EFDCC05A20738")))
        {
            ChatHelper.SendMessage("/at y");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S8B78904B756793DE"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S13F505BE5020DAEF")))
        {
            ChatHelper.SendMessage("/at n");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S8C6FFEC5007372E3"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SDDA282A6B025392E")))
        {
            var avail = plugin.IpcClient.IsTextAdvanceAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SAF081128BBE37FB7", avail));
        }

        ImGui.Spacing();
        } // end TextAdvance

        // ----------------------------------------------
        //  YesAlready
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S8CBEEE106B327ECC") + "##punish")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SAA91869ADB2AA762")))
        {
            plugin.IpcClient.YesAlreadySetEnabled(true);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC33F70CB23A67301"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SCB9AFCA899B745F1")))
        {
            plugin.IpcClient.YesAlreadySetEnabled(false);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S895A8F173D6A3DE9"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8187E68A9AEC939F")))
        {
            var enabled = plugin.IpcClient.YesAlreadyIsEnabled();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S93778E0582AB25A3", enabled));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S63A86F3CFBB0823B")))
        {
            plugin.IpcClient.YesAlreadyPause(5000);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SE732B8A89860CC11"));
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S5EDC42BE1010568D")))
        {
            var avail = plugin.IpcClient.IsYesAlreadyAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SDBA59E3F4769524D", avail));
        }

        ImGui.Spacing();
        } // end YesAlready

        // ----------------------------------------------
        //  Artisan
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S91117818F38F25E7") + "##punish")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6FECBADA18F78C0C")))
        {
            ChatHelper.SendMessage("/xlenableprofile Artisan");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SA9DE5148AEE453E8"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4125701A45DA235E")))
        {
            ChatHelper.SendMessage("/xldisableprofile Artisan");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4B48DCC45E204ED6"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S3403F06539F8E74B")))
        {
            var avail = plugin.IpcClient.IsArtisanAvailable();
            var busy = avail && plugin.IpcClient.ArtisanIsBusy();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S306BC4EA950C8A0E", avail, busy));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S45D229284922B30F")))
        {
            var status = plugin.IpcClient.ArtisanGetEnduranceStatus();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2E5D4E620C2C13DA", status));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SB4FC3496D6B3A663")))
        {
            plugin.IpcClient.ArtisanSetEnduranceStatus(true);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SAE08FF8D6E7BE94C"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SEA2B9E5CBCEB6E36")))
        {
            plugin.IpcClient.ArtisanSetEnduranceStatus(false);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SAB9C26B65B1BF800"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1748903CD582660D")))
        {
            var running = plugin.IpcClient.ArtisanIsListRunning();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S9C3FEBB35F3F7D67", running));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2DD6262AB5011904")))
        {
            var paused = plugin.IpcClient.ArtisanIsListPaused();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SCAC9B53CCD4D2E30", paused));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1E3955DFE9B5FAFA")))
        {
            plugin.IpcClient.ArtisanSetListPause(true);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SCD1B6180DD6BF2C9"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S33232CE9B417E1FD")))
        {
            plugin.IpcClient.ArtisanSetListPause(false);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S90DE8375F66146A8"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE16C552A5792C541")))
        {
            var stop = plugin.IpcClient.ArtisanGetStopRequest();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SD64AD332BE69DCF7", stop));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2497DDEEA188ADD5")))
        {
            plugin.IpcClient.ArtisanSetStopRequest(true);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S8C0613A0BE13ED56"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6FFE60EB9744699A")))
        {
            plugin.IpcClient.ArtisanSetStopRequest(false);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5F8A4C613EBE7E8F"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE75A5BEF69CB3F3F")))
        {
            var avail = plugin.IpcClient.IsArtisanAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SBF4AFED761CE9D3C", avail));
        }

        ImGui.Spacing();
        } // end Artisan

        // ----------------------------------------------
        //  Dropbox
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S4750D0A12E03A1BE")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SBD50D04C4CD3B51D")))
        {
            ChatHelper.SendMessage("/dropbox");
            ChatHelper.SendMessage("/dropbox OpenTradeTab");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S1A5960D278FADD7E"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S24CB64CD1EDE14FE")))
        {
            var busy = plugin.IpcClient.DropboxIsBusy();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S316F347984CC9827", busy));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S2869C0FD0475C5F3")))
        {
            var avail = plugin.IpcClient.IsDropboxAvailable();
            var busy = avail && plugin.IpcClient.DropboxIsBusy();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S5100C1B11BAEF206", avail, busy));
        }

        ImGui.Spacing();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC687574A06FFD811")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S520CA139FDFBC3AF"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S77D988F13A8A25C6"));
                    }
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5AC68103284B8FF0"));
                }
            }
            catch (Exception ex)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8DE206C6F867F082", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SEA6B9289252CBEEB")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC3A9DBBDCD74EBC3"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S77D988F13A8A25C6"));
                    }
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5AC68103284B8FF0"));
                }
            }
            catch (Exception ex)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S9368E06EC39FF603", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
        }

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC1C22B76BE0DE547")))
        {
            RunDebugDropboxBeginTrading(focusCurrentTargetFirst: false);
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S217A284EA8902F1D")))
        {
            RunDebugDropboxBeginTrading(focusCurrentTargetFirst: true);
        }

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S234A899503990225")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SFBE1BA40C1DD3DBC"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S2B556E7E450E2855"));
                    }
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5AC68103284B8FF0"));
                }
            }
            catch (Exception ex)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SB4C28331BFB3CD0A", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4F820BF7B6546663")))
        {
            SetDebugResult(GetTextErrorMonitorResult());
        }

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8ED51254CF16092E")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SFD815CAF6FEF03D4"));
                    }
                    else if (uiOpened)
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S9E9B8D913DA3BEF8"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S0227553934570C0D"));
                    }
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5AC68103284B8FF0"));
                }
            }
            catch (Exception ex)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S00D70CB4649F227E", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SEF026C7FAE03441C")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S0DE1D76017E49108"));
                    }
                    else if (uiOpened)
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S74D10EB9F3219497"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SD1AF3A350577D92B"));
                    }
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5AC68103284B8FF0"));
                }
            }
            catch (Exception ex)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2570B5828A24B3C6", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
        }

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8397CC865A4D6DE7")))
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
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S849A0F502FCC735B"));
                    }
                    else
                    {
                        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S77D988F13A8A25C6"));
                    }
                }
                else
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5AC68103284B8FF0"));
                }
            }
            catch (Exception ex)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8D53C321DA39F95D", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
        }

        ImGui.Spacing();
        } // end Dropbox

        // ----------------------------------------------
        //  Pandoras Box
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("SDF8EAE6D4C295C59")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S6D42B65C1CA5A845")))
        {
            var ok = plugin.IpcClient.PandoraSetFeatureEnabled("Auto-Sprint in Sanctuaries", true);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8C8B1CE0C810E6BE", ok));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD83622BCC8869F7C")))
        {
            var ok = plugin.IpcClient.PandoraSetFeatureEnabled("Auto-Sprint in Sanctuaries", false);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SFB11046B20A55766", ok));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S9FB0B09F7CD4F9DC")))
        {
            var avail = plugin.IpcClient.IsPandorasBoxAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SCEC5C43D0152282E", avail));
        }

        ImGui.Spacing();
        } // end Pandoras Box

        // ----------------------------------------------
        //  Deliveroo
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S922FD1EC8A1BDE5F")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S0605B802DAB16D57")))
        {
            ChatHelper.SendMessage("/deliveroo enable");
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S987DE176829DD508"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7FCDDAD7CF021599")))
        {
            var running = plugin.IpcClient.DeliverooIsTurnInRunning();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S3C3DF943040877A1", running));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7FA50BF8777CF726")))
        {
            var avail = plugin.IpcClient.IsDeliverooAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6EF2F46AF88C4CDC", avail));
        }

        ImGui.Spacing();
        } // end Deliveroo

        // ----------------------------------------------
        //  Splatoon
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S6F3F2D2BE3F28969")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S51C91AA993DA4DAB")))
        {
            var avail = plugin.IpcClient.IsSplatoonAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2BD4757B5C6345C7", avail));
        }

        ImGui.Spacing();
        } // end Splatoon

        // ----------------------------------------------
        //  vnavmesh
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S66696C37C0AF5DF7") + "##punish")))
        {
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SDB55CABADF6B7FE3")))
        {
            var ready = plugin.IpcClient.VnavIsReady();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SFC699F70C5244990", ready));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4F3227A16D3F1E14")))
        {
            var running = plugin.IpcClient.VnavPathIsRunning();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S73B3AAF2CA513A4E", running));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S26DE6C6BA4D67830")))
        {
            plugin.IpcClient.VnavStop();
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S17CFF3C86F7F809A"));
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S0ACD8F232B4D6F41")))
        {
            plugin.IpcClient.VnavRebuild();
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S07E40EF5CF904F46"));
        }

        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SFBB8A12880F967B3")))
        {
            var canFly = HasFlightUnlocked();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S265C66C318BBD911", canFly, Plugin.ClientState.TerritoryType));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S8B09046C51B1D1EF"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SDE9D1BDAEC16E152")))
        {
            var inSanc = InSanctuary();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S431B543D906D02DC", inSanc, !inSanc));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S41CB6787400C1599"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S40CC3D5EB5D6C255")))
        {
            var avail = plugin.IpcClient.IsVnavAvailable();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S22EF4B1C909C3D97", avail));
        }

        ImGui.Spacing();
        } // end vnavmesh


        } // end Punish

        // ----------------------------------------------
        //   [Key Inputs]
        // ----------------------------------------------
        if (global::XASlave.Localization.UiNative.CollapsingHeader(global::XASlave.Localization.UiLocale.Native("S5D4C3D0E646B56AD")))
        {
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA6F217B4B716B9B2"));
        ImGui.Spacing();

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8B6B263C0D4D21BE"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SC7026DDF68F21957"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S9EF5B81E6F4C76CE"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S48FD23F52AF49668"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SBED1616ACB6B92CA"));
        ImGui.Spacing();

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD7F90CB16AD7E691"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1025A88B93D62D11"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S1E571F87C342FCC8"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8285E2BAE3C8AC17"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA45D2ABFBC5ABE08"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SAE7963397BD9F5A6"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SD8E6682C6233B927"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE2F3E72E10ADC02F"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE51E291CD207C97E"));

        ImGui.Spacing();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S848704BB76761BEA"));

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4ADD5CD2A3D474DD")))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC9A0C848481FEA03"));
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S4D704D10F66EE6E0")))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_SPACE);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S404C72D08B8C03C5"));
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S130E6500758653A6")))
        {
            KeyInputHelper.PressKey(0x55);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SD1159842D8CDE94D"));
        }

        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S54E9703D540E0685")))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_NUMPAD0);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S984FBE10FD06E69F"));
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SF2938C0095E30CEC")))
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_CONTROL);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S7FBF123EB48E7253"));
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8605559F2AEF3617")))
        {
            KeyInputHelper.HoldKey(KeyInputHelper.VK_CONTROL);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S035F69CFCC07910D"));
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SE841F604FA222853")))
        {
            KeyInputHelper.ReleaseKey(KeyInputHelper.VK_CONTROL);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S69159AA5B4CF1C32"));
        }

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8EB2F115DEB15A1F"));
        ImGui.Spacing();
        } // end Key Inputs

    }

    private void DrawXaAbuseCallbacks()
    {
        ImGui.SetNextItemWidth(Scale(220f));
        global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S25BD3377DF88F39F") + "##xaAbuseCallbackAddon"), ref debugCallbackAddonName, 96);
        ImGui.SetNextItemWidth(-1f);
        global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S53B09E104F608DAA") + "##xaAbuseCallbackValues"), ref debugCallbackValues, 256);

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SED25037A6D12F8C1") + "##xaAbuseCallbackFire")))
            RunDebugRawAddonCallback();

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S1DDF3543F3A3778F") + "##xaAbuseCallbackAddonActive")))
        {
            var addonName = debugCallbackAddonName.Trim();
            if (string.IsNullOrWhiteSpace(addonName))
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5D75C164EE5260EF"));
            else
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6C3BEEEF7B583191", addonName, AddonHelper.IsAddonVisible(addonName), AddonHelper.IsAddonReady(addonName)));
        }

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S433D046FC5872913"));
    }

    private void DrawDalamudTestNotifications()
    {
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3CEBA4330F949B51"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SBA622AFD14BEB989", plugin.DalamudNotificationsSuck.LocalizedStatusText));

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

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("SB2667F9DD2143183", definition.ResultLabel));
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

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S692A06A3FB6FF598"));
        ImGui.TextColored(monitorColor, global::XASlave.Localization.UiLocale.FormatKey("S835DE5A73055A27A", monitorText));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SC1B6BA7598FF4E07", monitorStatus.MonitorRequested, monitorStatus.DialogueListenerSubscribed, monitorStatus.FrameworkSubscribed, monitorStatus.ClientLoggedIn));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S39DC584AAADDF8B2", monitorStatus.DialogueVisible, monitorStatus.DialogueReady, monitorStatus.DialogueSupported, monitorStatus.DialogueMatch));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S2B13D15CFCC57932", monitorStatus.DialogueWindowActive, monitorStatus.SecondsRemaining, monitorStatus.TitleMenuVisible));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SC2FC3C91CBB1E169", monitorStatus.DialogueText));
        ImGui.TextDisabled(monitorStatus.Detail);
        ImGui.TextColored(activeColor, global::XASlave.Localization.UiLocale.FormatKey("S1CD097C871BECF23", activeText));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SE8BFBE62F847C5DB", status.PluginLoaded, status.RuntimeInstanceResolved, status.ConfigWindowResolved, status.VisibilityMemberResolved));
        ImGui.TextDisabled(status.Detail);
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S02ED23336AF9B89A") + "##xaAbuseLobbyStatus")))
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S4AE383FD2840C133", monitorStatus.ShouldMonitor, monitorStatus.Detail, status.IsVisible, status.Detail));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S786D4F9A03A588E9") + "##xaAbuseLobbyClose")))
        {
            var closed = plugin.LobbyErrorAutoClose.TryCloseNoKillPluginPanelForDebug(out var message);
            var refreshedStatus = plugin.LobbyErrorAutoClose.GetNoKillPluginPanelDebugStatus();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S88088C375DC065AB", ((closed ? global::XASlave.Localization.UiMessage.Key("S26449EFC842701F3") : global::XASlave.Localization.UiMessage.Key("S2F2C85C7B32BBEC3"))), message, refreshedStatus.IsVisible, refreshedStatus.Detail));
        }
    }

    private global::XASlave.Localization.UiMessage GetXaAbuseStatusText()
    {
        var nameStatus = (!xaAbuseEnabled ? global::XASlave.Localization.UiMessage.Key("S504A36B40CF09538") : (xaAbuseAllVisiblePlayers ? global::XASlave.Localization.UiMessage.Key("S4D928BB32DD5650A") : global::XASlave.Localization.UiMessage.Key("S9C48716CEB098A78")));
        var overlayStatus = (!xaAbuseOverlayEnabled ? global::XASlave.Localization.UiMessage.Key("SDF89464545F32FC5") : (xaAbuseOverlayAllVisiblePlayers ? global::XASlave.Localization.UiMessage.Key("S46009C6BA04A10C5") : global::XASlave.Localization.UiMessage.Key("S42B2AC01D97B0D95")));

        if (xaAbuseOverlayEnabled)
            overlayStatus = global::XASlave.Localization.UiMessage.Concat(overlayStatus, (xaAbuseOverlayUseTexture ? global::XASlave.Localization.UiMessage.Key("S81ACC0544B04D2BD") : global::XASlave.Localization.UiMessage.Key("SB7C9E3FA6240426A")));

        return global::XASlave.Localization.UiMessage.Format("S80822F365EC9E087", nameStatus, overlayStatus);
    }

    private void EnableXaAbuse(bool allVisiblePlayers)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SE60E188DE265E6F0"));
            return;
        }

        xaAbuseEnabled = true;
        xaAbuseAllVisiblePlayers = allVisiblePlayers;
        Plugin.NamePlateGui.RequestRedraw();
        SetDebugResult((allVisiblePlayers ? global::XASlave.Localization.UiMessage.Key("S597B80BBDAC09726") : global::XASlave.Localization.UiMessage.Key("S4456FF4C0701E9C7")));
    }

    private void EnableXaAbuseOverlay(bool allVisiblePlayers)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SF77A50E3F553B388"));
            return;
        }

        xaAbuseOverlayEnabled = true;
        xaAbuseOverlayAllVisiblePlayers = allVisiblePlayers;
        xaAbuseOverlayEnabledAtUtc = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(xaAbuseOverlayText))
            xaAbuseOverlayText = XaAbuseDefaultOverlayText;
        if (string.IsNullOrWhiteSpace(xaAbuseOverlayTexturePath))
            xaAbuseOverlayTexturePath = XaAbuseDefaultTexturePath;

        SetDebugResult((allVisiblePlayers ? global::XASlave.Localization.UiMessage.Format("S2755117C30F25EEC", ((xaAbuseOverlayUseTexture ? global::XASlave.Localization.UiMessage.Key("S2FC390F75670DEED") : global::XASlave.Localization.UiMessage.Key("S982D9E3EB996F559")))) : global::XASlave.Localization.UiMessage.Format("SA5B09BE7BFDF0CD0", ((xaAbuseOverlayUseTexture ? global::XASlave.Localization.UiMessage.Key("S2FC390F75670DEED") : global::XASlave.Localization.UiMessage.Key("S982D9E3EB996F559"))))));
    }

    private void DisableXaAbuseOverlay()
    {
        var wasEnabled = xaAbuseOverlayEnabled;
        xaAbuseOverlayEnabled = false;
        xaAbuseOverlayAllVisiblePlayers = false;
        xaAbuseOverlayEnabledAtUtc = DateTime.MinValue;
        SetDebugResult((wasEnabled ? global::XASlave.Localization.UiMessage.Key("SDC7E13F576CEC0B3") : global::XASlave.Localization.UiMessage.Key("SCF6D8F5DA48D37AE")));
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
        SetDebugResult((wasEnabled ? global::XASlave.Localization.UiMessage.Key("S230DF61B53579084") : global::XASlave.Localization.UiMessage.Key("S3951C01D4A1BF72B")));
    }

    private void DrawXaAbuseXaDatabaseIpcTests()
    {
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA506A7B70435636F"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SE56BC8BCEECD0550"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8056CC0024EC010C"));
        ImGui.SetNextItemWidth(Scale(160f));
        if (global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("SBB4749F5B1A55AC5") + "##xaAbuseDbAetheryteId"), ref xaDbIpcTestAetheryteId))
            xaDbIpcTestAetheryteId = Math.Max(0, xaDbIpcTestAetheryteId);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SDAA9AD701C0332D8"));
        ImGui.Spacing();

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S40A11E07BC12CCEA") + "##xaAbuseDbIpcRun")))
        {
            RunXaDatabaseIpcClipboardTest();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S8C08FFC010DECFD2"));

        if (!string.IsNullOrEmpty(xaDbIpcTestReport))
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S45EA250CC123B0C2") + "##xaAbuseDbIpcCopy")))
            {
                ImGui.SetClipboardText(xaDbIpcTestReport);
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SAA188A4C158EC48F"));
            }

            ImGui.Spacing();
            using (ImRaii.Child("##xaAbuseDbIpcReport", new Vector2(0f, Scale(220f)), true))
            {
                ImGui.TextUnformatted(xaDbIpcTestReport);
            }
        }
    }

    private void RunXaDatabaseIpcClipboardTest()
    {
        const string SearchQuery = "potion";
        var aetheryteId = (uint)Math.Max(0, xaDbIpcTestAetheryteId);
        var ipc = plugin.IpcClient;
        var report = new StringBuilder();
        report.AppendLine("XA Database IPC Test Report");
        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (!ipc.IsXaDatabaseAvailable())
        {
            report.AppendLine("[FAIL] XA Database IPC unavailable - is the XA Database plugin installed and loaded?");
            xaDbIpcTestReport = report.ToString();
            ImGui.SetClipboardText(xaDbIpcTestReport);
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S27E09A9F802B89FD"));
            return;
        }

        var tested = 0;
        var returnedData = 0;

        void Append(string channel, string type, string value, bool hasData, bool includeFullValue = false)
        {
            tested++;
            if (hasData)
                returnedData++;

            if (value.Contains('\n'))
            {
                report.AppendLine($"{channel} ({type}):");
                var lines = value.Split('\n');
                const int MaxLines = 25;
                var lineLimit = includeFullValue ? lines.Length : MaxLines;
                for (var i = 0; i < lines.Length && i < lineLimit; i++)
                    report.AppendLine($"    {lines[i].TrimEnd('\r')}");
                if (lines.Length > lineLimit)
                    report.AppendLine($"    ... (+{lines.Length - lineLimit} more lines)");
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
        report.AppendLine("Storage inventories: Armoire (armoire) and Glamour Dresser (glamourDresser), current character, all recorded items.");
        report.AppendLine("Open each storage once to record its contents. recorded:false means unknown; an empty items list establishes absence only when success, requested and recorded are true.");
        report.AppendLine("The query reads last-observed data without opening storage. Full JSON is retained, including success:false or unrecorded sections; FAILED means no response was obtained.");
        const string StorageRequest = """{"version":1,"contentId":0,"sources":["armoire","glamourDresser"]}""";
        var storageOk = ipc.TrySearchCharacterStorageItemsJson(StorageRequest, out var storageJson, out var storageError);
        Append($"XA.Database.SearchCharacterStorageItemsJson({StorageRequest})", "string", storageOk ? storageJson : $"FAILED: {storageError}", storageOk, includeFullValue: true);

        report.AppendLine(new string('-', 72));
        var currentContentId = Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded
            ? Plugin.PlayerState.ContentId : 0UL;
        report.AppendLine($"Aetheryte test ID: {aetheryteId}; explicit character content ID: {currentContentId} (0 = current alias).");
        report.AppendLine("Access uses the latest recorded list, independently of the selected XA Database character. Inspect JSON known/capturedUtc/source for certainty and age.");
        report.AppendLine("A returned false is legitimate for an absent/unknown/hidden destination or owner, ID 0, or a provider-contained failure. FAILED means the subscriber could not obtain a result.");

        var characterAetherytesOk = ipc.TryGetCharacterAetherytesJson(0UL, out var characterAetherytesJson, out var characterAetherytesError);
        Append("XA.Database.GetCharacterAetherytesJson(0)", "string", characterAetherytesOk ? characterAetherytesJson : $"FAILED: {characterAetherytesError}", characterAetherytesOk, includeFullValue: true);

        var allAetherytesOk = ipc.TryGetAllCharacterAetherytesJson(out var allAetherytesJson, out var allAetherytesError);
        Append("XA.Database.GetAllCharacterAetherytesJson", "string", allAetherytesOk ? allAetherytesJson : $"FAILED: {allAetherytesError}", allAetherytesOk, includeFullValue: true);

        var hasAetheryteOk = ipc.TryHasAetheryte(aetheryteId, out var hasAetheryte, out var hasAetheryteError);
        Append($"XA.Database.HasAetheryte({aetheryteId})", "bool", hasAetheryteOk ? hasAetheryte.ToString() : $"FAILED: {hasAetheryteError}", hasAetheryteOk);

        var characterHasAetheryteOk = ipc.TryCharacterHasAetheryte(currentContentId, aetheryteId, out var characterHasAetheryte, out var characterHasAetheryteError);
        Append($"XA.Database.CharacterHasAetheryte({currentContentId}, {aetheryteId})", "bool", characterHasAetheryteOk ? characterHasAetheryte.ToString() : $"FAILED: {characterHasAetheryteError}", characterHasAetheryteOk);

        report.AppendLine(new string('-', 72));
        report.AppendLine($"Tested {tested} IPC channels, {returnedData} returned data. Empty values can be legitimate (no FC, no plot, no search hits).");
        report.AppendLine("This report records IPC responses; it does not prove every returned value is current or correct.");

        xaDbIpcTestReport = report.ToString();
        ImGui.SetClipboardText(xaDbIpcTestReport);
        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S70B9AD3318F91DEF", tested, returnedData));
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S41F9575515D88B03"));
            return;
        }

        if (!Plugin.PlayerState.IsLoaded)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S76AE989B4D8EAC5D"));
            return;
        }

        if (!plugin.IpcClient.IsAutoRetainerAvailable())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S17F62A7394E12671"));
            return;
        }

        debugAutoRetainerDepositGilKeepAmount = Math.Max(0, debugAutoRetainerDepositGilKeepAmount);
        var currentGil = GetDebugCurrentCharacterGil();
        if (currentGil <= debugAutoRetainerDepositGilKeepAmount)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SAC69AF07BB7C29CD", currentGil, debugAutoRetainerDepositGilKeepAmount));
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
            StopDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage.Format("S9D3590372873591E", global::XASlave.Localization.UiMessage.FromException(ex)), finishCurrentRetainer: false);
            return;
        }

        StartDebugAutoRetainerDepositGilMonitor(debugAutoRetainerDepositGilStartedUtc);
        StartDebugAutoRetainerDepositGilRetainerListOpenTask(debugAutoRetainerDepositGilStartedUtc);

        SetDebugResult((AddonHelper.IsAddonVisible("RetainerList") ? global::XASlave.Localization.UiMessage.Format("S3C7D41DC151486E4", debugAutoRetainerDepositGilKeepAmount) : global::XASlave.Localization.UiMessage.Format("S142D58B59E6B2EFD", debugAutoRetainerDepositGilKeepAmount)));
    }

    private void StartDebugAutoRetainerNonAutoInteract()
    {
        if (debugAutoRetainerNonAutoInteractActive)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S40E0ED77DAB1EC31"));
            return;
        }

        if (!Plugin.PlayerState.IsLoaded)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6C00041D82351980"));
            return;
        }

        if (!plugin.IpcClient.IsAutoRetainerAvailable())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S8B1E8D6E68D27672"));
            return;
        }

        debugAutoRetainerNonAutoInteractActive = true;
        debugAutoRetainerNonAutoInteractStartedUtc = DateTime.UtcNow;
        var runStartedUtc = debugAutoRetainerNonAutoInteractStartedUtc;

        RunWorker("AR non-auto interact", async token =>
        {
            var result = await OpenDebugRetainerListWithoutAutoRetainerEnableAsync(
                global::XASlave.Localization.UiMessage.Key("S9C63C989DC17587C"),
                () => debugAutoRetainerNonAutoInteractActive && debugAutoRetainerNonAutoInteractStartedUtc == runStartedUtc,
                token);

            if (debugAutoRetainerNonAutoInteractStartedUtc != runStartedUtc)
                return;

            debugAutoRetainerNonAutoInteractActive = false;
            SetDebugResult(result.Message);
        });

        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SA699F2D416EC5C2E"));
    }

    private void StopDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage message, bool finishCurrentRetainer)
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

    private void CompleteDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage message, bool abortArTasks, bool closeRetainerList)
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2F62AAD4B3462D83", message));
        }
        else
        {
            SetDebugResult(message);
        }
    }

    private void StartDebugRetainerListCancelRecovery(global::XASlave.Localization.UiMessage completionMessage)
    {
        RunWorker("AR RetainerList recovery", async token =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1000, token);

                const int maxAttempts = 8;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    var state = await Plugin.RunOnGameThread(GetDebugRetainerListCancelState);
                    if (state.CharacterSafeWaitReady)
                    {
                        await SetDebugResultOnFrameworkAsync(global::XASlave.Localization.UiMessage.Format("S1087821D4D2A3DEB", completionMessage));
                        return;
                    }

                    if (state.RetainerListVisible)
                    {
                        var cancelled = await Plugin.RunOnGameThread(CancelDebugRetainerList);
                        await System.Threading.Tasks.Task.Delay(1000, token);

                        state = await Plugin.RunOnGameThread(GetDebugRetainerListCancelState);
                        if (state.CharacterSafeWaitReady)
                        {
                            await SetDebugResultOnFrameworkAsync(global::XASlave.Localization.UiMessage.Format("SF68E94C4EBC3B24F", completionMessage));
                            return;
                        }

                        Plugin.Log.Information($"[XASlave] AR deposit gil: RetainerList cancel attempt {attempt}/{maxAttempts}, callback={cancelled}, visible={state.RetainerListVisible}, ready={state.RetainerListReady}, characterSafeWait={state.CharacterSafeWaitReady}.");
                        continue;
                    }

                    if (attempt >= 3)
                    {
                        await Plugin.RunOnGameThread(() =>
                        {
                            KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
                            return true;
                        });
                        Plugin.Log.Information($"[XASlave] AR deposit gil: CharacterSafeWait still false and RetainerList not visible; pressed ESC on recovery attempt {attempt}/{maxAttempts}.");
                    }

                    await System.Threading.Tasks.Task.Delay(1000, token);
                }

                var finalState = await Plugin.RunOnGameThread(GetDebugRetainerListCancelState);
                if (!finalState.CharacterSafeWaitReady && finalState.RetainerListVisible)
                {
                    await Plugin.RunOnGameThread(CancelDebugRetainerList);
                    await System.Threading.Tasks.Task.Delay(1000, token);
                    finalState = await Plugin.RunOnGameThread(GetDebugRetainerListCancelState);
                }

                await SetDebugResultOnFrameworkAsync((finalState.CharacterSafeWaitReady ? global::XASlave.Localization.UiMessage.Format("S2E0D8EAACDC94D3E", completionMessage) : global::XASlave.Localization.UiMessage.Format("S1081015260C548B0", completionMessage, finalState.RetainerListVisible, finalState.RetainerListReady, finalState.CharacterSafeWaitReady)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[XASlave] AR deposit gil RetainerList cancel recovery failed.");
                await SetDebugResultOnFrameworkAsync(global::XASlave.Localization.UiMessage.Format("SD6C151F1FE64F8B0", completionMessage, global::XASlave.Localization.UiMessage.FromException(ex)));
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

    private async System.Threading.Tasks.Task SetDebugResultOnFrameworkAsync(global::XASlave.Localization.UiMessage message)
    {
        await Plugin.RunOnGameThread(() =>
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
                global::XASlave.Localization.UiMessage.Format("S9E2FDEABE85AF766", currentGil, keepAmount),
                abortArTasks: true,
                closeRetainerList: true);
            return;
        }

        if (!plugin.IpcClient.AutoRetainerRequestRetainerListCustomTask(DebugAutoRetainerDepositGilPluginName))
        {
            StopDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage.Key("SB44B9C166ADED227"), finishCurrentRetainer: false);
            return;
        }

        debugAutoRetainerDepositGilListTaskRequested = true;
        debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;
        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S2972A58A9A77D3A8"));
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
                global::XASlave.Localization.UiMessage.Format("S959616CD4E33474A", retainerName, currentGil, keepAmount),
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

        RunWorker($"AR deposit gil for {retainerName}", async token =>
        {
            var stopAfterCurrentRetainer = false;
            var stopMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
            try
            {
                var result = await RunDebugAutoRetainerDepositGilForRetainerAsync(retainerName, keepAmount, token);
                debugAutoRetainerDepositGilProcessedRetainers++;
                debugAutoRetainerDepositGilRequestedTotal += result.RequestedDeposit;
                debugAutoRetainerDepositGilActualTotal += result.ActualDeposit;
                SetDebugResult(result.Message);
                stopAfterCurrentRetainer = result.ShouldStop;
                stopMessage = result.Message;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopAfterCurrentRetainer = true;
                stopMessage = global::XASlave.Localization.UiMessage.Format("S562A678887F9C23A", retainerName, global::XASlave.Localization.UiMessage.FromException(ex));
                SetDebugResult(stopMessage);
                Plugin.Log.Error(ex, $"[XASlave] AR deposit gil failed for retainer '{retainerName}'.");
            }
            finally
            {
                await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerFinishRetainerPostProcess());
                debugAutoRetainerDepositGilProcessingRetainer = false;
                debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;

                if (stopAfterCurrentRetainer)
                {
                    await System.Threading.Tasks.Task.Delay(100, token);
                    await Plugin.RunOnGameThread(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil(
                            global::XASlave.Localization.UiMessage.Format("S61EEE06994D3745B", stopMessage),
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

        RunWorker("AR deposit gil RetainerList open", async token =>
        {
            var result = await OpenDebugRetainerListWithoutAutoRetainerEnableAsync(
                global::XASlave.Localization.UiMessage.Key("S9AC8F87622EC0807"),
                () => debugAutoRetainerDepositGilActive && debugAutoRetainerDepositGilStartedUtc == runStartedUtc,
                token);

            if (!debugAutoRetainerDepositGilActive || debugAutoRetainerDepositGilStartedUtc != runStartedUtc)
                return;

            debugAutoRetainerDepositGilOpeningRetainerList = false;
            if (result.Opened)
            {
                debugAutoRetainerDepositGilLastActivityUtc = DateTime.UtcNow;
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S13D5AD8416A7C42B"));
                return;
            }

            StopDebugAutoRetainerDepositGil(result.Message, finishCurrentRetainer: false);
        });
    }

    private async System.Threading.Tasks.Task<DebugAutoRetainerBellOpenResult> OpenDebugRetainerListWithoutAutoRetainerEnableAsync(
        global::XASlave.Localization.UiMessage operationName,
        Func<bool> shouldContinue,
        System.Threading.CancellationToken token)
    {
        if (await Plugin.RunOnGameThread(() => IsDebugAddonReady("RetainerList")))
        {
            var alreadySuppressed = await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerGetSuppressed());
            if (alreadySuppressed)
            {
                var released = await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerSetSuppressed(false));
                if (!released)
                    return new(false, global::XASlave.Localization.UiMessage.Format("S203870FFE5C67603", operationName));

                await System.Threading.Tasks.Task.Delay(250, token);
                return new(true, global::XASlave.Localization.UiMessage.Format("S157DD0DA7F7F5F19", operationName));
            }

            return new(true, global::XASlave.Localization.UiMessage.Format("SF45544F61215C2DF", operationName));
        }

        var suppressedByTask = false;
        var wasSuppressedBeforeOpen = false;
        var releasedSuppression = false;
        try
        {
            var alreadySuppressed = await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerGetSuppressed());
            wasSuppressedBeforeOpen = alreadySuppressed;
            if (!alreadySuppressed)
            {
                var suppressed = await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerSetSuppressed(true));
                if (!suppressed)
                    return new(false, global::XASlave.Localization.UiMessage.Format("S97828F672744F07A", operationName));

                suppressedByTask = true;
                await System.Threading.Tasks.Task.Delay(250, token);
            }

            var started = Environment.TickCount64;
            var interacted = false;
            while (shouldContinue() && Environment.TickCount64 - started < 20000)
            {
                var opened = await Plugin.RunOnGameThread(() =>
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
                    await System.Threading.Tasks.Task.Delay(250, token);
                    if (suppressedByTask || wasSuppressedBeforeOpen)
                    {
                        var released = await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerSetSuppressed(false));
                        if (!released)
                            return new(false, global::XASlave.Localization.UiMessage.Format("S45133A300CA08987", operationName));

                        releasedSuppression = true;
                        await System.Threading.Tasks.Task.Delay(250, token);
                    }

                    var suffix = ((suppressedByTask || wasSuppressedBeforeOpen) ? global::XASlave.Localization.UiMessage.Key("S8AE413F520778DA6") : global::XASlave.Localization.UiMessage.Key("SF4CEF23E7F16EBC0"));
                    return new(true, global::XASlave.Localization.UiMessage.Format("S20DBFF42655F1425", operationName, suffix));
                }

                await System.Threading.Tasks.Task.Delay(interacted ? 750 : 500, token);
            }

            return new(false, global::XASlave.Localization.UiMessage.Format("S85F76F97B52902AB", operationName));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, global::XASlave.Localization.UiMessage.Format("S7E9E1DD38A440B9A", operationName, global::XASlave.Localization.UiMessage.FromException(ex)));
        }
        finally
        {
            if (suppressedByTask && !releasedSuppression)
            {
                try { await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerSetSuppressed(false)); } catch { }
            }
        }
    }

    private void StartDebugAutoRetainerDepositGilMonitor(DateTime runStartedUtc)
    {
        RunWorker("AR deposit gil monitor", async token =>
        {
            while (debugAutoRetainerDepositGilActive && debugAutoRetainerDepositGilStartedUtc == runStartedUtc)
            {
                await System.Threading.Tasks.Task.Delay(1000, token);

                if (!debugAutoRetainerDepositGilActive || debugAutoRetainerDepositGilStartedUtc != runStartedUtc)
                    return;

                var now = DateTime.UtcNow;
                var elapsed = now - runStartedUtc;
                var idleFor = now - debugAutoRetainerDepositGilLastActivityUtc;

                if (!debugAutoRetainerDepositGilListTaskRequested && elapsed > TimeSpan.FromMinutes(2))
                {
                    await Plugin.RunOnGameThread(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage.Key("S918EBE86DE04886A"), abortArTasks: true, closeRetainerList: true);
                        return true;
                    });
                    return;
                }

                if (elapsed > TimeSpan.FromMinutes(15))
                {
                    await Plugin.RunOnGameThread(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil(global::XASlave.Localization.UiMessage.Key("S3429170F13F2911E"), abortArTasks: true, closeRetainerList: true);
                        return true;
                    });
                    return;
                }

                if (!debugAutoRetainerDepositGilListTaskRequested || debugAutoRetainerDepositGilProcessingRetainer)
                    continue;

                if (debugAutoRetainerDepositGilProcessedRetainers == 0 && idleFor < TimeSpan.FromSeconds(10))
                    continue;

                var arBusy = await Plugin.RunOnGameThread(() => plugin.IpcClient.AutoRetainerPluginStateIsBusy());
                if (!arBusy && idleFor > TimeSpan.FromSeconds(3))
                {
                    var remainingGil = await Plugin.RunOnGameThread(GetDebugCurrentCharacterGil);
                    await Plugin.RunOnGameThread(() =>
                    {
                        CompleteDebugAutoRetainerDepositGil(
                            global::XASlave.Localization.UiMessage.Format("S1DA2D219B02690DF", debugAutoRetainerDepositGilProcessedRetainers, debugAutoRetainerDepositGilRequestedTotal, debugAutoRetainerDepositGilActualTotal, remainingGil),
                            abortArTasks: false,
                            closeRetainerList: true);
                        return true;
                    });
                    return;
                }
            }
        });
    }

    private async System.Threading.Tasks.Task<DebugAutoRetainerDepositGilResult> RunDebugAutoRetainerDepositGilForRetainerAsync(
        string retainerName,
        int keepAmount,
        System.Threading.CancellationToken token)
    {
        var beforeGil = await Plugin.RunOnGameThread(GetDebugCurrentCharacterGil);
        var requestedDeposit = Math.Max(0, beforeGil - keepAmount);
        if (requestedDeposit <= 0)
        {
            return new(0, 0, beforeGil, true, false, global::XASlave.Localization.UiMessage.Format("SAC69EE39CD295212", retainerName, beforeGil, keepAmount));
        }

        if (!await WaitForDebugFrameworkConditionAsync(() => IsDebugAddonReady("SelectString"), 5000, token))
            return new(0, 0, beforeGil, true, true, global::XASlave.Localization.UiMessage.Format("S4FD0D8A87314718D", retainerName));

        if (!await Plugin.RunOnGameThread(SelectDebugAutoRetainerGilMenu))
            return new(0, 0, beforeGil, true, true, global::XASlave.Localization.UiMessage.Format("SA7DFCACDF1AA1399", retainerName));

        if (!await WaitForDebugFrameworkConditionAsync(() => IsDebugAddonReady("Bank"), 5000, token))
            return new(0, 0, beforeGil, true, true, global::XASlave.Localization.UiMessage.Format("S8B7F0BF3B4233E29", retainerName));

        await System.Threading.Tasks.Task.Delay(500, token);

        if (!await Plugin.RunOnGameThread(() => FireDebugBankSwapDepositMode()))
            return new(0, 0, beforeGil, true, true, global::XASlave.Localization.UiMessage.Format("S672CFD1558728663", retainerName));

        await System.Threading.Tasks.Task.Delay(500, token);

        beforeGil = await Plugin.RunOnGameThread(GetDebugCurrentCharacterGil);
        requestedDeposit = Math.Max(0, beforeGil - keepAmount);
        if (requestedDeposit <= 0)
        {
            await Plugin.RunOnGameThread(() => FireDebugBankProcessOrCancel(forceCancel: true));
            return new(0, 0, beforeGil, true, false, global::XASlave.Localization.UiMessage.Format("S4D1432EB21941108", retainerName, beforeGil, keepAmount));
        }

        var depositAmount = (uint)Math.Min(requestedDeposit, uint.MaxValue);
        if (!await Plugin.RunOnGameThread(() => FireDebugBankSetDepositAmount(depositAmount)))
            return new(requestedDeposit, 0, beforeGil, true, true, global::XASlave.Localization.UiMessage.Format("SA1C03272C1156756", retainerName, requestedDeposit));

        await System.Threading.Tasks.Task.Delay(250, token);

        if (!await Plugin.RunOnGameThread(() => FireDebugBankProcessOrCancel(forceCancel: false)))
            return new(requestedDeposit, 0, beforeGil, true, true, global::XASlave.Localization.UiMessage.Format("S1B0441104B7327B5", retainerName));

        await System.Threading.Tasks.Task.Delay(1000, token);

        var afterGil = await Plugin.RunOnGameThread(GetDebugCurrentCharacterGil);
        var actualDeposit = Math.Max(0, beforeGil - afterGil);
        var shouldStop = afterGil <= keepAmount;
        var capDetail = (!shouldStop && actualDeposit < requestedDeposit ? global::XASlave.Localization.UiMessage.Format("S6B16B757B19DEC3C", retainerName, DebugAutoRetainerGilCap) : global::XASlave.Localization.UiMessage.Raw(string.Empty));
        return new(
            requestedDeposit,
            actualDeposit,
            afterGil,
            shouldStop,
            false,
            global::XASlave.Localization.UiMessage.Format("S22BDA5AE18BFBAAA", retainerName, requestedDeposit, actualDeposit, afterGil, capDetail));
    }

    private static async System.Threading.Tasks.Task<bool> WaitForDebugFrameworkConditionAsync(
        Func<bool> condition,
        int timeoutMs,
        System.Threading.CancellationToken token,
        int pollMs = 100)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (await Plugin.RunOnGameThread(condition))
                return true;

            await System.Threading.Tasks.Task.Delay(pollMs, token);
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
        if (!NativeArrayAccess.TryGetNode(&addon->UldManager, nodeIndex, out var node) || !node->IsVisible())
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

    private static string FormatNullableBool(bool? value)
        => value.HasValue ? value.Value ? "true" : "false" : "null";

    private void RunDebugRawAddonCallback()
    {
        var addonName = debugCallbackAddonName.Trim();
        if (string.IsNullOrWhiteSpace(addonName))
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S5D75C164EE5260EF"));
            return;
        }

        if (!TryParseDebugCallbackValuesMessage(debugCallbackValues, out var values, out var error))
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2ACEDDA1BF88F7F1", error));
            return;
        }

        var ok = FireDebugRawAddonCallback(addonName, values);
        SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S9FA3CBC60CDE832A", addonName, string.Join(", ", values.Select(value => value.Display))) : global::XASlave.Localization.UiMessage.Format("SB10762582C425361", addonName)));
    }

    private static unsafe bool FireDebugRawAddonCallback(string addonName, IReadOnlyList<DebugCallbackValue> values)
    {
        if (values.Count > MaxDebugCallbackValues)
        {
            Plugin.Log.Warning($"[XASlave] Callback test refused {values.Count} values; maximum is {MaxDebugCallbackValues}.");
            return false;
        }

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
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SDFBB857068089C11", addonName, optionNumber, callbackIndex));
            return;
        }

        if (!AddonHelper.IsAddonReady(addonName))
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8D0A7ADDF9E2D0F1", addonName, optionNumber, callbackIndex));
            return;
        }

        var ok = AddonHelper.FireCallback(addonName, callbackIndex);
        SetDebugResult((ok ? global::XASlave.Localization.UiMessage.Format("S1AB37BFCB24BAC1B", addonName, optionNumber, callbackIndex) : global::XASlave.Localization.UiMessage.Format("S4FBB6798FA30846B", addonName, callbackIndex)));
    }

    private void RunDebugTargetByName()
    {
        var targetName = debugTargetPlayerName.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SFD4D83B01605AFC1"));
            return;
        }

        AddonHelper.TargetByName(targetName);
        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S3307EDF217D3BC1F", targetName, targetName));
    }

    private void RunDebugFocusCurrentTarget()
    {
        var target = Plugin.TargetManager.Target;
        if (target == null)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SB9633D261EF80C74"));
            return;
        }

        Plugin.TargetManager.FocusTarget = target;
        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8829746FAE20AEA3", target.Name));
    }

    private void RunDebugClearFocusTarget()
    {
        var previous = Plugin.TargetManager.FocusTarget;
        Plugin.TargetManager.FocusTarget = null;
        SetDebugResult((previous == null ? global::XASlave.Localization.UiMessage.Key("S18E1196AC9E88CC1") : global::XASlave.Localization.UiMessage.Format("S7CB60D11FAB40D41", previous.Name)));
    }

    // Dropbox's BeginTradingQueue IPC silently ignores the call unless its TaskManager is idle AND the
    // focus target is a player; ItemQueueUI.BeginTrading() then trades the persisted item queue with them.
    private void RunDebugDropboxBeginTrading(bool focusCurrentTargetFirst)
    {
        if (!plugin.IpcClient.IsDropboxAvailable())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SEECEBC7B8B58B0B0"));
            return;
        }

        if (plugin.IpcClient.DropboxIsBusy())
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("SDE5077A4ABFFAE5C"));
            return;
        }

        if (focusCurrentTargetFirst && Plugin.TargetManager.FocusTarget is not IPlayerCharacter)
        {
            if (Plugin.TargetManager.Target is not IPlayerCharacter target)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4CB32BDF596D576B"));
                return;
            }

            Plugin.TargetManager.FocusTarget = target;
        }

        if (Plugin.TargetManager.FocusTarget is not IPlayerCharacter focus)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S6690D1E2AE744729"));
            return;
        }

        var invoked = plugin.IpcClient.DropboxBeginTrading();
        var busyAfter = plugin.IpcClient.DropboxIsBusy();
        SetDebugResult(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S019E51D5AEC4CBCA", invoked, focus.Name, busyAfter), ((busyAfter ? global::XASlave.Localization.UiMessage.Key("S35E97A03D69876DD") : global::XASlave.Localization.UiMessage.Key("S87818F7B1E1B4C55")))));
    }

    private global::XASlave.Localization.UiMessage GetSelectStringDebugStatus()
    {
        const string addonName = "SelectString";
        var visible = AddonHelper.IsAddonVisible(addonName);
        var ready = AddonHelper.IsAddonReady(addonName);
        var options = GetResolvedAddonListEntries(addonName);
        return (options.Count == 0 ? global::XASlave.Localization.UiMessage.Format("SFCF72892AAB1CFC3", addonName, visible, ready) : global::XASlave.Localization.UiMessage.Format("S1CBD078CBB60DCA1", addonName, visible, ready, options.Count, options[0].CallbackIndex + 1, options[0].CallbackIndex, options[0].Text));
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
            SetDebugResult(global::XASlave.Localization.UiMessage.Key("S9CC84D672300C73F"));
            return;
        }

        debugXaFcChestCheckRunning = true;
        SetDebugResult(global::XASlave.Localization.UiMessage.Key("S0AEFA4143D20D1C4"));

        RunWorker("XA FC Chest check", async token =>
        {
            try
            {
                var (zoneName, zoneMatch, xaDbReady, vnavReady) = await Plugin.RunOnGameThread(() =>
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
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S81D0F2BA4D8BAC56"));
                    return;
                }

                if (!zoneMatch)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S40D783D866579BE9", zoneName));
                    return;
                }

                if (!vnavReady)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("SE09515776AB32C57"));
                    return;
                }

                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S2C9A039C26602BE3", zoneName));
                await Plugin.RunOnGameThread(() => AddonHelper.TargetByName("Company Chest"));

                if (!await WaitForDebugTargetMatchAsync("Company Chest", 3000, token))
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S16F512188CF60176"));
                    return;
                }

                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S62BFDA4B5C11A5EE"));
                var pathStarted = await Plugin.RunOnGameThread(() => AddonHelper.TryPathToCurrentTarget(1.5f));
                if (!pathStarted)
                {
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S11516530FA2881E5"));
                    return;
                }

                if (!await WaitForDebugTargetInRangeAsync("Company Chest", 1.5f, 20000, token))
                {
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    SetDebugResult(global::XASlave.Localization.UiMessage.Key("S0E2C2110BED5D285"));
                    return;
                }

                if (!await TryOpenFcChestWithRecoveryAsync(token))
                {
                    return;
                }

                await System.Threading.Tasks.Task.Delay(500, token);
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC3CAED067DAC6AFE"));
                var saveOk = await Plugin.RunOnGameThread(() => plugin.SaveToXaDatabaseAndRecordSync());

                if (await CloseDebugAddonWithEscAsync("FreeCompanyChest", 12000, token))
                {
                    SetDebugResult((saveOk ? global::XASlave.Localization.UiMessage.Key("SBF9785445E09BC80") : global::XASlave.Localization.UiMessage.Key("SBA74E514EEC80B38")));
                }
                else
                {
                    SetDebugResult((saveOk ? global::XASlave.Localization.UiMessage.Key("SD5EFA41320E78B05") : global::XASlave.Localization.UiMessage.Key("S70E4788F09A3AB48")));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("S1D8EAF07D133D010", global::XASlave.Localization.UiMessage.FromException(ex)));
            }
            finally
            {
                debugXaFcChestCheckRunning = false;
            }
        }, exclusive: true);
    }

    private async System.Threading.Tasks.Task<bool> WaitForDebugTargetMatchAsync(
        string targetName,
        int timeoutMs,
        System.Threading.CancellationToken token,
        int pollMs = 100)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs, token);
            elapsed += pollMs;

            var matched = await Plugin.RunOnGameThread(() => AddonHelper.CurrentTargetMatches(targetName));
            if (matched)
                return true;
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> WaitForDebugTargetInRangeAsync(
        string targetName,
        float stopDistance,
        int timeoutMs,
        System.Threading.CancellationToken token,
        int pollMs = 200)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs, token);
            elapsed += pollMs;

            var inRange = await Plugin.RunOnGameThread(() =>
                AddonHelper.IsCurrentTargetWithinStopDistanceAndStopped(targetName, stopDistance));
            if (inRange)
                return true;
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> TryOpenFcChestWithRecoveryAsync(System.Threading.CancellationToken token)
    {
        const string targetName = "Company Chest";
        const string addonName = "FreeCompanyChest";
        const int maxRecoveryAttempts = 2;

        for (var recoveryAttempt = 0; recoveryAttempt <= maxRecoveryAttempts; recoveryAttempt++)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE0537DC85B83A219", targetName));
            var interactOk = await Plugin.RunOnGameThread(() =>
            {
                AddonHelper.DismissTextError();
                return AddonHelper.InteractWithTarget();
            });
            if (!interactOk)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S80F705BE002ACBBC"));
                return false;
            }

            var elapsed = 0;
            const int pollMs = 100;
            const int interactTimeoutMs = 5000;

            while (elapsed < interactTimeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(pollMs, token);
                elapsed += pollMs;

                var (addonVisible, matchedText) = await Plugin.RunOnGameThread(() =>
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
                        await Plugin.RunOnGameThread(() => AddonHelper.DismissTextError());
                        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S710827D27DABA1E1", matchedText, maxRecoveryAttempts));
                        return false;
                    }

                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S0CC4CF773621BF35", matchedText, recoveryAttempt + 1, maxRecoveryAttempts));
                    await Plugin.RunOnGameThread(() =>
                    {
                        AddonHelper.DismissTextError();
                        AddonHelper.TryPathToCurrentTarget(1.5f);
                    });

                    await System.Threading.Tasks.Task.Delay(500, token);
                    await Plugin.RunOnGameThread(() => plugin.IpcClient.VnavStop());
                    await Plugin.RunOnGameThread(() =>
                    {
                        AddonHelper.ResetCamera();
                        AddonHelper.DismissTextError();
                        AddonHelper.TargetByName(targetName);
                    });
                    await System.Threading.Tasks.Task.Delay(250, token);
                    break;
                }
            }

            if (elapsed >= interactTimeoutMs)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S4656BCE1D5BC2D24"));
                return false;
            }
        }

        SetDebugResult(global::XASlave.Localization.UiMessage.Key("SC889148C021BA35F"));
        return false;
    }

    private async System.Threading.Tasks.Task<bool> CloseDebugAddonWithEscAsync(
        string addonName,
        int timeoutMs,
        System.Threading.CancellationToken token,
        int pollMs = 100,
        int escIntervalMs = 1000)
    {
        int elapsed = 0;
        int closedChecks = 0;
        int lastEscAtMs = -escIntervalMs;

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S354B44CC46453FF6", addonName));

        while (elapsed < timeoutMs)
        {
            var visible = await Plugin.RunOnGameThread(() => AddonHelper.IsAddonVisible(addonName));
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
                    await Plugin.RunOnGameThread(() => KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE));
                }
            }

            await System.Threading.Tasks.Task.Delay(pollMs, token);
            elapsed += pollMs;
        }

        return false;
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
    private async System.Threading.Tasks.Task<bool> WaitForMovementComplete(
        System.Threading.CancellationToken token,
        int timeoutMs = 60000,
        int pollMs = 200)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            await System.Threading.Tasks.Task.Delay(pollMs, token);
            elapsed += pollMs;

            var idle = await Plugin.RunOnGameThread(() => IsMovementIdle());
            if (idle) return true;
        }
        return false;
    }

    private unsafe global::XASlave.Localization.UiMessage GetTextErrorMonitorResult()
    {
        const string addonName = "_TextError";
        var trackedSummary = string.Join(" | ", xagmanTradeFailureTexts.Select(entry => entry.Text));

        var addon = AddonHelper.GetAddon(addonName);
        if (addon == null)
        {
            return global::XASlave.Localization.UiMessage.Format("S944E2D8CADAB9428", addonName, trackedSummary);
        }

        var textEntries = AddonHelper.GetAddonTextEntries(addonName)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matches = GetXagmanTradeFailureMatches(textEntries);
        var matchSummary = matches.Count == 0
            ? global::XASlave.Localization.UiMessage.Key("S6E9C960FD025B2E5")
            : global::XASlave.Localization.UiMessage.Raw(string.Join(" | ", matches.Select(entry => $"{entry.Kind}: {entry.Text}")));

        if (matchSummary.Original.Length > 220)
            matchSummary = global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Truncate(matchSummary, 217), global::XASlave.Localization.UiMessage.Raw("..."));

        return global::XASlave.Localization.UiMessage.Format("SEAF1417F974F7A75", addonName, addon->IsVisible, addon->IsReady, addon->UldManager.NodeListCount, textEntries.Count, trackedSummary, matches.Count, matchSummary);
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
            await Plugin.RunOnGameThread(() =>
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
