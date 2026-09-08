namespace XASlave.Services;

internal enum TaskRunnerStartDecision
{
    Start,
    Busy,
    Empty,
}

internal static class TaskRunnerStartPolicy
{
    public static TaskRunnerStartDecision Evaluate(bool isRunning, int stepCount)
    {
        if (isRunning)
            return TaskRunnerStartDecision.Busy;
        return stepCount > 0 ? TaskRunnerStartDecision.Start : TaskRunnerStartDecision.Empty;
    }
}
