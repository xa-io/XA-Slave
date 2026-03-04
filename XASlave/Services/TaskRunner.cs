using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// Generic step-based task engine that runs on Framework.Update.
/// Reuses the same pattern as AutoCollectionService but accepts any task definition.
/// Each task is a list of TaskStep objects executed sequentially.
/// </summary>
public sealed class TaskRunner : IDisposable
{
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IDtrBar dtrBar;
    private readonly IToastGui toastGui;
    private IDtrBarEntry? dtrEntry;

    private readonly List<TaskStep> steps = new();
    private int stepIndex = -1;
    private DateTime stepStart;
    private bool stepActionDone;
    private bool running;
    private Action? onFinished;
    private Action<string>? onLog;

    public bool IsRunning => running;

    /// <summary>
    /// When true, the logout handler should NOT cancel this task.
    /// Set by the relogger since logout is expected during /ays relog.
    /// </summary>
    public bool SuppressLogoutCancel { get; set; }
    public string CurrentTaskName { get; private set; } = string.Empty;
    public string StatusText { get; private set; } = string.Empty;
    public int CurrentStep => stepIndex;
    public int TotalSteps => steps.Count;

    // Progress tracking
    public int CompletedItems { get; set; }
    public int TotalItems { get; set; }
    public string CurrentItemLabel { get; set; } = string.Empty;

    // Log messages for UI display
    private readonly List<string> logMessages = new();
    public IReadOnlyList<string> LogMessages => logMessages;
    private const int MaxLogMessages = 200;

    // Characters that failed to relog — for summary at end of task
    public List<string> FailedCharacters { get; } = new();

    public TaskRunner(ICondition condition, IFramework framework, IPluginLog log, IDtrBar dtrBar, IToastGui toastGui)
    {
        this.condition = condition;
        this.framework = framework;
        this.log = log;
        this.dtrBar = dtrBar;
        this.toastGui = toastGui;

        // DTR bar always available — shows "Idle" when no task running
        InitDtrBar();
    }

    /// <summary>Start executing a list of steps as a named task.</summary>
    public void Start(string taskName, List<TaskStep> taskSteps, Action? onFinished = null, Action<string>? onLog = null)
    {
        if (running) return;

        this.onFinished = onFinished;
        this.onLog = onLog;
        CurrentTaskName = taskName;
        CompletedItems = 0;
        TotalItems = 0;
        CurrentItemLabel = string.Empty;
        logMessages.Clear();
        FailedCharacters.Clear();

        steps.Clear();
        steps.AddRange(taskSteps);

        if (steps.Count == 0)
        {
            onFinished?.Invoke();
            return;
        }

        stepIndex = 0;
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        running = true;
        StatusText = steps[0].Name;
        framework.Update += OnTick;

        AddLog($"[{taskName}] Started with {steps.Count} steps.");
        log.Information($"[XASlave] TaskRunner: '{taskName}' started with {steps.Count} steps.");

        // Show DTR bar progress
        UpdateDtrBar();
    }

    /// <summary>Append additional steps to a running task (for dynamic character rotation).</summary>
    public void AppendSteps(List<TaskStep> additionalSteps)
    {
        steps.AddRange(additionalSteps);
    }

    public void Cancel()
    {
        if (!running) return;
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = "Cancelled";
        AddLog($"[{CurrentTaskName}] Cancelled.");
        log.Information($"[XASlave] TaskRunner: '{CurrentTaskName}' cancelled.");
        SetDtrIdle();
    }

    public void AddLog(string message)
    {
        if (logMessages.Count >= MaxLogMessages)
            logMessages.RemoveAt(0);
        logMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        onLog?.Invoke(message);
    }

    public void ClearLog()
    {
        logMessages.Clear();
    }

    public bool IsNormalCondition()
    {
        return !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.BoundByDuty]
            && !condition[ConditionFlag.WatchingCutscene]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent]
            && !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51];
    }

    private void OnTick(IFramework fw)
    {
        if (!running || stepIndex < 0 || stepIndex >= steps.Count)
        {
            Finish();
            return;
        }

        var step = steps[stepIndex];
        var elapsed = (float)(DateTime.UtcNow - stepStart).TotalSeconds;

        // Execute OnEnter once
        if (!stepActionDone)
        {
            if (step.OnEnter != null)
            {
                try { step.OnEnter(); }
                catch (Exception ex)
                {
                    log.Error($"[XASlave] TaskRunner step '{step.Name}' action error: {ex.Message}");
                    AddLog($"Error in '{step.Name}': {ex.Message}");
                }
            }
            stepActionDone = true;
        }

        // Check completion
        try
        {
            if (step.IsComplete())
            {
                AdvanceStep();
                return;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[XASlave] TaskRunner step '{step.Name}' check error: {ex.Message}");
            AddLog($"Check error in '{step.Name}': {ex.Message}");
        }

        // Timeout handling
        if (elapsed > step.TimeoutSec)
        {
            if (step.MaxRetries > 0 && step.RetryCount < step.MaxRetries)
            {
                step.RetryCount++;
                stepActionDone = false;
                stepStart = DateTime.UtcNow;
                AddLog($"Retrying '{step.Name}' ({step.RetryCount}/{step.MaxRetries})");
                return;
            }

            AddLog($"Timeout on '{step.Name}' after {step.TimeoutSec}s — skipping.");
            log.Warning($"[XASlave] TaskRunner step '{step.Name}' timed out after {step.TimeoutSec}s.");
            try { step.OnTimeout?.Invoke(); }
            catch (Exception ex) { log.Error($"[XASlave] TaskRunner step '{step.Name}' OnTimeout error: {ex.Message}"); }
            AdvanceStep();
        }
    }

    private void AdvanceStep()
    {
        stepIndex++;
        if (stepIndex >= steps.Count)
        {
            Finish();
            return;
        }
        stepStart = DateTime.UtcNow;
        stepActionDone = false;
        StatusText = steps[stepIndex].Name;
        UpdateDtrBar();
    }

    private void Finish()
    {
        if (!running) return;
        running = false;
        framework.Update -= OnTick;
        stepIndex = -1;
        StatusText = "Complete";
        AddLog($"[{CurrentTaskName}] Finished.");
        log.Information($"[XASlave] TaskRunner: '{CurrentTaskName}' finished.");

        // Reset DTR bar to idle
        SetDtrIdle();

        // Toast notification — must run on framework thread
        try
        {
            var taskName = CurrentTaskName;
            var completed = CompletedItems;
            var total = TotalItems;
            var failCount = FailedCharacters.Count;
            framework.RunOnFrameworkThread(() =>
            {
                var msg = failCount > 0
                    ? $"XA Slave: {taskName} complete ({completed}/{total}, {failCount} failed)"
                    : $"XA Slave: {taskName} complete ({completed}/{total})";
                toastGui.ShowNormal(msg);
            });
        }
        catch { /* toast may fail silently */ }

        try { onFinished?.Invoke(); }
        catch (Exception ex) { log.Error($"[XASlave] TaskRunner onFinished error: {ex.Message}"); }
    }

    /// <summary>Initialize DTR bar entry — always visible, shows "Idle" by default.</summary>
    private void InitDtrBar()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            dtrEntry.Text = "XA: Idle";
            dtrEntry.Shown = true;
        }
        catch { /* DTR bar may not be available */ }
    }

    private void UpdateDtrBar()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            if (TotalItems > 0)
                dtrEntry.Text = $"XA: {CompletedItems}/{TotalItems}";
            else
                dtrEntry.Text = $"XA: {CurrentTaskName}";
            dtrEntry.Shown = true;
        }
        catch { /* DTR bar may not be available */ }
    }

    /// <summary>Reset DTR bar to idle state (always stays visible).</summary>
    private void SetDtrIdle()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            dtrEntry.Text = "XA: Idle";
            dtrEntry.Shown = true;
        }
        catch { }
    }

    private void RemoveDtrBar()
    {
        try
        {
            if (dtrEntry != null)
            {
                dtrEntry.Shown = false;
                dtrEntry.Remove();
                dtrEntry = null;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (running)
        {
            running = false;
            framework.Update -= OnTick;
        }
        RemoveDtrBar();
    }
}

/// <summary>
/// A single step in a task sequence.
/// Mirrors the CollectionStep pattern from AutoCollectionService.
/// </summary>
public class TaskStep
{
    public string Name { get; init; } = string.Empty;
    public Action? OnEnter { get; init; }
    public Func<bool> IsComplete { get; init; } = () => true;
    public float TimeoutSec { get; init; } = 10f;
    public int MaxRetries { get; init; } = 0;
    public Action? OnTimeout { get; init; }
    internal int RetryCount { get; set; } = 0;
}
