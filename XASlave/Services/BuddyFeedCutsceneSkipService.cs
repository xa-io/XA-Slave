using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

public unsafe sealed class BuddyFeedCutsceneSkipService : IDisposable
{
    private static readonly string[] HousingZoneMarkers =
    {
        "Mist",
        "Lavender Beds",
        "Goblet",
        "Shirogane",
        "Empyreum",
    };

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private Hook<PlayFeedBuddySceneDelegate>? playFeedBuddySceneHook;
    private bool initialized;
    private bool enabled;
    private bool subscribed;
    private bool startupArmDeferred;
    private bool unavailable;

    public BuddyFeedCutsceneSkipService(
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IClientState clientState,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.clientState = clientState;
        this.log = log;
    }

    public bool IsEnabled => enabled;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            if (enabled)
                TryEnableHookIfEligible();

            RefreshStatusText();
            return enabled;
        }

        if (!value)
        {
            enabled = false;
            startupArmDeferred = false;
            UpdateSubscriptions(false);
            if (playFeedBuddySceneHook is { IsDisposed: false, IsEnabled: true })
                playFeedBuddySceneHook.Disable();

            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        startupArmDeferred = false;
        UpdateSubscriptions(true);

        if (TryEnableHookIfEligible())
        {
            RefreshStatusText();
            return true;
        }

        if (unavailable)
        {
            enabled = false;
            UpdateSubscriptions(false);
            RefreshStatusText();
            return false;
        }

        RefreshStatusText();
        return true;
    }

    public bool RestoreEnabledOnStartup()
    {
        enabled = true;
        startupArmDeferred = true;
        UpdateSubscriptions(true);
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        startupArmDeferred = false;
        UpdateSubscriptions(false);
        if (playFeedBuddySceneHook is { IsDisposed: false })
            playFeedBuddySceneHook.Dispose();

        playFeedBuddySceneHook = null;
    }

    private void UpdateSubscriptions(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
        {
            clientState.Login += OnLogin;
            clientState.TerritoryChanged += OnTerritoryChanged;
        }
        else
        {
            clientState.Login -= OnLogin;
            clientState.TerritoryChanged -= OnTerritoryChanged;
        }

        subscribed = targetEnabled;
    }

    private void OnLogin()
    {
        if (!enabled)
            return;

        TryEnableHookIfEligible();
        RefreshStatusText();
    }

    private void OnTerritoryChanged(uint _)
    {
        if (!enabled)
            return;

        TryEnableHookIfEligible();
        RefreshStatusText();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        try
        {
            if (!sigScanner.TryScanText(Sigs.PlayFeedBuddySceneSig, out var address) || address == nint.Zero)
            {
                unavailable = true;
                log.Warning("[XASlave] Buddy Feed Cutscene Skip could not find the feed cutscene signature.");
                return;
            }

            playFeedBuddySceneHook = interopProvider.HookFromAddress<PlayFeedBuddySceneDelegate>(address, PlayFeedBuddySceneDetour);
        }
        catch (Exception ex)
        {
            unavailable = true;
            log.Warning(ex, "[XASlave] Buddy Feed Cutscene Skip failed to initialize.");
        }
    }

    private bool TryEnableHookIfEligible()
    {
        if (!enabled || unavailable || !ShouldArmHookNow())
            return false;

        EnsureInitialized();
        if (playFeedBuddySceneHook == null || playFeedBuddySceneHook.IsDisposed)
        {
            unavailable = true;
            RefreshStatusText();
            return false;
        }

        if (!playFeedBuddySceneHook.IsEnabled)
            playFeedBuddySceneHook.Enable();

        startupArmDeferred = false;
        RefreshStatusText();
        return true;
    }

    private bool ShouldArmHookNow()
    {
        if (!clientState.IsLoggedIn)
            return false;

        var zoneName = AddonHelper.GetCurrentZoneName();
        if (string.IsNullOrWhiteSpace(zoneName) || zoneName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var marker in HousingZoneMarkers)
        {
            if (zoneName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (unavailable)
        {
            StatusText = "Unavailable - buddy feed signature missing.";
            return;
        }

        if (playFeedBuddySceneHook is { IsDisposed: false, IsEnabled: true })
        {
            StatusText = "Enabled - buddy feed cutscenes are skipped.";
            return;
        }

        StatusText = startupArmDeferred
            ? "Ready - buddy feed hook will arm after a later housing-zone transition instead of during plugin startup."
            : "Ready - buddy feed hook will arm when you enter a housing district.";
    }

    private void PlayFeedBuddySceneDetour(HousingManager* manager)
    {
        if (!enabled)
            playFeedBuddySceneHook?.Original(manager);
    }

    private delegate void PlayFeedBuddySceneDelegate(HousingManager* manager);
}
