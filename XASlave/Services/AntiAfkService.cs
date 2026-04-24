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

    public string StatusText { get; private set; } = "Disabled";

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
                StatusText = "Enabled - waiting for the local AFK timer module.";
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
        StatusText = enabled
            ? "Enabled - resets the local AFK timer every 2 min."
            : "Disabled";
    }
}
