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
    internal string Status { get; private set; } = "Disabled";
    internal bool IsLocked => enabled && combat && lease is { Registered: true, Destroyed: false, BlocksMovement: true };
    internal bool CleanupPending => lease is { Detached: false } && !lease.BlocksMovement;

    internal WindowLockCoordinator(IWindowSubclassPlatform platform) { this.platform = platform; }

    internal void WaitForFramework() { Status = "Waiting for framework window admission"; }

    internal void SetDesired(bool enable, bool inCombat)
    {
        generation++; enabled = enable; combat = inCombat;
        if (!enabled || !combat)
        {
            lease?.Deactivate();
            try { lease?.RequestRemoval(); }
            catch (Exception error) { Status = "Cleanup pending: " + error.Message; }
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
                if (!lease.Detached) { Status = "Cleanup pending: the old window lease is inactive and remains rooted."; return; }
                lease = null;
            }
            if (currentGeneration != generation) return;
            if (!enabled) { Status = "Disabled"; return; }
            if (!combat) { Status = "Enabled: position locking is armed for combat."; return; }
            if (target.Handle == 0) { Status = "Waiting for game window"; return; }
            if (target.Process != platform.CurrentProcess) { Status = "Unsupported game window process"; return; }
            if (target.Thread != platform.CurrentThread) { Status = "Unsupported window thread"; return; }
            if (lease != null)
            {
                Status = IsLocked ? "Locked: game window position" : "Cleanup pending";
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
                Status = candidate.Detached ? "Failed: window lock admission changed or registration failed." : "Cleanup pending";
                return;
            }
            candidate.Activate();
            if (currentGeneration != generation || !enabled || !combat || platform.ReadTarget() != target)
            {
                candidate.Deactivate(); candidate.RequestRemoval();
                if (candidate.Detached) lease = null;
                Status = "Cleanup pending: lock activation was revoked.";
                return;
            }
            Status = IsLocked ? "Locked: game window position" : "Failed: window lease is not active.";
        }
        catch (Exception error)
        {
            lease?.Deactivate();
            try { lease?.RequestRemoval(); } catch { /* Retain the inactive lease on cleanup failure. */ }
            Status = "Failed: " + error.Message;
        }
    }

    internal void Stop()
    {
        SetDesired(false, false);
        if (lease == null) { Status = "Disabled"; return; }
        try { lease.RequestRemoval(); }
        catch (Exception error) { Status = "Cleanup pending: " + error.Message; return; }
        Status = lease.Detached ? "Disabled" : "Cleanup pending: movement blocking is off; callback state remains rooted.";
        if (lease.Detached) lease = null;
    }
}
