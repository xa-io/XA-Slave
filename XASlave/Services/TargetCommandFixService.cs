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
    private bool requiredByXagman;
    private bool subscribed;

    public TargetCommandFixService(IChatGui chatGui, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.log = log;
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public bool SetEnabled(bool value)
    {
        if (value != enabled)
            enabled = value;

        RefreshEffectiveState();
        return enabled;
    }

    public void SetRequiredByXagman(bool value)
    {
        if (value == requiredByXagman)
            return;

        requiredByXagman = value;
        RefreshEffectiveState();
    }

    public void Dispose()
    {
        enabled = false;
        requiredByXagman = false;
        Unsubscribe();
    }

    private void RefreshEffectiveState()
    {
        var shouldEnable = enabled || requiredByXagman;
        if (!shouldEnable)
        {
            Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        Subscribe();

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (requiredByXagman && !enabled ? global::XASlave.Localization.UiMessage.Key("SDB73313C3B4F07B6") : (requiredByXagman ? global::XASlave.Localization.UiMessage.Key("SFD5A748407AED826") : global::XASlave.Localization.UiMessage.Key("S45E27CAC194E49BA"))));
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
        if (!subscribed || message.IsHandled || message.LogKind != XivChatType.ErrorMessage)
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
