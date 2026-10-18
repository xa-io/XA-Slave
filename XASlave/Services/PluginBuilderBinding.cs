using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;

namespace XASlave.Services;

internal sealed record PluginBuilderBinding(PluginBuilderSource Source, Type[] Parameters, Type ResultType,
    MemberInfo? Member, FieldInfo[] OwnerPath, MethodInfo? ProviderMethod, WeakReference<Delegate>? ProviderIdentity);

internal static class PluginBuilderBindingRules
{
    internal static object? ResolveLoadedPlugin(Plugin plugin, string name)
    {
        if (name == "XASlave") return plugin;
        var host = typeof(IDalamudPluginInterface).Assembly;
        if (Plugin.PluginInterface.GetType() != host.GetType("Dalamud.Plugin.DalamudPluginInterface")) return null;
        var managerType = host.GetType("Dalamud.Plugin.Internal.PluginManager");
        var localType = host.GetType("Dalamud.Plugin.Internal.Types.LocalPlugin");
        var mode = host.GetType("Dalamud.ExceptionPropagationMode");
        var service = host.GetType("Dalamud.Service`1");
        if (managerType == null || localType == null || mode == null || service == null) return null;
        var closed = service.MakeGenericType(managerType);
        var get = closed.GetMethod("GetNullable", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { mode }, modifiers: null);
        if (get?.ReturnType != managerType || get.DeclaringType != closed || !mode.IsEnum
            || !Enum.TryParse(mode, "None", out var none) || Convert.ToInt32(none) != 2) return null;
        var manager = get.Invoke(null, new[] { none });
        if (manager?.GetType() != managerType) return null;
        var installed = managerType.GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance);
        if (installed?.GetMethod?.DeclaringType != managerType || installed.GetValue(manager) is not IEnumerable entries) return null;
        object? result = null;
        var count = 0;
        foreach (var entry in entries)
        {
            if (++count > 2048) return null;
            if (entry == null || entry.GetType().Assembly != host || !localType.IsInstanceOfType(entry)) continue;
            var internalName = localType.GetProperty("InternalName", BindingFlags.Public | BindingFlags.Instance);
            var loaded = localType.GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Instance);
            if (internalName?.GetMethod?.DeclaringType?.Assembly != host || loaded?.GetMethod?.DeclaringType?.Assembly != host) return null;
            if (!Equals(internalName.GetValue(entry), name) || !Equals(loaded.GetValue(entry), true)) continue;
            for (var type = entry.GetType(); type != null && type.Assembly == host; type = type.BaseType)
            {
                var field = type.GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                var instance = field.GetValue(entry);
                if (result != null || instance == null) return null;
                result = instance;
                break;
            }
        }
        return result;
    }

    internal static bool SupportsArgument(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(byte)
            || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(char);
    }

    internal static object? ParseArgument(PluginBuilderArgument argument, Type type)
    {
        var value = argument.Value ?? string.Empty;
        if (value.Length > 2048) throw new InvalidOperationException("Argument exceeds 2048 characters.");
        if (argument.UseCurrentCharacterId && argument.UseCurrentCharacterName) throw new InvalidOperationException("Choose one character binding.");
        if (argument.UseCurrentCharacterId)
        {
            if (type != typeof(ulong) || !Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
                throw new InvalidOperationException("Current character ID requires a loaded character and UInt64 parameter.");
            return Plugin.PlayerState.ContentId;
        }
        if (argument.UseCurrentCharacterName)
        {
            if (type != typeof(string) || !Plugin.PlayerState.IsLoaded) throw new InvalidOperationException("Current name requires a loaded character and String parameter.");
            return Plugin.PlayerState.CharacterName.ToString();
        }
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null && value == "null") return null;
        type = underlying ?? type;
        if (!SupportsArgument(type)) throw new InvalidOperationException("Unsupported argument type.");
        if (type == typeof(string)) return value;
        if (type.IsEnum)
        {
            if (!Enum.TryParse(type, value, ignoreCase: false, out var parsed)) throw new InvalidOperationException("Invalid enum argument.");
            return parsed;
        }
        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    internal static string TypeKey(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    internal static string MemberId(MemberInfo member, IEnumerable<FieldInfo> path)
        => "member:" + member.Module.ModuleVersionId.ToString("D") + ":" + member.MetadataToken.ToString(CultureInfo.InvariantCulture)
            + ":" + string.Join("/", path.Select(field => field.MetadataToken.ToString(CultureInfo.InvariantCulture)));

    internal static bool Matches(PluginBuilderSource saved, PluginBuilderSource live)
        => saved.Id == live.Id && saved.Kind == live.Kind && saved.PluginName == live.PluginName
            && saved.ModuleVersionId == live.ModuleVersionId && saved.Channel == live.Channel
            && saved.DeclaringType == live.DeclaringType && saved.MetadataToken == live.MetadataToken
            && saved.Static == live.Static && saved.ReturnType == live.ReturnType
            && saved.FieldPath != null && saved.FieldPath.SequenceEqual(live.FieldPath, StringComparer.Ordinal)
            && saved.ParameterTypes != null && saved.ParameterTypes.SequenceEqual(live.ParameterTypes, StringComparer.Ordinal);
}
