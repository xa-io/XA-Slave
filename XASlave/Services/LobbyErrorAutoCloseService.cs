using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class LobbyErrorAutoCloseService : IDisposable
{
    private const string DialogueAddonName = "Dialogue";
    private const int TemporaryMonitorSeconds = 10;
    private const int ClickThrottleMilliseconds = 400;
    private static readonly string[] DialogueMarkers =
    [
        "90000",
        "90001",
        "90002",
        "90003",
        "90004",
        "90005",
        "90006",
        "90007",
        "2002",
        "3050",
        "3088",
        "3102",
        "5006",
        "Connection with the server was lost.",
        "You are still logged into the game.",
        "Please allow a few moments for the logout process to complete.",
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;

    private bool enabled;
    private bool subscribed;
    private DateTime temporaryMonitorUntilUtc = DateTime.MinValue;
    private DateTime lastConfirmAttemptUtc = DateTime.MinValue;

    public LobbyErrorAutoCloseService(IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            UpdateSubscription();
            StatusText = "Disabled";
            return false;
        }

        try
        {
            enabled = true;
            UpdateSubscription();
            StatusText = "Enabled - disconnect and lobby Dialogue popups are auto-confirmed locally.";
            return true;
        }
        catch (Exception ex)
        {
            enabled = false;
            temporaryMonitorUntilUtc = DateTime.MinValue;
            UpdateSubscription();
            StatusText = "Unavailable - failed to arm the lobby Dialogue monitor.";
            log.Warning(ex, "[XASlave] Failed to enable Auto Close Lobby Errors.");
            return false;
        }
    }

    public void ArmTemporaryMonitor()
    {
        var nextExpiry = DateTime.UtcNow.AddSeconds(TemporaryMonitorSeconds);
        if (nextExpiry > temporaryMonitorUntilUtc)
            temporaryMonitorUntilUtc = nextExpiry;

        try
        {
            UpdateSubscription();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to arm the temporary lobby Dialogue monitor for Instant Logout.");
        }
    }

    public void Dispose()
    {
        enabled = false;
        temporaryMonitorUntilUtc = DateTime.MinValue;
        UpdateSubscription();
    }

    private bool IsTemporaryMonitorActive()
    {
        return DateTime.UtcNow < temporaryMonitorUntilUtc;
    }

    private void UpdateSubscription()
    {
        var shouldSubscribe = enabled || IsTemporaryMonitorActive();
        if (subscribed == shouldSubscribe)
            return;

        if (shouldSubscribe)
            addonLifecycle.RegisterListener(AddonEvent.PreDraw, DialogueAddonName, OnDialogueAddon);
        else
            addonLifecycle.UnregisterListener(OnDialogueAddon);

        subscribed = shouldSubscribe;
    }

    private void OnDialogueAddon(AddonEvent _, AddonArgs args)
    {
        if (args.Addon.IsNull)
            return;

        if (!enabled && !IsTemporaryMonitorActive())
        {
            UpdateSubscription();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastConfirmAttemptUtc).TotalMilliseconds < ClickThrottleMilliseconds)
            return;

        try
        {
            if (!ShouldConfirmDialogue())
                return;

            if (!AddonHelper.ClickAddonText(DialogueAddonName, "OK"))
                return;

            lastConfirmAttemptUtc = now;
            log.Information("[XASlave] Auto-confirmed a disconnect/lobby Dialogue popup.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Close Lobby Errors failed while handling the Dialogue popup.");
        }
    }

    private static bool ShouldConfirmDialogue()
    {
        if (!AddonHelper.IsAddonReady(DialogueAddonName) || !AddonHelper.AddonHasText(DialogueAddonName, "OK"))
            return false;

        foreach (var marker in DialogueMarkers)
        {
            if (AddonHelper.AddonHasText(DialogueAddonName, marker, true))
                return true;
        }

        return false;
    }
}
