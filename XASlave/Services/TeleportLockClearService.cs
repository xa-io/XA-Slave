using System;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

public unsafe sealed class TeleportLockClearService : IDisposable
{
    private const uint TeleportLockLogMessageId = 1665;
    private const uint TeleportGeneralActionId = 7;

    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;
    private long lastRetryTick;

    public TeleportLockClearService(IChatGui chatGui, IPluginLog log)
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
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            UpdateSubscription(false);
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        enabled = true;
        UpdateSubscription(true);
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S8F04415D023F7CDD"));
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        UpdateSubscription(false);
    }

    private void UpdateSubscription(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
            chatGui.LogMessage += OnLogMessage;
        else
            chatGui.LogMessage -= OnLogMessage;

        subscribed = targetEnabled;
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (!enabled || message.LogMessageId != TeleportLockLogMessageId || !Plugin.ClientState.IsLoggedIn)
            return;

        var now = Environment.TickCount64;
        if (now - lastRetryTick < 750)
            return;

        try
        {
            message.PreventOriginal();

            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return;

            actionManager->UseAction(ActionType.GeneralAction, TeleportGeneralActionId);
            lastRetryTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Clear Teleportation Lock failed while retrying Teleport.");
        }
    }
}
