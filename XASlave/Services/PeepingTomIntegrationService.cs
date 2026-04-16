using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class PeepingTomIntegrationService : IDisposable
{
    private const string NormalizedPluginName = "peepingtom";
    private static readonly BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Stopwatch refreshTimer = Stopwatch.StartNew();

    private bool forceEnabled;
    private bool subscribed;

    public PeepingTomIntegrationService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
    }

    public bool IsForceEnabled => forceEnabled;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetForceEnabled(bool value)
    {
        forceEnabled = value;
        UpdateSubscriptions();
        RefreshNow();
        return forceEnabled;
    }

    public void Dispose()
    {
        forceEnabled = false;
        UpdateSubscriptions();
        StatusText = "Disabled";
    }

    private void UpdateSubscriptions()
    {
        if (forceEnabled == subscribed)
            return;

        if (forceEnabled)
        {
            framework.Update += OnFrameworkUpdate;
            refreshTimer.Restart();
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
        }

        subscribed = forceEnabled;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (refreshTimer.Elapsed < TimeSpan.FromMilliseconds(250))
            return;

        refreshTimer.Restart();
        RefreshNow();
    }

    private void RefreshNow()
    {
        if (!forceEnabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!TryGetPeepingTomInstance(out var pluginInstance) || pluginInstance == null)
        {
            StatusText = "Enabled - waiting for Peeping Tom to load.";
            return;
        }

        ForcePvpGateOff(pluginInstance);
        StatusText = ReadConfigBool(pluginInstance, "KeepHistory")
            ? "Enabled - Peeping Tom PvP tracking is forced on."
            : "Enabled - Peeping Tom PvP tracking is forced on. Peeping Tom history is disabled in its own config.";
    }

    private static bool ReadConfigBool(object pluginInstance, string propertyName)
    {
        var config = GetPropertyValue(pluginInstance, "Config");
        if (config == null)
            return false;

        var property = config.GetType().GetProperty(propertyName, InstanceBindings);
        return property?.GetValue(config) switch
        {
            bool value => value,
            _ => false,
        };
    }

    private static void ForcePvpGateOff(object pluginInstance)
    {
        var property = pluginInstance.GetType().GetProperty("InPvp", InstanceBindings);
        if (property != null)
        {
            try
            {
                property.SetValue(pluginInstance, false);
                return;
            }
            catch
            {
            }
        }

        var field = pluginInstance.GetType().GetField("<InPvp>k__BackingField", InstanceBindings);
        field?.SetValue(pluginInstance, false);
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(instance);
    }

    private bool TryGetPeepingTomInstance(out object? pluginInstance)
    {
        pluginInstance = TryGetPluginInstanceFromCollection(pluginInterface.InstalledPlugins);
        if (pluginInstance != null)
            return true;

        pluginInstance = TryGetPluginInstanceFromInternalManager();
        return pluginInstance != null;
    }

    private object? TryGetPluginInstanceFromInternalManager()
    {
        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pluginManagerServiceType == null || pluginManagerType == null)
                return null;

            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);
            if (pluginManager == null)
                return null;

            var installedPlugins = pluginManager.GetType().GetProperty("InstalledPlugins", InstanceBindings)?.GetValue(pluginManager) as IEnumerable;
            return installedPlugins == null ? null : TryGetPluginInstanceFromCollection(installedPlugins);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Force PeepingTom could not inspect Dalamud's internal plugin manager.");
            return null;
        }
    }

    private static object? TryGetPluginInstanceFromCollection(IEnumerable installedPlugins)
    {
        foreach (var pluginState in installedPlugins)
        {
            if (pluginState == null || !IsLoaded(pluginState) || !IsPeepingTom(pluginState))
                continue;

            var pluginType = pluginState.GetType().Name == "LocalDevPlugin"
                ? pluginState.GetType().BaseType
                : pluginState.GetType();
            if (pluginType == null)
                continue;

            var instanceField = pluginType.GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic);
            var instance = instanceField?.GetValue(pluginState);
            if (instance != null)
                return instance;
        }

        return null;
    }

    private static bool IsLoaded(object pluginState)
    {
        return GetBooleanProperty(pluginState, "IsLoaded");
    }

    private static bool IsPeepingTom(object pluginState)
    {
        return IsMatchingPluginName(GetStringProperty(pluginState, "InternalName"))
               || IsMatchingPluginName(GetStringProperty(pluginState, "Name"));
    }

    private static bool IsMatchingPluginName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Replace(" ", string.Empty).Equals(NormalizedPluginName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetStringProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(instance)?.ToString();
    }

    private static bool GetBooleanProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(instance) switch
        {
            bool value => value,
            _ => false,
        };
    }
}
