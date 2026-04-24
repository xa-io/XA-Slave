using System;
using Dalamud;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Lua;

namespace XASlave.Services;

public unsafe sealed class AutoSkipCutsceneService : IDisposable
{
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<CutsceneHandleInputDelegate>? cutsceneHandleInputHook;
    private Hook<PlayCutsceneDelegate>? playCutsceneHook;
    private Hook<LuaFunctionDelegate>? playCutsceneLuaHook;
    private Hook<IsCutsceneSeenDelegate>? isCutsceneSeenHook;
    private Hook<LuaFunctionDelegate>? playStaffRollHook;
    private Hook<LuaFunctionDelegate>? playToBeContinuedHook;

    private nint cutsceneUnskippablePatchAddress;
    private byte[]? cutsceneUnskippableOriginalBytes;
    private bool cutsceneUnskippablePatchApplied;
    private bool initialized;
    private bool enabled;
    private bool frameworkSubscribed;
    private int availableSurfaceCount;
    private DateTime lastPromptAttemptUtc = DateTime.MinValue;

    public AutoSkipCutsceneService(
        ICondition condition,
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.condition = condition;
        this.framework = framework;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public bool IsEnabled => enabled;

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
            Disable();
            StatusText = "Disabled";
            return false;
        }

        if (!frameworkSubscribed)
        {
            framework.Update += OnFrameworkUpdate;
            frameworkSubscribed = true;
        }

        enabled = true;
        EnsureInitializedForEnabledState();
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        Disable();
        DisposeHook(ref cutsceneHandleInputHook);
        DisposeHook(ref playCutsceneHook);
        DisposeHook(ref playCutsceneLuaHook);
        DisposeHook(ref isCutsceneSeenHook);
        DisposeHook(ref playStaffRollHook);
        DisposeHook(ref playToBeContinuedHook);
    }

    private void Disable()
    {
        enabled = false;
        availableSurfaceCount = 0;
        if (frameworkSubscribed)
        {
            framework.Update -= OnFrameworkUpdate;
            frameworkSubscribed = false;
        }

        ToggleHook(cutsceneHandleInputHook, false, "CutsceneHandleInput");
        ToggleHook(playCutsceneHook, false, "PlayCutscene");
        ToggleHook(playCutsceneLuaHook, false, "PlayCutsceneLua");
        ToggleHook(isCutsceneSeenHook, false, "IsCutsceneSeen");
        ToggleHook(playStaffRollHook, false, "PlayStaffRoll");
        ToggleHook(playToBeContinuedHook, false, "PlayToBeContinued");
        RestoreCutsceneUnskippablePatch();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;
        cutsceneHandleInputHook = TryCreateHook<CutsceneHandleInputDelegate>(Sigs.CutsceneHandleInputSig, CutsceneHandleInputDetour, "CutsceneHandleInput");
        playCutsceneHook = TryCreateHook<PlayCutsceneDelegate>(Sigs.PlayCutsceneSig, PlayCutsceneDetour, "PlayCutscene");
        playCutsceneLuaHook = TryCreateHook<LuaFunctionDelegate>(Sigs.PlayCutsceneLuaSig, PlayCutsceneLuaDetour, "PlayCutsceneLua");
        isCutsceneSeenHook = TryCreateHook<IsCutsceneSeenDelegate>(Sigs.IsCutsceneSeenSig, IsCutsceneSeenDetour, "IsCutsceneSeen");
        playStaffRollHook = TryCreateHook<LuaFunctionDelegate>(Sigs.PlayStaffRollSig, PlayStaffRollDetour, "PlayStaffRoll");
        playToBeContinuedHook = TryCreateHook<LuaFunctionDelegate>(Sigs.PlayToBeContinuedSig, PlayToBeContinuedDetour, "PlayToBeContinued");

        if (!sigScanner.TryScanText(Sigs.CutsceneUnskippablePatchSig, out cutsceneUnskippablePatchAddress))
            log.Warning("[XASlave] Auto Skip Cutscenes could not find the unskippable cutscene patch signature.");
    }

    private void EnsureInitializedForEnabledState()
    {
        EnsureInitialized();
        if (!HasAnyCutsceneSurface())
        {
            StatusText = "Unavailable - cutscene signatures were not found.";
            log.Warning("[XASlave] Auto Skip Cutscenes unavailable: no cutscene hook or patch signatures were found.");
            return;
        }

        availableSurfaceCount = 0;
        availableSurfaceCount += ToggleHook(cutsceneHandleInputHook, true, "CutsceneHandleInput");
        availableSurfaceCount += ToggleHook(playCutsceneHook, true, "PlayCutscene");
        availableSurfaceCount += ToggleHook(playCutsceneLuaHook, true, "PlayCutsceneLua");
        availableSurfaceCount += ToggleHook(isCutsceneSeenHook, true, "IsCutsceneSeen");
        availableSurfaceCount += ToggleHook(playStaffRollHook, true, "PlayStaffRoll");
        availableSurfaceCount += ToggleHook(playToBeContinuedHook, true, "PlayToBeContinued");
        if (cutsceneUnskippablePatchAddress != nint.Zero)
            availableSurfaceCount++;

        if (availableSurfaceCount == 0)
        {
            StatusText = "Unavailable - cutscene hooks failed to enable.";
            log.Warning("[XASlave] Auto Skip Cutscenes could not enable any hook or patch surfaces.");
            return;
        }

        SyncCutscenePatchState();
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!initialized)
        {
            StatusText = "Ready - cutscene hooks will arm when the next cutscene starts.";
            return;
        }

        if (availableSurfaceCount == 0)
        {
            StatusText = "Unavailable - cutscene hooks failed to enable.";
            return;
        }

        StatusText = $"Enabled ({availableSurfaceCount} cutscene surfaces available).";
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
                return null;

            var hook = interopProvider.HookFromAddress(address, detour);
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to create {label} hook.");
            return null;
        }
    }

    private bool HasAnyCutsceneSurface()
    {
        return cutsceneHandleInputHook != null
            || playCutsceneHook != null
            || playCutsceneLuaHook != null
            || isCutsceneSeenHook != null
            || playStaffRollHook != null
            || playToBeContinuedHook != null
            || cutsceneUnskippablePatchAddress != nint.Zero;
    }

    private int ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return 0;

        try
        {
            if (targetEnabled)
            {
                if (!hook.IsEnabled)
                    hook.Enable();
                return hook.IsEnabled ? 1 : 0;
            }

            if (hook.IsEnabled)
                hook.Disable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }

        return 0;
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void SyncCutscenePatchState()
    {
        if (enabled)
            ApplyCutsceneUnskippablePatch();
        else
            RestoreCutsceneUnskippablePatch();
    }

    private bool ApplyCutsceneUnskippablePatch()
    {
        if (cutsceneUnskippablePatchAddress == nint.Zero)
            return false;

        if (cutsceneUnskippablePatchApplied)
            return true;

        if (cutsceneUnskippableOriginalBytes == null
            && !SafeMemory.ReadBytes((IntPtr)cutsceneUnskippablePatchAddress, 1, out cutsceneUnskippableOriginalBytes))
        {
            log.Warning("[XASlave] Auto Skip Cutscenes failed to read the original unskippable branch byte.");
            return false;
        }

        if (!SafeMemory.WriteBytes((IntPtr)cutsceneUnskippablePatchAddress, [0xEB]))
        {
            log.Warning("[XASlave] Auto Skip Cutscenes failed to apply the unskippable cutscene branch patch.");
            return false;
        }

        cutsceneUnskippablePatchApplied = true;
        return true;
    }

    private void RestoreCutsceneUnskippablePatch()
    {
        if (!cutsceneUnskippablePatchApplied || cutsceneUnskippablePatchAddress == nint.Zero || cutsceneUnskippableOriginalBytes == null)
            return;

        if (!SafeMemory.WriteBytes((IntPtr)cutsceneUnskippablePatchAddress, cutsceneUnskippableOriginalBytes))
            log.Warning("[XASlave] Auto Skip Cutscenes failed to restore the unskippable cutscene branch byte.");

        cutsceneUnskippablePatchApplied = false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        SyncCutscenePatchState();
        if (!enabled || !condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        var now = DateTime.UtcNow;
        if ((now - lastPromptAttemptUtc).TotalMilliseconds < 750)
            return;

        lastPromptAttemptUtc = now;
        if (AddonHelper.IsAddonReady("SelectString"))
            AddonHelper.FireCallbackAndClose("SelectString", 0);
    }

    private byte CutsceneHandleInputDetour(nint a1, float a2)
    {
        try
        {
            if (enabled && condition[ConditionFlag.OccupiedInCutSceneEvent] && *(ulong*)(a1 + 56) != 0)
                ApplyCutsceneUnskippablePatch();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes input detour failed before original call.");
        }

        return cutsceneHandleInputHook?.Original(a1, a2) ?? 0;
    }

    private nint PlayCutsceneDetour(EventFramework* eventFramework, lua_State* state)
    {
        if (enabled)
            return 1;

        return playCutsceneHook?.Original(eventFramework, state) ?? 0;
    }

    private ulong PlayCutsceneLuaDetour(lua_State* state)
    {
        if (!enabled)
            return playCutsceneLuaHook?.Original(state) ?? 0;

        if (state != null)
        {
            state->top->tt = 2;
            state->top->value.n = 1;
            state->top += 1;
        }

        return 1;
    }

    private ulong PlayStaffRollDetour(lua_State* state)
    {
        if (enabled)
            return 1;

        return playStaffRollHook?.Original(state) ?? 0;
    }

    private ulong PlayToBeContinuedDetour(lua_State* state)
    {
        if (enabled)
            return 1;

        return playToBeContinuedHook?.Original(state) ?? 0;
    }

    private bool IsCutsceneSeenDetour(UIState* state, uint cutsceneId)
    {
        if (enabled)
            return true;

        return isCutsceneSeenHook?.Original(state, cutsceneId) ?? true;
    }

    private delegate byte CutsceneHandleInputDelegate(nint a1, float a2);

    private delegate nint PlayCutsceneDelegate(EventFramework* eventFramework, lua_State* state);

    private delegate ulong LuaFunctionDelegate(lua_State* state);

    private delegate bool IsCutsceneSeenDelegate(UIState* state, uint cutsceneId);
}
