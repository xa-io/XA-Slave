using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class TargetCommandFixService : IDisposable
{
    private static readonly Regex[] TargetErrorPatterns =
    [
        new(@"^[\u201c""](?<name>.+)[\u201d""] is not a valid target name\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^Der Unterbefehl \[Name des Ziels\] an der \d+\. Stelle des Textkommandos \((?<name>.+)\) ist fehlerhaft\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^Le \d+er? argument .+ est incorrect \((?<name>.*?)\)\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;

    public TargetCommandFixService(IChatGui chatGui, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        Subscribe();
        enabled = true;
        StatusText = "Enabled - failed /target lookups select the closest targetable matching actor.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        chatGui.CheckMessageHandled += OnChatMessageHandled;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        chatGui.CheckMessageHandled -= OnChatMessageHandled;
        subscribed = false;
    }

    private void OnChatMessageHandled(IHandleableChatMessage message)
    {
        if (!enabled || message.IsHandled || message.LogKind != XivChatType.ErrorMessage)
            return;

        var requestedName = ExtractTargetName(message.Message.TextValue);
        if (string.IsNullOrWhiteSpace(requestedName))
            return;

        if (!AddonHelper.TryTargetByName(requestedName, out var matchedName))
            return;

        message.PreventOriginal();
        log.Debug("[XASlave] /target fix selected {MatchedName} for failed target lookup {RequestedName}.", matchedName, requestedName);
    }

    private static string ExtractTargetName(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        foreach (var pattern in TargetErrorPatterns)
        {
            var match = pattern.Match(message);
            if (match.Success)
                return match.Groups["name"].Value.Trim();
        }

        return string.Empty;
    }
}
