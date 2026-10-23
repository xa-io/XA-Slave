using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XASlave.Services;

public unsafe sealed class AntiAfkService : IDisposable
{
    private const int ResetIntervalMilliseconds = 2 * 60 * 1000;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;
    private long lastResetTick;

    public AntiAfkService(
        IFramework framework,
        IClientState clientState,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.log = log;
        RefreshStatusText();
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            RefreshStatusText();
            return enabled;
        }

        enabled = value;
        lastResetTick = 0;
        UpdateSubscription(enabled);
        RefreshStatusText();
        return enabled;
    }

    public void Dispose()
    {
        enabled = false;
        lastResetTick = 0;
        UpdateSubscription(false);
        RefreshStatusText();
    }

    private void UpdateSubscription(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
            framework.Update += OnFrameworkUpdate;
        else
            framework.Update -= OnFrameworkUpdate;

        subscribed = targetEnabled;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled || !clientState.IsLoggedIn)
            return;

        var now = Environment.TickCount64;
        if (now - lastResetTick < ResetIntervalMilliseconds)
            return;

        try
        {
            var inputTimer = InputTimerModule.Instance();
            if (inputTimer == null)
            {
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S776293309FCD2937"));
                return;
            }

            inputTimer->AfkTimer = 0f;
            inputTimer->ContentInputTimer = 0f;
            inputTimer->InputTimer = 0f;
            if (inputTimer->Status < 0)
                inputTimer->Status = 0;
            lastResetTick = now;
            RefreshStatusText();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Anti-AFK failed while resetting the AFK timer.");
        }
    }

    private void RefreshStatusText()
    {
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (enabled ? global::XASlave.Localization.UiMessage.Key("SA01612A647A35A8F") : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")));
    }
}
