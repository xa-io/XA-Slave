using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

internal enum StepMachineTickResult
{
    Running,
    Advanced,
    Completed,
}

/// <summary>
/// Shared, lifecycle-neutral task-step executor. Owners retain framework subscriptions, DTR/UI,
/// completion policy, and integration cleanup; this class owns the one native-action step loop.
/// </summary>
internal sealed class StepMachine
{
    internal const int MaxSkipsPerTick = 32;

    private readonly Action<string, Exception> logError;
    private readonly Action<string>? activityLog;
    private readonly Func<DateTime> utcNow;
    private readonly List<TaskStep> steps = new();
    private int stepIndex = -1;
    private DateTime stepStartUtc;
    private bool stepActionDone;

    public StepMachine(IPluginLog log, string ownerLabel, Action<string>? activityLog = null)
        : this(
            (context, exception) => log.Error($"[XASlave] {ownerLabel} {context} error: {exception.Message}"),
            activityLog,
            () => DateTime.UtcNow)
    {
    }

    internal StepMachine(
        Action<string, Exception> logError,
        Action<string>? activityLog = null,
        Func<DateTime>? utcNow = null)
    {
        this.logError = logError;
        this.activityLog = activityLog;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsRunning { get; private set; }
    public int CurrentStep => stepIndex;
    public int TotalSteps => steps.Count;
    public float CurrentStepElapsedSeconds => IsRunning
        ? Math.Max(0f, (float)(utcNow() - stepStartUtc).TotalSeconds)
        : 0f;
    public string CurrentStepName => IsRunning && (uint)stepIndex < (uint)steps.Count
        ? steps[stepIndex].Name
        : string.Empty;

    public void Start(IEnumerable<TaskStep> source)
    {
        steps.Clear();
        steps.AddRange(source);
        foreach (var step in steps)
            step.RetryCount = 0;

        stepIndex = steps.Count > 0 ? 0 : -1;
        stepStartUtc = utcNow();
        stepActionDone = false;
        IsRunning = stepIndex >= 0;
    }

    public void Append(IEnumerable<TaskStep> additionalSteps)
    {
        foreach (var step in additionalSteps)
        {
            step.RetryCount = 0;
            steps.Add(step);
        }
    }

    public void Stop()
    {
        IsRunning = false;
        stepIndex = -1;
        stepActionDone = false;
    }

    public StepMachineTickResult Tick(
        Action<int, TaskStep>? onStepStarted = null,
        Action<int, TaskStep, float>? onStepCompleted = null,
        Action<int, TaskStep, float>? onRetry = null,
        Action<int, TaskStep, float>? onTimeout = null)
    {
        if (!IsRunning || (uint)stepIndex >= (uint)steps.Count)
            return Complete();

        var skipped = 0;
        while (IsRunning && (uint)stepIndex < (uint)steps.Count)
        {
            var pendingStep = steps[stepIndex];
            bool shouldSkip;
            try
            {
                shouldSkip = pendingStep.ShouldSkip != null && pendingStep.ShouldSkip();
            }
            catch (Exception ex)
            {
                ReportError($"step '{pendingStep.Name}' ShouldSkip", ex, $"Skip-check error in '{pendingStep.Name}': {ex.Message}");
                shouldSkip = false;
            }

            if (!shouldSkip)
                break;

            Advance();
            if (!IsRunning)
                return StepMachineTickResult.Completed;
            if (++skipped >= MaxSkipsPerTick)
                return StepMachineTickResult.Advanced;
        }

        var step = steps[stepIndex];
        var elapsed = (float)(utcNow() - stepStartUtc).TotalSeconds;

        if (!stepActionDone)
        {
            onStepStarted?.Invoke(stepIndex, step);
            if (step.OnEnter != null)
            {
                try
                {
                    step.OnEnter();
                }
                catch (Exception ex)
                {
                    ReportError($"step '{step.Name}' action", ex, $"Error in '{step.Name}': {ex.Message}");
                }
            }

            stepActionDone = true;
        }

        try
        {
            if (step.IsComplete())
            {
                onStepCompleted?.Invoke(stepIndex, step, elapsed);
                Advance();
                return IsRunning ? StepMachineTickResult.Advanced : StepMachineTickResult.Completed;
            }
        }
        catch (Exception ex)
        {
            ReportError($"step '{step.Name}' check", ex, $"Check error in '{step.Name}': {ex.Message}");
        }

        if (elapsed <= step.TimeoutSec)
            return StepMachineTickResult.Running;

        if (step.MaxRetries > 0 && step.RetryCount < step.MaxRetries)
        {
            step.RetryCount++;
            stepActionDone = false;
            stepStartUtc = utcNow();
            onRetry?.Invoke(stepIndex, step, elapsed);
            return StepMachineTickResult.Running;
        }

        onTimeout?.Invoke(stepIndex, step, elapsed);
        try
        {
            step.OnTimeout?.Invoke();
        }
        catch (Exception ex)
        {
            ReportError($"step '{step.Name}' timeout handler", ex, $"Timeout-handler error in '{step.Name}': {ex.Message}");
        }

        if (!IsRunning)
            return StepMachineTickResult.Completed;

        Advance();
        return IsRunning ? StepMachineTickResult.Advanced : StepMachineTickResult.Completed;
    }

    private void Advance()
    {
        stepIndex++;
        if (stepIndex >= steps.Count)
        {
            Stop();
            return;
        }

        stepStartUtc = utcNow();
        stepActionDone = false;
    }

    private StepMachineTickResult Complete()
    {
        Stop();
        return StepMachineTickResult.Completed;
    }

    private void ReportError(string context, Exception exception, string activity)
    {
        logError(context, exception);
        activityLog?.Invoke(activity);
    }
}
