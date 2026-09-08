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
    private string externalStatusText = string.Empty;

    private readonly StepMachine stepMachine;
    private bool hasActiveRun;
    private Action? onFinished;
    private Action<string>? onLog;
    private bool suppressCompletionReport;
    private bool haltRequested;
    private string haltReason = string.Empty;
    private readonly TaskRunResults taskRunResults = new();
    private int historySequenceNumber;

    public bool IsRunning => hasActiveRun;

    /// <summary>
    /// When true, the logout handler should NOT cancel this task.
    /// Set by the relogger since logout is expected during /ays relog.
    /// </summary>
    public bool SuppressLogoutCancel { get; set; }
    public string CurrentTaskName { get; private set; } = string.Empty;
    public string StatusText { get; private set; } = string.Empty;
    public int CurrentStep => stepMachine.CurrentStep;
    public int TotalSteps => stepMachine.TotalSteps;

    // Progress tracking
    public int CompletedItems { get; set; }
    public int TotalItems { get; set; }
    public string CurrentItemLabel { get; set; } = string.Empty;

    // Log messages for UI display
    private const int MaxLogMessages = 8000;
    private readonly BoundedLogBuffer logMessages = new(MaxLogMessages);
    public IReadOnlyList<string> LogMessages => logMessages.Snapshot(includeEvictionNotice: true);

    // Detached snapshots for UI/report consumers. Mutations remain owned by TaskRunner.
    public IReadOnlyList<string> FailedCharacters => taskRunResults.FailedCharactersSnapshot();

    // Characters that logged in successfully but could not complete the per-character process
    // (e.g. stuck in a duty that could not be left) - marked PURPLE in the processing list.
    public IReadOnlyList<string> IncompleteCharacters => taskRunResults.IncompleteCharactersSnapshot();

    // Per-item active-processing timers (seconds). Started when an item begins processing and
    // recorded when it finishes (completed/failed/incomplete) - used for per-character timers + ETA.
    public IReadOnlyDictionary<string, double> ItemDurations => taskRunResults.ItemDurationsSnapshot();

    public bool RecordFailedCharacter(string characterName) => taskRunResults.RecordFailedCharacter(characterName);

    public bool RecordIncompleteCharacter(string characterName) => taskRunResults.RecordIncompleteCharacter(characterName);

    /// <summary>Begin the active-processing timer for an item (idempotent per item).</summary>
    public void RecordItemStart(string item)
    {
        taskRunResults.RecordItemStart(item, DateTime.UtcNow);
    }

    /// <summary>Record the elapsed processing time for a finished item (first finish wins).</summary>
    public void RecordItemEnd(string item)
    {
        taskRunResults.RecordItemEnd(item, DateTime.UtcNow);
    }

    public TaskRunner(ICondition condition, IFramework framework, IPluginLog log, IDtrBar dtrBar, IToastGui toastGui)
    {
        this.condition = condition;
        this.framework = framework;
        this.log = log;
        this.dtrBar = dtrBar;
        this.toastGui = toastGui;
        stepMachine = new StepMachine(log, "TaskRunner", AddLog);

        // DTR bar always available - shows "Idle" when no task running
        InitDtrBar();
    }

    /// <summary>
    /// Start executing a list of steps as a named task. Set preserveRunHistory for another sequence
    /// within the same coordinated run to retain logs, character outcomes, and timers.
    /// </summary>
    /// <returns><see langword="true"/> only when this call takes ownership of the runner.</returns>
    public bool Start(
        string taskName,
        List<TaskStep> taskSteps,
        Action? onFinished = null,
        Action<string>? onLog = null,
        bool suppressCompletionReport = false,
        int? totalItems = null,
        bool? suppressLogoutCancel = null,
        bool preserveRunHistory = false)
    {
        var normalizedSteps = taskSteps ?? [];
        var startDecision = TaskRunnerStartPolicy.Evaluate(hasActiveRun, normalizedSteps.Count);
        if (startDecision == TaskRunnerStartDecision.Busy)
        {
            log.Warning($"[XASlave] TaskRunner: '{taskName}' rejected - '{CurrentTaskName}' is already running.");
            return false;
        }

        if (startDecision == TaskRunnerStartDecision.Empty)
        {
            log.Warning($"[XASlave] TaskRunner: '{taskName}' rejected - no steps.");
            return false;
        }

        this.onFinished = onFinished;
        this.onLog = onLog;
        this.suppressCompletionReport = suppressCompletionReport;
        haltRequested = false;
        haltReason = string.Empty;
        CurrentTaskName = taskName;
        CompletedItems = 0;
        if (totalItems.HasValue)
            TotalItems = Math.Max(0, totalItems.Value);
        if (suppressLogoutCancel.HasValue)
            SuppressLogoutCancel = suppressLogoutCancel.Value;
        // Some dynamic owners (notably Xagman) establish progress before Start. Preserve that
        // value when totalItems is omitted; Cancel/Finish clear it before the next idle run.
        CurrentItemLabel = string.Empty;
        if (!preserveRunHistory)
        {
            logMessages.Clear();
            taskRunResults.Clear();
            historySequenceNumber = 0;
        }
        historySequenceNumber++;

        stepMachine.Start(normalizedSteps);
        hasActiveRun = true;
        StatusText = stepMachine.CurrentStepName;
        framework.Update += OnTick;

        AddLog(preserveRunHistory
            ? $"[{taskName}] Starting sequence {historySequenceNumber}; earlier log entries and character results retained."
            : $"[{taskName}] Starting sequence {historySequenceNumber} of a new run; earlier log entries and character results cleared.");
        AddLog($"[{taskName}] Started with {stepMachine.TotalSteps} steps.");
        log.Information($"[XASlave] TaskRunner: '{taskName}' started with {stepMachine.TotalSteps} steps.");

        // Show DTR bar progress
        UpdateDtrBar();
        return true;
    }

    /// <summary>Append additional steps to a running task (for dynamic character rotation).</summary>
    public void AppendSteps(List<TaskStep> additionalSteps)
    {
        stepMachine.Append(additionalSteps);
    }

    public void Cancel()
    {
        if (!hasActiveRun) return;
        stepMachine.Stop();
        hasActiveRun = false;
        framework.Update -= OnTick;
        haltRequested = false;
        haltReason = string.Empty;
        StatusText = "Cancelled";
        SuppressLogoutCancel = false;
        suppressCompletionReport = false;
        TotalItems = 0;
        AddLog($"[{CurrentTaskName}] Cancelled.");
        log.Information($"[XASlave] TaskRunner: '{CurrentTaskName}' cancelled.");
        // NOTE: onFinished is intentionally NOT invoked on cancellation. Some callers register a
        // *continuation* as onFinished (e.g. the Xagman Tony item-sell task schedules a
        // full-inventory fallback that can broadcast peer completion), which must run only on
        // natural completion — never when a run is cancelled or auto-cancelled by logout.
        // Callers that need cleanup on cancel perform it in their own stop path.
        SetDtrIdle();
    }

    /// <summary>
    /// Stops the current sequence as an unsuccessful terminal outcome. Unlike normal completion,
    /// this never invokes completion continuations or reports the task as complete.
    /// </summary>
    public void RequestHalt(string reason)
    {
        if (!hasActiveRun || haltRequested)
            return;

        haltRequested = true;
        haltReason = string.IsNullOrWhiteSpace(reason) ? "A safety requirement failed." : reason.Trim();
        stepMachine.Stop();
        StatusText = "Halted";
        AddLog($"[{CurrentTaskName}] Halted: {haltReason}");
        log.Warning($"[XASlave] TaskRunner: '{CurrentTaskName}' halted: {haltReason}");
    }

    public void AddLog(string message)
    {
        logMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        onLog?.Invoke(message);
    }

    public bool VerboseTaskLoggingEnabled => IsVerboseTaskLoggingEnabled();

    public void AddVerboseLog(string message)
    {
        if (IsVerboseTaskLoggingEnabled())
            AddLog(message);
    }

    public void ClearLog()
    {
        logMessages.Clear();
        AddLog("[Task history] Log cleared by request; character results retained.");
    }

    /// <summary>Explicitly start fresh history for a coordinated run before its first sequence.</summary>
    public void ClearRunHistory()
    {
        logMessages.Clear();
        taskRunResults.Clear();
        historySequenceNumber = 0;
        AddLog("[Task history] Log and character results cleared for a new run.");
    }

    public void SetExternalStatus(string statusText)
    {
        var normalizedStatusText = statusText?.Trim() ?? string.Empty;
        if (string.Equals(externalStatusText, normalizedStatusText, StringComparison.Ordinal))
            return;

        externalStatusText = normalizedStatusText;
        if (!hasActiveRun)
            UpdateDtrBar();
    }

    public void ClearExternalStatus()
    {
        if (string.IsNullOrEmpty(externalStatusText))
            return;

        externalStatusText = string.Empty;
        if (!hasActiveRun)
            UpdateDtrBar();
    }

    public bool IsNormalCondition()
    {
        return CharacterSafetyHelper.IsNormalCondition(condition);
    }

    private string FormatStepLabel(int index, TaskStep step)
    {
        return $"[{index + 1}/{stepMachine.TotalSteps}] {step.Name}";
    }

    private static bool IsVerboseTaskLoggingEnabled()
    {
        return Plugin.Instance?.Configuration.VerboseTaskLogging ?? false;
    }

    private void LogStepStart(int index, TaskStep step)
    {
        if (IsVerboseTaskLoggingEnabled())
            AddLog($"STEP START {FormatStepLabel(index, step)} (timeout {step.TimeoutSec:0.##}s)");
    }

    private void LogStepComplete(int index, TaskStep step, float elapsed)
    {
        if (IsVerboseTaskLoggingEnabled())
            AddLog($"STEP DONE {FormatStepLabel(index, step)} in {elapsed:0.00}s");
    }

    private void OnTick(IFramework fw)
        => ProcessTick();

    /// <summary>
    /// Runs one production lifecycle tick. Kept independent from the Dalamud event argument so the
    /// owner itself can be regression-tested without a live framework loop.
    /// </summary>
    internal void ProcessTick()
    {
        if (!hasActiveRun)
            return;

        if (haltRequested)
        {
            FinalizeHalt();
            return;
        }

        var previousStep = stepMachine.CurrentStep;
        var result = stepMachine.Tick(
            onStepStarted: LogStepStart,
            onStepCompleted: LogStepComplete,
            onRetry: (index, step, elapsed) =>
            {
                if (IsVerboseTaskLoggingEnabled())
                    AddLog($"STEP RETRY {FormatStepLabel(index, step)} ({step.RetryCount}/{step.MaxRetries}) after {elapsed:0.00}s");
            },
            onTimeout: (index, step, elapsed) =>
            {
                AddLog($"STEP TIMEOUT {FormatStepLabel(index, step)} after {elapsed:0.00}s (limit {step.TimeoutSec:0.##}s) - skipping.");
                log.Warning($"[XASlave] TaskRunner step '{step.Name}' timed out after {step.TimeoutSec}s.");
            });

        if (haltRequested)
        {
            FinalizeHalt();
            return;
        }

        if (result == StepMachineTickResult.Completed)
        {
            Finish();
            return;
        }

        StatusText = stepMachine.CurrentStepName;
        if (stepMachine.CurrentStep != previousStep)
            UpdateDtrBar();
    }

    private void Finish()
    {
        if (!hasActiveRun)
            return;

        hasActiveRun = false;
        framework.Update -= OnTick;
        haltRequested = false;
        haltReason = string.Empty;
        StatusText = "Complete";
        SuppressLogoutCancel = false;
        if (!suppressCompletionReport)
            AddLog($"[{CurrentTaskName}] Finished.");
        log.Information($"[XASlave] TaskRunner: '{CurrentTaskName}' finished.");

        // Reset DTR bar to idle
        SetDtrIdle();

        // Toast notification - must run on framework thread
        try
        {
            if (!suppressCompletionReport)
            {
                var taskName = CurrentTaskName;
                var completed = CompletedItems;
                var total = TotalItems;
                var failCount = FailedCharacters.Count;
                var incompleteCount = IncompleteCharacters.Count;
                _ = Plugin.RunOnGameThread(() =>
                {
                    string msg;
                    if (failCount > 0 && incompleteCount > 0)
                        msg = $"XA Slave: {taskName} complete ({completed}/{total}, {failCount} failed, {incompleteCount} incomplete)";
                    else if (failCount > 0)
                        msg = $"XA Slave: {taskName} complete ({completed}/{total}, {failCount} failed)";
                    else if (incompleteCount > 0)
                        msg = $"XA Slave: {taskName} complete ({completed}/{total}, {incompleteCount} incomplete)";
                    else
                        msg = $"XA Slave: {taskName} complete ({completed}/{total})";
                    toastGui.ShowNormal(msg);
                });
            }
        }
        catch { /* toast may fail silently */ }

        try { onFinished?.Invoke(); }
        catch (Exception ex) { log.Error($"[XASlave] TaskRunner onFinished error: {ex.Message}"); }
        finally { suppressCompletionReport = false; TotalItems = 0; }
    }

    private void FinalizeHalt()
    {
        if (!hasActiveRun)
            return;

        hasActiveRun = false;
        framework.Update -= OnTick;
        stepMachine.Stop();
        StatusText = "Halted";
        SuppressLogoutCancel = false;
        suppressCompletionReport = false;
        CurrentItemLabel = string.Empty;
        TotalItems = 0;
        haltRequested = false;
        haltReason = string.Empty;
        SetDtrIdle();
    }

    /// <summary>Initialize DTR bar entry - always visible, shows "Idle" by default.</summary>
    private void InitDtrBar()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            UpdateDtrBar();
        }
        catch { /* DTR bar may not be available */ }
    }

    private void UpdateDtrBar()
    {
        try
        {
            dtrEntry ??= dtrBar.Get("XA Slave");
            if (hasActiveRun && TotalItems > 0)
                dtrEntry.Text = $"XA: {CurrentTaskName} {CompletedItems}/{TotalItems}";
            else if (hasActiveRun)
                dtrEntry.Text = $"XA: {CurrentTaskName}";
            else if (!string.IsNullOrWhiteSpace(externalStatusText))
                dtrEntry.Text = $"XA: {externalStatusText}";
            else
                dtrEntry.Text = "XA: Idle";
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
            UpdateDtrBar();
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
        if (hasActiveRun)
        {
            stepMachine.Stop();
            hasActiveRun = false;
            framework.Update -= OnTick;
        }
        haltRequested = false;
        haltReason = string.Empty;
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
    public Func<bool>? ShouldSkip { get; init; }
    public float TimeoutSec { get; init; } = 10f;
    public int MaxRetries { get; init; } = 0;
    public Action? OnTimeout { get; init; }
    internal int RetryCount { get; set; } = 0;
}
