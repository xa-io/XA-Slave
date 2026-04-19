using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace XASlave.Services;

public unsafe sealed class ExpertDeliveryUnlockService : IDisposable
{
    private const byte ForcedRankFloor = 11;

    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<GetGrandCompanyRankDelegate>? getGrandCompanyRankHook;
    private bool initialized;
    private bool enabled;

    public ExpertDeliveryUnlockService(
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
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
            DisableHook();
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (getGrandCompanyRankHook == null)
        {
            StatusText = "Unavailable - PlayerState.GetGrandCompanyRank hook is missing.";
            return false;
        }

        getGrandCompanyRankHook.Enable();
        enabled = true;
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        DisableHook();

        if (getGrandCompanyRankHook is { IsDisposed: false })
            getGrandCompanyRankHook.Dispose();

        getGrandCompanyRankHook = null;
        StatusText = "Disabled";
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        try
        {
            var address = (nint)PlayerState.Addresses.GetGrandCompanyRank.Value;
            if (address == nint.Zero)
                return;

            getGrandCompanyRankHook = interopProvider.HookFromAddress<GetGrandCompanyRankDelegate>(address, GetGrandCompanyRankDetour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Unlock Expert Delivery failed to create PlayerState.GetGrandCompanyRank hook.");
        }
    }

    private void DisableHook()
    {
        if (getGrandCompanyRankHook is { IsDisposed: false, IsEnabled: true })
            getGrandCompanyRankHook.Disable();
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        StatusText = $"Enabled - local GC rank floor is {ForcedRankFloor}.";
    }

    private byte GetGrandCompanyRankDetour(PlayerState* playerState)
    {
        var original = getGrandCompanyRankHook?.Original(playerState) ?? 0;
        return Math.Max(original, ForcedRankFloor);
    }

    private delegate byte GetGrandCompanyRankDelegate(PlayerState* playerState);
}
