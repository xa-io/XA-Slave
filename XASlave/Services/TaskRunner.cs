using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
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

    private readonly List<TaskStep> steps = new();
    private int stepIndex = -1;
    private DateTime stepStart;
    private bool stepActionDone;
    private bool running;
    private Action? onFinished;
    private Action<string>? onLog;

    public bool IsRunning => running;
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

    public TaskRunner(ICondition condition, IFramework framework, IPluginLog log)
    {
        this.condition = condition;
        this.framework = framework;
        this.log = log;
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
        try { onFinished?.Invoke(); }
        catch (Exception ex) { log.Error($"[XASlave] TaskRunner onFinished error: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (running)
        {
            running = false;
            framework.Update -= OnTick;
        }
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
    internal int RetryCount { get; set; } = 0;
}
