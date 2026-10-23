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
        global::XASlave.Localization.UiLocale.Native("S68A7FA263FC63161"),
        global::XASlave.Localization.UiLocale.Native("SB36AA5F78E53C088"),
        global::XASlave.Localization.UiLocale.Native("S366E0E4B39D020FF"),
        global::XASlave.Localization.UiLocale.Native("S014C904878830A10"),
        global::XASlave.Localization.UiLocale.Native("SA7A3C05E6969509F"),
        global::XASlave.Localization.UiLocale.Native("S7C41EA963B4BE208"),
        global::XASlave.Localization.UiLocale.Native("SD1B983921043AB2C"),
        global::XASlave.Localization.UiLocale.Native("S654348BA1CECB6B1"),
        global::XASlave.Localization.UiLocale.Native("S06BF74E4617D4D72"),
        global::XASlave.Localization.UiLocale.Native("S622279ADC465EFAF"),
        global::XASlave.Localization.UiLocale.Native("SDBB51ABCD33BE406"),
        global::XASlave.Localization.UiLocale.Native("S280AB693835179C8"),
        global::XASlave.Localization.UiLocale.Native("S6A06BE7E16762BC2"),
        global::XASlave.Localization.UiLocale.Native("S5007C185811F2F5E"),
        global::XASlave.Localization.UiLocale.Native("SB2324A8D1EED6559"),
        global::XASlave.Localization.UiLocale.Native("SE6BD86C77011F88C"),
        global::XASlave.Localization.UiLocale.Native("S3D160C166BFEC580"),
        global::XASlave.Localization.UiLocale.Native("S1FCC64972D49F7A5"),
        global::XASlave.Localization.UiLocale.Native("SAC472E4FA952FFDA"),
        global::XASlave.Localization.UiLocale.Native("SFD96D8E3B1B46F4F"),
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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        EnsureInitialized();
        if (getGrandCompanyRankHook == null)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S5181AABFBD73D3CC"));
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
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S10B081F9BE0AE879", global::XASlave.Localization.UiMessage.Authored(GetForcedRankFloorLabel(forcedRankFloor))));
    }

    private byte GetGrandCompanyRankDetour(PlayerState* playerState)
    {
        var original = getGrandCompanyRankHook?.OriginalDisposeSafe(playerState) ?? 0;
        try
        {
            return Math.Max(original, forcedRankFloor);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Expert Delivery Unlock rank detour failed after the original call.");
            return original;
        }
    }

    private delegate byte GetGrandCompanyRankDelegate(PlayerState* playerState);
}
