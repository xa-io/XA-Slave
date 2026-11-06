using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using XASlave.Localization;

namespace XASlave.Services;

public enum BusyStatusReadiness { Ready, Pending, Blocked }

/// <summary>Shared Busy demand/suppression leases with character-bound ownership and observed command receipts.</summary>
public unsafe sealed class AutoBusyStatusService : IDisposable
{
    private const ulong BusyMask = 1UL << 12;
    private const long PollMilliseconds = 250;
    private const long LeaseWaitMilliseconds = 15_000;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly Func<bool> ownerDisposed;
    private readonly object sync = new();
    private readonly BusyStatusCoordinator coordinator = new();
    private readonly Dictionary<long, BusyStatusLease> leases = new();
    private bool enabled, disposed;
    private long generation, nextLeaseId, nextPoll;
    private UiMessage status = UiMessage.Authored("Auto Busy is disabled.");

    public AutoBusyStatusService(IFramework framework, IClientState clientState, IPlayerState playerState,
        IObjectTable objectTable, ICondition condition, IPluginLog log, Func<bool> ownerDisposed)
    {
        this.framework = framework; this.clientState = clientState; this.playerState = playerState;
        this.objectTable = objectTable; this.condition = condition; this.log = log; this.ownerDisposed = ownerDisposed;
        framework.Update += Update;
        clientState.Logout += OnLogout;
    }

    public bool Enabled { get { lock (sync) return enabled; } }
    public string StatusText { get { lock (sync) return status.Original; } }
    public string LocalizedStatusText { get { lock (sync) return status.Display; } }
    internal UiMessage StatusTextMessage { get { lock (sync) return status; } }

    public bool SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return false;
            if (enabled == value) return enabled;
            enabled = value; generation++;
            coordinator.ResetIntent();
            nextPoll = 0;
            status = value ? UiMessage.Authored("Waiting for a ready character to update Busy.") : UiMessage.Authored("Auto Busy is disabled.");
            return enabled;
        }
    }

    public BusyStatusLease AcquireBusyLease(string owner)
        => Acquire(owner, false);

    public BusyStatusLease AcquireTradeSuppression(string owner)
        => Acquire(owner, true);

    private BusyStatusLease Acquire(string owner, bool suppression)
    {
        lock (sync)
        {
            var contentId = !suppression && framework.IsInFrameworkUpdateThread && playerState.IsLoaded ? playerState.ContentId : 0;
            var lease = new BusyStatusLease(this, ++nextLeaseId, owner ?? string.Empty, suppression, contentId);
            if (disposed) { lease.Released = true; return lease; }
            leases.Add(lease.Id, lease);
            generation++; nextPoll = 0;
            return lease;
        }
    }

    public sealed class BusyStatusLease : IDisposable
    {
        private readonly AutoBusyStatusService service;
        internal readonly long Id;
        internal readonly bool Suppression;
        internal readonly ulong ContentId;
        internal bool Released;
        internal ulong WaitingContentId;
        internal long WaitingSince;
        private UiMessage reason = UiMessage.Authored("Waiting for a ready character to update Busy.");
        public string Owner { get; }
        internal BusyStatusLease(AutoBusyStatusService service, long id, string owner, bool suppression, ulong contentId)
        {
            this.service = service; Id = id; Owner = owner; Suppression = suppression; ContentId = contentId;
        }
        public BusyStatusReadiness Readiness => service.Readiness(this);
        internal UiMessage ReasonMessage { get { lock (service.sync) return reason; } }
        public string Reason => ReasonMessage.Original;
        internal BusyStatusReadiness Report(BusyStatusReadiness result, UiMessage message) { reason = message; return result; }
        public void Dispose() => service.Release(this);
    }

    private void Release(BusyStatusLease lease)
    {
        lock (sync)
        {
            if (lease.Released) return;
            lease.Released = true;
            if (leases.Remove(lease.Id)) { generation++; nextPoll = 0; }
        }
    }

    private BusyStatusReadiness Readiness(BusyStatusLease lease)
    {
        lock (sync)
        {
            if (disposed || lease.Released || !leases.ContainsKey(lease.Id))
                return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Busy lease has been released."));
            if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
                return lease.Report(BusyStatusReadiness.Pending, UiMessage.Authored("Waiting for a ready character to update Busy."));
            try
            {
                var owner = generation;
                if (!IsCurrent(owner))
                    return lease.Report(BusyStatusReadiness.Pending, UiMessage.Authored("Waiting for a ready character to update Busy."));
                var observed = Capture();
                if (lease.Released || disposed)
                    return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Busy lease has been released."));
                if (!IsCurrent(owner))
                    return lease.Report(BusyStatusReadiness.Pending, UiMessage.Authored("Waiting for a ready character to update Busy."));
                if (!lease.Suppression && (lease.ContentId == 0 || observed.ContentId != lease.ContentId))
                    return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Busy lease is no longer valid for this character."));
                if (observed.ContentId == 0 || !observed.Safe)
                {
                    lease.WaitingSince = 0; lease.WaitingContentId = 0;
                    return lease.Report(BusyStatusReadiness.Pending, UiMessage.Authored("Waiting for a ready character to update Busy."));
                }
                if (lease.WaitingContentId != observed.ContentId || lease.WaitingSince == 0)
                {
                    lease.WaitingContentId = observed.ContentId;
                    lease.WaitingSince = Environment.TickCount64;
                }
                if (coordinator.ContentId == observed.ContentId && coordinator.Failed)
                    return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Busy status could not be confirmed; disable and re-enable to retry."));
                if (observed.Readable && coordinator.ContentId == observed.ContentId && !coordinator.Pending)
                {
                    if (lease.Suppression)
                    {
                        if (!observed.Busy)
                            return lease.Report(BusyStatusReadiness.Ready, UiMessage.Authored("Busy status is ready for trading."));
                        if (!coordinator.Owned)
                            return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Manual Busy is blocking this trade. Turn Busy off manually to continue."));
                    }
                    else if (observed.Busy)
                        return lease.Report(BusyStatusReadiness.Ready, UiMessage.Authored("Busy is active."));
                    else if (coordinator.ManualChange)
                        return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Busy was changed outside Auto Busy; disable and re-enable to resume."));
                }
                if (Environment.TickCount64 - lease.WaitingSince >= LeaseWaitMilliseconds)
                    return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Authored("Busy readiness timed out."));
                return lease.Report(BusyStatusReadiness.Pending, UiMessage.Authored("Waiting for Busy status confirmation."));
            }
            catch (Exception error)
            {
                return lease.Report(BusyStatusReadiness.Blocked, UiMessage.Format(UiLocale.Key("Auto Busy failed: {0}"), UiMessage.FromException(error)));
            }
        }
    }

    private BusyStatusObservation Capture()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || playerState.ContentId == 0 || objectTable.LocalPlayer == null)
            return default;
        var contentId = playerState.ContentId;
        var safe = !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51]
            && !condition[ConditionFlag.LoggingOut] && !condition[ConditionFlag.WatchingCutscene]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent] && !condition[ConditionFlag.TradeOpen];
        var info = InfoModule.Instance();
        if (info == null || info->LocalContentId != contentId)
            return new(contentId, false, false, safe);
        // The mask is server-maintained; a character's single displayed status may hide Busy behind another priority.
        var busy = (info->OnlineStatusFlags & BusyMask) != 0;
        return new(contentId, safe, busy, safe);
    }

    private bool IsCurrent(long owner)
    {
        if (disposed || generation != owner || framework.IsFrameworkUnloading) return false;
        if (ownerDisposed()) return false;
        return !disposed && generation == owner && !framework.IsFrameworkUnloading;
    }

    private void Update(IFramework _)
    {
        lock (sync)
        {
            if (disposed || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading) return;
            if (!enabled && leases.Count == 0 && !coordinator.Owned && !coordinator.Pending) return;
            var now = Environment.TickCount64;
            if (now < nextPoll) return;
            nextPoll = now + PollMilliseconds;
            var owner = generation;
            try
            {
                if (!IsCurrent(owner)) return;
                var observed = Capture();
                if (!IsCurrent(owner)) return;
                var suppressed = leases.Values.Any(lease => lease.Suppression && !lease.Released);
                var demand = enabled || leases.Values.Any(lease => !lease.Suppression && !lease.Released && lease.ContentId != 0 && lease.ContentId == observed.ContentId);
                var command = coordinator.Advance(observed, demand, suppressed, now);
                PublishStatus();
                if (command == BusyStatusCommand.None) return;
                var fresh = Capture();
                if (!IsCurrent(owner) || !fresh.Readable || !fresh.Safe || fresh.ContentId != observed.ContentId || fresh.Busy != observed.Busy)
                {
                    coordinator.CancelUnsent();
                    return;
                }
                // Pending intent precedes the native call; reentrant disable/release must still observe its receipt.
                var accepted = ChatHelper.TrySend(command == BusyStatusCommand.On ? "/busy on" : "/busy off");
                if (disposed || coordinator.ContentId != observed.ContentId) return;
                coordinator.Submitted(accepted);
            }
            catch (Exception error)
            {
                if (disposed || generation != owner) return;
                coordinator.Pending = false; coordinator.Failed = true;
                status = UiMessage.Format(UiLocale.Key("Auto Busy failed: {0}"), UiMessage.FromException(error));
                log.Warning(error, "[XASlave] Auto Busy stopped after an error.");
            }
        }
    }

    private void PublishStatus()
    {
        status = coordinator.State switch
        {
            BusyStatusState.Disabled => UiMessage.Authored("Auto Busy is disabled."),
            BusyStatusState.Waiting => UiMessage.Authored("Waiting for a ready character to update Busy."),
            BusyStatusState.Active => UiMessage.Authored("Busy is active."),
            BusyStatusState.Preserved => UiMessage.Authored("Existing Busy status is preserved."),
            BusyStatusState.ManualChange => UiMessage.Authored("Busy was changed outside Auto Busy; disable and re-enable to resume."),
            BusyStatusState.Confirming => UiMessage.Authored("Waiting for Busy status confirmation."),
            BusyStatusState.Failed => UiMessage.Authored("Busy status could not be confirmed; disable and re-enable to retry."),
            BusyStatusState.Retry => UiMessage.Authored("Busy command was rejected; waiting before retry."),
            BusyStatusState.Suppressed => UiMessage.Authored("Busy is temporarily released for trading."),
            _ => UiMessage.Authored("Waiting for a ready character to update Busy."),
        };
    }

    private void OnLogout(int type, int code)
    {
        lock (sync)
        {
            generation++; coordinator.ResetForCharacter(0); nextPoll = 0;
            foreach (var lease in leases.Values) { lease.WaitingSince = 0; lease.WaitingContentId = 0; }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; enabled = false; generation++;
            framework.Update -= Update; clientState.Logout -= OnLogout;
            if (coordinator.Owned || (coordinator.Pending && coordinator.PendingTarget))
                log.Warning("[XASlave] Busy applied by Auto Busy may remain active; use /busy off if needed.");
            foreach (var lease in leases.Values) lease.Released = true;
            leases.Clear(); coordinator.ResetForCharacter(0);
        }
    }
}
