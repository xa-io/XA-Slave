using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;

namespace XASlave.Services;

internal sealed record PluginInspectionIdentity(
    string InternalName,
    string DisplayName,
    string Version,
    string AssemblyName,
    string ModuleVersionId,
    DateTime CapturedUtc);

internal sealed record PluginInspectionCommand(string Command, string Help, string Handler, string Attribution);

internal sealed record PluginInspectionParameter(
    string Name,
    string Type,
    string Direction,
    bool Optional,
    IReadOnlyList<string> Attributes);

internal sealed record PluginInspectionMember(
    string Kind,
    string Name,
    string Signature,
    string Visibility,
    bool Static,
    string ReturnType,
    IReadOnlyList<PluginInspectionParameter> Parameters,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> Accessors,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> CandidateLabels);

internal sealed record PluginInspectionType(
    string Name,
    string Kind,
    string Visibility,
    string BaseType,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> CandidateLabels,
    IReadOnlyList<PluginInspectionMember> Members);

internal sealed class PluginInspectionReport
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion => "xa-slave.plugin-inspection/v2";
    public PluginInspectionIdentity Identity { get; }
    public IReadOnlyList<PluginInspectionCommand> Commands { get; }
    public IReadOnlyList<PluginInspectionType> Types { get; }
    public IReadOnlyList<string> PreferredControlOrder { get; } = Array.AsReadOnly(new[] { "ipc", "command", "reflection" });
    public PluginInspectionIpcSnapshot? Ipc { get; internal set; }
    public IReadOnlyList<PluginInspectionIpcDeclaration> IpcDeclarations { get; internal set; } = Array.Empty<PluginInspectionIpcDeclaration>();
    public List<string> Limitations { get; }
    public bool Truncated { get; }
    public bool CommandCaptureIncomplete { get; internal set; }
    public bool HasMainUi { get; internal set; }
    public bool HasConfigUi { get; internal set; }
    public string AssemblyVersion { get; internal set; } = string.Empty;
    public bool Partial => Truncated || CommandCaptureIncomplete || Ipc?.Incomplete == true;

    internal PluginInspectionReport(PluginInspectionIdentity identity,
        IReadOnlyList<PluginInspectionCommand> commands, IReadOnlyList<PluginInspectionType> types,
        List<string> limitations, bool truncated)
    {
        Identity = identity;
        Commands = commands;
        Types = types;
        Limitations = limitations;
        Truncated = truncated;
    }
}

/// <summary>Selected-assembly metadata only. Never reads inspected values or invokes inspected code.</summary>
internal static class PluginInspectionCatalog
{
    private const int MaxTypes = 1000;
    private const int MaxMembers = 20000;
    private const int MaxCommands = 1000;
    private const int MaxIpcDeclarations = 2048;
    private const int MaxParameters = 64;
    private const int MaxAttributes = 64;
    private const int MaxNameLength = 512;
    private const int MaxSignatureLength = 2048;
    private const int MaxTypeDepth = 12;
    private const int MaxCapturedTextCharacters = 2_000_000;
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(5);
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static PluginInspectionReport Capture(Assembly assembly, PluginInspectionIdentity identity,
        IReadOnlyList<PluginInspectionCommand> commands, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(commands);
        token.ThrowIfCancellationRequested();
        var capture = new CaptureState(token, identity.InternalName);
        var copiedIdentity = identity with
        {
            InternalName = capture.Text(identity.InternalName, MaxNameLength),
            DisplayName = capture.Text(identity.DisplayName, MaxNameLength),
            Version = capture.Text(identity.Version, MaxNameLength),
            AssemblyName = capture.Text(identity.AssemblyName, MaxNameLength),
            ModuleVersionId = capture.Text(identity.ModuleVersionId, MaxNameLength),
        };
        var copiedCommands = new List<PluginInspectionCommand>();
        for (var index = 0; index < commands.Count && index < MaxCommands && capture.Continue(); index++)
        {
            var command = commands[index];
            copiedCommands.Add(new(capture.Text(command.Command, MaxNameLength),
                capture.Text(command.Help, MaxSignatureLength), capture.Text(command.Handler, MaxSignatureLength),
                capture.Text(command.Attribution, MaxSignatureLength)));
        }
        if (commands.Count > MaxCommands)
            capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S89B703BF11B560B3", MaxCommands));

        var types = new List<PluginInspectionType>();
        var visitedTypes = 0;
        if (capture.Continue())
        {
            foreach (var type in GetTypes(assembly, capture))
            {
                if (!capture.Continue()) break;
                if (type == null || type.Assembly != assembly) continue;
                if (visitedTypes >= MaxTypes)
                {
                    capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S877B0F719BFB53BA", MaxTypes));
                    break;
                }
                visitedTypes++;

                try { types.Add(CaptureType(type, capture)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { capture.Failure(global::XASlave.Localization.UiMessage.Key("S8367FF8B6F11E253"), exception); }
                if (capture.MemberLimitReached) break;
            }
        }

        token.ThrowIfCancellationRequested();
        return PluginInspectionPresentation.Attach(new PluginInspectionReport(copiedIdentity, Freeze(copiedCommands), Freeze(types), capture.Limitations, capture.Truncated)
        {
            IpcDeclarations = Freeze(capture.IpcDeclarations),
        }, capture.LimitationMessages);
    }

    private static IEnumerable<Type?> GetTypes(Assembly assembly, CaptureState capture)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            capture.Failure(global::XASlave.Localization.UiMessage.Key("S5A075720C6D8B196"), exception);
            foreach (var loaderException in exception.LoaderExceptions.Take(MaxAttributes))
            {
                if (!capture.Continue()) break;
                if (loaderException != null) capture.Failure(global::XASlave.Localization.UiMessage.Key("S41AC4C98F8BD8287"), loaderException);
            }
            if (exception.LoaderExceptions.Length > MaxAttributes)
                capture.Incomplete(global::XASlave.Localization.UiMessage.Key("S051A37FFFB69D84C"));
            return exception.Types;
        }
        catch (Exception exception)
        {
            capture.Failure(global::XASlave.Localization.UiMessage.Key("S2977D6085515E209"), exception);
            return Array.Empty<Type>();
        }
    }

    private static PluginInspectionType CaptureType(Type type, CaptureState capture)
    {
        var typeName = TypeName(type, capture);
        var kind = type.IsInterface ? "interface" : type.IsEnum ? "enum"
            : typeof(MulticastDelegate).IsAssignableFrom(type) ? "delegate" : type.IsValueType ? "struct" : "class";
        var visibility = TypeVisibility(type);
        var labels = new List<string>();
        var baseType = type.BaseType;
        var baseTypeName = baseType == null ? string.Empty : TypeName(baseType, capture);
        var genericParameters = capture.Continue()
            ? GenericParameters(type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes, capture)
            : Array.Empty<string>();
        var attributes = Attributes(type, capture);
        var currentBase = baseType;
        for (var depth = 0; currentBase != null && depth < 32 && capture.Continue(); depth++)
        {
            if (currentBase.FullName == "Dalamud.Interface.Windowing.Window")
            {
                labels.Add(global::XASlave.Localization.UiMessage.Key("S5D593860DD22350B").Original);
                break;
            }
            currentBase = currentBase.BaseType;
            if (depth == 31 && currentBase != null)
                capture.Incomplete(global::XASlave.Localization.UiMessage.Key("SCCC9726724874B3B"));
        }

        var members = new List<PluginInspectionMember>();
        if (capture.Continue())
        {
            // A single reflection call cannot be interrupted; the next budget check reports overruns.
            MemberInfo[] declared;
            try { declared = type.GetMembers(DeclaredMembers); }
            catch (Exception exception)
            {
                capture.Failure(global::XASlave.Localization.UiMessage.Key("SF1D129D3EE82D442"), exception);
                declared = Array.Empty<MemberInfo>();
            }
            foreach (var member in declared)
            {
                if (!capture.Continue()) break;
                if (member is not (FieldInfo or PropertyInfo or EventInfo or MethodBase)) continue;
                if (!capture.ReserveMember()) break;
                try
                {
                    var snapshot = CaptureMember(member, capture);
                    members.Add(snapshot);
                    if (snapshot.CandidateLabels.Count > 0 && labels.Count < 2
                        && !labels.Any(label => label.StartsWith("IPC candidate", StringComparison.Ordinal)))
                        labels.Add(global::XASlave.Localization.UiMessage.Key("SC688547FBF5B403C").Original);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { capture.Failure(global::XASlave.Localization.UiMessage.Key("S1B7FA52A04841C4D"), exception); }
            }
        }

        return new(typeName, kind, visibility, baseTypeName, genericParameters,
            attributes, Freeze(labels), Freeze(members));
    }

    private static PluginInspectionMember CaptureMember(MemberInfo member, CaptureState capture)
    {
        var name = capture.Text(member.Name, MaxNameLength);
        string kind;
        string visibility;
        bool isStatic;
        Type? valueType;
        ParameterInfo[] parameters;
        Type[] generics = Type.EmptyTypes;
        var accessors = new List<string>();

        switch (member)
        {
            case FieldInfo field:
                kind = field.IsLiteral ? "constant" : field.IsInitOnly ? "readonly field" : "field";
                visibility = FieldVisibility(field);
                isStatic = field.IsStatic;
                valueType = field.FieldType;
                parameters = Array.Empty<ParameterInfo>();
                break;
            case PropertyInfo property:
                kind = "property";
                var propertyAccessors = property.GetAccessors(nonPublic: true);
                visibility = AccessorVisibility(propertyAccessors);
                isStatic = propertyAccessors.Any(accessor => accessor.IsStatic);
                valueType = property.PropertyType;
                parameters = property.GetIndexParameters();
                foreach (var accessor in propertyAccessors)
                    accessors.Add(AccessorDescription(accessor, capture));
                break;
            case EventInfo eventInfo:
                kind = "event";
                var eventAccessors = new[] { eventInfo.GetAddMethod(true), eventInfo.GetRemoveMethod(true), eventInfo.GetRaiseMethod(true) }
                    .Where(accessor => accessor != null).Cast<MethodInfo>().ToArray();
                visibility = AccessorVisibility(eventAccessors);
                isStatic = eventAccessors.Any(accessor => accessor.IsStatic);
                valueType = eventInfo.EventHandlerType;
                parameters = Array.Empty<ParameterInfo>();
                foreach (var accessor in eventAccessors)
                    accessors.Add(AccessorDescription(accessor, capture));
                break;
            case MethodBase method:
                kind = method is ConstructorInfo ? "constructor" : "method";
                visibility = MethodVisibility(method);
                isStatic = method.IsStatic;
                valueType = (method as MethodInfo)?.ReturnType;
                parameters = method.GetParameters();
                generics = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
                break;
            default:
                throw new NotSupportedException();
        }

        var returnType = valueType == null ? "void" : TypeName(valueType, capture);
        var parameterSnapshots = Parameters(parameters, capture);
        var genericSnapshots = GenericParameters(generics, capture);
        var genericSuffix = generics.Length == 0 ? string.Empty
            : "<" + string.Join(", ", generics.Take(MaxParameters).Select(argument => capture.Text(argument.Name, MaxNameLength))) + ">";
        var parameterSuffix = member is MethodBase ? "(" + string.Join(", ", parameterSnapshots.Select(ParameterSignature)) + ")"
            : member is PropertyInfo && parameters.Length > 0 ? "[" + string.Join(", ", parameterSnapshots.Select(ParameterSignature)) + "]" : string.Empty;
        var signature = capture.Text($"{visibility} {(isStatic ? "static " : string.Empty)}{returnType} {name}{genericSuffix}{parameterSuffix}", MaxSignatureLength);
        var labels = new List<string>();
        if (IsCallGateType(valueType, capture) || parameters.Take(MaxParameters).Any(parameter => IsCallGateType(parameter.ParameterType, capture)))
            labels.Add(global::XASlave.Localization.UiMessage.Key("S1CCEE5F4AA081370").Original);

        try
        {
            if (capture.IpcDeclarations.Count >= MaxIpcDeclarations)
                capture.Incomplete(global::XASlave.Localization.UiMessage.Key("SB2EB459DCAFEF64D"));
            else
            {
                var declarationIssueCount = capture.IssueCount;
                var declarations = PluginInspectionIpcMetadata.Capture(member, capture.PluginInternalName,
                    capture.Continue, capture.Text, type => TypeName(type, capture), capture.Incomplete);
                var remaining = MaxIpcDeclarations - capture.IpcDeclarations.Count;
                if (declarations.Count > remaining) capture.Incomplete(global::XASlave.Localization.UiMessage.Key("SB5BF967E9FD8627F"));
                foreach (var declaration in declarations.Take(remaining))
                    capture.IpcDeclarations.Add(capture.IssueCount == declarationIssueCount ? declaration
                        : declaration with { SignatureComplete = false });
                if (declarations.Count > 0)
                    labels.Add(global::XASlave.Localization.UiMessage.Key("S0FDD21C7A81B5CFC").Original);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { capture.Failure(global::XASlave.Localization.UiMessage.Key("SDE7CC33B649013CF"), exception); }

        return new(kind, name, signature, visibility, isStatic, returnType, parameterSnapshots,
            genericSnapshots, Freeze(accessors), Attributes(member, capture), Freeze(labels));
    }

    private static IReadOnlyList<PluginInspectionParameter> Parameters(ParameterInfo[] parameters, CaptureState capture)
    {
        var result = new List<PluginInspectionParameter>();
        if (parameters.Length > MaxParameters) capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S584E7D0054D4A5A6", MaxParameters));
        foreach (var parameter in parameters.Take(MaxParameters))
        {
            if (!capture.Continue()) break;
            var direction = parameter.IsOut ? "out" : parameter.ParameterType.IsByRef ? parameter.IsIn ? "in" : "ref" : "value";
            result.Add(new(capture.Text(parameter.Name ?? string.Empty, MaxNameLength),
                TypeName(parameter.ParameterType, capture), direction, parameter.IsOptional, Attributes(parameter, capture)));
        }
        return Freeze(result);
    }

    private static string ParameterSignature(PluginInspectionParameter parameter)
        => $"{(parameter.Direction == "value" ? string.Empty : parameter.Direction + " ")}{parameter.Type} {parameter.Name}{(parameter.Optional ? " [optional]" : string.Empty)}";

    private static IReadOnlyList<string> GenericParameters(Type[] arguments, CaptureState capture)
    {
        var result = new List<string>();
        if (arguments.Length > MaxParameters) capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S584E7D0054D4A5A6", MaxParameters));
        foreach (var argument in arguments.Take(MaxParameters))
        {
            if (!capture.Continue()) break;
            if (!argument.IsGenericParameter) { result.Add(TypeName(argument, capture)); continue; }
            var constraints = new List<string>();
            var flags = argument.GenericParameterAttributes;
            if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0) constraints.Add("class");
            if ((flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) constraints.Add("struct");
            var metadataConstraints = argument.GetGenericParameterConstraints();
            if (metadataConstraints.Length > MaxParameters) capture.Incomplete(global::XASlave.Localization.UiMessage.Key("SA4CF50F3AB1CE822"));
            foreach (var constraint in metadataConstraints.Take(MaxParameters))
            {
                if (!capture.Continue()) break;
                constraints.Add(TypeName(constraint, capture));
            }
            if ((flags & GenericParameterAttributes.DefaultConstructorConstraint) != 0) constraints.Add("new()");
            var variance = (flags & GenericParameterAttributes.Covariant) != 0 ? "out "
                : (flags & GenericParameterAttributes.Contravariant) != 0 ? "in " : string.Empty;
            result.Add(capture.Text(variance + argument.Name
                + (constraints.Count == 0 ? string.Empty : " : " + string.Join(", ", constraints)), MaxSignatureLength));
        }
        return Freeze(result);
    }

    private static IReadOnlyList<string> Attributes(MemberInfo member, CaptureState capture)
    {
        if (!capture.Continue()) return Array.Empty<string>();
        try { return AttributeNames(CustomAttributeData.GetCustomAttributes(member), capture); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { capture.Failure(global::XASlave.Localization.UiMessage.Key("SDB942819D22B469D"), exception); return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> Attributes(ParameterInfo parameter, CaptureState capture)
    {
        if (!capture.Continue()) return Array.Empty<string>();
        try { return AttributeNames(CustomAttributeData.GetCustomAttributes(parameter), capture); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { capture.Failure(global::XASlave.Localization.UiMessage.Key("SA867FADFEBC160A1"), exception); return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> AttributeNames(IList<CustomAttributeData> attributes, CaptureState capture)
    {
        var result = new List<string>();
        if (attributes.Count > MaxAttributes) capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S254A124EE7AC74FF", MaxAttributes));
        foreach (var attribute in attributes.Take(MaxAttributes))
        {
            if (!capture.Continue()) break;
            result.Add(TypeName(attribute.AttributeType, capture));
        }
        return Freeze(result);
    }

    private static string TypeName(Type type, CaptureState capture, int depth = 0)
    {
        if (!capture.Continue()) return "<capture deadline>";
        if (depth >= MaxTypeDepth)
        {
            capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S300AD1332A2CB224", MaxTypeDepth));
            return "<type nesting limit>";
        }
        if (type.HasElementType)
        {
            var element = type.GetElementType();
            var rank = type.IsArray ? type.GetArrayRank() : 0;
            var suffix = type.IsArray ? "[" + new string(',', Math.Min(Math.Max(rank - 1, 0), 32)) + "]"
                : type.IsPointer ? "*" : "&";
            return capture.Text((element == null ? "<unknown element>" : TypeName(element, capture, depth + 1)) + suffix, MaxSignatureLength);
        }
        if (type.IsGenericParameter) return capture.Text(type.Name, MaxNameLength);
        if (!type.IsGenericType) return capture.Text(type.FullName ?? type.Name, MaxSignatureLength);
        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if (arguments.Length > MaxParameters) capture.Incomplete(global::XASlave.Localization.UiMessage.Format("S584E7D0054D4A5A6", MaxParameters));
        return capture.Text((definition.FullName ?? definition.Name) + "<"
            + string.Join(", ", arguments.Take(MaxParameters).Select(argument => TypeName(argument, capture, depth + 1))) + ">", MaxSignatureLength);
    }

    private static bool IsCallGateType(Type? type, CaptureState capture, int depth = 0)
    {
        if (type == null || depth >= MaxTypeDepth || !capture.Continue()) return false;
        var name = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
        if (name == "Dalamud.Plugin.Ipc.ICallGateProvider" || name == "Dalamud.Plugin.Ipc.ICallGateSubscriber"
            || name?.StartsWith("Dalamud.Plugin.Ipc.ICallGateProvider`", StringComparison.Ordinal) == true
            || name?.StartsWith("Dalamud.Plugin.Ipc.ICallGateSubscriber`", StringComparison.Ordinal) == true) return true;
        if (type.HasElementType) return IsCallGateType(type.GetElementType(), capture, depth + 1);
        return type.IsGenericType && type.GetGenericArguments().Take(MaxParameters)
            .Any(argument => IsCallGateType(argument, capture, depth + 1));
    }

    private static string AccessorDescription(MethodInfo method, CaptureState capture)
        => capture.Text($"{MethodVisibility(method)} {(method.IsStatic ? "static " : string.Empty)}{method.Name}", MaxSignatureLength);

    private static string AccessorVisibility(IEnumerable<MethodInfo> methods)
        => string.Join(" / ", methods.Select(MethodVisibility).Distinct(StringComparer.Ordinal));

    private static string TypeVisibility(Type type) => type.IsPublic || type.IsNestedPublic ? "public"
        : type.IsNestedPrivate ? "private" : type.IsNestedFamANDAssem ? "private protected"
        : type.IsNestedFamORAssem ? "protected internal" : type.IsNestedFamily ? "protected" : "internal";

    private static string FieldVisibility(FieldInfo field) => field.IsPublic ? "public" : field.IsPrivate ? "private"
        : field.IsFamilyAndAssembly ? "private protected" : field.IsFamilyOrAssembly ? "protected internal"
        : field.IsFamily ? "protected" : field.IsAssembly ? "internal" : "private scope";

    private static string MethodVisibility(MethodBase method) => method.IsPublic ? "public" : method.IsPrivate ? "private"
        : method.IsFamilyAndAssembly ? "private protected" : method.IsFamilyOrAssembly ? "protected internal"
        : method.IsFamily ? "protected" : method.IsAssembly ? "internal" : "private scope";

    private static ReadOnlyCollection<T> Freeze<T>(List<T> values) => Array.AsReadOnly(values.ToArray());

    private sealed class CaptureState
    {
        private readonly CancellationToken token;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int memberCount;
        private int capturedTextCharacters;
        internal bool Truncated { get; private set; }
        internal bool MemberLimitReached { get; private set; }
        internal int IssueCount { get; private set; }
        internal string PluginInternalName { get; }
        internal List<PluginInspectionIpcDeclaration> IpcDeclarations { get; } = new();
        internal List<global::XASlave.Localization.UiMessage> LimitationMessages { get; } = new()
        {
            global::XASlave.Localization.UiMessage.Key("SE6E19E574C3C233F"),
            global::XASlave.Localization.UiMessage.Key("S04FABBFD8955496E"),
            global::XASlave.Localization.UiMessage.Key("SF5F7E3FCD9BDEAE8"),
            global::XASlave.Localization.UiMessage.Key("SFB230DDC03EAA867"),
            global::XASlave.Localization.UiMessage.Key("S2BF3DE69A9AB95DA"),
            global::XASlave.Localization.UiMessage.Key("S026024E3E6348527"),
            global::XASlave.Localization.UiMessage.Key("S906A1865FCB874D1"),
        };
        internal List<string> Limitations => LimitationMessages.Select(message => message.Original).ToList();

        internal CaptureState(CancellationToken token, string pluginInternalName)
        {
            this.token = token;
            PluginInternalName = pluginInternalName;
        }

        internal bool Continue()
        {
            token.ThrowIfCancellationRequested();
            if (capturedTextCharacters >= MaxCapturedTextCharacters) return false;
            if (clock.Elapsed < CaptureBudget) return true;
            Incomplete(global::XASlave.Localization.UiMessage.Key("S9423B579BB05ACF7"));
            return false;
        }

        internal bool ReserveMember()
        {
            if (memberCount < MaxMembers) { memberCount++; return true; }
            MemberLimitReached = true;
            Incomplete(global::XASlave.Localization.UiMessage.Format("S193D534683B14C31", MaxMembers));
            return false;
        }

        internal string Text(string? text, int maximum)
        {
            if (text == null) return string.Empty;
            var remaining = MaxCapturedTextCharacters - capturedTextCharacters;
            if (remaining <= 0) return string.Empty;
            if (text.Length >= remaining)
            {
                maximum = Math.Min(maximum, remaining);
                Incomplete(global::XASlave.Localization.UiMessage.Key("S49B3019EF19DEC37"));
                capturedTextCharacters = MaxCapturedTextCharacters;
            }
            else capturedTextCharacters += Math.Min(text.Length, maximum);
            if (text.Length <= maximum) return text;
            Incomplete(global::XASlave.Localization.UiMessage.Key("S89A67701F82F2844"));
            var length = maximum - 1;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text[..length] + "…";
        }

        internal void Failure(global::XASlave.Localization.UiMessage scope, Exception exception)
            => Incomplete(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(scope, global::XASlave.Localization.UiMessage.Key("SB3A7321F8FEEABAE")), global::XASlave.Localization.UiMessage.Raw(Text(exception.GetType().FullName ?? exception.GetType().Name, MaxNameLength))), global::XASlave.Localization.UiMessage.Raw(").")));

        internal void Incomplete(global::XASlave.Localization.UiMessage message)
        {
            IssueCount++;
            Truncated = true;
            if (LimitationMessages.Any(existing => string.Equals(existing.Original, message.Original, StringComparison.Ordinal))) return;
            if (LimitationMessages.Count < 63) LimitationMessages.Add(message);
            else if (LimitationMessages.Count == 63) LimitationMessages.Add(global::XASlave.Localization.UiMessage.Key("S69DC87DA3A6DD6F5"));
        }
    }
}


// Presentation metadata is attached to the report identity, never looked up from provider text.
// Raw JSON fields and positional-record equality remain unchanged.
internal static class PluginInspectionPresentation
{
    private sealed record Presentation(global::XASlave.Localization.UiMessage? Status, global::XASlave.Localization.UiMessage[] Limitations);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Presentation> Presentations = new();

    internal static T Attach<T>(T owner, IEnumerable<global::XASlave.Localization.UiMessage> limitations, global::XASlave.Localization.UiMessage? status = null) where T : class
    {
        Presentations.AddOrUpdate(owner, new Presentation(status, limitations.ToArray()));
        return owner;
    }

    internal static void Append(object owner, IEnumerable<global::XASlave.Localization.UiMessage> limitations)
    {
        Presentations.TryGetValue(owner, out var existing);
        Presentations.AddOrUpdate(owner, new Presentation(existing?.Status,
            (existing?.Limitations ?? Array.Empty<global::XASlave.Localization.UiMessage>()).Concat(limitations).ToArray()));
    }

    internal static string Status(object owner, string original)
        => Presentations.TryGetValue(owner, out var presentation)
            ? global::XASlave.Localization.UiMessage.Capture(presentation.Status, original).Display : original;

    internal static IEnumerable<string> Limitations(object owner, IReadOnlyList<string> originals)
    {
        Presentations.TryGetValue(owner, out var presentation);
        for (var index = 0; index < originals.Count; index++)
            yield return presentation != null && index < presentation.Limitations.Length
                ? global::XASlave.Localization.UiMessage.Capture(presentation.Limitations[index], originals[index]).Display : originals[index];
    }
}
