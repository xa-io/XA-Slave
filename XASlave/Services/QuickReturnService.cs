using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace XASlave.Services;

public unsafe sealed class QuickReturnService : IDisposable
{
    private const int ReturnGeneralActionId = 6;
    private const int InstantReturnCommandId = 214;

    private readonly IClientState clientState;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<ReturnDelegate>? returnHook;
    private bool initialized;
    private bool enabled;

    public QuickReturnService(
        IClientState clientState,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            RefreshStatusText();
            return enabled;
        }

        if (!value)
        {
            enabled = false;
            UpdateHookState(false);
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (returnHook == null)
        {
            StatusText = "Unavailable - Return hook missing.";
            return false;
        }

        enabled = true;
        UpdateHookState(true);
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        UpdateHookState(false);

        if (returnHook is { IsDisposed: false })
            returnHook.Dispose();

        returnHook = null;
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        try
        {
            var address = (nint)AgentReturn.MemberFunctionPointers.Return;
            if (address == nint.Zero)
                return;

            returnHook = interopProvider.HookFromAddress<ReturnDelegate>(address, ReturnDetour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Instant Return failed to create the Return hook.");
        }
    }

    private void UpdateHookState(bool targetEnabled)
    {
        if (returnHook == null || returnHook.IsDisposed)
            return;

        try
        {
            if (targetEnabled)
            {
                if (!returnHook.IsEnabled)
                    returnHook.Enable();
            }
            else if (returnHook.IsEnabled)
            {
                returnHook.Disable();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Instant Return failed to {(targetEnabled ? "enable" : "disable")} the Return hook.");
        }
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        StatusText = "Enabled - skips Return cast/cooldown, leaves the Return prompt manual, and stays off in PvP.";
    }

    private void ReturnDetour(AgentReturn* agent)
    {
        if (!enabled || agent == null || !clientState.IsLoggedIn)
        {
            returnHook?.Original(agent);
            return;
        }

        try
        {
            if (clientState.IsPvPExcludingDen)
            {
                returnHook?.Original(agent);
                return;
            }

            var actionManager = ActionManager.Instance();
            if (actionManager == null || actionManager->GetActionStatus(ActionType.GeneralAction, ReturnGeneralActionId) != 0)
            {
                returnHook?.Original(agent);
                return;
            }

            ExitPartyIfNeeded();

            if (!GameMain.ExecuteCommand(InstantReturnCommandId))
            {
                returnHook?.Original(agent);
                return;
            }

            log.Information("[XASlave] Instant Return sent the fast Return command.");
            return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Instant Return failed while intercepting Return.");
        }

        returnHook?.Original(agent);
    }

    private void ExitPartyIfNeeded()
    {
        try
        {
            if (!InfoProxyCrossRealm.IsLocalPlayerInParty())
                return;

            var partyProxy = InfoProxyPartyMember.Instance();
            if (partyProxy == null)
                return;

            if (InfoProxyCrossRealm.IsLocalPlayerPartyLeader())
                partyProxy->DisbandParty();
            else
                partyProxy->LeaveParty();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Instant Return failed while updating party state before Return.");
        }
    }

    private delegate void ReturnDelegate(AgentReturn* agent);
}
