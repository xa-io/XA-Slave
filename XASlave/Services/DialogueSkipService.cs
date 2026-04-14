using System;
using System.Runtime.InteropServices;
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

        EnsureInitialized();
        enabled = true;
        UpdateHookState(true);
        UpdateSubscription(true);
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

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        var talkBase0 = TryScanBaseAddress(Sigs.TalkBaseSig0, "TalkBase0");
        talkHook = TryCreateLuaHook<TalkDelegate>(talkBase0, "Talk", TalkDetour, "Talk");
        talkAsyncHook = TryCreateLuaHook<TalkDelegate>(talkBase0, "TalkAsync", TalkDetour, "TalkAsync");

        var talkBase1 = TryScanBaseAddress(Sigs.TalkBaseSig1, "TalkBase1");
        systemTalkHook = TryCreateLuaHook<TalkDelegate>(talkBase1, "SystemTalk", TalkDetour, "SystemTalk");
        logMessageNoSkipHook = TryCreateLuaHook<LuaFunctionDelegate>(talkBase1, "LogMessageNoSkip", LuaStateTalkDetour, "LogMessageNoSkip");

        var talkBase2 = TryScanBaseAddress(Sigs.TalkBaseSig2, "TalkBase2");
        shortTalkHook = TryCreateLuaHook<TalkDelegate>(talkBase2, "ShortTalk", TalkDetour, "ShortTalk");
        shortTalkWithLineVoiceHook = TryCreateLuaHook<TalkDelegate>(talkBase2, "ShortTalkWithLineVoice", TalkDetour, "ShortTalkWithLineVoice");

        var talkBase3 = TryScanBaseAddress(Sigs.TalkBaseSig3, "TalkBase3");
        craftLeveTalkHook = TryCreateLuaHook<LuaFunctionDelegate>(talkBase3, "CraftLeveTalk", LuaStateTalkDetour, "CraftLeveTalk");

        var talkBase4 = TryScanBaseAddress(Sigs.TalkBaseSig4, "TalkBase4");
        guildleveAssignmentTalkHook = TryCreateLuaHook<LuaFunctionDelegate>(talkBase4, "GuildleveAssignmentTalk", LuaStateTalkDetour, "GuildleveAssignmentTalk");

        availableHookSurfaces = CountHookSurfaces();
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        StatusText = availableHookSurfaces > 0
            ? $"Enabled - Talk addon auto-advance plus {availableHookSurfaces} native dialogue surfaces are active locally."
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
                log.Warning($"[XASlave] Skip Dialogue could not resolve {label}.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(functionAddress, detour);
            log.Information($"[XASlave] Skip Dialogue created {label} hook at 0x{functionAddress:X}.");
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Skip Dialogue failed to create {label} hook.");
            return null;
        }
    }

    private static nint GetLuaFunctionByName(nint luaSetupFunctionStartAddress, string functionName, int scanSize = 8192)
    {
        if (luaSetupFunctionStartAddress == nint.Zero || string.IsNullOrEmpty(functionName))
            return nint.Zero;

        var functionBytes = new byte[scanSize];

        try
        {
            Marshal.Copy(luaSetupFunctionStartAddress, functionBytes, 0, scanSize);
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
            var currentInstructionAddress = (long)luaSetupFunctionStartAddress + i;
            var nextInstructionAddress = currentInstructionAddress + 7;
            var stringAddress = nextInstructionAddress + displacement;
            var referencedString = Marshal.PtrToStringAnsi((nint)stringAddress);
            if (!string.Equals(referencedString, functionName, StringComparison.Ordinal))
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
            var currentInstructionAddress = (long)luaSetupFunctionStartAddress + i;
            var nextInstructionAddress = currentInstructionAddress + 7;
            return (nint)(nextInstructionAddress + displacement);
        }

        return nint.Zero;
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

    private static ulong LuaStateTalkDetour(lua_State* state)
    {
        var value = state->top;
        value->tt = 2;
        value->value.n = 1;
        state->top += 1;
        return 1;
    }

    private static nint TalkDetour(EventSceneModuleImplBase* _) => 1;

    private delegate nint TalkDelegate(EventSceneModuleImplBase* scene);

    private delegate ulong LuaFunctionDelegate(lua_State* state);
}
