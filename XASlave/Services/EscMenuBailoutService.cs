using System;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class EscMenuBailoutService : IDisposable
{
    private const string WatchedAddonName = "SystemMenu";

    private readonly IFramework framework;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;
    private DateTime? visibleSinceUtc;
    private long lastCloseAttemptTick;
    private int timeoutSeconds = 5;

    public EscMenuBailoutService(IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        UpdateStatusText();
    }

    public static int[] TimeoutOptions { get; } = { 1, 2, 3, 4, 5, 10, 15};

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public void ApplyConfiguration(int seconds)
    {
        timeoutSeconds = NormalizeTimeoutSeconds(seconds);
        UpdateStatusText();
    }

    public static int NormalizeTimeoutSeconds(int seconds)
    {
        var best = TimeoutOptions[0];
        var bestDistance = Math.Abs(seconds - best);
        for (var i = 1; i < TimeoutOptions.Length; i++)
        {
            var candidate = TimeoutOptions[i];
            var distance = Math.Abs(seconds - candidate);
            if (distance >= bestDistance)
                continue;

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        enabled = value;
        ResetState();
        UpdateSubscription(enabled);
        UpdateStatusText();
        return enabled;
    }

    public void Dispose()
    {
        enabled = false;
        ResetState();
        UpdateSubscription(false);
        UpdateStatusText();
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
        if (!enabled || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
        {
            ResetState();
            return;
        }

        if (!AddonHelper.IsAddonVisible(WatchedAddonName))
        {
            ResetState();
            return;
        }

        var now = DateTime.UtcNow;
        visibleSinceUtc ??= now;
        if ((now - visibleSinceUtc.Value).TotalSeconds < timeoutSeconds)
            return;

        var currentTick = Environment.TickCount64;
        if (currentTick - lastCloseAttemptTick < 500)
            return;

        try
        {
            AddonHelper.CloseAddon(WatchedAddonName);
            lastCloseAttemptTick = currentTick;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Bailout ESC Menu failed while closing SystemMenu.");
        }
    }

    private void ResetState()
    {
        visibleSinceUtc = null;
        lastCloseAttemptTick = 0;
    }

    private void UpdateStatusText()
    {
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (enabled ? global::XASlave.Localization.UiMessage.Format("SF74935C447A21F54", timeoutSeconds) : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")));
    }
}
