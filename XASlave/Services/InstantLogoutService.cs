using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace XASlave.Services;

public sealed class InstantLogoutService : IDisposable
{
    private const uint LogoutContentsFinderRowId = 167;
    private const int KillGameTimeoutMs = 15000;

    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;
    private readonly LobbyErrorAutoCloseService lobbyErrorAutoClose;

    private RequestContentsFinderDelegate? requestContentsFinder;
    private bool initialized;
    private bool enabled;
    private bool subscribed;
    private bool killGamePending;
    private long killGameRequestedAt;

    public InstantLogoutService(IClientState clientState, IFramework framework, ISigScanner sigScanner, IPluginLog log, LobbyErrorAutoCloseService lobbyErrorAutoClose)
    {
        this.clientState = clientState;
        this.framework = framework;
        this.sigScanner = sigScanner;
        this.log = log;
        this.lobbyErrorAutoClose = lobbyErrorAutoClose;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            killGamePending = false;
            UpdateSubscription(false);
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (requestContentsFinder == null)
        {
            StatusText = "Unavailable - contents finder request signature missing.";
            return false;
        }

        enabled = true;
        StatusText = "Enabled - `/xa logout` hard logs out and `/xa killgame` exits after logout.";
        return true;
    }

    public bool RequestLogout(bool force = false)
    {
        if (!force && !enabled)
            return false;

        if (!clientState.IsLoggedIn)
            return false;

        EnsureInitialized();
        if (requestContentsFinder == null)
        {
            StatusText = "Unavailable - contents finder request signature missing.";
            return false;
        }

        try
        {
            lobbyErrorAutoClose.ArmTemporaryMonitor();
            var option = ContentsFinderOption.Default;
            return requestContentsFinder([LogoutContentsFinderRowId], 1, 0, ref option);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Instant Logout failed while submitting the contents finder logout request.");
            return false;
        }
    }

    public bool RequestKillGame(bool force = false)
    {
        if (!force && !enabled)
            return false;

        if (!clientState.IsLoggedIn)
        {
            ChatHelper.SendMessage("/xlkill");
            return true;
        }

        if (!RequestLogout(force))
            return false;

        killGamePending = true;
        killGameRequestedAt = Environment.TickCount64;
        UpdateSubscription(true);
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        killGamePending = false;
        UpdateSubscription(false);
        requestContentsFinder = null;
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
        if (!killGamePending)
        {
            UpdateSubscription(false);
            return;
        }

        if (!clientState.IsLoggedIn)
        {
            killGamePending = false;
            UpdateSubscription(false);
            ChatHelper.SendMessage("/xlkill");
            return;
        }

        if (Environment.TickCount64 - killGameRequestedAt <= KillGameTimeoutMs)
            return;

        killGamePending = false;
        UpdateSubscription(false);
        log.Warning("[XASlave] Instant Logout kill-game follow-up timed out before the logout completed.");
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        if (!sigScanner.TryScanText(Sigs.RequestContentsFinderSig, out var address) || address == nint.Zero)
        {
            log.Warning("[XASlave] Instant Logout request signature was not found.");
            return;
        }

        requestContentsFinder = Marshal.GetDelegateForFunctionPointer<RequestContentsFinderDelegate>(address);
        log.Information($"[XASlave] Located Instant Logout contents finder request at 0x{address:X}.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 10)]
    private struct ContentsFinderOption
    {
        [FieldOffset(0)] public byte Supply;
        [FieldOffset(1)] public byte Config817to820;
        [FieldOffset(2)] public byte UnrestrictedParty;
        [FieldOffset(3)] public byte MinimalIL;
        [FieldOffset(4)] public byte LevelSync;
        [FieldOffset(5)] public byte SilenceEcho;
        [FieldOffset(6)] public byte ExplorerMode;
        [FieldOffset(7)] public ContentsFinder.LootRule LootRules;
        [FieldOffset(8)] public byte IsLimitedLevelingRoulette;
        [FieldOffset(9)] public byte Unknown9;

        public static ContentsFinderOption Default => new()
        {
            Config817to820 = 1,
        };
    }

    private delegate bool RequestContentsFinderDelegate(uint[] rowIds, uint rowCount, uint a3, ref ContentsFinderOption option);
}
