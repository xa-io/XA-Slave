using System;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Common.Lua;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class DialogueSkipService : IDisposable
{
    private const int TotalNativeHookSurfaces = 8;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<TalkDelegate>? talkHook;
    private Hook<TalkDelegate>? talkAsyncHook;
    private Hook<TalkDelegate>? systemTalkHook;
    private Hook<LuaFunctionDelegate>? logMessageNoSkipHook;
    private Hook<TalkDelegate>? shortTalkHook;
    private Hook<TalkDelegate>? shortTalkWithLineVoiceHook;
    private Hook<LuaFunctionDelegate>? craftLeveTalkHook;
    private Hook<LuaFunctionDelegate>? guildleveAssignmentTalkHook;

    private bool initialized;
    private bool enabled;
    private bool subscribed;
    private long lastAdvanceTick;
    private int availableHookSurfaces;

    public DialogueSkipService(IAddonLifecycle addonLifecycle, ISigScanner sigScanner, IGameInteropProvider interopProvider, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
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
            enabled = false;
            UpdateHookState(false);
            UpdateSubscription(false);
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        UpdateSubscription(true);
        // Re-arm the native dialogue hooks on re-enable. They are created and enabled the first
        // time OnTalkAddon runs (guarded by !initialized), but after a disable/enable cycle
        // `initialized` is already true, so without this the hooks would stay disabled and only
        // the synthetic Talk-click fallback would work.
        if (initialized)
        {
            EnsureInitialized(retryMissing: true);
            UpdateHookState(true);
        }
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        UpdateHookState(false);
        UpdateSubscription(false);
        DisposeHook(ref talkHook);
        DisposeHook(ref talkAsyncHook);
        DisposeHook(ref systemTalkHook);
        DisposeHook(ref logMessageNoSkipHook);
        DisposeHook(ref shortTalkHook);
        DisposeHook(ref shortTalkWithLineVoiceHook);
        DisposeHook(ref craftLeveTalkHook);
        DisposeHook(ref guildleveAssignmentTalkHook);
    }

    private void EnsureInitialized(bool retryMissing = false)
    {
        if (initialized && !retryMissing)
            return;

        initialized = true;

        var talkBase0 = TryScanBaseAddress(Sigs.TalkBaseSig0, "TalkBase0");
        talkHook ??= TryCreateLuaHook<TalkDelegate>(talkBase0, "Talk", TalkDetour, "Talk");
        talkAsyncHook ??= TryCreateLuaHook<TalkDelegate>(talkBase0, "TalkAsync", TalkDetour, "TalkAsync");

        var talkBase1 = TryScanBaseAddress(Sigs.TalkBaseSig1, "TalkBase1");
        systemTalkHook ??= TryCreateLuaHook<TalkDelegate>(talkBase1, "SystemTalk", TalkDetour, "SystemTalk");
        logMessageNoSkipHook ??= TryCreateLuaHook<LuaFunctionDelegate>(talkBase1, "LogMessageNoSkip", LuaStateTalkDetour, "LogMessageNoSkip");

        var talkBase2 = TryScanBaseAddress(Sigs.TalkBaseSig2, "TalkBase2");
        shortTalkHook ??= TryCreateLuaHook<TalkDelegate>(talkBase2, "ShortTalk", TalkDetour, "ShortTalk");
        shortTalkWithLineVoiceHook ??= TryCreateLuaHook<TalkDelegate>(talkBase2, "ShortTalkWithLineVoice", TalkDetour, "ShortTalkWithLineVoice");

        var talkBase3 = TryScanBaseAddress(Sigs.TalkBaseSig3, "TalkBase3");
        craftLeveTalkHook ??= TryCreateLuaHook<LuaFunctionDelegate>(talkBase3, "CraftLeveTalk", LuaStateTalkDetour, "CraftLeveTalk");

        var talkBase4 = TryScanBaseAddress(Sigs.TalkBaseSig4, "TalkBase4");
        guildleveAssignmentTalkHook ??= TryCreateLuaHook<LuaFunctionDelegate>(talkBase4, "GuildleveAssignmentTalk", LuaStateTalkDetour, "GuildleveAssignmentTalk");

        availableHookSurfaces = CountHookSurfaces();
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
            StatusText = "Ready - Talk auto-advance is active and native dialogue hooks will arm on first dialogue.";
            return;
        }

        StatusText = availableHookSurfaces > 0
            ? availableHookSurfaces < TotalNativeHookSurfaces
                ? $"Partially enabled - Talk addon auto-advance plus {availableHookSurfaces}/{TotalNativeHookSurfaces} native dialogue surfaces are active locally."
                : $"Enabled - Talk addon auto-advance plus all {TotalNativeHookSurfaces} native dialogue surfaces are active locally."
            : "Enabled - Talk addon auto-advance is active locally.";
    }

    private int CountHookSurfaces()
    {
        var count = 0;
        count += talkHook != null ? 1 : 0;
        count += talkAsyncHook != null ? 1 : 0;
        count += systemTalkHook != null ? 1 : 0;
        count += logMessageNoSkipHook != null ? 1 : 0;
        count += shortTalkHook != null ? 1 : 0;
        count += shortTalkWithLineVoiceHook != null ? 1 : 0;
        count += craftLeveTalkHook != null ? 1 : 0;
        count += guildleveAssignmentTalkHook != null ? 1 : 0;
        return count;
    }

    private void UpdateHookState(bool targetEnabled)
    {
        ToggleHook(talkHook, targetEnabled, "Talk");
        ToggleHook(talkAsyncHook, targetEnabled, "TalkAsync");
        ToggleHook(systemTalkHook, targetEnabled, "SystemTalk");
        ToggleHook(logMessageNoSkipHook, targetEnabled, "LogMessageNoSkip");
        ToggleHook(shortTalkHook, targetEnabled, "ShortTalk");
        ToggleHook(shortTalkWithLineVoiceHook, targetEnabled, "ShortTalkWithLineVoice");
        ToggleHook(craftLeveTalkHook, targetEnabled, "CraftLeveTalk");
        ToggleHook(guildleveAssignmentTalkHook, targetEnabled, "GuildleveAssignmentTalk");
    }

    private void UpdateSubscription(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
            addonLifecycle.RegisterListener(AddonEvent.PreDraw, "Talk", OnTalkAddon);
        else
            addonLifecycle.UnregisterListener(OnTalkAddon);

        subscribed = targetEnabled;
    }

    private nint TryScanBaseAddress(ProtectedSig signature, string label)
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] Skip Dialogue could not find {label}.");
                return nint.Zero;
            }

            return address;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Skip Dialogue failed while scanning {label}.");
            return nint.Zero;
        }
    }

    private Hook<T>? TryCreateLuaHook<T>(nint baseAddress, string functionName, T detour, string label)
        where T : Delegate
    {
        if (baseAddress == nint.Zero)
            return null;

        try
        {
            var functionAddress = GetLuaFunctionByName(baseAddress, functionName);
            if (functionAddress == nint.Zero)
            {
                log.Warning($"[XASlave] Skip Dialogue could not validate {label}'s computed Lua function address; retry by disabling and re-enabling the feature.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(functionAddress, detour);
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Skip Dialogue failed to create {label} hook.");
            return null;
        }
    }

    private nint GetLuaFunctionByName(nint luaSetupFunctionStartAddress, string functionName, int scanSize = 8192)
    {
        if (luaSetupFunctionStartAddress == nint.Zero || string.IsNullOrEmpty(functionName) || scanSize <= 0)
            return nint.Zero;

        var textBase = sigScanner.Module.BaseAddress + (sigScanner.TextSectionBase - sigScanner.SearchBase);
        if (!NativeAddressPolicy.TryGetBoundedLength(
                luaSetupFunctionStartAddress,
                textBase,
                sigScanner.TextSectionSize,
                scanSize,
                out var boundedScanSize)
            || boundedScanSize < 7)
        {
            return nint.Zero;
        }

        var functionBytes = new byte[boundedScanSize];

        try
        {
            Marshal.Copy(luaSetupFunctionStartAddress, functionBytes, 0, boundedScanSize);
        }
        catch
        {
            return nint.Zero;
        }

        const byte leaString0 = 0x4C;
        const byte leaString1 = 0x8D;
        const byte leaString2 = 0x05;
        const byte leaFunction2 = 0x0D;

        var stringLeaIndex = -1;
        for (var i = 0; i <= functionBytes.Length - 7; i++)
        {
            if (functionBytes[i] != leaString0 || functionBytes[i + 1] != leaString1 || functionBytes[i + 2] != leaString2)
                continue;

            var displacement = BitConverter.ToInt32(functionBytes, i + 3);
            var nextInstructionAddress = (long)luaSetupFunctionStartAddress + i + 7;
            var stringAddress = nextInstructionAddress + displacement;
            if (!IsExactAsciiStringAt((nint)stringAddress, functionName))
                continue;

            stringLeaIndex = i;
            break;
        }

        if (stringLeaIndex == -1)
            return nint.Zero;

        var searchLimit = Math.Max(0, stringLeaIndex - 100);
        for (var i = stringLeaIndex - 1; i >= searchLimit; i--)
        {
            if (i + 7 >= functionBytes.Length)
                continue;

            if (functionBytes[i] != leaString0 || functionBytes[i + 1] != leaString1 || functionBytes[i + 2] != leaFunction2)
                continue;

            var displacement = BitConverter.ToInt32(functionBytes, i + 3);
            var nextInstructionAddress = (long)luaSetupFunctionStartAddress + i + 7;
            var functionAddress = (nint)(nextInstructionAddress + displacement);
            return NativeAddressPolicy.IsRangeValid(functionAddress, textBase, sigScanner.TextSectionSize, 1)
                ? functionAddress
                : nint.Zero;
        }

        return nint.Zero;
    }

    private bool IsExactAsciiStringAt(nint address, string expected)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        if (!NativeAddressPolicy.IsRangeValid(
                address,
                sigScanner.Module.BaseAddress,
                sigScanner.Module.ModuleMemorySize,
                expectedBytes.Length + 1))
        {
            return false;
        }

        var actual = new byte[expectedBytes.Length + 1];
        try
        {
            Marshal.Copy(address, actual, 0, actual.Length);
        }
        catch
        {
            return false;
        }

        return actual[^1] == 0 && actual.AsSpan(0, expectedBytes.Length).SequenceEqual(expectedBytes);
    }

    private void ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return;

        try
        {
            if (targetEnabled)
            {
                if (!hook.IsEnabled)
                    hook.Enable();
            }
            else if (hook.IsEnabled)
            {
                hook.Disable();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Skip Dialogue failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void OnTalkAddon(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        if (!initialized)
        {
            EnsureInitialized();
            UpdateHookState(true);
            RefreshStatusText();
        }

        var now = Environment.TickCount64;
        if (now - lastAdvanceTick < 150)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsReady || !addon->IsVisible || addon->RootNode == null)
                return;

            var evt = stackalloc AtkEvent[1]
            {
                new()
                {
                    Listener = (AtkEventListener*)addon,
                    State = new AtkEventState
                    {
                        StateFlags = (AtkEventStateFlags)132
                    },
                    Target = &AtkStage.Instance()->AtkEventTarget
                }
            };

            var data = stackalloc AtkEventData[1];
            for (var i = 0; i < sizeof(AtkEventData); i++)
                ((byte*)data)[i] = 0;

            addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
            addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
            addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
            lastAdvanceTick = now;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Dialogue failed while advancing Talk.");
        }
    }

    private ulong LuaStateTalkDetour(lua_State* state)
    {
        try
        {
            if (state == null
                || state->top == null
                || state->stack_last == null
                || state->top >= state->stack_last)
            {
                return 0;
            }

            var value = state->top;
            value->tt = 2;
            value->value.n = 1;
            state->top += 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Dialogue Lua detour failed; suppressing the dialogue safely.");
        }

        return 1;
    }

    private nint TalkDetour(EventSceneModuleImplBase* _)
    {
        try
        {
            return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Dialogue Talk detour failed; suppressing the dialogue safely.");
            return 1;
        }
    }

    private delegate nint TalkDelegate(EventSceneModuleImplBase* scene);

    private delegate ulong LuaFunctionDelegate(lua_State* state);
}
