using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XASlave.Services;

/// <summary>Writes the inspection schema without reflection over protected model metadata.</summary>
internal static class PluginInspectionJson
{
    internal static string Serialize(PluginInspectionReport report)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            WriteReport(writer, report);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static void WriteReport(Utf8JsonWriter writer, PluginInspectionReport? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", value.SchemaVersion);
        writer.WritePropertyName("identity");
        WriteIdentity(writer, value.Identity);
        WriteArray(writer, "commands", value.Commands, WriteCommand);
        WriteArray(writer, "types", value.Types, WriteType);
        WriteStrings(writer, "preferredControlOrder", value.PreferredControlOrder);
        writer.WritePropertyName("ipc");
        WriteIpcSnapshot(writer, value.Ipc);
        WriteArray(writer, "ipcDeclarations", value.IpcDeclarations, WriteIpcDeclaration);
        WriteStrings(writer, "limitations", value.Limitations);
        writer.WriteBoolean("truncated", value.Truncated);
        writer.WriteBoolean("commandCaptureIncomplete", value.CommandCaptureIncomplete);
        writer.WriteBoolean("hasMainUi", value.HasMainUi);
        writer.WriteBoolean("hasConfigUi", value.HasConfigUi);
        writer.WriteString("assemblyVersion", value.AssemblyVersion);
        writer.WriteBoolean("partial", value.Partial);
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, PluginInspectionIdentity? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("internalName", value.InternalName);
        writer.WriteString("displayName", value.DisplayName);
        writer.WriteString("version", value.Version);
        writer.WriteString("assemblyName", value.AssemblyName);
        writer.WriteString("moduleVersionId", value.ModuleVersionId);
        writer.WriteString("capturedUtc", value.CapturedUtc);
        writer.WriteEndObject();
    }

    private static void WriteCommand(Utf8JsonWriter writer, PluginInspectionCommand? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("command", value.Command);
        writer.WriteString("help", value.Help);
        writer.WriteString("handler", value.Handler);
        writer.WriteString("attribution", value.Attribution);
        writer.WriteEndObject();
    }

    private static void WriteParameter(Utf8JsonWriter writer, PluginInspectionParameter? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("type", value.Type);
        writer.WriteString("direction", value.Direction);
        writer.WriteBoolean("optional", value.Optional);
        WriteStrings(writer, "attributes", value.Attributes);
        writer.WriteEndObject();
    }

    private static void WriteMember(Utf8JsonWriter writer, PluginInspectionMember? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        writer.WriteString("name", value.Name);
        writer.WriteString("signature", value.Signature);
        writer.WriteString("visibility", value.Visibility);
        writer.WriteBoolean("static", value.Static);
        writer.WriteString("returnType", value.ReturnType);
        WriteArray(writer, "parameters", value.Parameters, WriteParameter);
        WriteStrings(writer, "genericParameters", value.GenericParameters);
        WriteStrings(writer, "accessors", value.Accessors);
        WriteStrings(writer, "attributes", value.Attributes);
        WriteStrings(writer, "candidateLabels", value.CandidateLabels);
        writer.WriteEndObject();
    }

    private static void WriteType(Utf8JsonWriter writer, PluginInspectionType? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("kind", value.Kind);
        writer.WriteString("visibility", value.Visibility);
        writer.WriteString("baseType", value.BaseType);
        WriteStrings(writer, "genericParameters", value.GenericParameters);
        WriteStrings(writer, "attributes", value.Attributes);
        WriteStrings(writer, "candidateLabels", value.CandidateLabels);
        WriteArray(writer, "members", value.Members, WriteMember);
        writer.WriteEndObject();
    }

    private static void WriteIpcSnapshot(Utf8JsonWriter writer, PluginInspectionIpcSnapshot? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("status", value.Status);
        writer.WriteString("hostAssemblyVersion", value.HostAssemblyVersion);
        writer.WriteString("capturedUtc", value.CapturedUtc);
        writer.WriteBoolean("incomplete", value.Incomplete);
        WriteArray(writer, "channels", value.Channels, WriteIpcChannel);
        WriteStrings(writer, "limitations", value.Limitations);
        writer.WriteEndObject();
    }

    private static void WriteIpcChannel(Utf8JsonWriter writer, PluginInspectionIpcChannel? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteBoolean("nameComplete", value.NameComplete);
        writer.WriteBoolean("hasFunction", value.HasFunction);
        writer.WriteBoolean("hasAction", value.HasAction);
        writer.WriteNumber("subscriptionCount", value.SubscriptionCount);
        writer.WriteBoolean("attributedToSelectedPlugin", value.AttributedToSelectedPlugin);
        writer.WriteBoolean("selectedPluginSubscribes", value.SelectedPluginSubscribes);
        writer.WritePropertyName("function");
        WriteIpcDelegate(writer, value.Function);
        writer.WritePropertyName("action");
        WriteIpcDelegate(writer, value.Action);
        WriteArray(writer, "subscriptions", value.Subscriptions, WriteIpcDelegate);
        writer.WriteString("registrationState", value.RegistrationState);
        writer.WriteEndObject();
    }

    private static void WriteIpcDelegate(Utf8JsonWriter writer, PluginInspectionIpcDelegate? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("delegateType", value.DelegateType);
        writer.WriteString("invokeSignature", value.InvokeSignature);
        writer.WriteString("returnType", value.ReturnType);
        writer.WriteBoolean("signatureComplete", value.SignatureComplete);
        WriteArray(writer, "parameters", value.Parameters, WriteIpcParameter);
        WriteArray(writer, "handlers", value.Handlers, WriteIpcHandler);
        writer.WriteBoolean("attributedToSelectedPlugin", value.AttributedToSelectedPlugin);
        writer.WriteEndObject();
    }

    private static void WriteIpcParameter(Utf8JsonWriter writer, PluginInspectionIpcParameter? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("type", value.Type);
        writer.WriteString("direction", value.Direction);
        writer.WriteBoolean("optional", value.Optional);
        writer.WriteEndObject();
    }

    private static void WriteIpcHandler(Utf8JsonWriter writer, PluginInspectionIpcHandler? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("method", value.Method);
        writer.WriteString("methodAssembly", value.MethodAssembly);
        writer.WriteString("methodModuleVersionId", value.MethodModuleVersionId);
        writer.WriteString("targetType", value.TargetType);
        writer.WriteString("targetAssembly", value.TargetAssembly);
        writer.WriteString("targetModuleVersionId", value.TargetModuleVersionId);
        writer.WriteBoolean("attributedToSelectedPlugin", value.AttributedToSelectedPlugin);
        writer.WriteString("attribution", value.Attribution);
        writer.WriteEndObject();
    }

    private static void WriteIpcDeclaration(Utf8JsonWriter writer, PluginInspectionIpcDeclaration? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("declaringType", value.DeclaringType);
        writer.WriteString("member", value.Member);
        writer.WriteString("attributeType", value.AttributeType);
        writer.WriteString("role", value.Role);
        writer.WriteString("channelName", value.ChannelName);
        writer.WriteString("channelNameTemplate", value.ChannelNameTemplate);
        if (value.ApplyPrefix.HasValue)
            writer.WriteBoolean("applyPrefix", value.ApplyPrefix.Value);
        else
            writer.WriteNull("applyPrefix");
        writer.WriteString("returnType", value.ReturnType);
        WriteArray(writer, "parameters", value.Parameters, WriteParameter);
        WriteStrings(writer, "callGateTypeArguments", value.CallGateTypeArguments);
        writer.WriteBoolean("signatureComplete", value.SignatureComplete);
        WriteStrings(writer, "notes", value.Notes);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        writer.WritePropertyName(name);
        if (values == null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteArray<T>(Utf8JsonWriter writer, string name, IReadOnlyList<T>? values,
        Action<Utf8JsonWriter, T> writeItem)
    {
        writer.WritePropertyName(name);
        if (values == null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var value in values)
            writeItem(writer, value);
        writer.WriteEndArray();
    }
}
