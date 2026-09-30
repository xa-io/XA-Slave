using System;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;

namespace XASlave.Services;

/// <summary>
/// Observes one explicitly requested AutoRetainer inventory pass. AutoRetainer owns
/// item eligibility, protections, travel, seal caps and purchase/continuation policy.
/// There is no scoped provider cancellation contract; this adapter never aborts it.
/// </summary>
internal sealed class AutoRetainerInventoryOperation
{
    internal enum Kind { Discard, ExpertDelivery }
    internal enum State { Running, Finished, Failed }

    private const BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticBindings = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const long IdleSettleMilliseconds = 1000;
    private readonly IpcClient ipcClient;
    private readonly Kind kind;
    private readonly Func<bool> isExpectedCharacter;
    private object? provider;
    private object? taskManager;
    private FieldInfo? taskManagerField;
    private PropertyInfo? queueBusyProperty;
    private FieldInfo? deliveryOperationField;
    private ulong contentId;
    private long startedAt;
    private long? idleSince;
    private bool started;
    private bool noDiscardWorkQueued;
    private State state = State.Running;

    internal AutoRetainerInventoryOperation(IpcClient ipcClient, Kind kind, Func<bool> isExpectedCharacter)
    {
        this.ipcClient = ipcClient;
        this.kind = kind;
        this.isExpectedCharacter = isExpectedCharacter;
    }

    internal int TimeoutSeconds => kind == Kind.Discard ? 300 : 900;
    internal bool MayStillBeRunning { get; private set; }
    internal string Status { get; private set; } = "Not started";

    internal State Start()
    {
        if (started) return Fail("The inventory pass was already started; it will not be dispatched twice.");
        started = true;
        startedAt = Environment.TickCount64;
        try
        {
            RequireFrameworkThread();
            if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded || IsLoading()
                || Plugin.PlayerState.ContentId == 0 || !isExpectedCharacter())
                return Fail("The expected character is not ready for inventory operations.");
            contentId = Plugin.PlayerState.ContentId;
            provider = ResolveLoadedProvider();
            var providerType = provider.GetType();
            taskManagerField = providerType.GetField("TaskManager", InstanceBindings)
                ?? throw new InvalidOperationException("AutoRetainer TaskManager contract is unavailable.");
            taskManager = taskManagerField.GetValue(provider)
                ?? throw new InvalidOperationException("AutoRetainer TaskManager is unavailable.");
            queueBusyProperty = taskManager.GetType().GetProperty("IsBusy", InstanceBindings);
            if (queueBusyProperty?.PropertyType != typeof(bool) || queueBusyProperty.GetMethod == null
                || queueBusyProperty.GetIndexParameters().Length != 0)
                throw new InvalidOperationException("AutoRetainer queue status contract is unsupported.");
            var assembly = providerType.Assembly;
            deliveryOperationField = assembly.GetType("AutoRetainer.Modules.GcHandin.AutoGCHandin")?
                .GetField("Operation", StaticBindings);
            if (deliveryOperationField?.FieldType != typeof(bool))
                throw new InvalidOperationException("AutoRetainer delivery status contract is unsupported.");

            var taskTypeName = kind == Kind.Discard
                ? "AutoRetainer.Scheduler.Tasks.TaskRecursiveItemDiscard"
                : "AutoRetainer.Scheduler.Tasks.TaskDeliverItems";
            var methodName = kind == Kind.Discard ? "EnqueueIfNeeded" : "Enqueue";
            var parameterTypes = kind == Kind.Discard ? Type.EmptyTypes : new[] { typeof(bool) };
            var method = assembly.GetType(taskTypeName)?.GetMethod(methodName, StaticBindings, null, parameterTypes, null);
            if (method == null || method.ContainsGenericParameters
                || method.ReturnType != (kind == Kind.Discard ? typeof(void) : typeof(bool)))
                throw new InvalidOperationException("AutoRetainer inventory enqueue contract is unsupported.");

            if (ReadBusy()) return Fail("AutoRetainer or Lifestream is already busy; inventory work was not queued.");
            ValidateSession();
            // A provider can enqueue work and then throw. Retain this uncertainty until
            // idle has been positively observed, including when reflection invocation fails.
            MayStillBeRunning = true;
            var accepted = method.Invoke(null, kind == Kind.Discard ? null : new object[] { false });
            if (kind == Kind.ExpertDelivery && accepted is not true)
            {
                MayStillBeRunning = false;
                return Fail("AutoRetainer rejected Expert Delivery; check this character's GC and delivery settings.");
            }
            if (kind == Kind.Discard)
            {
                // The void discard entry point synchronously queues its master task.
                // Inspect before yielding so a short pass cannot be mistaken for no work.
                noDiscardWorkQueued = !ReadQueueBusy();
                if (noDiscardWorkQueued) MayStillBeRunning = false;
            }
            Status = noDiscardWorkQueued ? "AutoRetainer queued no discard work; waiting for provider idle." : "AutoRetainer accepted the inventory pass.";
            return state;
        }
        catch (Exception ex)
        {
            return Fail($"AutoRetainer inventory start failed: {FailureMessage(ex)}");
        }
    }

    internal State Poll()
    {
        if (state != State.Running) return state;
        if (!started) return Fail("The inventory pass has not been started.");
        try
        {
            ValidateSession();
            if (Environment.TickCount64 - startedAt >= TimeoutSeconds * 1000L)
                return Fail($"AutoRetainer inventory pass exceeded its {TimeoutSeconds}-second limit.");
            // AR 4.6.2.11 queues GCContinuation synchronously and sets Operation before
            // the initiating queue task returns. Closing, purchases and redelivery are
            // queued synchronously as Operation ends. Check every ownership indicator;
            // the settling interval is not a substitute for pending-work detection.
            var busy = ReadBusy();
            if (busy || IsLoading())
            {
                idleSince = null;
                return State.Running;
            }
            idleSince ??= Environment.TickCount64;
            if (Environment.TickCount64 - idleSince.Value < IdleSettleMilliseconds)
                return State.Running;
            MayStillBeRunning = false;
            Status = noDiscardWorkQueued
                ? "Discard pass finished: AutoRetainer queued no work and is idle."
                : "Inventory pass finished: AutoRetainer and Lifestream are idle; provider caps and protection settings still apply.";
            return state = State.Finished;
        }
        catch (Exception ex)
        {
            return Fail($"AutoRetainer inventory monitoring failed: {FailureMessage(ex)}");
        }
    }

    private void ValidateSession()
    {
        RequireFrameworkThread();
        if (!Plugin.ClientState.IsLoggedIn || contentId == 0 || Plugin.PlayerState.ContentId != contentId)
            throw new InvalidOperationException("The character logged out or the player session changed.");
        // Provider travel can temporarily remove the local actor. Keep observing the
        // same logged-in content ID during that load, and never complete while loading.
        if (!IsLoading() && (!Plugin.PlayerState.IsLoaded || !isExpectedCharacter()))
            throw new InvalidOperationException("The expected character is no longer loaded.");
        if (!ReferenceEquals(ResolveLoadedProvider(), provider)
            || !ReferenceEquals(taskManagerField?.GetValue(provider), taskManager))
            throw new InvalidOperationException("The AutoRetainer instance or task manager changed during the pass.");
    }

    private bool ReadBusy()
    {
        if (!ipcClient.TryGetAutoRetainerBusy(out var providerBusy)
            || !ipcClient.TryGetLifestreamBusy(out var travelBusy))
            throw new InvalidOperationException("AutoRetainer or Lifestream busy status is unavailable.");
        var queueBusy = ReadQueueBusy();
        if (deliveryOperationField?.GetValue(null) is not bool deliveryBusy)
            throw new InvalidOperationException("AutoRetainer delivery status is unavailable.");
        return providerBusy || travelBusy || queueBusy || deliveryBusy;
    }

    private bool ReadQueueBusy()
    {
        if (queueBusyProperty?.GetValue(taskManager) is not bool busy)
            throw new InvalidOperationException("AutoRetainer queue status is unavailable.");
        return busy;
    }

    private static object ResolveLoadedProvider()
    {
        if (!Plugin.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "AutoRetainer" && x.IsLoaded))
            throw new InvalidOperationException("AutoRetainer is not loaded.");
        var instance = AutoRetainerUiReflectionService.TryGetAutoRetainerPluginInstance(allowAssemblyFallback: false);
        if (instance == null || instance.GetType().FullName != "AutoRetainer.AutoRetainer"
            || instance.GetType().Assembly.GetName().Name != "AutoRetainer"
            || !ReferenceEquals(instance.GetType().GetField("P", StaticBindings)?.GetValue(null), instance))
            throw new InvalidOperationException("The live AutoRetainer instance could not be verified.");
        return instance;
    }

    private static bool IsLoading()
        => Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];

    private static void RequireFrameworkThread()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Inventory operations require the framework update thread.");
    }

    private State Fail(string message)
    {
        Status = message;
        return state = State.Failed;
    }

    private static string FailureMessage(Exception ex)
        => (ex is TargetInvocationException invocation ? invocation.InnerException ?? ex : ex).Message;
}
