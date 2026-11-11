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
        => TryResolve(host, initializedPluginInterface, out _);

    internal static DalamudPeerLifecycle? TryResolve(Assembly host, object initializedPluginInterface, out string reason)
    {
        reason = "Host lifecycle interface is unavailable or incompatible";
        try
        {
            // The actual DalamudPluginInterface constructor already obtains
            // Service<PluginManager>.Get before returning. This provenance is
            // required: GetNullable's generic static initializer can otherwise
            // block waiting for ServiceContainer in an uninitialized process.
            var interfaceType = RequiredType(host, "Dalamud.Plugin.DalamudPluginInterface");
            if (initializedPluginInterface == null || initializedPluginInterface.GetType() != interfaceType)
                return null;
            reason = "Host lifecycle types are unavailable";
            var manager = RequiredType(host, "Dalamud.Plugin.Internal.PluginManager");
            var local = RequiredType(host, "Dalamud.Plugin.Internal.Types.LocalPlugin");
            var phase = RequiredType(host, "Dalamud.Plugin.Internal.Types.PluginState");
            var propagation = RequiredType(host, "Dalamud.ExceptionPropagationMode");
            reason = "Host lifecycle enum contract is incompatible";
            ValidateEnum(phase, ("Unloaded", 0), ("UnloadError", 1), ("Unloading", 2), ("Loaded", 3),
                ("LoadError", 4), ("Loading", 5), ("DependencyResolutionFailed", 6));
            ValidateEnum(propagation, ("PropagateAll", 0), ("PropagateNonUnloaded", 1), ("None", 2));
            reason = "Host plugin manager service getter is unavailable or incompatible";
            var service = RequiredType(host, "Dalamud.Service`1").MakeGenericType(manager);
            var get = service.GetMethod("GetNullable", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: [propagation], modifiers: null);
            if (get == null || get.ReturnType != manager || get.IsGenericMethod || get.DeclaringType?.Assembly != host)
                return null;
            reason = "Host installed-plugin collection getter is unavailable or incompatible";
            var installed = RequiredGetter(manager, "InstalledPlugins", typeof(IEnumerable<>).MakeGenericType(local));
            reason = "Host plugin name getter is unavailable or incompatible";
            var name = RequiredGetter(local, "InternalName", typeof(string));
            reason = "Host plugin installation ID getter is unavailable or incompatible";
            var id = RequiredGetter(local, "EffectiveWorkingPluginId", typeof(Guid));
            reason = "Host plugin state getter is unavailable or incompatible";
            var pluginState = RequiredGetter(local, "State", phase);
            var result = new DalamudPeerLifecycle(local, get, Enum.ToObject(propagation, 2), installed, name, id, pluginState);
            reason = string.Empty;
            return result;
        }
        catch (Exception error)
        {
            reason += $" ({error.GetType().Name})";
            return null;
        }
    }

    internal PeerPluginObservation Observe(string peerInternalName)
    {
        if (string.IsNullOrWhiteSpace(peerInternalName))
            return new(PeerPluginPhase.Unknown, Guid.Empty, "Peer plugin name is unavailable");
        try
        {
            // The installed SDK's GetNullable(None) returns immediately when its
            // service task is incomplete/faulted. Do not use Get/GetAsync here.
            var manager = getNullable.Invoke(null, [noPropagation]);
            if (manager == null)
                return new(PeerPluginPhase.Unknown, Guid.Empty, "Host plugin manager is unavailable");
            if (installedPlugins.GetValue(manager) is not IEnumerable plugins)
                return new(PeerPluginPhase.Unknown, Guid.Empty, "Host installed-plugin collection is unavailable");
            PeerPluginObservation? active = null;
            Guid? unloadedId = null;
            var matchingIds = new HashSet<Guid>();
            var count = 0;
            foreach (var plugin in plugins)
            {
                if (++count > 1024)
                    return new(PeerPluginPhase.Unknown, Guid.Empty, "Host installed-plugin collection exceeds the validation limit");
                if (plugin == null || !localPluginType.IsInstanceOfType(plugin) ||
                    plugin.GetType().Assembly != localPluginType.Assembly)
                    return new(PeerPluginPhase.Unknown, Guid.Empty, "Host installed-plugin collection contains an invalid record");
                if (internalName.GetValue(plugin) is not string name)
                    return new(PeerPluginPhase.Unknown, Guid.Empty, "Host installed-plugin record has an invalid name");
                if (!string.Equals(name, peerInternalName, StringComparison.Ordinal)) continue;
                if (installationId.GetValue(plugin) is not Guid id || id == Guid.Empty)
                    return new(PeerPluginPhase.Unknown, Guid.Empty, "Peer installation ID is unavailable or invalid");
                if (!matchingIds.Add(id))
                    return new(PeerPluginPhase.Unknown, Guid.Empty, "Peer installation ID appears more than once");
                var rawState = state.GetValue(plugin);
                if (rawState == null || rawState.GetType() != state.PropertyType)
                    return new(PeerPluginPhase.Unknown, id, "Peer lifecycle state is unavailable or invalid");
                var value = Convert.ToInt32(rawState);
                var observed = value switch
                {
                    0 => PeerPluginPhase.Unloaded,
                    2 => PeerPluginPhase.Unloading,
                    3 => PeerPluginPhase.Loaded,
                    5 => PeerPluginPhase.Loading,
                    1 or 4 or 6 => PeerPluginPhase.Failed,
                    _ => PeerPluginPhase.Unknown,
                };
                if (observed == PeerPluginPhase.Unknown)
                    return new(PeerPluginPhase.Unknown, id, "Peer lifecycle state is not recognized");
                if (observed == PeerPluginPhase.Unloaded)
                {
                    // Stable across host enumeration order. Retained IPC publications
                    // still have to pass FeatureConflictPeer's normal checks.
                    if (!unloadedId.HasValue || id.CompareTo(unloadedId.Value) < 0) unloadedId = id;
                    continue;
                }
                if (active.HasValue)
                    return new(PeerPluginPhase.Unknown, Guid.Empty, "Multiple peer installations are not fully unloaded");
                var detail = observed switch
                {
                    PeerPluginPhase.Loading => "Peer plugin is loading",
                    PeerPluginPhase.Unloading => "Peer plugin is unloading",
                    PeerPluginPhase.Failed => "Peer plugin load, unload or dependency resolution failed",
                    _ => null,
                };
                active = new(observed, id, detail);
            }
            // Choose only after validating the entire collection, so a later
            // malformed or ambiguous record cannot be hidden by a loaded match.
            return active ?? (unloadedId.HasValue
                ? new PeerPluginObservation(PeerPluginPhase.Unloaded, unloadedId.Value)
                : new PeerPluginObservation(PeerPluginPhase.Absent, Guid.Empty));
        }
        catch (Exception error)
        {
            return new(PeerPluginPhase.Unknown, Guid.Empty, $"Peer lifecycle could not be read ({error.GetType().Name})");
        }
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
