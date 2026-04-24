using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace XASlave.Services;

public unsafe sealed class ExpertDeliveryUnlockService : IDisposable
{
    public const int MinForcedRankFloor = 0;
    public const int MaxForcedRankFloor = 19;
    public const int DefaultForcedRankFloor = 11;
    private static readonly string[] ForcedRankFloorLabels =
    {
        "0 - No Spoof / Real Rank",
        "1 - Storm Private Third Class",
        "2 - Storm Private Second Class",
        "3 - Storm Private First Class",
        "4 - Storm Corporal",
        "5 - Storm Sergeant Third Class",
        "6 - Storm Sergeant Second Class",
        "7 - Storm Sergeant First Class",
        "8 - Chief Storm Sergeant",
        "9 - Second Storm Lieutenant",
        "10 - First Storm Lieutenant",
        "11 - Storm Captain",
        "12 - Second Storm Commander",
        "13 - First Storm Commander",
        "14 - High Storm Commander",
        "15 - Rear Storm Marshal",
        "16 - Vice Storm Marshal",
        "17 - Storm Marshal",
        "18 - Grand Storm Marshal",
        "19 - Storm Champion",
    };

    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<GetGrandCompanyRankDelegate>? getGrandCompanyRankHook;
    private bool initialized;
    private bool enabled;
    private byte forcedRankFloor = (byte)DefaultForcedRankFloor;

    public ExpertDeliveryUnlockService(
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public bool IsEnabled => enabled;
    public int ForcedRankFloor => forcedRankFloor;

    public string StatusText { get; private set; } = "Disabled";

    public static int NormalizeForcedRankFloor(int value)
    {
        return Math.Clamp(value, MinForcedRankFloor, MaxForcedRankFloor);
    }

    public static string GetForcedRankFloorLabel(int value)
    {
        var normalizedValue = NormalizeForcedRankFloor(value);
        return ForcedRankFloorLabels[normalizedValue - MinForcedRankFloor];
    }

    public void ApplyConfiguration(int forcedRankFloor)
    {
        this.forcedRankFloor = (byte)NormalizeForcedRankFloor(forcedRankFloor);
        RefreshStatusText();
    }

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

        StatusText = $"Enabled - local GC rank floor is {GetForcedRankFloorLabel(forcedRankFloor)}.";
    }

    private byte GetGrandCompanyRankDetour(PlayerState* playerState)
    {
        var original = getGrandCompanyRankHook?.Original(playerState) ?? 0;
        return Math.Max(original, forcedRankFloor);
    }

    private delegate byte GetGrandCompanyRankDelegate(PlayerState* playerState);
}
