using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XASlave.Services;

public unsafe sealed class ReplaceUnownedMountHotbarsService : IDisposable
{
    private const uint MountRouletteGeneralActionId = 9;

    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<RaptureHotbarModule.Delegates.GetSlotAppearance>? appearanceHook;
    private Hook<RaptureHotbarModule.Delegates.ExecuteSlot>? executeHook;
    private bool initialized;
    private bool enabled;
    private bool appearanceFailureLogged;
    private bool executionFailureLogged;

    public ReplaceUnownedMountHotbarsService(
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            DisableHooks();
            StatusText = "Disabled";
            return false;
        }

        if (enabled)
            return true;

        appearanceFailureLogged = false;
        executionFailureLogged = false;
        EnsureInitialized();
        if (appearanceHook == null || executeHook == null)
        {
            StatusText = "Unavailable - required hotbar hooks are missing.";
            return false;
        }

        try
        {
            // Arm execution first so the replacement icon can never briefly retain
            // the original unowned Mount command when clicked.
            if (!executeHook.IsEnabled)
                executeHook.Enable();
            if (!appearanceHook.IsEnabled)
                appearanceHook.Enable();

            enabled = true;
            StatusText = "Enabled - unowned Mount hotbar slots display and execute Mount Roulette; saved hotbars are unchanged.";
            return true;
        }
        catch (Exception ex)
        {
            enabled = false;
            DisableHooks();
            StatusText = "Unavailable - failed to enable the required hotbar hooks.";
            log.Warning(ex, "[XASlave] Replace Unowned Mount Hotbars failed to enable its hooks.");
            return false;
        }
    }

    public void Dispose()
    {
        enabled = false;
        DisableHooks();
        DisposeHook(ref appearanceHook);
        DisposeHook(ref executeHook);
        StatusText = "Disabled";
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        try
        {
            var appearanceAddress = RaptureHotbarModule.Addresses.GetSlotAppearance.Value;
            var executeAddress = RaptureHotbarModule.Addresses.ExecuteSlot.Value;
            if (appearanceAddress == nint.Zero || executeAddress == nint.Zero)
                return;

            appearanceHook = interopProvider.HookFromAddress<RaptureHotbarModule.Delegates.GetSlotAppearance>(
                appearanceAddress,
                GetSlotAppearanceDetour);
            executeHook = interopProvider.HookFromAddress<RaptureHotbarModule.Delegates.ExecuteSlot>(
                executeAddress,
                ExecuteSlotDetour);
        }
        catch (Exception ex)
        {
            DisposeHook(ref appearanceHook);
            DisposeHook(ref executeHook);
            log.Warning(ex, "[XASlave] Replace Unowned Mount Hotbars failed to create its hooks.");
        }
    }

    private void DisableHooks()
    {
        // Hide the replacement before removing its matching execution redirect.
        DisableHook(appearanceHook, "appearance");
        DisableHook(executeHook, "execution");
    }

    private void DisableHook<T>(Hook<T>? hook, string label)
        where T : Delegate
    {
        if (hook is not { IsDisposed: false, IsEnabled: true })
            return;

        try
        {
            hook.Disable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Replace Unowned Mount Hotbars failed to disable its {label} hook.");
        }
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private static bool ShouldReplace(RaptureHotbarModule.HotbarSlot* slot)
    {
        if (slot == null ||
            slot->CommandType != RaptureHotbarModule.HotbarSlotType.Mount ||
            slot->CommandId == 0)
        {
            return false;
        }

        var playerState = PlayerState.Instance();
        return playerState != null &&
               playerState->IsLoaded &&
               !playerState->IsMountUnlocked(slot->CommandId);
    }

    private uint GetSlotAppearanceDetour(
        RaptureHotbarModule.HotbarSlotType* actionType,
        uint* actionId,
        ushort* unkC4,
        RaptureHotbarModule* module,
        RaptureHotbarModule.HotbarSlot* slot)
    {
        var originalResult = appearanceHook!.Original(actionType, actionId, unkC4, module, slot);
        if (!enabled || actionType == null || actionId == null)
            return originalResult;

        try
        {
            if (!ShouldReplace(slot))
                return originalResult;

            *actionType = RaptureHotbarModule.HotbarSlotType.GeneralAction;
            *actionId = MountRouletteGeneralActionId;
            return MountRouletteGeneralActionId;
        }
        catch (Exception ex)
        {
            if (!appearanceFailureLogged)
            {
                appearanceFailureLogged = true;
                log.Warning(ex, "[XASlave] Replace Unowned Mount Hotbars failed while resolving a slot appearance.");
            }

            return originalResult;
        }
    }

    private byte ExecuteSlotDetour(
        RaptureHotbarModule* module,
        RaptureHotbarModule.HotbarSlot* slot)
    {
        if (!enabled)
            return executeHook!.Original(module, slot);

        bool shouldReplace;
        try
        {
            shouldReplace = ShouldReplace(slot);
        }
        catch (Exception ex)
        {
            if (!executionFailureLogged)
            {
                executionFailureLogged = true;
                log.Warning(ex, "[XASlave] Replace Unowned Mount Hotbars failed while executing a slot.");
            }

            return executeHook!.Original(module, slot);
        }

        if (!shouldReplace)
            return executeHook!.Original(module, slot);

        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null ||
                actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0)
            {
                return 0;
            }

            return actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId)
                ? (byte)1
                : (byte)0;
        }
        catch (Exception ex)
        {
            if (!executionFailureLogged)
            {
                executionFailureLogged = true;
                log.Warning(ex, "[XASlave] Replace Unowned Mount Hotbars failed while dispatching Mount Roulette.");
            }

            return 0;
        }
    }
}
