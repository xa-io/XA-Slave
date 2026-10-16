using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
    private const string SupportedTitleEditSha256 = "66B2F37E86678CA9619DE9C7747BE6E72D37C1B75F574A2900434A1E81292690";
    private const string SupportedTitleEditModule = "26aa1d69-094c-482b-b372-84d84f9955ef";

    private static Type Manager => typeof(IDalamudHook).Assembly.GetType("Dalamud.Hooking.Internal.HookManager")
        ?? throw new InvalidOperationException("The preview hook registry is unavailable.");

    internal static Lock GetSyncRoot()
    {
        var assembly = typeof(IDalamudHook).Assembly;
        if (!IsSupportedBackendPair(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Reloaded.Hooks.ReloadedHooks).Assembly.Location)))))
            throw new InvalidOperationException("View Item requires a revalidated Dalamud hook backend.");
        return Manager.GetProperty("HookEnableSyncRoot", StaticFlags)?.GetValue(null) as Lock
            ?? throw new InvalidOperationException("The preview hook synchronization changed.");
    }

    // Each pair has matching emitted hook/loader metadata and backend bodies.
    // Do not combine independently admitted host and backend artifacts.
    internal static bool IsSupportedBackendPair(string dalamudSha256, string reloadedSha256)
    {
        return (dalamudSha256 == SupportedDalamudSha256 && reloadedSha256 == SupportedReloadedSha256)
            || (dalamudSha256 == Dalamud15037Sha256 && reloadedSha256 == Reloaded15037Sha256);
    }

    // Caller holds the actual Dalamud Lock through comparison, creation and enable.
    internal static byte[]? ReadSavedEntry(nint entry, int length, bool allowTitleEditCreate = false)
    {
        var manager = Manager;
        if (manager.GetProperty("TrackedHooks", StaticFlags)?.GetValue(null) is not IDictionary tracked
            || manager.GetProperty("Unhookers", StaticFlags)?.GetValue(null) is not IDictionary unhookers)
            throw new InvalidOperationException("The preview hook registry changed.");
        foreach (var value in tracked.Values)
        {
            if (value == null || value.GetType().GetProperty("Hook", InstanceFlags)?.GetValue(value) is not IDalamudHook hook)
                throw new InvalidOperationException("The preview hook registration changed.");
            if (hook.IsDisposed) continue;
            var offset = (long)hook.Address - (long)entry;
            if (offset >= 0 && offset < length)
            {
                // Only the reviewed TitleEdit character-creation hook may chain here.
                // Its Original remains in the chain; no foreign hook is changed.
                if (offset == 0 && allowTitleEditCreate && IsReviewedTitleEditCreate(value, hook)) continue;
                throw new InvalidOperationException("The preview renderer has another registered hook. "
                    + ViewSingleItemRendererDiagnostics.DescribeOwners(entry, length));
            }
        }
        if (!unhookers.Contains(entry)) return null;
        var saved = unhookers[entry];
        var type = typeof(IDalamudHook).Assembly.GetType("Dalamud.Hooking.Internal.Unhooker");
        if (saved == null || type == null || saved.GetType() != type
            || type.GetField("address", InstanceFlags)?.GetValue(saved) is not nint address || address != entry
            || type.GetField("trimmed", InstanceFlags)?.GetValue(saved) is not true
            || type.GetField("originalBytes", InstanceFlags)?.GetValue(saved) is not byte[] prefix)
            throw new InvalidOperationException("The saved preview renderer entry cannot be verified.");
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
            || assembly.GetName().Name != "TitleEdit" || assembly.ManifestModule.ModuleVersionId.ToString() != SupportedTitleEditModule
            || method.Module != assembly.ManifestModule || method.IsStatic
            || target.Assembly != assembly || method.DeclaringType != target
            || target.FullName != "TitleEdit.PluginServices.Lobby.LobbyService" || method.Name != "CreateBattleCharacterDetour"
            || method.ReturnType != typeof(uint) || parameters.Length != 3
            || parameters[0].ParameterType != typeof(ClientObjectManager).MakePointerType()
            || parameters[1].ParameterType != typeof(uint) || parameters[2].ParameterType != typeof(byte)
            || (target.GetField("creatingCharSelectGameObjects", InstanceFlags)?.GetValue(callback.Target) is not false))
            return false;
        var path = assembly.Location;
        if (string.IsNullOrEmpty(path))
        {
            // Dalamud loads plugins from streams, leaving Assembly.Location empty.
            // Use only this assembly's owning, reviewed loader; never search for a DLL.
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            var loaderType = typeof(IDalamudHook).Assembly.GetType("Dalamud.Plugin.Internal.Loader.ManagedLoadContext");
            if (context == null || loaderType == null || context.GetType() != loaderType
                || (loaderType.GetField("mainAssemblyPath", InstanceFlags)?.GetValue(context) is not string sourcePath))
                return false;
            path = sourcePath;
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == SupportedTitleEditSha256;
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
