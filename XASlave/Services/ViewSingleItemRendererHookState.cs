using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace XASlave.Services;

// Read-only admission of verified retained entries and the reviewed Create chain.
// Executable memory is never restored here; Dalamud owns all hook changes.
internal static class ViewSingleItemRendererHookState
{
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SupportedDalamudSha256 = "C726C1DE70B76A7ACC970F48E29589CD61205F08DAEC9586D112A977583055F5";
    private const string SupportedReloadedSha256 = "B8960D0FD8341524DF2197A457F0152A12DC6203613D27872B204FEF9DCDEFAD";
    private const string Dalamud15037Sha256 = "715E5F1454F1A6C274E1585A90FF395E01565B72B8D6EF9D380407856B39232F";
    private const string Reloaded15037Sha256 = "EAEEFDB7D832571723481FA0B822CE1708D90A40D54F1CB0F75A1788DDEC05A7";
    private const string Dalamud15038Sha256 = "AD4C2B94679DDE94DFB4F9390C506D2CF293AD1626B52A8FE9FF5CB642CCF759";
    private const string Reloaded15038Sha256 = "D9CD3C718302AF9BAC00F7AC846EE732575186FC8456AC111FE679EA0059F6AC";
    private const string Dalamud15039Sha256 = "F0550B8942E2EF142C6537A1F3D29DD73E70F51E0719AF27483D9294985E8A55";
    private const string Reloaded15039Sha256 = "A867659817E745BD38BA28C7E0F649BDFCCC5D4B4B01A45E375E27B6382E41FE";

    private static Type Manager => typeof(IDalamudHook).Assembly.GetType("Dalamud.Hooking.Internal.HookManager")
        ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3AE573BF0C96C5BF"), originalMessageText => new InvalidOperationException(originalMessageText));

    internal static Lock GetSyncRoot()
    {
        var assembly = typeof(IDalamudHook).Assembly;
        if (!IsSupportedBackendPair(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Reloaded.Hooks.ReloadedHooks).Assembly.Location)))))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S1B916DD686A02912"), originalMessageText => new InvalidOperationException(originalMessageText));
        return Manager.GetProperty("HookEnableSyncRoot", StaticFlags)?.GetValue(null) as Lock
            ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEC1483689B45BC06"), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    // Each pair has matching emitted hook/loader metadata and backend bodies.
    // Do not combine independently admitted host and backend artifacts.
    internal static bool IsSupportedBackendPair(string dalamudSha256, string reloadedSha256)
    {
        return (dalamudSha256 == SupportedDalamudSha256 && reloadedSha256 == SupportedReloadedSha256)
            || (dalamudSha256 == Dalamud15037Sha256 && reloadedSha256 == Reloaded15037Sha256)
            || (dalamudSha256 == Dalamud15038Sha256 && reloadedSha256 == Reloaded15038Sha256)
            || (dalamudSha256 == Dalamud15039Sha256 && reloadedSha256 == Reloaded15039Sha256);
    }

    // Caller holds the actual Dalamud Lock through comparison, creation and enable.
    internal static byte[]? ReadSavedEntry(nint entry, int length, bool allowTitleEditCreate = false)
    {
        var manager = Manager;
        if (manager.GetProperty("TrackedHooks", StaticFlags)?.GetValue(null) is not IDictionary tracked
            || manager.GetProperty("Unhookers", StaticFlags)?.GetValue(null) is not IDictionary unhookers)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S8B2C50C46320794C"), originalMessageText => new InvalidOperationException(originalMessageText));
        foreach (var value in tracked.Values)
        {
            if (value == null || value.GetType().GetProperty("Hook", InstanceFlags)?.GetValue(value) is not IDalamudHook hook)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD6D017E0C7471B0D"), originalMessageText => new InvalidOperationException(originalMessageText));
            if (hook.IsDisposed) continue;
            var offset = (long)hook.Address - (long)entry;
            if (offset >= 0 && offset < length)
            {
                // Only the reviewed TitleEdit character-creation hook may chain here.
                // Its Original remains in the chain; no foreign hook is changed.
                if (offset == 0 && allowTitleEditCreate && IsReviewedTitleEditCreate(value, hook)) continue;
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("SD171E2FC725ABBD1" , ViewSingleItemRendererDiagnostics.DescribeOwners(entry, length)), originalMessageText => new InvalidOperationException(originalMessageText));
            }
        }
        if (!unhookers.Contains(entry)) return null;
        var saved = unhookers[entry];
        var type = typeof(IDalamudHook).Assembly.GetType("Dalamud.Hooking.Internal.Unhooker");
        if (saved == null || type == null || saved.GetType() != type
            || type.GetField("address", InstanceFlags)?.GetValue(saved) is not nint address || address != entry
            || type.GetField("trimmed", InstanceFlags)?.GetValue(saved) is not true
            || type.GetField("originalBytes", InstanceFlags)?.GetValue(saved) is not byte[] prefix)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S18DFB6F8E816747F"), originalMessageText => new InvalidOperationException(originalMessageText));
        return (byte[])prefix.Clone();
    }

    private static bool IsReviewedTitleEditCreate(object registration, IDalamudHook hook)
    {
        var backend = hook.GetType();
        var type = registration.GetType();
        var assembly = type.GetProperty("Assembly", InstanceFlags)?.GetValue(registration) as Assembly;
        var callback = type.GetProperty("Delegate", InstanceFlags)?.GetValue(registration) as Delegate;
        if (assembly == null || callback == null || callback.Target == null || callback.GetInvocationList().Length != 1)
            return false;
        var method = callback.Method;
        var target = callback.Target.GetType();
        var parameters = method.GetParameters();
        if (hook.BackendName != "Reloaded" || backend.Assembly != typeof(IDalamudHook).Assembly
            || !backend.IsGenericType || backend.GetGenericTypeDefinition().FullName != "Dalamud.Hooking.Internal.ReloadedHook`1"
            || backend.GetGenericArguments()[0] != callback.GetType()
            || assembly.GetName().Name != "TitleEdit"
            || method.Module != assembly.ManifestModule || method.IsStatic
            || target.Assembly != assembly || method.DeclaringType != target
            || target.FullName != "TitleEdit.PluginServices.Lobby.LobbyService" || method.Name != "CreateBattleCharacterDetour"
            || method.ReturnType != typeof(uint) || parameters.Length != 3
            || parameters[0].ParameterType != typeof(ClientObjectManager).MakePointerType()
            || parameters[1].ParameterType != typeof(uint) || parameters[2].ParameterType != typeof(byte)
            || (target.GetField("creatingCharSelectGameObjects", InstanceFlags)?.GetValue(callback.Target) is not false))
            return false;
        return IsForwardingTitleEditCreate(method, target, callback, hook);
    }

    // Inspect the loaded callback, not a plugin version, file hash or installation path.
    // Only its non-lobby path is admitted: Original(thisPtr, index, assignCompanion),
    // then return the result. Unknown code shapes fail closed without executing them.
    private static bool IsForwardingTitleEditCreate(MethodInfo method, Type target, Delegate callback, IDalamudHook hook)
    {
        try
        {
            var body = method.GetMethodBody();
            var il = body?.GetILAsByteArray();
            if (body == null || il == null || body.ExceptionHandlingClauses.Count != 0
                || body.LocalVariables.Count == 0 || body.LocalVariables[0].LocalType != typeof(uint)
                || !MatchesTitleEditForwarding(il)) return false;
            var hookField = target.GetField("createBattleCharacterHook", InstanceFlags);
            var flagField = target.GetField("creatingCharSelectGameObjects", InstanceFlags);
            var hookType = typeof(Hook<>).MakeGenericType(callback.GetType());
            if (hookField == null || flagField == null || hookField.IsStatic || flagField.IsStatic
                || hookField.DeclaringType != target || flagField.DeclaringType != target
                || hookField.FieldType != hookType || flagField.FieldType != typeof(bool)
                || !ReferenceEquals(hookField.GetValue(callback.Target), hook)) return false;
            var module = method.Module;
            var original = hookType.GetProperty("Original")?.GetMethod;
            var invoke = callback.GetType().GetMethod("Invoke");
            if (original == null || invoke == null) return false;
            return module.ResolveField(BitConverter.ToInt32(il, 2)) == hookField
                && module.ResolveField(BitConverter.ToInt32(il, 15)) == flagField
                && module.ResolveField(BitConverter.ToInt32(il, 33)) == flagField
                && module.ResolveMethod(BitConverter.ToInt32(il, 7)) == original
                && module.ResolveMethod(BitConverter.ToInt32(il, 26)) == invoke;
        }
        catch (Exception)
        {
            // Missing/unsupported managed metadata cannot establish safe forwarding.
            return false;
        }
    }

    internal static bool MatchesTitleEditForwarding(byte[] il)
    {
        // Tokens vary between builds and are resolved to actual members above.
        // The first branch selects arg3 when the lobby flag is false; the second
        // flag test skips all lobby-only work and returns exactly Original's result.
        if (il.Length < 44 || il[0] != 0x02 || il[1] != 0x7B || il[6] != 0x6F
            || il[11] != 0x03 || il[12] != 0x04 || il[13] != 0x02 || il[14] != 0x7B
            || il[19] != 0x2D || il[20] != 3 || il[21] != 0x05
            || il[22] != 0x2B || il[23] != 1 || il[24] != 0x17 || il[25] != 0x6F
            || il[30] != 0x0A || il[31] != 0x02 || il[32] != 0x7B || il[37] != 0x39)
            return false;
        var skip = BitConverter.ToInt32(il, 38);
        return skip >= 0 && skip == il.Length - 44
            && il[il.Length - 2] == 0x06 && il[il.Length - 1] == 0x2A;
    }

    internal static bool MatchesSupportedCode(byte[] original, byte[] loaded, byte[]? savedEntry, int entryLength = 7)
    {
        if (loaded.AsSpan().SequenceEqual(original)) return true;
        // Reloaded 4.2.1 pads its seven-byte absolute indirect jump to whole
        // instructions. These exact supported entries cover seven or ten bytes.
        if ((entryLength != 7 && entryLength != 10)
            || original.Length != loaded.Length || loaded.Length < entryLength
            || savedEntry == null || savedEntry.Length != entryLength
            || loaded[0] != 0xFF || loaded[1] != 0x24 || loaded[2] != 0x25
            || !savedEntry.AsSpan().SequenceEqual(original.AsSpan(0, entryLength))) return false;
        for (var offset = 7; offset < entryLength; offset++)
            if (loaded[offset] != 0x90) return false;
        var comparison = (byte[])loaded.Clone();
        savedEntry.CopyTo(comparison, 0);
        return comparison.AsSpan().SequenceEqual(original);
    }
}
