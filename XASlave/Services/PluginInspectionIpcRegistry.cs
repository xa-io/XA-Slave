using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Dalamud.Plugin;

namespace XASlave.Services;

internal sealed record PluginInspectionIpcParameter(string Name, string Type, string Direction, bool Optional);

internal sealed record PluginInspectionIpcHandler(
    string Method, string MethodAssembly, string MethodModuleVersionId,
    string TargetType, string TargetAssembly, string TargetModuleVersionId,
    bool AttributedToSelectedPlugin, string Attribution);

internal sealed record PluginInspectionIpcDelegate(
    string DelegateType, string InvokeSignature, string ReturnType, bool SignatureComplete,
    IReadOnlyList<PluginInspectionIpcParameter> Parameters,
    IReadOnlyList<PluginInspectionIpcHandler> Handlers, bool AttributedToSelectedPlugin);

internal sealed record PluginInspectionIpcChannel(
    string Name, bool NameComplete, bool HasFunction, bool HasAction, int SubscriptionCount,
    bool AttributedToSelectedPlugin, bool SelectedPluginSubscribes,
    PluginInspectionIpcDelegate? Function, PluginInspectionIpcDelegate? Action,
    IReadOnlyList<PluginInspectionIpcDelegate> Subscriptions)
{
    internal global::XASlave.Localization.UiMessage RegistrationMessage => (HasFunction || HasAction ? global::XASlave.Localization.UiMessage.Key("SD25D34CB0C025ED5") : (SubscriptionCount > 0 ? global::XASlave.Localization.UiMessage.Key("S57EC2F07651D2AB5") : global::XASlave.Localization.UiMessage.Key("SB9D93D2C3661E977")));
    public string RegistrationState => RegistrationMessage.Original;
}

internal sealed record PluginInspectionIpcSnapshot(
    string Status, string HostAssemblyVersion, DateTime CapturedUtc, bool Incomplete,
    IReadOnlyList<PluginInspectionIpcChannel> Channels, IReadOnlyList<string> Limitations);

/// <summary>Reads only the host registry and delegate metadata; never calls an IPC endpoint or peer member.</summary>
internal static class PluginInspectionIpcRegistry
{
    private const int MaxChannels = 4096;
    private const int MaxHandlers = 10000;
    private const int MaxSubscriptions = 64;
    private const int MaxInvocationList = 16;
    private const int MaxParameters = 64;
    private const int MaxTypeDepth = 12;
    private const int MaxTextLength = 2048;
    private const int MaxCapturedTextCharacters = 2_000_000;
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(5);
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // Grounded against installed Dalamud 15.0.3.8, read-only decompilation of:
    // Service<T>.GetNullable, CallGate.Gates, CallGateChannel.Action/Func/Subscriptions.
    // Source counterparts: Dalamud/Service/Service{T}.cs and Dalamud/Plugin/Ipc/Internal/.
    // Gates and Subscriptions produce immutable BCL snapshots; their host caches may be populated.
    // Reflection execution is restricted to these five accessors. No GetIpcSubscriber/provider factory is used.
    internal static PluginInspectionIpcSnapshot Capture(Assembly selectedAssembly,
        IDalamudPluginInterface initializedPluginInterface, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selectedAssembly);
        ArgumentNullException.ThrowIfNull(initializedPluginInterface);
        var state = new CaptureState(token);
        var capturedUtc = DateTime.UtcNow;
        var host = typeof(IDalamudPluginInterface).Assembly;
        var hostVersion = host.GetName().Version?.ToString() ?? string.Empty;
        var channels = new List<PluginInspectionIpcChannel>();
        var status = global::XASlave.Localization.UiMessage.Key("SDDD9818ABACFDE7A");
        try
        {
            token.ThrowIfCancellationRequested();
            var contract = ResolveContract(host, initializedPluginInterface);
            if (!state.Continue()) return Result(global::XASlave.Localization.UiMessage.Key("S9834A14AB9BCAA0F"));
            // The actual initialized host interface establishes that service initialization has completed.
            // GetNullable(None) returns immediately for an unavailable/faulted service; never use Get/GetAsync.
            var registry = contract.GetNullable.Invoke(null, new[] { contract.NoPropagation });
            if (registry == null) return Result(global::XASlave.Localization.UiMessage.Key("S702FE424F0EBEB41"), incomplete: true);
            if (registry.GetType() != contract.RegistryType) throw new NotSupportedException();
            if (!state.Continue()) return Result(global::XASlave.Localization.UiMessage.Key("S9834A14AB9BCAA0F"));
            var snapshot = contract.Gates.GetValue(registry);
            if (snapshot?.GetType() != contract.DictionaryType || snapshot is not IDictionary gates)
                throw new NotSupportedException();
            if (gates.Count > MaxChannels) state.Incomplete(global::XASlave.Localization.UiMessage.Key("S8C7ECD75B0E2459D"));
            var visitedChannels = 0;
            foreach (DictionaryEntry entry in gates)
            {
                if (visitedChannels >= MaxChannels || !state.Continue()) break;
                visitedChannels++;
                if (entry.Key is not string name || entry.Value?.GetType() != contract.ChannelType)
                    throw new NotSupportedException();
                try { channels.Add(CaptureChannel(name, entry.Value, contract, selectedAssembly, state)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { state.Failure(global::XASlave.Localization.UiMessage.Key("S9B67097118FC25C2"), exception); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (NotSupportedException)
        {
            status = global::XASlave.Localization.UiMessage.Key("SE13C722135132D84");
            state.Incomplete(global::XASlave.Localization.UiMessage.Key("S7FF3E36B79B29F9C"));
        }
        catch (Exception exception)
        {
            status = global::XASlave.Localization.UiMessage.Key("S6A3CC4250CB913C9");
            state.Failure(global::XASlave.Localization.UiMessage.Key("S5439776DB85C6DC9"), exception);
        }
        token.ThrowIfCancellationRequested();
        return Result(status);

        PluginInspectionIpcSnapshot Result(global::XASlave.Localization.UiMessage resultStatus, bool incomplete = false)
        {
            token.ThrowIfCancellationRequested();
            if (resultStatus.Original == "available" && (incomplete || state.IsIncomplete)) resultStatus = global::XASlave.Localization.UiMessage.Key("S9834A14AB9BCAA0F");
            return PluginInspectionPresentation.Attach(new PluginInspectionIpcSnapshot(resultStatus.Original, hostVersion, capturedUtc, incomplete || state.IsIncomplete,
                Array.AsReadOnly(channels.ToArray()), Array.AsReadOnly(state.Limitations.ToArray())), state.LimitationMessages, resultStatus);
        }
    }

    private static HostContract ResolveContract(Assembly host, IDalamudPluginInterface initializedPluginInterface)
    {
        var interfaceType = RequiredType(host, "Dalamud.Plugin.DalamudPluginInterface");
        if (initializedPluginInterface.GetType() != interfaceType) throw new NotSupportedException();
        var registry = RequiredType(host, "Dalamud.Plugin.Ipc.Internal.CallGate");
        var channel = RequiredType(host, "Dalamud.Plugin.Ipc.Internal.CallGateChannel");
        var propagation = RequiredType(host, "Dalamud.ExceptionPropagationMode");
        if (!propagation.IsEnum || Enum.GetUnderlyingType(propagation) != typeof(int)
            || Enum.GetNames(propagation).Length != 3
            || !Enum.TryParse(propagation, "None", false, out var none) || Convert.ToInt32(none) != 2)
            throw new NotSupportedException();
        var service = RequiredType(host, "Dalamud.Service`1").MakeGenericType(registry);
        var get = service.GetMethod("GetNullable", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
            binder: null, types: new[] { propagation }, modifiers: null);
        if (get == null || get.ReturnType != registry || get.IsGenericMethod || get.DeclaringType != service)
            throw new NotSupportedException();
        return new(registry, channel, typeof(ImmutableDictionary<,>).MakeGenericType(typeof(string), channel), get, none!,
            RequiredGetter(registry, "Gates", typeof(IReadOnlyDictionary<,>).MakeGenericType(typeof(string), channel)),
            RequiredGetter(channel, "Action", typeof(Delegate)), RequiredGetter(channel, "Func", typeof(Delegate)),
            RequiredGetter(channel, "Subscriptions", typeof(IReadOnlyList<Delegate>)));
    }

    // Runtime consumers may inspect the current provider but must execute only through official subscribers.
    internal static Delegate? FindProvider(string name, bool action, Assembly selectedAssembly, IDalamudPluginInterface pluginInterface)
    {
        var contract = ResolveContract(typeof(IDalamudPluginInterface).Assembly, pluginInterface);
        var registry = contract.GetNullable.Invoke(null, new[] { contract.NoPropagation });
        if (registry?.GetType() != contract.RegistryType) return null;
        var snapshot = contract.Gates.GetValue(registry);
        if (snapshot?.GetType() != contract.DictionaryType || snapshot is not IDictionary gates) return null;
        var channel = gates[name];
        if (channel?.GetType() != contract.ChannelType) return null;
        var value = (action ? contract.Action : contract.Func).GetValue(channel) as Delegate;
        if (value == null) return null;
        var handlers = value.GetInvocationList();
        if (handlers.Length != 1) return null;
        var handler = handlers[0];
        return handler.Method.Module.Assembly == selectedAssembly || handler.Target?.GetType().Assembly == selectedAssembly
            ? value : null;
    }

    private static Type RequiredType(Assembly host, string name)
    {
        var type = host.GetType(name, throwOnError: false);
        if (type == null || type.Assembly != host) throw new NotSupportedException();
        return type;
    }

    private static PropertyInfo RequiredGetter(Type owner, string name, Type expectedType)
    {
        var property = owner.GetProperty(name, PublicInstance);
        if (property == null || property.PropertyType != expectedType || property.GetIndexParameters().Length != 0
            || property.GetMethod is not { IsPublic: true, IsStatic: false, IsVirtual: false } getter
            || getter.DeclaringType != owner || getter.DeclaringType.Assembly != typeof(IDalamudPluginInterface).Assembly)
            throw new NotSupportedException();
        return property;
    }

    private static PluginInspectionIpcChannel CaptureChannel(string name, object channel, HostContract contract,
        Assembly selectedAssembly, CaptureState state)
    {
        var nameRevision = state.IncompleteRevision;
        var channelName = state.Text(name);
        var nameComplete = state.IncompleteRevision == nameRevision && string.Equals(channelName, name, StringComparison.Ordinal);
        // These are host-owned auto-properties, not plugin properties. Read each delegate once.
        var action = contract.Action.GetValue(channel) as Delegate;
        var function = contract.Func.GetValue(channel) as Delegate;
        if (contract.Subscriptions.GetValue(channel) is not ImmutableList<Delegate> subscriptions)
            throw new NotSupportedException();
        var actionInfo = action == null ? null : CaptureDelegate(action, selectedAssembly, state);
        var functionInfo = function == null ? null : CaptureDelegate(function, selectedAssembly, state);
        var subscribers = new List<PluginInspectionIpcDelegate>();
        if (subscriptions.Count > MaxSubscriptions) state.Incomplete(global::XASlave.Localization.UiMessage.Key("SECE0E0464DD1694C"));
        for (var index = 0; index < subscriptions.Count && index < MaxSubscriptions && state.Continue(); index++)
            subscribers.Add(CaptureDelegate(subscriptions[index], selectedAssembly, state));
        return new(channelName, nameComplete, function != null, action != null, subscriptions.Count,
            actionInfo?.AttributedToSelectedPlugin == true || functionInfo?.AttributedToSelectedPlugin == true,
            subscribers.Any(value => value.AttributedToSelectedPlugin), functionInfo, actionInfo,
            Array.AsReadOnly(subscribers.ToArray()));
    }

    private static PluginInspectionIpcDelegate CaptureDelegate(Delegate value, Assembly selectedAssembly, CaptureState state)
    {
        var signatureRevision = state.IncompleteRevision;
        var delegateType = value.GetType();
        // Invoke describes the actual delegate call shape; Method may differ for closed/bound delegates.
        var invoke = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (invoke == null) throw new NotSupportedException();
        var supported = !delegateType.ContainsGenericParameters && !invoke.ContainsGenericParameters;
        if (!supported) state.Incomplete(global::XASlave.Localization.UiMessage.Key("S8F5749EEB0223563"));
        var delegateTypeName = TypeName(delegateType, state);
        var parameters = new List<PluginInspectionIpcParameter>();
        var parameterMetadata = invoke.GetParameters();
        if (parameterMetadata.Length > MaxParameters) state.Incomplete(global::XASlave.Localization.UiMessage.Key("S5371416D90D43A3C"));
        foreach (var parameter in parameterMetadata.Take(MaxParameters))
        {
            if (!state.Continue()) break;
            supported &= SignatureTypeSupported(parameter.ParameterType, state);
            var direction = parameter.IsOut ? "out" : parameter.ParameterType.IsByRef ? parameter.IsIn ? "in" : "ref" : "value";
            parameters.Add(new(state.Text(parameter.Name), TypeName(parameter.ParameterType, state), direction, parameter.IsOptional));
        }
        supported &= SignatureTypeSupported(invoke.ReturnType, state);
        var returnType = TypeName(invoke.ReturnType, state);
        var signature = state.Text(returnType + " (" + string.Join(", ", parameters.Select(parameter =>
            (parameter.Direction == "value" ? string.Empty : parameter.Direction + " ") + parameter.Type + " " + parameter.Name)) + ")");
        var signatureComplete = state.Continue() && supported && parameters.Count == parameterMetadata.Length
            && state.IncompleteRevision == signatureRevision;
        var handlers = new List<PluginInspectionIpcHandler>();
        var invocationList = value.GetInvocationList();
        if (invocationList.Length > MaxInvocationList) state.Incomplete(global::XASlave.Localization.UiMessage.Key("S0060ACB2028394BD"));
        foreach (var handler in invocationList.Take(MaxInvocationList))
        {
            if (!state.ReserveHandler()) break;
            var method = handler.Method;
            var methodAssembly = method.Module.Assembly;
            var targetType = handler.Target?.GetType();
            var targetAssembly = targetType?.Assembly;
            var methodMatches = methodAssembly == selectedAssembly;
            var targetMatches = targetAssembly == selectedAssembly;
            handlers.Add(new(state.Text((method.DeclaringType?.FullName ?? "<dynamic>") + "." + method.Name),
                state.Text(methodAssembly.FullName), state.Text(method.Module.ModuleVersionId.ToString("D")),
                targetType == null ? string.Empty : TypeName(targetType, state), state.Text(targetAssembly?.FullName),
                state.Text(targetAssembly?.ManifestModule.ModuleVersionId.ToString("D")), methodMatches || targetMatches,
                methodMatches ? "handler method belongs to selected assembly instance"
                    : targetMatches ? "delegate target type belongs to selected assembly instance"
                    : "other or unknown owner; shared wrapper targets are not traversed"));
        }
        return new(delegateTypeName, signature, returnType, signatureComplete, Array.AsReadOnly(parameters.ToArray()),
            Array.AsReadOnly(handlers.ToArray()), handlers.Any(handler => handler.AttributedToSelectedPlugin));
    }

    private static bool SignatureTypeSupported(Type type, CaptureState state, int depth = 0)
    {
        if (!state.Continue()) return false;
        if (depth >= MaxTypeDepth)
        {
            state.Incomplete(global::XASlave.Localization.UiMessage.Key("S0B7EEAC56092D65E"));
            return false;
        }
        if (type.IsByRef || type.IsPointer || type.IsFunctionPointer || type.IsByRefLike || type.ContainsGenericParameters)
        {
            state.Incomplete(global::XASlave.Localization.UiMessage.Key("SDD55F83DB3756E00"));
            return false;
        }
        if (type.HasElementType)
        {
            var element = type.GetElementType();
            if (element != null) return SignatureTypeSupported(element, state, depth + 1);
            state.Incomplete(global::XASlave.Localization.UiMessage.Key("S462CC7473CD012DD"));
            return false;
        }
        if (!type.IsGenericType) return true;
        var arguments = type.GetGenericArguments();
        if (arguments.Length > MaxParameters)
        {
            state.Incomplete(global::XASlave.Localization.UiMessage.Key("SD4A7C9157423B6CD"));
            return false;
        }
        var supported = true;
        foreach (var argument in arguments)
            supported &= SignatureTypeSupported(argument, state, depth + 1);
        return supported;
    }

    private static string TypeName(Type type, CaptureState state, int depth = 0)
    {
        if (!state.Continue()) return "<capture limit>";
        if (depth >= MaxTypeDepth) { state.Incomplete(global::XASlave.Localization.UiMessage.Key("S010ECF0C456E65FF")); return "<type nesting limit>"; }
        if (type.HasElementType)
        {
            var element = type.GetElementType();
            var suffix = type.IsArray ? "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.IsByRef ? "&" : "*";
            return state.Text((element == null ? "?" : TypeName(element, state, depth + 1)) + suffix);
        }
        if (!type.IsGenericType) return state.Text(type.FullName ?? type.Name);
        var arguments = type.GetGenericArguments();
        if (arguments.Length > MaxParameters) state.Incomplete(global::XASlave.Localization.UiMessage.Key("SC539C0E1ED48A701"));
        return state.Text((type.GetGenericTypeDefinition().FullName ?? type.Name) + "<"
            + string.Join(", ", arguments.Take(MaxParameters).Select(argument => TypeName(argument, state, depth + 1))) + ">");
    }

    private sealed record HostContract(Type RegistryType, Type ChannelType, Type DictionaryType,
        MethodInfo GetNullable, object NoPropagation, PropertyInfo Gates, PropertyInfo Action,
        PropertyInfo Func, PropertyInfo Subscriptions);

    private sealed class CaptureState
    {
        private readonly CancellationToken token;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int textCharacters;
        private int handlers;
        internal bool IsIncomplete { get; private set; }
        internal long IncompleteRevision { get; private set; }
        internal List<global::XASlave.Localization.UiMessage> LimitationMessages { get; } = new()
        {
            global::XASlave.Localization.UiMessage.Key("SDED17DDCA08DAC56"),
            global::XASlave.Localization.UiMessage.Key("SF1BC5079D63CCB55"),
            global::XASlave.Localization.UiMessage.Key("SE63DC26A53EFCFB6"),
            global::XASlave.Localization.UiMessage.Key("S312E618480F29257"),
            global::XASlave.Localization.UiMessage.Key("S031A8089AE07CD68"),
            global::XASlave.Localization.UiMessage.Key("S0E7960774F67F424"),
            global::XASlave.Localization.UiMessage.Key("S9AD9658A083718B5"),
            global::XASlave.Localization.UiMessage.Key("SC7FDC4675DA7CDA2"),
            global::XASlave.Localization.UiMessage.Key("SBEDAE754182EE864"),
            global::XASlave.Localization.UiMessage.Key("SCCFED0BFC095D483"),
        };
        internal List<string> Limitations => LimitationMessages.Select(message => message.Original).ToList();

        internal CaptureState(CancellationToken token) => this.token = token;

        internal bool Continue()
        {
            token.ThrowIfCancellationRequested();
            if (textCharacters >= MaxCapturedTextCharacters || handlers >= MaxHandlers)
            {
                Incomplete(global::XASlave.Localization.UiMessage.Key("S33E078E6A8471FAC"));
                return false;
            }
            if (clock.Elapsed < CaptureBudget) return true;
            Incomplete(global::XASlave.Localization.UiMessage.Key("S76C2DD478A3CE924"));
            return false;
        }

        internal bool ReserveHandler()
        {
            if (!Continue()) return false;
            handlers++;
            if (handlers == MaxHandlers) Incomplete(global::XASlave.Localization.UiMessage.Key("S22C860BC81BB3928"));
            return true;
        }

        internal string Text(string? text)
        {
            if (text == null) return string.Empty;
            var remaining = MaxCapturedTextCharacters - textCharacters;
            if (remaining <= 0)
            {
                if (text.Length > 0) Incomplete(global::XASlave.Localization.UiMessage.Key("SC6548C1D7E1D027C"));
                return string.Empty;
            }
            var maximum = Math.Min(MaxTextLength, remaining);
            var length = Math.Min(maximum, text.Length);
            if (length < text.Length)
            {
                Incomplete(global::XASlave.Localization.UiMessage.Key("SF9CD6267B116ACE9"));
                if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            }
            textCharacters += length;
            if (length == remaining) Incomplete(global::XASlave.Localization.UiMessage.Key("S201E14016DE7DA60"));
            return text[..length];
        }

        internal void Failure(global::XASlave.Localization.UiMessage scope, Exception exception)
            => Incomplete(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(scope, global::XASlave.Localization.UiMessage.Key("SB3A7321F8FEEABAE")), global::XASlave.Localization.UiMessage.Raw(Text(exception.GetType().FullName))), global::XASlave.Localization.UiMessage.Raw(").")));

        internal void Incomplete(global::XASlave.Localization.UiMessage message)
        {
            IsIncomplete = true;
            IncompleteRevision++;
            if (Limitations.Count < 64 && !Limitations.Contains(message.Original, StringComparer.Ordinal)) LimitationMessages.Add(message);
        }
    }
}
