using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

public unsafe sealed class BuddyFeedCutsceneSkipService : IDisposable
{
    private const string PlayFeedBuddySceneSig =
        "E8 ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 33 C0 48 83 C4 ?? C3 CC CC CC CC CC CC CC CC CC CC CC 48 83 EC";

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<PlayFeedBuddySceneDelegate>? playFeedBuddySceneHook;
    private bool initialized;
    private bool enabled;

    public BuddyFeedCutsceneSkipService(
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public bool IsEnabled => enabled;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            if (playFeedBuddySceneHook is { IsDisposed: false, IsEnabled: true })
                playFeedBuddySceneHook.Disable();

            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (playFeedBuddySceneHook == null || playFeedBuddySceneHook.IsDisposed)
        {
            StatusText = "Unavailable - buddy feed signature missing.";
            return false;
        }

        playFeedBuddySceneHook.Enable();
        enabled = true;
        StatusText = "Enabled - buddy feed cutscenes are skipped.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        if (playFeedBuddySceneHook is { IsDisposed: false })
            playFeedBuddySceneHook.Dispose();

        playFeedBuddySceneHook = null;
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        try
        {
            if (!sigScanner.TryScanText(PlayFeedBuddySceneSig, out var address) || address == nint.Zero)
            {
                log.Warning("[XASlave] Buddy Feed Cutscene Skip could not find the feed cutscene signature.");
                return;
            }

            playFeedBuddySceneHook = interopProvider.HookFromAddress<PlayFeedBuddySceneDelegate>(address, PlayFeedBuddySceneDetour);
            log.Information($"[XASlave] Buddy Feed Cutscene Skip hook created at 0x{address:X}.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Buddy Feed Cutscene Skip failed to initialize.");
        }
    }

    private void PlayFeedBuddySceneDetour(HousingManager* manager)
    {
        if (!enabled)
            playFeedBuddySceneHook?.Original(manager);
    }

    private delegate void PlayFeedBuddySceneDelegate(HousingManager* manager);
}
