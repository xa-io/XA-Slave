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

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            UpdateSubscription(false);
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        UpdateSubscription(true);
        StatusText = "Enabled - teleport lock log 1665 is suppressed and Teleport is retried locally.";
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
