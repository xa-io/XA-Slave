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
            capture.Incomplete($"Command list limited to {MaxCommands} entries.");

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
                    capture.Incomplete($"Type list limited to {MaxTypes} entries from the selected assembly.");
                    break;
                }
                visitedTypes++;

                try { types.Add(CaptureType(type, capture)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { capture.Failure("Type metadata", exception); }
                if (capture.MemberLimitReached) break;
            }
        }

        token.ThrowIfCancellationRequested();
        return new(copiedIdentity, Freeze(copiedCommands), Freeze(types), capture.Limitations, capture.Truncated)
        {
            IpcDeclarations = Freeze(capture.IpcDeclarations),
        };
    }

    private static IEnumerable<Type?> GetTypes(Assembly assembly, CaptureState capture)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            capture.Failure("Assembly type enumeration returned a partial list", exception);
            foreach (var loaderException in exception.LoaderExceptions.Take(MaxAttributes))
            {
                if (!capture.Continue()) break;
                if (loaderException != null) capture.Failure("Type loader", loaderException);
            }
            if (exception.LoaderExceptions.Length > MaxAttributes)
                capture.Incomplete("Additional type loader diagnostics omitted.");
            return exception.Types;
        }
        catch (Exception exception)
        {
            capture.Failure("Assembly type enumeration", exception);
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
                labels.Add("Window candidate (unverified): derives from Dalamud.Interface.Windowing.Window; no window instance or embedding support was tested.");
                break;
            }
            currentBase = currentBase.BaseType;
            if (depth == 31 && currentBase != null)
                capture.Incomplete("Base-type evidence traversal limited to 32 levels.");
        }

        var members = new List<PluginInspectionMember>();
        if (capture.Continue())
        {
            // A single reflection call cannot be interrupted; the next budget check reports overruns.
            MemberInfo[] declared;
            try { declared = type.GetMembers(DeclaredMembers); }
            catch (Exception exception)
            {
                capture.Failure("Declared member enumeration", exception);
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
                        labels.Add("IPC candidate (unverified): members reference a call-gate contract or known IPC declaration; consult ipc and ipcDeclarations for separate registry and metadata evidence.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { capture.Failure("Member metadata", exception); }
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
            labels.Add("IPC contract reference (unverified): signature contains Dalamud.Plugin.Ipc.ICallGateProvider/ICallGateSubscriber; no channel or callable registration is established.");

        try
        {
            if (capture.IpcDeclarations.Count >= MaxIpcDeclarations)
                capture.Incomplete("IPC declarations limited to 2048 entries; ordinary member metadata continues.");
            else
            {
                var declarationIssueCount = capture.IssueCount;
                var declarations = PluginInspectionIpcMetadata.Capture(member, capture.PluginInternalName,
                    capture.Continue, capture.Text, type => TypeName(type, capture), capture.Incomplete);
                var remaining = MaxIpcDeclarations - capture.IpcDeclarations.Count;
                if (declarations.Count > remaining) capture.Incomplete("IPC declarations limited to 2048 entries.");
                foreach (var declaration in declarations.Take(remaining))
                    capture.IpcDeclarations.Add(capture.IssueCount == declarationIssueCount ? declaration
                        : declaration with { SignatureComplete = false });
                if (declarations.Count > 0)
                    labels.Add("EzIPC declaration (unverified): see ipcDeclarations for role, channel template and signature; see ipc for observed registrations.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { capture.Failure("IPC declaration metadata", exception); }

        return new(kind, name, signature, visibility, isStatic, returnType, parameterSnapshots,
            genericSnapshots, Freeze(accessors), Attributes(member, capture), Freeze(labels));
    }

    private static IReadOnlyList<PluginInspectionParameter> Parameters(ParameterInfo[] parameters, CaptureState capture)
    {
        var result = new List<PluginInspectionParameter>();
        if (parameters.Length > MaxParameters) capture.Incomplete($"Parameter or generic-argument lists limited to {MaxParameters} entries.");
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
        if (arguments.Length > MaxParameters) capture.Incomplete($"Parameter or generic-argument lists limited to {MaxParameters} entries.");
        foreach (var argument in arguments.Take(MaxParameters))
        {
            if (!capture.Continue()) break;
            if (!argument.IsGenericParameter) { result.Add(TypeName(argument, capture)); continue; }
            var constraints = new List<string>();
            var flags = argument.GenericParameterAttributes;
            if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0) constraints.Add("class");
            if ((flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) constraints.Add("struct");
            var metadataConstraints = argument.GetGenericParameterConstraints();
            if (metadataConstraints.Length > MaxParameters) capture.Incomplete("Generic constraint list truncated.");
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
        catch (Exception exception) { capture.Failure("Attribute metadata", exception); return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> Attributes(ParameterInfo parameter, CaptureState capture)
    {
        if (!capture.Continue()) return Array.Empty<string>();
        try { return AttributeNames(CustomAttributeData.GetCustomAttributes(parameter), capture); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { capture.Failure("Parameter attribute metadata", exception); return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> AttributeNames(IList<CustomAttributeData> attributes, CaptureState capture)
    {
        var result = new List<string>();
        if (attributes.Count > MaxAttributes) capture.Incomplete($"Attribute lists limited to {MaxAttributes} entries.");
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
            capture.Incomplete($"Type-signature nesting limited to {MaxTypeDepth} levels.");
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
        if (arguments.Length > MaxParameters) capture.Incomplete($"Parameter or generic-argument lists limited to {MaxParameters} entries.");
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
        internal List<string> Limitations { get; } = new()
        {
            "Metadata inventory only: no inspected getters, constructors, methods, attribute constructors, or field values were evaluated. Discovered members are unverified and are not automatically safe or callable controls.",
            "Only the selected assembly is enumerated. Dependency types appear as signatures or base-type evidence; their members are not recursively inspected.",
            "Command attribution is supplied by the host snapshot and can be unknown or ambiguous. Subcommands parsed inside handlers are not enumerated.",
            "IPC member labels and EzIPC declarations are metadata evidence only. The separate ipc snapshot records observed registrations; neither source proves safe invocation, semantics or future availability.",
            "Window candidates use base-type evidence only. No live window, draw delegate, rendering, or embedding support was tested.",
            "Capture has cooperative five-second and cancellation checks. Individual reflection calls, including GetTypes/GetMembers, cannot be interrupted and may exceed that deadline.",
            "Attribute type names and allowlisted EzIPC declaration arguments are captured through CustomAttributeData without constructing attributes. Runtime object/configuration values are omitted.",
        };

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
            Incomplete("Cooperative five-second metadata capture deadline reached; remaining metadata was omitted.");
            return false;
        }

        internal bool ReserveMember()
        {
            if (memberCount < MaxMembers) { memberCount++; return true; }
            MemberLimitReached = true;
            Incomplete($"Member list limited to {MaxMembers} entries across the selected assembly.");
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
                Incomplete("Cumulative metadata text limit reached; remaining metadata was omitted.");
                capturedTextCharacters = MaxCapturedTextCharacters;
            }
            else capturedTextCharacters += Math.Min(text.Length, maximum);
            if (text.Length <= maximum) return text;
            Incomplete("Long names, help text, or signatures were truncated to the catalog text bounds.");
            var length = maximum - 1;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text[..length] + "…";
        }

        internal void Failure(string scope, Exception exception)
            => Incomplete(scope + " unavailable (" + Text(exception.GetType().FullName ?? exception.GetType().Name, MaxNameLength) + ").");

        internal void Incomplete(string message)
        {
            IssueCount++;
            Truncated = true;
            if (Limitations.Contains(message, StringComparer.Ordinal)) return;
            if (Limitations.Count < 63) Limitations.Add(message);
            else if (Limitations.Count == 63) Limitations.Add("Additional metadata diagnostics omitted after the diagnostic limit.");
        }
    }
}
