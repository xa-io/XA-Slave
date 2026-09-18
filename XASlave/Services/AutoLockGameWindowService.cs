using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class AutoLockGameWindowService : IDisposable
{
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly object sync = new();
    private readonly WindowSubclassPlatform platform;
    private readonly WindowLockCoordinator coordinator;
    private bool enabled, disposed, subscribed;
    private long nextReconcile;
    private string lastAction = "No actions yet.";

    public AutoLockGameWindowService(ICondition condition, IPluginLog log)
    {
        this.condition = condition;
        this.log = log;
        framework = Plugin.Framework;
        platform = new WindowSubclassPlatform(() => Plugin.PluginInterface.UiBuilder.WindowHandlePtr);
        coordinator = new WindowLockCoordinator(platform);
    }

    public string StatusText { get { lock (sync) return coordinator.Status; } }
    public string LastActionText { get { lock (sync) return lastAction; } }
    public bool IsLocked { get { lock (sync) return coordinator.IsLocked; } }

    public bool SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return false;
            enabled = value;
            coordinator.SetDesired(value, value && condition[ConditionFlag.InCombat]);
            nextReconcile = 0;
            if (!value)
            {
                coordinator.Stop(); Unsubscribe(); Publish(); return false;
            }
            if (!subscribed)
            {
                condition.ConditionChange += OnConditionChange;
                framework.Update += Update;
                subscribed = true;
            }
            // A call outside the framework only arms admission. Update revalidates
            // current combat/window state before any native registration.
            if (framework.IsInFrameworkUpdateThread) Reconcile();
            else coordinator.WaitForFramework();
            if (coordinator.Status.StartsWith("Failed:", StringComparison.Ordinal) || coordinator.Status.StartsWith("Unsupported", StringComparison.Ordinal))
            {
                enabled = false; coordinator.SetDesired(false, false); Unsubscribe();
                return false;
            }
            return true;
        }
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        lock (sync)
        {
            if (disposed || !enabled || flag != ConditionFlag.InCombat) return;
            coordinator.SetDesired(true, value);
            nextReconcile = 0;
            if (framework.IsInFrameworkUpdateThread) Reconcile();
        }
    }

    private void Update(IFramework _)
    {
        lock (sync)
        {
            if (disposed || !enabled || !framework.IsInFrameworkUpdateThread || Environment.TickCount64 < nextReconcile) return;
            Reconcile();
        }
    }

    private void Reconcile()
    {
        coordinator.SetDesired(enabled, enabled && condition[ConditionFlag.InCombat]);
        coordinator.Reconcile();
        nextReconcile = Environment.TickCount64 + 250;
        Publish();
        if (coordinator.Status.StartsWith("Failed:", StringComparison.Ordinal))
        {
            // A failed registration must not allocate another retained native relay
            // every update. Keep its failure visible until the user explicitly retries.
            enabled = false; coordinator.SetDesired(false, false); Unsubscribe();
        }
    }

    private void Publish()
    {
        if (lastAction != coordinator.Status) lastAction = coordinator.Status;
        var diagnostic = platform.TakeDiagnostic();
        if (diagnostic != null) log.Warning("[XASlave] Window position lock: {Diagnostic}", diagnostic);
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;
        condition.ConditionChange -= OnConditionChange;
        framework.Update -= Update;
        subscribed = false;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; enabled = false;
            coordinator.SetDesired(false, false);
            Unsubscribe(); coordinator.Stop();
            // Inert callback cleanup owns its lifetime and never returns to this service.
        }
    }
}
