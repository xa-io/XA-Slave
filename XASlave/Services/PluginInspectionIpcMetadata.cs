using System;
using System.Collections.Generic;
using System.Reflection;

namespace XASlave.Services;

internal sealed record PluginInspectionIpcDeclaration(
    string DeclaringType,
    string Member,
    string AttributeType,
    string Role,
    string? ChannelName,
    string ChannelNameTemplate,
    bool? ApplyPrefix,
    string ReturnType,
    IReadOnlyList<PluginInspectionParameter> Parameters,
    IReadOnlyList<string> CallGateTypeArguments,
    bool SignatureComplete,
    IReadOnlyList<string> Notes);

/// <summary>Known IPC attribute metadata only; declarations never establish a live registration.</summary>
internal static class PluginInspectionIpcMetadata
{
    private const string EzIpcAttribute = "ECommons.EzIpcManager.EzIPCAttribute";
    private const string EzIpcEventAttribute = "ECommons.EzIpcManager.EzIPCEventAttribute";
    private const int MaxAttributes = 64;
    private const int MaxNamedArguments = 16;
    private const int MaxParameters = 64;
    private const int MaxNameLength = 512;
    private const int MaxChannelLength = 2048;

    internal static IReadOnlyList<PluginInspectionIpcDeclaration> Capture(MemberInfo member,
        string pluginInternalName, Func<bool> shouldContinue, Func<string?, int, string> captureText,
        Func<Type, string> typeName, Action<global::XASlave.Localization.UiMessage> markIncomplete)
    {
        var result = new List<PluginInspectionIpcDeclaration>();
        if (member is not (MethodInfo or FieldInfo or PropertyInfo) || !shouldContinue())
            return Array.Empty<PluginInspectionIpcDeclaration>();

        // CustomAttributeData reads constructor arguments without constructing the attribute.
        var attributes = CustomAttributeData.GetCustomAttributes(member);
        if (attributes.Count > MaxAttributes)
            markIncomplete(global::XASlave.Localization.UiMessage.Key("SD542956A33CFD8CF"));
        for (var index = 0; index < attributes.Count && index < MaxAttributes && shouldContinue(); index++)
        {
            var attribute = attributes[index];
            var attributeName = attribute.AttributeType.FullName;
            if (attributeName != EzIpcAttribute && attributeName != EzIpcEventAttribute) continue;
            result.Add(CaptureDeclaration(member, attribute, attributeName, pluginInternalName,
                shouldContinue, captureText, typeName, markIncomplete));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static PluginInspectionIpcDeclaration CaptureDeclaration(MemberInfo member,
        CustomAttributeData attribute, string attributeName, string pluginInternalName,
        Func<bool> shouldContinue, Func<string?, int, string> captureText,
        Func<Type, string> typeName, Action<global::XASlave.Localization.UiMessage> markIncomplete)
    {
        var notes = new List<string>
        {
            global::XASlave.Localization.UiMessage.Key("SFB1DB4DF69F9929A").Original,
        };
        var isEvent = attributeName == EzIpcEventAttribute;
        var isMethod = member is MethodInfo;
        var role = isEvent ? isMethod ? "event-subscriber" : "event-publisher"
            : isMethod ? "provider" : "consumer";
        var validAttribute = TryReadAttribute(attribute, isEvent, shouldContinue,
            out var declaredName, out var applyPrefix, out var actionReturnType);
        if (!validAttribute)
        {
            notes.Add(global::XASlave.Localization.UiMessage.Key("S56DF6C6C599B00C3").Original);
            markIncomplete(global::XASlave.Localization.UiMessage.Key("SDC5F2288F0833CB9"));
        }

        string? channelName = null;
        var channelTemplate = string.Empty;
        if (validAttribute && shouldContinue())
        {
            // EzIPC substitutes %m then %p, but its Init prefix is supplied at runtime.
            var rawName = declaredName ?? member.Name;
            // Bound before replacement to avoid constructing oversized substituted strings.
            var bounded = rawName.Length <= MaxChannelLength && member.Name.Length <= MaxNameLength
                && pluginInternalName.Length <= MaxNameLength;
            if (bounded)
            {
                var substituted = rawName.Replace("%m", member.Name, StringComparison.Ordinal)
                    .Replace("%p", pluginInternalName, StringComparison.Ordinal);
                var template = applyPrefix ? "{runtime-prefix}." + substituted : substituted;
                channelTemplate = captureText(template, MaxChannelLength);
                if (!applyPrefix && substituted.Length > 0 && channelTemplate == substituted)
                    channelName = channelTemplate;
                else if (!applyPrefix && substituted.Length == 0)
                    notes.Add(global::XASlave.Localization.UiMessage.Key("S757EEC5E1D794CC1").Original);
                else if (channelTemplate != template)
                    notes.Add(global::XASlave.Localization.UiMessage.Key("S2C9B9DD287D4085F").Original);
            }
            else
            {
                channelTemplate = captureText(rawName, MaxChannelLength);
                notes.Add(global::XASlave.Localization.UiMessage.Key("S64F6152EB9CFA805").Original);
                markIncomplete(global::XASlave.Localization.UiMessage.Key("SFAFF5EE3A92B19E2"));
            }
            if (applyPrefix)
                notes.Add(global::XASlave.Localization.UiMessage.Key("SF635062E18AB5703").Original);
        }

        var signature = GetSignature(member);
        var signatureComplete = validAttribute && signature != null && shouldContinue();
        var parameters = new List<PluginInspectionParameter>();
        var callGateArguments = new List<string>();
        var returnType = string.Empty;
        if (signature != null && shouldContinue())
        {
            if (signature.ContainsGenericParameters)
            {
                signatureComplete = false;
                notes.Add(global::XASlave.Localization.UiMessage.Key("S4229F577695D689C").Original);
            }
            var parameterInfos = signature.GetParameters();
            if (parameterInfos.Length > MaxParameters)
            {
                signatureComplete = false;
                markIncomplete(global::XASlave.Localization.UiMessage.Key("S3B17B0A8C24AE618"));
            }
            for (var index = 0; index < parameterInfos.Length && index < MaxParameters; index++)
            {
                if (!shouldContinue()) { signatureComplete = false; break; }
                var parameter = parameterInfos[index];
                var parameterType = typeName(parameter.ParameterType);
                var direction = parameter.IsOut ? "out" : parameter.ParameterType.IsByRef
                    ? parameter.IsIn ? "in" : "ref" : "value";
                parameters.Add(new(captureText(parameter.Name, MaxNameLength), parameterType,
                    direction, parameter.IsOptional, Array.Empty<string>()));
                callGateArguments.Add(parameterType);
                if (parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer || parameter.ParameterType.IsFunctionPointer
                    || parameter.ParameterType.IsByRefLike || parameter.ParameterType.ContainsGenericParameters)
                    signatureComplete = false;
            }
            returnType = typeName(signature.ReturnType);
            if (isEvent && signature.ReturnType != typeof(void))
            {
                signatureComplete = false;
                notes.Add(global::XASlave.Localization.UiMessage.Key("SC554817A9417DE4A").Original);
            }
            if (signature.ReturnType.IsByRef || signature.ReturnType.IsPointer || signature.ReturnType.IsFunctionPointer
                || signature.ReturnType.IsByRefLike || signature.ReturnType.ContainsGenericParameters)
                signatureComplete = false;
            if (signature.ReturnType == typeof(void) && (actionReturnType == typeof(void)
                || actionReturnType.IsByRef || actionReturnType.IsPointer || actionReturnType.IsFunctionPointer
                || actionReturnType.IsByRefLike || actionReturnType.ContainsGenericParameters))
                signatureComplete = false;
            if (shouldContinue() && validAttribute)
                callGateArguments.Add(typeName(signature.ReturnType == typeof(void)
                    ? actionReturnType : signature.ReturnType));
            else signatureComplete = false;
            if (!shouldContinue()) signatureComplete = false;
        }
        else
        {
            signatureComplete = false;
            notes.Add(global::XASlave.Localization.UiMessage.Key("S2A9D88B7E7C23C90").Original);
        }

        return new(member.DeclaringType == null ? string.Empty : typeName(member.DeclaringType),
            captureText(member.Name, MaxNameLength), captureText(attributeName, MaxNameLength), role,
            channelName, channelTemplate, validAttribute ? applyPrefix : null, returnType,
            Array.AsReadOnly(parameters.ToArray()), Array.AsReadOnly(callGateArguments.ToArray()),
            signatureComplete, Array.AsReadOnly(notes.ToArray()));
    }

    private static MethodInfo? GetSignature(MemberInfo member)
    {
        if (member is MethodInfo method) return method;
        var valueType = member is FieldInfo field ? field.FieldType
            : member is PropertyInfo property ? property.PropertyType : null;
        return valueType != null && typeof(MulticastDelegate).IsAssignableFrom(valueType)
            ? valueType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance) : null;
    }

    private static bool TryReadAttribute(CustomAttributeData attribute, bool isEvent,
        Func<bool> shouldContinue, out string? name, out bool applyPrefix, out Type actionReturnType)
    {
        name = null;
        applyPrefix = true;
        actionReturnType = typeof(object);
        var arguments = attribute.ConstructorArguments;
        // Known ECommons forms: (string, bool, Type), optionally followed by SafeWrapper.
        if (arguments.Count != 3 && (isEvent || arguments.Count != 4)) return false;
        if (!TryString(arguments[0], out name) || !TryBoolean(arguments[1], out applyPrefix)
            || !TryType(arguments[2], out actionReturnType)) return false;
        if (arguments.Count == 4 && !IsWrapper(arguments[3])) return false;
        var namedArguments = attribute.NamedArguments;
        if (namedArguments.Count > MaxNamedArguments) return false;
        for (var index = 0; index < namedArguments.Count; index++)
        {
            if (!shouldContinue()) return false;
            var argument = namedArguments[index];
            // Read only allowlisted attribute metadata, never a reflected instance field/getter.
            switch (argument.MemberName)
            {
                case "IPCName":
                    if (!TryString(argument.TypedValue, out name)) return false;
                    break;
                case "ApplyPrefix":
                    if (!TryBoolean(argument.TypedValue, out applyPrefix)) return false;
                    break;
                case "ActionLastGenericType":
                    // A constructor null means object, but a named field null overwrites that default.
                    if (argument.TypedValue.Value == null || !TryType(argument.TypedValue, out actionReturnType)) return false;
                    break;
                case "Wrapper" when !isEvent:
                    if (!IsWrapper(argument.TypedValue)) return false;
                    break;
                default:
                    return false;
            }
        }
        return shouldContinue();
    }

    private static bool TryString(CustomAttributeTypedArgument argument, out string? value)
    {
        value = argument.Value as string;
        return argument.ArgumentType == typeof(string) && (argument.Value == null || value != null);
    }

    private static bool TryBoolean(CustomAttributeTypedArgument argument, out bool value)
    {
        value = argument.Value is bool result && result;
        return argument.ArgumentType == typeof(bool) && argument.Value is bool;
    }

    private static bool TryType(CustomAttributeTypedArgument argument, out Type value)
    {
        value = argument.Value as Type ?? typeof(object);
        return argument.ArgumentType == typeof(Type) && (argument.Value == null || argument.Value is Type);
    }

    private static bool IsWrapper(CustomAttributeTypedArgument argument)
        => argument.ArgumentType.FullName == "ECommons.EzIpcManager.SafeWrapper"
            && argument.Value is int;
}
