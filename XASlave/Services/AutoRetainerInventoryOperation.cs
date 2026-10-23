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
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("SBA35F0C47D862763");
    internal string Status { get; private set; } = global::XASlave.Localization.UiLocale.Native("SBA35F0C47D862763");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, Status);

    internal State Start()
    {
        if (started) return Fail(global::XASlave.Localization.UiMessage.Key("S72926ABC181248E4"));
        started = true;
        startedAt = Environment.TickCount64;
        try
        {
            RequireFrameworkThread();
            if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded || IsLoading()
                || Plugin.PlayerState.ContentId == 0 || !isExpectedCharacter())
                return Fail(global::XASlave.Localization.UiMessage.Key("SEBA42A0C409030F4"));
            contentId = Plugin.PlayerState.ContentId;
            provider = ResolveLoadedProvider();
            var providerType = provider.GetType();
            taskManagerField = providerType.GetField("TaskManager", InstanceBindings)
                ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S56883EFD71A5329A"), originalMessageText => new InvalidOperationException(originalMessageText));
            taskManager = taskManagerField.GetValue(provider)
                ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE310299F651182D7"), originalMessageText => new InvalidOperationException(originalMessageText));
            queueBusyProperty = taskManager.GetType().GetProperty("IsBusy", InstanceBindings);
            if (queueBusyProperty?.PropertyType != typeof(bool) || queueBusyProperty.GetMethod == null
                || queueBusyProperty.GetIndexParameters().Length != 0)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9A4F30B4AFFD12FA"), originalMessageText => new InvalidOperationException(originalMessageText));
            var assembly = providerType.Assembly;
            deliveryOperationField = assembly.GetType("AutoRetainer.Modules.GcHandin.AutoGCHandin")?
                .GetField("Operation", StaticBindings);
            if (deliveryOperationField?.FieldType != typeof(bool))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S34A9F5C34BBC6135"), originalMessageText => new InvalidOperationException(originalMessageText));

            var taskTypeName = kind == Kind.Discard
                ? "AutoRetainer.Scheduler.Tasks.TaskRecursiveItemDiscard"
                : "AutoRetainer.Scheduler.Tasks.TaskDeliverItems";
            var methodName = kind == Kind.Discard ? "EnqueueIfNeeded" : "Enqueue";
            var parameterTypes = kind == Kind.Discard ? Type.EmptyTypes : new[] { typeof(bool) };
            var method = assembly.GetType(taskTypeName)?.GetMethod(methodName, StaticBindings, null, parameterTypes, null);
            if (method == null || method.ContainsGenericParameters
                || method.ReturnType != (kind == Kind.Discard ? typeof(void) : typeof(bool)))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5FAF1F07E1B224A7"), originalMessageText => new InvalidOperationException(originalMessageText));

            if (ReadBusy()) return Fail(global::XASlave.Localization.UiMessage.Key("S2E18C0ABBF462EB6"));
            ValidateSession();
            // A provider can enqueue work and then throw. Retain this uncertainty until
            // idle has been positively observed, including when reflection invocation fails.
            MayStillBeRunning = true;
            var accepted = method.Invoke(null, kind == Kind.Discard ? null : new object[] { false });
            if (kind == Kind.ExpertDelivery && accepted is not true)
            {
                MayStillBeRunning = false;
                return Fail(global::XASlave.Localization.UiMessage.Key("S224B6E0CF0130256"));
            }
            if (kind == Kind.Discard)
            {
                // The void discard entry point synchronously queues its master task.
                // Inspect before yielding so a short pass cannot be mistaken for no work.
                noDiscardWorkQueued = !ReadQueueBusy();
                if (noDiscardWorkQueued) MayStillBeRunning = false;
            }
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (noDiscardWorkQueued ? global::XASlave.Localization.UiMessage.Key("S8BF20163B1C330DB") : global::XASlave.Localization.UiMessage.Key("S7D3C0F2AA696405E")));
            return state;
        }
        catch (Exception ex)
        {
            return Fail(global::XASlave.Localization.UiMessage.Format("SABE5FCE7BE0EAD27" , FailureMessage(ex)));
        }
    }

    internal State Poll()
    {
        if (state != State.Running) return state;
        if (!started) return Fail(global::XASlave.Localization.UiMessage.Key("SF01F6BB82D6B214F"));
        try
        {
            ValidateSession();
            if (Environment.TickCount64 - startedAt >= TimeoutSeconds * 1000L)
                return Fail(global::XASlave.Localization.UiMessage.Format("S8AE94E7BB4E81DF9" , TimeoutSeconds));
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
            Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (noDiscardWorkQueued ? global::XASlave.Localization.UiMessage.Key("SCB1E1D82A8531A16") : global::XASlave.Localization.UiMessage.Key("SEB0EFD77C6624EBD")));
            return state = State.Finished;
        }
        catch (Exception ex)
        {
            return Fail(global::XASlave.Localization.UiMessage.Format("S79ABA54A7A1FBFC3" , FailureMessage(ex)));
        }
    }

    private void ValidateSession()
    {
        RequireFrameworkThread();
        if (!Plugin.ClientState.IsLoggedIn || contentId == 0 || Plugin.PlayerState.ContentId != contentId)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S41B5C4FCC4013D32"), originalMessageText => new InvalidOperationException(originalMessageText));
        // Provider travel can temporarily remove the local actor. Keep observing the
        // same logged-in content ID during that load, and never complete while loading.
        if (!IsLoading() && (!Plugin.PlayerState.IsLoaded || !isExpectedCharacter()))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD5BD2CAA4C0BAD34"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (!ReferenceEquals(ResolveLoadedProvider(), provider)
            || !ReferenceEquals(taskManagerField?.GetValue(provider), taskManager))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5E7E1DA8FEB9E741"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    private bool ReadBusy()
    {
        if (!ipcClient.TryGetAutoRetainerBusy(out var providerBusy)
            || !ipcClient.TryGetLifestreamBusy(out var travelBusy))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB84A4D33B108B254"), originalMessageText => new InvalidOperationException(originalMessageText));
        var queueBusy = ReadQueueBusy();
        if (deliveryOperationField?.GetValue(null) is not bool deliveryBusy)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S817DA0C8B71FE1B2"), originalMessageText => new InvalidOperationException(originalMessageText));
        return providerBusy || travelBusy || queueBusy || deliveryBusy;
    }

    private bool ReadQueueBusy()
    {
        if (queueBusyProperty?.GetValue(taskManager) is not bool busy)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S6BA5A3CC277B40A7"), originalMessageText => new InvalidOperationException(originalMessageText));
        return busy;
    }

    private static object ResolveLoadedProvider()
    {
        if (!Plugin.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "AutoRetainer" && x.IsLoaded))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB291C74355DC1094"), originalMessageText => new InvalidOperationException(originalMessageText));
        var instance = AutoRetainerUiReflectionService.TryGetAutoRetainerPluginInstance(allowAssemblyFallback: false);
        if (instance == null || instance.GetType().FullName != "AutoRetainer.AutoRetainer"
            || instance.GetType().Assembly.GetName().Name != "AutoRetainer"
            || !ReferenceEquals(instance.GetType().GetField("P", StaticBindings)?.GetValue(null), instance))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S061603E1AC1769BD"), originalMessageText => new InvalidOperationException(originalMessageText));
        return instance;
    }

    private static bool IsLoading()
        => Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];

    private static void RequireFrameworkThread()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3D27D9EF9AF13D87"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    private State Fail(global::XASlave.Localization.UiMessage message)
    {
        Status = global::XASlave.Localization.UiMessage.Store(ref statusMessage, message);
        return state = State.Failed;
    }

    private static global::XASlave.Localization.UiMessage FailureMessage(Exception ex)
        => global::XASlave.Localization.UiMessage.FromException(ex is TargetInvocationException invocation ? invocation.InnerException ?? ex : ex);
}
