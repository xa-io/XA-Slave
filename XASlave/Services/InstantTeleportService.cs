using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XA.FeatureConflicts;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace XASlave.Services;

public unsafe sealed class InstantTeleportService : IDisposable, IInstantTeleportRuntime
{
    private readonly Configuration configuration;
    private readonly IGameInteropProvider interop;
    private readonly ISigScanner scanner;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IPartyList party;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly FeatureConflictEndpoint conflict;
    private readonly FeatureConflictPeerReader peer;
    private readonly FeatureConflictPeerReader otherTeleportPeer;
    private bool requestedEnabled;
    private bool preferencePending;
    private bool initialized;
    private bool transitionBusy;
    private bool cleanupFailed;
    private readonly InstantTeleportRecoveryState recovery = new();
    private Hook<ExecuteDelegate>? executeHook;
    private Hook<SendDelegate>? sendHook;
    private Hook<ActionDelegate>? actionHook;
    private InstantTeleportWorkflow? workflow;
    private TeleportFrame frame;
    private TeleportSample? sample;
    private int opcode;
    private nint movementStateAddress;
    private int playersInsideSafeDistance;
    private int zoneOffset;
    private int connectionOffset;
    private bool enabled;
    private bool disposed;
    private bool cancelRequested;
    private int commandsSeen;
    private int positionPacketsSeen;
    private int castActionsSuppressed;
    private bool? lastTriggerResult;
    private string lastLoggedState = string.Empty;
    private string status = "Disabled";

    public InstantTeleportService(Configuration configuration, IGameInteropProvider interop, ISigScanner scanner,
        IFramework framework, IClientState client, ICondition condition, IObjectTable objects,
        IPartyList party, IChatGui chat, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        this.configuration = configuration;
        this.interop = interop;
        this.scanner = scanner;
        this.framework = framework;
        this.client = client;
        this.condition = condition;
        this.objects = objects;
        this.party = party;
        this.chat = chat;
        this.log = log;
        conflict = new FeatureConflictEndpoint(pluginInterface, framework, Guid.NewGuid(), "XASlave.InstantTeleport.GetConflictState.v1");
        try
        {
            peer = FeatureConflictPeerReader.FromCapability(pluginInterface, framework, "XA.MovementConflicts.Owner.v1",
                "XA.MovementConflicts.Invulnerability.v1", "XA.MovementConflicts.ObjectCarrying.v1", "XA.MovementConflicts.CameraAlignment.v1");
        }
        catch { conflict.Retire(new(false, false, false)); throw; }
        try { otherTeleportPeer = new FeatureConflictPeerReader(pluginInterface, framework, "XATestbench", "XATestbench.InstantTeleport.GetConflictState.v1"); }
        catch { peer.Dispose(); conflict.Retire(new(false, false, false)); throw; }
        framework.Update += OnUpdate;
        chat.LogMessage += OnLogMessage;
    }

    public string StatusText { get { lock (gate) return RecoveryRequired ? "Recovery required: waiting for an accepted ordinary position update or a new area connection." : enabled ? !PeersPermitted ? "Paused: " + (!peer.Permitted ? peer.Reason : otherTeleportPeer.Reason) : workflow?.Status ?? status : status; } }
    public string LastActionText { get { lock (gate) return $"Requests: {workflow?.Requests ?? 0}; dispatched: {workflow?.Dispatched ?? 0}; area transports: {workflow?.TransportsObserved ?? 0}; failures: {workflow?.Failures ?? 0}; native cancels blocked: {workflow?.NativeCancelsSuppressed ?? 0}."; } }
    public string ReadinessText { get { lock (gate) return !enabled ? "Disabled" : workflow?.Active == true ? "Teleport pending" : workflow?.GetStartBlockReason(frame, sample, Environment.TickCount64) ?? "Ready to bypass the next teleport."; } }
    public string DiagnosticsText { get { lock (gate) return $"Position opcode: {opcode}; teleport commands seen: {commandsSeen}; position packets seen: {positionPacketsSeen}; prepared positions: {workflow?.PreparedPositions ?? 0}; current movement state: {(movementStateAddress != 0 ? "resolved" : "unavailable")}; cast actions blocked: {castActionsSuppressed}; last Teleport action result: {lastTriggerResult?.ToString() ?? "none"}."; } }
    public string ProximityText { get { lock (gate) return $"Players inside {TeleportProximity.ClampDistance(configuration.InstantTeleportSafeDistanceYalms):0.#} yalms: {playersInsideSafeDistance}."; } }
    public bool IsTeleporting { get { lock (gate) return workflow?.Active == true; } }
    public int TeleportsProcessed { get { lock (gate) return workflow?.Dispatched ?? 0; } }
    public bool IsEnabled { get { lock (gate) return enabled; } }

    public void RequestCancel() { lock (gate) cancelRequested = true; }

    public bool SetEnabled(bool value)
    {
        lock (gate)
        {
            if (disposed) return false;
            requestedEnabled = value;
            preferencePending = true;
            if (initialized && framework.IsInFrameworkUpdateThread) ApplyPreference();
            else status = "Waiting for the framework thread.";
            return preferencePending ? requestedEnabled : enabled;
        }
    }

    public void InitializePreference(bool value)
    {
        lock (gate) { initialized = true; requestedEnabled = value; preferencePending = true; }
    }

    private bool PeersPermitted => peer.Permitted && otherTeleportPeer.Permitted;
    private FeatureConflictDecision ReadPeers()
    {
        var primary = peer.Read();
        var other = otherTeleportPeer.Read();
        return primary.Permitted ? other : primary;
    }

    private bool RecoveryRequired => cleanupFailed || (recovery.Pending && workflow?.Active != true);
    private FeatureConflictActivity ConflictActivity() => new(enabled, workflow?.Active == true || transitionBusy, RecoveryRequired);

    private void ApplyPreference()
    {
        if (!preferencePending || transitionBusy) return;
        preferencePending = false;
        conflict.Publication.MarkReady();
        transitionBusy = true;
        try
        {
            if (requestedEnabled)
            {
                var applied = conflict.Publication.TryEnable(ReadPeers, () => SetEnabledCore(true), ConflictActivity);
                if (!applied) status = "Unavailable: " + conflict.Publication.LastReason;
            }
            else
            {
                if (!conflict.Publication.TryDisable(() => SetEnabledCore(false), ConflictActivity))
                    cleanupFailed = true;
            }
        }
        finally { transitionBusy = false; conflict.Publication.Refresh(ConflictActivity()); }
    }

    private bool SetEnabledCore(bool value)
    {
        lock (gate)
        {
            if (disposed || value == enabled) return enabled;
            if (!value)
            {
                enabled = false;
                try { workflow?.Abort("Disabled.", CurrentFrame(), Environment.TickCount64, false); }
                finally
                {
                    workflow?.Abort("Disabled.", default, Environment.TickCount64, false);
                    sample = null;
                    executeHook?.Disable();
                    actionHook?.Disable();
                    sendHook?.Disable();
                    status = "Disabled";
                }
                return false;
            }
            if (RecoveryRequired) { status = "Unavailable: teleport recovery is outstanding."; return false; }
            try
            {
                EnsureHooks();
                sample = null;
                frame = default;
                cancelRequested = false;
                sendHook!.Enable();
                actionHook!.Enable();
                executeHook!.Enable();
                enabled = true;
                return true;
            }
            catch (Exception ex)
            {
                CleanupHooks();
                status = "Unavailable: native teleport contracts could not be resolved.";
                log.Warning(ex, "[XASlave] Instant Teleport initialization failed.");
                return false;
            }
        }
    }

    private void EnsureHooks()
    {
        if (executeHook != null && sendHook != null && actionHook != null) return;
        var execute = (nint)GameMain.MemberFunctionPointers.ExecuteCommand;
        var send = (nint)ZoneClient.MemberFunctionPointers.SendPacket;
        var action = (nint)ActionManager.MemberFunctionPointers.UseActionLocation;
        if (execute == 0 || send == 0 || action == 0) throw new InvalidOperationException("Missing generated native function.");
        // This is an opcode immediate, NEVER a hook target. Verify the surrounding instruction shape.
        var instruction = scanner.ScanText(Sigs.InstantTeleportOpcodeSig);
        opcode = Marshal.ReadInt32(instruction, 4);
        if (opcode is <= 0 or > ushort.MaxValue || Marshal.ReadByte(instruction) != 0xC7 ||
            Marshal.ReadByte(instruction, 1) != 0x44 || Marshal.ReadByte(instruction, 2) != 0x24 ||
            Marshal.ReadByte(instruction, 8) != 0xF7 || Marshal.ReadByte(instruction, 9) != 0xD9)
            throw new InvalidOperationException("Unrecognized position opcode instruction.");
        zoneOffset = checked((int)Marshal.OffsetOf<NetworkModule>("ZoneClient"));
        // Decode the transport pointer offset from unmodified SendPacket text; other plugins may hook it.
        var originalSend = scanner.SearchBase + (send - scanner.Module.BaseAddress);
        if (Marshal.ReadByte(originalSend) != 0x48 || Marshal.ReadByte(originalSend, 1) != 0x83 ||
            Marshal.ReadByte(originalSend, 2) != 0xEC || Marshal.ReadByte(originalSend, 4) != 0x48 ||
            Marshal.ReadByte(originalSend, 5) != 0x8B || Marshal.ReadByte(originalSend, 6) != 0x89)
            throw new InvalidOperationException("Unrecognized zone send prologue.");
        connectionOffset = Marshal.ReadInt32(originalSend, 7);
        if (zoneOffset < 0 || zoneOffset > sizeof(NetworkModule) - sizeof(nint) ||
            connectionOffset < 0 || connectionOffset > sizeof(ZoneClient) - sizeof(nint))
            throw new InvalidOperationException("Invalid network member offset.");
        ResolveMovementState();
        workflow ??= new InstantTeleportWorkflow(this, opcode);
        executeHook = interop.HookFromAddress<ExecuteDelegate>(execute, ExecuteDetour);
        sendHook = interop.HookFromAddress<SendDelegate>(send, SendDetour);
        actionHook = interop.HookFromAddress<ActionDelegate>(action, ActionDetour);
    }

    private void ResolveMovementState()
    {
        movementStateAddress = 0;
        try
        {
            // The audited getter returns this byte plus flags 0x10/0x20/0x100; modulo four is just its low two bits.
            // Read the byte directly, without calling native methods or retaining a character pointer.
            var getter = scanner.ScanText(Sigs.InstantTeleportMovementStateGetterSig);
            var original = scanner.SearchBase + (getter - scanner.Module.BaseAddress);
            if (Marshal.ReadByte(original, 10) != 0x0F || Marshal.ReadByte(original, 11) != 0xB6 || Marshal.ReadByte(original, 12) != 0x05)
                throw new InvalidOperationException("Unrecognized movement state load.");
            var address = getter + 17 + Marshal.ReadInt32(original, 13);
            var offset = address - scanner.Module.BaseAddress;
            if (offset < 0 || offset >= scanner.Module.ModuleMemorySize)
                throw new InvalidOperationException("Movement state address is outside the game module.");
            movementStateAddress = address;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Instant Teleport current movement state unavailable; captured movement fallback remains enabled.");
        }
    }

    private TeleportOwner ReadOwner()
    {
        var native = NativeFramework.Instance();
        var playerState = PlayerState.Instance();
        if (!client.IsLoggedIn || playerState == null || !playerState->IsLoaded || native == null ||
            native->NetworkModuleProxy == null || native->NetworkModuleProxy->NetworkModule == null) return default;
        var network = (nint)native->NetworkModuleProxy->NetworkModule;
        var zone = Marshal.ReadIntPtr(network, zoneOffset);
        var connection = zone == 0 ? 0 : Marshal.ReadIntPtr(zone, connectionOffset);
        return new TeleportOwner(playerState->ContentId, client.TerritoryType, network, zone, connection);
    }

    private TeleportFrame CurrentFrame()
    {
        // Object enumeration is confined to OnUpdate; callbacks validate current native ownership.
        var current = ReadOwner();
        if (current != frame.Owner || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.Unconscious]) return frame with { Owner = current, Usable = false, CanStart = false, CanRestore = false };
        return InstantTeleportPeerPolicy.Apply(frame, PeersPermitted && !RecoveryRequired && conflict.Publication.CanActivate);
    }

    private void OnUpdate(IFramework _)
    {
        lock (gate)
        {
            if (disposed || !initialized || !framework.IsInFrameworkUpdateThread) return;
            var now = Environment.TickCount64;
            try
            {
                ApplyPreference();
                if (!enabled)
                {
                    if (recovery.Pending) recovery.ObserveOwner(ReadOwner());
                    return;
                }
                ReadPeers();
                var player = objects.LocalPlayer;
                var owner = ReadOwner();
                recovery.ObserveOwner(owner);
                if (owner != frame.Owner) sample = null;
                var canRestore = owner.Valid && player is { IsDead: false, CurrentHp: > 0 } &&
                    !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] &&
                    !condition[ConditionFlag.Unconscious];
                var usable = canRestore && !client.IsPvP && !condition[ConditionFlag.BoundByDuty] &&
                    !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.WatchingCutscene] &&
                    !condition[ConditionFlag.WatchingCutscene78] && !condition[ConditionFlag.OccupiedInCutSceneEvent] &&
                    !condition[ConditionFlag.InFlight] && !condition[ConditionFlag.Jumping] &&
                    !condition[ConditionFlag.Swimming] && !condition[ConditionFlag.Diving] &&
                    !condition[ConditionFlag.Mounted] && PeersPermitted && !RecoveryRequired && conflict.Publication.CanActivate;
                var anyPlayers = false;
                playersInsideSafeDistance = 0;
                if (usable)
                {
                    foreach (var other in objects.OfType<IPlayerCharacter>())
                    {
                        if (other.GameObjectId == player!.GameObjectId) continue;
                        anyPlayers = true;
                        if (TeleportProximity.WithinDistance(player.Position, other.Position, configuration.InstantTeleportSafeDistanceYalms))
                            playersInsideSafeDistance++;
                    }
                }
                var nearby = playersInsideSafeDistance != 0;
                var canStart = usable && !condition[ConditionFlag.Casting] && !condition[ConditionFlag.Occupied] &&
                    !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent];
                frame = new TeleportFrame(owner, now, player?.EntityId ?? 0, player?.Position ?? default,
                    player?.Rotation ?? 0, usable, canStart, party.Length <= 1 && !nearby, anyPlayers, canRestore)
                {
                    MovementVariant = usable && movementStateAddress != 0 ? (byte)(Marshal.ReadByte(movementStateAddress) & 3) : null,
                };
                workflow!.SoloOnly = configuration.InstantTeleportSoloOnly;
                if (cancelRequested)
                {
                    workflow.Abort("Cancelled by user.", frame, now, false);
                    cancelRequested = false;
                    sample = null;
                }
                workflow.Tick(frame, now);
                if (!usable) sample = null;
                LogStateChange();
            }
            catch (Exception ex)
            {
                workflow?.Abort("Stopped: unable to read game state.", default, now, true);
                sample = null;
                frame = default;
                log.Warning(ex, "[XASlave] Instant Teleport update failed.");
            }
            finally { conflict.Publication.Refresh(ConflictActivity()); }
        }
    }

    private bool ExecuteDetour(int command, int p1, int p2, int p3, int p4)
    {
        lock (gate)
        {
            if (enabled)
            {
                try
                {
                    if (command == 201) { workflow!.Transport(); sample = null; frame = default; }
                    else if (command == 204)
                    {
                        if (cancelRequested)
                        {
                            workflow!.Abort("Cancelled by user.", CurrentFrame(), Environment.TickCount64, false, cancelNative: false);
                            cancelRequested = false;
                        }
                        else if (workflow!.PreventNativeCancel(CurrentFrame(), Environment.TickCount64)) return false;
                        sample = null;
                    }
                    else if (InstantTeleportWorkflow.IsTeleport(command))
                    {
                        commandsSeen++;
                        if (cancelRequested)
                        {
                            workflow!.Abort("Cancelled by user.", CurrentFrame(), Environment.TickCount64, false);
                            sample = null;
                            cancelRequested = false;
                            return executeHook!.Original(command, p1, p2, p3, p4);
                        }
                        if (!framework.IsInFrameworkUpdateThread) return executeHook!.Original(command, p1, p2, p3, p4);
                        workflow!.SoloOnly = configuration.InstantTeleportSoloOnly;
                        var accepted = workflow.TryStart(new TeleportCommand(command, p1, p2, p3, p4), CurrentFrame(), sample, Environment.TickCount64);
                        LogStateChange();
                        // Match the intercepted command contract: the original command was deferred, not executed.
                        if (accepted) { sample = null; return false; }
                    }
                }
                catch (Exception ex)
                {
                    workflow?.Abort("Stopped: teleport hook failed.", default, Environment.TickCount64, true);
                    sample = null;
                    log.Warning(ex, "[XASlave] Instant Teleport command callback failed.");
                }
            }
            return executeHook!.Original(command, p1, p2, p3, p4);
        }
    }

    private bool SendDetour(ZoneClient* zone, nint packet, uint a3, uint a4, bool prioritize)
    {
        lock (gate)
        {
            TeleportSample? candidate = null;
            var ordinaryOwner = default(TeleportOwner);
            try
            {
                if (enabled && packet != 0 && Marshal.ReadInt32(packet) == opcode)
                {
                    positionPacketsSeen++;
                    var current = CurrentFrame();
                    var now = Environment.TickCount64;
                    var validLength = (ulong)Marshal.ReadInt64(packet, 8) == TeleportPositionPacket.Length;
                    if (validLength && current.Owner.Zone == (nint)zone && workflow!.CanSuppress(current, now)) return false;
                    // Let this genuine packet restore normal movement. Never resume suppression after passing one through.
                    if (workflow!.Active)
                        workflow.Abort("Stopped: position state or packet layout changed.", validLength ? current : default, now, true);
                    if (validLength && current.CanRestore && current.Owner.Valid && current.Owner.Zone == (nint)zone &&
                        now >= current.At && now - current.At <= InstantTeleportWorkflow.FrameMaxAgeMs)
                    {
                        var bytes = new byte[TeleportPositionPacket.Size];
                        Marshal.Copy(packet, bytes, 0, bytes.Length);
                        if (TeleportPositionPacket.Valid(bytes, opcode) && TeleportPositionPacket.Grounded(bytes) &&
                            Vector3.DistanceSquared(TeleportPositionPacket.Position(bytes), current.Position) <= 0.0025f)
                            ordinaryOwner = current.Owner;
                    }
                    if (validLength && current.Owner.Valid && current.Usable && current.Owner.Zone == (nint)zone &&
                        now >= current.At && now - current.At <= InstantTeleportWorkflow.FrameMaxAgeMs)
                    {
                        var bytes = new byte[TeleportPositionPacket.Size];
                        Marshal.Copy(packet, bytes, 0, bytes.Length);
                        if (TeleportPositionPacket.Valid(bytes, opcode)) candidate = new TeleportSample(current.Owner, now, bytes, a3, a4);
                    }
                }
            }
            catch (Exception ex)
            {
                workflow?.Abort("Stopped: position callback failed.", default, Environment.TickCount64, true);
                sample = null;
                log.Warning(ex, "[XASlave] Instant Teleport position callback failed.");
            }
            var sent = sendHook!.Original(zone, packet, a3, a4, prioritize);
            if (ordinaryOwner.Valid) recovery.ObserveOrdinaryPosition(ordinaryOwner, sent);
            if (sent && candidate != null && workflow?.Active == false) sample = candidate;
            return sent;
        }
    }

    private bool ActionDetour(ActionManager* manager, ActionType type, uint id, ulong target, Vector3* location, uint extra, byte a7)
    {
        lock (gate)
        {
            try
            {
                if (enabled && workflow!.SuppressAction((uint)type, id, CurrentFrame(), Environment.TickCount64))
                {
                    castActionsSuppressed++;
                    return false;
                }
            }
            catch { workflow?.Abort("Stopped: action callback failed.", default, Environment.TickCount64, true); }
            return actionHook!.Original(manager, type, id, target, location, extra, a7);
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        lock (gate)
        {
            if (enabled) workflow?.NotifyFailure(message.LogMessageId);
        }
    }

    bool IInstantTeleportRuntime.SendPosition(TeleportOwner owner, byte[] bytes, uint a3, uint a4)
        => recovery.Send(owner, workflow?.Active == true, () => SendPositionCore(owner, bytes, a3, a4));

    private bool SendPositionCore(TeleportOwner owner, byte[] bytes, uint a3, uint a4)
    {
        if (owner != ReadOwner() || !owner.Valid || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) return false;
        fixed (byte* packet = bytes) return sendHook!.Original((ZoneClient*)owner.Zone, (nint)packet, a3, a4, true);
    }

    bool IInstantTeleportRuntime.Execute(TeleportCommand command) => executeHook!.Original(command.Id, command.P1, command.P2, command.P3, command.P4);

    void IInstantTeleportRuntime.TriggerTeleport(uint entityId)
    {
        var manager = ActionManager.Instance();
        if (manager == null) throw new InvalidOperationException("Action manager unavailable.");
        var location = Vector3.Zero;
        lastTriggerResult = actionHook!.Original(manager, ActionType.GeneralAction, 7, entityId, &location, 0, 0);
    }

    private void LogStateChange()
    {
        var current = workflow?.Status ?? status;
        if (current == lastLoggedState) return;
        lastLoggedState = current;
        log.Information($"[XASlave] Instant Teleport: {current} {LastActionText}");
    }

    private void CleanupHooks()
    {
        HookCleanup.DisposeAndClear(ref executeHook);
        HookCleanup.DisposeAndClear(ref actionHook);
        HookCleanup.DisposeAndClear(ref sendHook);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { conflict.Publication.BeginShutdown(); }
            catch { cleanupFailed = true; }
            transitionBusy = true;
            try { SetEnabledCore(false); }
            catch { cleanupFailed = true; throw; }
            finally
            {
                disposed = true;
                framework.Update -= OnUpdate;
                chat.LogMessage -= OnLogMessage;
                try { CleanupHooks(); }
                catch { cleanupFailed = true; }
                finally
                {
                    transitionBusy = false;
                    peer.Dispose();
                    otherTeleportPeer.Dispose();
                    try { conflict.Retire(ConflictActivity()); }
                    catch (Exception ex) { log.Warning(ex, "Instant Teleport conflict advertisement could not retire."); }
                }
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool ExecuteDelegate(int command, int p1, int p2, int p3, int p4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SendDelegate(ZoneClient* zone, nint packet, uint a3, uint a4, [MarshalAs(UnmanagedType.U1)] bool prioritize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool ActionDelegate(ActionManager* manager, ActionType type, uint id, ulong target, Vector3* location, uint extra, byte a7);
}
