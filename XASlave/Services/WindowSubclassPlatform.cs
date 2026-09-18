using System;

namespace XASlave.Services;

// Framework-only discovery. No reference to this platform is stored in a lease.
internal sealed class WindowSubclassPlatform : IWindowSubclassPlatform
{
    private readonly Func<nint> readWindow;
    private WindowLockTarget previous;
    private long epoch;
    private WindowSubclassLease? latest;
    private bool destructionObserved;
    public uint CurrentProcess => (uint)Environment.ProcessId;
    public uint CurrentThread => WindowSubclassApi.GetCurrentThreadId();

    internal WindowSubclassPlatform(Func<nint> readWindow) { this.readWindow = readWindow; }

    public WindowLockTarget ReadTarget()
    {
        var window = readWindow();
        uint process = 0, thread = 0;
        if (window != 0 && WindowSubclassApi.IsWindow(window)) thread = WindowSubclassApi.GetWindowThreadProcessId(window, out process);
        else window = 0;
        var newDestruction = latest is { Destroyed: true } && !destructionObserved;
        if (newDestruction) destructionObserved = true;
        if (window != previous.Handle || process != previous.Process || thread != previous.Thread || newDestruction) epoch++;
        previous = new(window, epoch, process, thread);
        return previous;
    }

    public IWindowSubclassLease InstallInert(WindowLockTarget target)
    {
        var candidate = new WindowSubclassLease(target);
        latest = candidate; destructionObserved = false;
        candidate.Install();
        return candidate;
    }

    internal string? TakeDiagnostic() => latest?.TakeDiagnostic();
}
