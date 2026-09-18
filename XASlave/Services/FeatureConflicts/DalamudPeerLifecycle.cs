using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace XA.FeatureConflicts;

// Host lifecycle properties only. Never inspect a peer plugin instance,
// configuration, fields, or private implementation assembly.
internal sealed class DalamudPeerLifecycle
{
    private readonly Type localPluginType;
    private readonly MethodInfo getNullable;
    private readonly object noPropagation;
    private readonly PropertyInfo installedPlugins;
    private readonly PropertyInfo internalName;
    private readonly PropertyInfo installationId;
    private readonly PropertyInfo state;

    private DalamudPeerLifecycle(Type localPluginType, MethodInfo getNullable, object noPropagation,
        PropertyInfo installedPlugins, PropertyInfo internalName, PropertyInfo installationId, PropertyInfo state)
    {
        this.localPluginType = localPluginType;
        this.getNullable = getNullable;
        this.noPropagation = noPropagation;
        this.installedPlugins = installedPlugins;
        this.internalName = internalName;
        this.installationId = installationId;
        this.state = state;
    }

    internal static DalamudPeerLifecycle? TryResolve(Assembly host, object initializedPluginInterface)
    {
        try
        {
            // The actual DalamudPluginInterface constructor already obtains
            // Service<PluginManager>.Get before returning. This provenance is
            // required: GetNullable's generic static initializer can otherwise
            // block waiting for ServiceContainer in an uninitialized process.
            var interfaceType = RequiredType(host, "Dalamud.Plugin.DalamudPluginInterface");
            if (initializedPluginInterface == null || initializedPluginInterface.GetType() != interfaceType)
                return null;
            var manager = RequiredType(host, "Dalamud.Plugin.Internal.PluginManager");
            var local = RequiredType(host, "Dalamud.Plugin.Internal.Types.LocalPlugin");
            var phase = RequiredType(host, "Dalamud.Plugin.Internal.Types.PluginState");
            var propagation = RequiredType(host, "Dalamud.ExceptionPropagationMode");
            ValidateEnum(phase, ("Unloaded", 0), ("UnloadError", 1), ("Unloading", 2), ("Loaded", 3),
                ("LoadError", 4), ("Loading", 5), ("DependencyResolutionFailed", 6));
            ValidateEnum(propagation, ("PropagateAll", 0), ("PropagateNonUnloaded", 1), ("None", 2));
            var service = RequiredType(host, "Dalamud.Service`1").MakeGenericType(manager);
            var get = service.GetMethod("GetNullable", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: [propagation], modifiers: null);
            if (get == null || get.ReturnType != manager || get.IsGenericMethod || get.DeclaringType?.Assembly != host)
                return null;
            return new(local, get, Enum.ToObject(propagation, 2),
                RequiredGetter(manager, "InstalledPlugins", typeof(IEnumerable<>).MakeGenericType(local)),
                RequiredGetter(local, "InternalName", typeof(string)), RequiredGetter(local, "EffectiveWorkingPluginId", typeof(Guid)),
                RequiredGetter(local, "State", phase));
        }
        catch { return null; }
    }

    internal PeerPluginObservation Observe(string peerInternalName)
    {
        if (string.IsNullOrWhiteSpace(peerInternalName)) return default;
        try
        {
            // The installed SDK's GetNullable(None) returns immediately when its
            // service task is incomplete/faulted. Do not use Get/GetAsync here.
            var manager = getNullable.Invoke(null, [noPropagation]);
            if (manager == null || installedPlugins.GetValue(manager) is not IEnumerable plugins) return default;
            var found = false;
            var result = new PeerPluginObservation(PeerPluginPhase.Absent, Guid.Empty);
            var count = 0;
            foreach (var plugin in plugins)
            {
                if (++count > 1024 || plugin == null || !localPluginType.IsInstanceOfType(plugin) ||
                    plugin.GetType().Assembly != localPluginType.Assembly) return default;
                if (internalName.GetValue(plugin) is not string name) return default;
                if (!string.Equals(name, peerInternalName, StringComparison.Ordinal)) continue;
                if (found || installationId.GetValue(plugin) is not Guid id || id == Guid.Empty) return default;
                found = true;
                var value = Convert.ToInt32(state.GetValue(plugin));
                var observed = value switch
                {
                    0 => PeerPluginPhase.Unloaded,
                    2 => PeerPluginPhase.Unloading,
                    3 => PeerPluginPhase.Loaded,
                    5 => PeerPluginPhase.Loading,
                    1 or 4 or 6 => PeerPluginPhase.Failed,
                    _ => PeerPluginPhase.Unknown,
                };
                result = new(observed, id);
            }
            return result;
        }
        catch { return default; }
    }

    private static Type RequiredType(Assembly host, string name) =>
        host.GetType(name, throwOnError: true)!;

    private static PropertyInfo RequiredGetter(Type owner, string name, Type propertyType)
    {
        var property = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || property.PropertyType != propertyType || property.GetIndexParameters().Length != 0 ||
            property.GetMethod is not { IsPublic: true, IsStatic: false } getter || getter.DeclaringType?.Assembly != owner.Assembly)
            throw new InvalidOperationException("The host lifecycle contract changed.");
        return property;
    }

    private static void ValidateEnum(Type type, params (string Name, int Value)[] required)
    {
        if (!type.IsEnum || Enum.GetUnderlyingType(type) != typeof(int) || Enum.GetNames(type).Length != required.Length)
            throw new InvalidOperationException("The host lifecycle enum changed.");
        foreach (var (name, value) in required)
            if (!Enum.TryParse(type, name, false, out var parsed) || Convert.ToInt32(parsed) != value)
                throw new InvalidOperationException("The host lifecycle enum changed.");
    }
}
