using System;
using System.Collections.Generic;

namespace XASlave.Services;

public enum PluginBuilderControlKind { Info, Action }
public enum PluginBuilderSourceKind { IpcFunction, IpcAction, Command, ReflectionField, ReflectionProperty, ReflectionMethod }

public sealed class PluginBuilderArgument
{
    public string Value { get; set; } = string.Empty;
    public bool UseCurrentCharacterId { get; set; }
    public bool UseCurrentCharacterName { get; set; }
}

public sealed class PluginBuilderSource
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public PluginBuilderSourceKind Kind { get; set; }
    public string PluginName { get; set; } = string.Empty;
    public string ModuleVersionId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string DeclaringType { get; set; } = string.Empty;
    public int MetadataToken { get; set; }
    public bool Static { get; set; }
    public List<string> FieldPath { get; set; } = new();
    public List<string> ParameterTypes { get; set; } = new();
    public List<string> ParameterNames { get; set; } = new();
    public string ReturnType { get; set; } = string.Empty;
    public bool CanRead { get; set; }
    public bool CanExecute { get; set; }
    public string UnsupportedReason { get; set; } = string.Empty;
    internal global::XASlave.Localization.UiMessage? UnsupportedMessage { get; set; }
    internal global::XASlave.Localization.UiMessage UnsupportedPresentation => global::XASlave.Localization.UiMessage.Capture(UnsupportedMessage, UnsupportedReason);
    internal string LocalizedUnsupportedReason => UnsupportedPresentation.Display;
    public bool RequiresReflectionConfirmation => Kind is PluginBuilderSourceKind.ReflectionField or PluginBuilderSourceKind.ReflectionProperty or PluginBuilderSourceKind.ReflectionMethod;
}

public sealed class PluginBuilderControlDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PageId { get; set; }
    public string Label { get; set; } = string.Empty;
    public PluginBuilderControlKind Kind { get; set; }
    public PluginBuilderSource Source { get; set; } = new();
    public List<PluginBuilderArgument> Arguments { get; set; } = new();
    public int AutoRefreshSeconds { get; set; }
    public bool ReflectionReadConfirmed { get; set; }
    public string MemberPath { get; set; } = string.Empty;
    public bool SameLine { get; set; }
    public int Width { get; set; }
}

public sealed class PluginBuilderPageDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Overview";
}

public sealed class PluginBuilderWindowDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Plugin Window";
    // Legacy discovery hints; each bound control owns its exact plugin and module identity.
    public string PluginName { get; set; } = string.Empty;
    public string ModuleVersionId { get; set; } = string.Empty;
    public string CommandName { get; set; } = string.Empty;
    public bool ShowTimestamps { get; set; }
    public bool ShowSidebar { get; set; } = true;
    public int SidebarWidth { get; set; } = 160;
    public List<PluginBuilderPageDefinition> Pages { get; set; } = new();
    public List<PluginBuilderControlDefinition> Controls { get; set; } = new();
    public bool OpenOnLoad { get; set; }
}

internal sealed record PluginBuilderCatalog(string PluginName, string ModuleVersionId, IReadOnlyList<PluginBuilderSource> Sources);
internal sealed record PluginBuilderSample(string Status, string Text, DateTime? TimestampUtc)
{
    private sealed record Presentation(global::XASlave.Localization.UiMessage? StatusMessage, global::XASlave.Localization.UiMessage? TextMessage);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PluginBuilderSample, Presentation> Presentations = new();
    private static readonly Presentation EmptyPresentation = new(null, null);
    private Presentation Messages => Presentations.TryGetValue(this, out var value) ? value : EmptyPresentation;
    internal global::XASlave.Localization.UiMessage? StatusMessage
    {
        get => Messages.StatusMessage;
        init => Presentations.AddOrUpdate(this, Messages with { StatusMessage = value });
    }
    internal global::XASlave.Localization.UiMessage? TextMessage
    {
        get => Messages.TextMessage;
        init => Presentations.AddOrUpdate(this, Messages with { TextMessage = value });
    }
    private PluginBuilderSample(PluginBuilderSample original)
    {
        Status = original.Status;
        Text = original.Text;
        TimestampUtc = original.TimestampUtc;
        if (Presentations.TryGetValue(original, out var presentation)) Presentations.Add(this, presentation);
    }
    internal string LocalizedStatus => global::XASlave.Localization.UiMessage.Resolve(StatusMessage, Status);
    internal string LocalizedText => global::XASlave.Localization.UiMessage.Resolve(TextMessage, Text);
}
