using System;

namespace XASlave.Services;

internal readonly record struct WindowLockTarget(nint Handle, long Epoch, uint Process, uint Thread);

internal interface IWindowSubclassLease
{
    WindowLockTarget Target { get; }
    bool Registered { get; }
    bool Detached { get; }
    bool Destroyed { get; }
    bool BlocksMovement { get; }
    void Activate();
    void Deactivate();
    void RequestRemoval();
}

internal interface IWindowSubclassPlatform
{
    WindowLockTarget ReadTarget();
    uint CurrentProcess { get; }
    uint CurrentThread { get; }
    IWindowSubclassLease InstallInert(WindowLockTarget target);
}

// Framework coordinator only. A native lease must retain its own callback/root after
// this object is gone; cleanup requests never transfer that lifetime to the service.
internal sealed class WindowLockCoordinator
{
    private readonly IWindowSubclassPlatform platform;
    private IWindowSubclassLease? lease;
    private long generation;
    private bool enabled, combat;
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string Status { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, Status);
    internal bool IsLocked => enabled && combat && lease is { Registered: true, Destroyed: false, BlocksMovement: true };
    internal bool CleanupPending => lease is { Detached: false } && !lease.BlocksMovement;

    internal WindowLockCoordinator(IWindowSubclassPlatform platform) { this.platform = platform; }

    internal void WaitForFramework() { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S5E525FAF99E9464D")); }

    internal void SetDesired(bool enable, bool inCombat)
    {
        generation++; enabled = enable; combat = inCombat;
        if (!enabled || !combat)
        {
            lease?.Deactivate();
            try { lease?.RequestRemoval(); }
            catch (Exception error) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S18484189CB01E58E" , global::XASlave.Localization.UiMessage.FromException(error))); }
        }
    }

    internal void Reconcile()
    {
        var currentGeneration = generation;
        try
        {
            var target = platform.ReadTarget();
            if (lease != null && (!enabled || !combat || lease.Destroyed || !lease.Registered || !lease.BlocksMovement || lease.Target != target || target.Process != platform.CurrentProcess || target.Thread != platform.CurrentThread))
            {
                lease.Deactivate(); lease.RequestRemoval();
                if (!lease.Detached) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S41B152FCF37D3292")); return; }
                lease = null;
            }
            if (currentGeneration != generation) return;
            if (!enabled) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")); return; }
            if (!combat) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S18E08B55FC670541")); return; }
            if (target.Handle == 0) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SF6E79B4DC7B09CBE")); return; }
            if (target.Process != platform.CurrentProcess) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S88686A1159D7E5D2")); return; }
            if (target.Thread != platform.CurrentThread) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S0828430226AAD1B3")); return; }
            if (lease != null)
            {
                Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (IsLocked ? global::XASlave.Localization.UiMessage.Key("S03B838C4F572E616") : global::XASlave.Localization.UiMessage.Key("S747D464599CC00C2")));
                return;
            }
            // Installation starts inert. Reentrant disable/replacement cannot turn a
            // successful native registration into a late movement-blocking callback.
            var candidate = platform.InstallInert(target);
            lease = candidate;
            if (!candidate.Registered || candidate.Destroyed || currentGeneration != generation || !enabled || !combat || platform.ReadTarget() != target)
            {
                candidate.Deactivate(); candidate.RequestRemoval();
                if (candidate.Detached) lease = null;
                Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (candidate.Detached ? global::XASlave.Localization.UiMessage.Key("SA5019236BABA54F5") : global::XASlave.Localization.UiMessage.Key("S747D464599CC00C2")));
                return;
            }
            candidate.Activate();
            if (currentGeneration != generation || !enabled || !combat || platform.ReadTarget() != target)
            {
                candidate.Deactivate(); candidate.RequestRemoval();
                if (candidate.Detached) lease = null;
                Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SFB66FA3206FD999C"));
                return;
            }
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (IsLocked ? global::XASlave.Localization.UiMessage.Key("S03B838C4F572E616") : global::XASlave.Localization.UiMessage.Key("S0F9867865FBEBF77")));
        }
        catch (Exception error)
        {
            lease?.Deactivate();
            try { lease?.RequestRemoval(); } catch { /* Retain the inactive lease on cleanup failure. */ }
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S03154105C765F481" , global::XASlave.Localization.UiMessage.FromException(error)));
        }
    }

    internal void Stop()
    {
        SetDesired(false, false);
        if (lease == null) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")); return; }
        try { lease.RequestRemoval(); }
        catch (Exception error) { Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S18484189CB01E58E" , global::XASlave.Localization.UiMessage.FromException(error))); return; }
        Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (lease.Detached ? global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E") : global::XASlave.Localization.UiMessage.Key("S31ABE62528FC167E")));
        if (lease.Detached) lease = null;
    }
}
