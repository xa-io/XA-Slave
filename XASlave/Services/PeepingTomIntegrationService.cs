using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class PeepingTomIntegrationService : IDisposable
{
    private const string NormalizedPluginName = "peepingtom";
    private static readonly BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private readonly Stopwatch refreshTimer = Stopwatch.StartNew();
    private readonly List<PreservedTargeterSnapshot> preservedTargeters = new();

    private bool forceEnabled;
    private bool preserveHistoryOnLogoutEnabled;
    private bool subscribed;
    private bool pendingRestore;
    private int lastRestoredCount;

    public PeepingTomIntegrationService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.clientState = clientState;
        this.log = log;
    }

    public bool IsForceEnabled => forceEnabled;

    public bool IsPreserveHistoryOnLogoutEnabled => preserveHistoryOnLogoutEnabled;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetForceEnabled(bool value)
    {
        forceEnabled = value;
        if (!forceEnabled)
        {
            pendingRestore = false;
            lastRestoredCount = 0;
            preservedTargeters.Clear();
            StatusText = "Disabled";
        }
        else if (preserveHistoryOnLogoutEnabled && preservedTargeters.Count > 0)
        {
            pendingRestore = true;
        }

        UpdateSubscriptions();
        RefreshNow();
        return forceEnabled;
    }

    public bool SetPreserveHistoryOnLogoutEnabled(bool value)
    {
        preserveHistoryOnLogoutEnabled = value;
        if (!preserveHistoryOnLogoutEnabled)
        {
            pendingRestore = false;
            lastRestoredCount = 0;
            preservedTargeters.Clear();
        }
        else if (forceEnabled && preservedTargeters.Count > 0 && clientState.IsLoggedIn)
        {
            pendingRestore = true;
        }

        RefreshNow();
        return preserveHistoryOnLogoutEnabled;
    }

    public void Dispose()
    {
        forceEnabled = false;
        preserveHistoryOnLogoutEnabled = false;
        pendingRestore = false;
        lastRestoredCount = 0;
        preservedTargeters.Clear();
        UpdateSubscriptions();
        StatusText = "Disabled";
    }

    private void UpdateSubscriptions()
    {
        var shouldSubscribe = forceEnabled;
        if (shouldSubscribe == subscribed)
            return;

        if (shouldSubscribe)
        {
            framework.Update += OnFrameworkUpdate;
            clientState.Login += OnLogin;
            clientState.Logout += OnLogout;
            refreshTimer.Restart();
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
            clientState.Login -= OnLogin;
            clientState.Logout -= OnLogout;
        }

        subscribed = shouldSubscribe;
    }

    private void OnLogin()
    {
        lastRestoredCount = 0;
        if (preserveHistoryOnLogoutEnabled && preservedTargeters.Count > 0)
            pendingRestore = true;

        refreshTimer.Restart();
    }

    private void OnLogout(int type, int code)
    {
        if (!preserveHistoryOnLogoutEnabled)
            return;

        pendingRestore = preservedTargeters.Count > 0;
        lastRestoredCount = 0;
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
            StatusText = preserveHistoryOnLogoutEnabled && preservedTargeters.Count > 0
                ? $"Enabled - waiting for Peeping Tom to load ({preservedTargeters.Count} history entries cached)."
                : "Enabled - waiting for Peeping Tom to load.";
            return;
        }

        ForcePvpGateOff(pluginInstance);

        if (pendingRestore)
            TryRestoreHistory(pluginInstance);

        if (preserveHistoryOnLogoutEnabled)
            CaptureHistorySnapshot(pluginInstance);

        StatusText = BuildStatusText(pluginInstance);
    }

    private string BuildStatusText(object pluginInstance)
    {
        var status = "Enabled - Peeping Tom PvP tracking is forced on.";
        if (!preserveHistoryOnLogoutEnabled)
            return status;

        var keepHistory = ReadConfigBool(pluginInstance, "KeepHistory");
        if (lastRestoredCount > 0)
        {
            status += $" Restored {lastRestoredCount} history entr";
            status += lastRestoredCount == 1 ? "y" : "ies";
            status += " after login.";
        }
        else if (preservedTargeters.Count > 0)
        {
            status += $" {preservedTargeters.Count} history entries cached for the next character switch.";
        }
        else
        {
            status += " Preserve-on-logout is armed.";
        }

        if (!keepHistory)
            status += " Peeping Tom history display is currently off in its own config.";

        return status;
    }

    private void CaptureHistorySnapshot(object pluginInstance)
    {
        if (!TryGetWatcher(pluginInstance, out var watcher) || watcher == null)
            return;

        var limit = GetHistoryLimit(pluginInstance);
        if (limit <= 0)
        {
            preservedTargeters.Clear();
            return;
        }

        var snapshot = new List<PreservedTargeterSnapshot>(limit);
        var seen = new HashSet<ulong>();
        AppendSnapshotEntries(snapshot, seen, GetTargeters(watcher, "CurrentTargeters"), limit);
        AppendSnapshotEntries(snapshot, seen, GetTargeters(watcher, "PreviousTargeters"), limit);

        if (snapshot.Count == 0 && preservedTargeters.Count > 0)
            return;

        preservedTargeters.Clear();
        preservedTargeters.AddRange(snapshot);
    }

    private void AppendSnapshotEntries(List<PreservedTargeterSnapshot> snapshot, HashSet<ulong> seen, IEnumerable<object> source, int limit)
    {
        foreach (var targeter in source)
        {
            if (snapshot.Count >= limit)
                break;

            if (!TryCreateSnapshot(targeter, out var snapshotEntry))
                continue;

            if (!seen.Add(snapshotEntry.GameObjectId))
                continue;

            snapshot.Add(snapshotEntry);
        }
    }

    private void TryRestoreHistory(object pluginInstance)
    {
        if (preservedTargeters.Count == 0)
        {
            pendingRestore = false;
            lastRestoredCount = 0;
            return;
        }

        if (!TryGetWatcher(pluginInstance, out var watcher) || watcher == null)
            return;

        var previousList = GetMutableTargeterList(watcher);
        if (previousList == null)
            return;

        var targeterType = ResolveTargeterType(watcher, previousList);
        if (targeterType == null)
            return;

        previousList.Clear();

        var restored = 0;
        var limit = GetHistoryLimit(pluginInstance);
        foreach (var snapshot in preservedTargeters.Take(limit))
        {
            try
            {
                var targeter = CreateTargeterInstance(targeterType, snapshot);
                if (targeter == null)
                    break;

                previousList.Add(targeter);
                restored++;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[XASlave] Force PeepingTom could not restore a preserved target-history entry.");
                previousList.Clear();
                preservedTargeters.Clear();
                restored = 0;
                break;
            }
        }

        lastRestoredCount = restored;
        pendingRestore = false;
    }

    private static Type? ResolveTargeterType(object watcher, IList previousList)
    {
        var listType = previousList.GetType();
        if (listType.IsGenericType)
            return listType.GetGenericArguments().FirstOrDefault();

        var previousTargetersProperty = watcher.GetType().GetProperty("PreviousTargeters", InstanceBindings);
        var previousTargetersType = previousTargetersProperty?.PropertyType.GenericTypeArguments.FirstOrDefault();
        if (previousTargetersType != null)
            return previousTargetersType;

        var currentTargetersProperty = watcher.GetType().GetProperty("CurrentTargeters", InstanceBindings);
        return currentTargetersProperty?.PropertyType.GenericTypeArguments.FirstOrDefault();
    }

    private static object? CreateTargeterInstance(Type targeterType, PreservedTargeterSnapshot snapshot)
    {
        var constructor = targeterType.GetConstructor(
            InstanceBindings,
            binder: null,
            new[] { typeof(SeString), typeof(uint), typeof(uint), typeof(ulong), typeof(DateTime) },
            modifiers: null);
        if (constructor == null)
            return null;

        var name = new SeStringBuilder().AddText(snapshot.Name).Build();
        return constructor.Invoke(new object[] { name, snapshot.HomeWorldId, snapshot.EntityId, snapshot.GameObjectId, snapshot.When });
    }

    private static IList? GetMutableTargeterList(object watcher)
    {
        var previousTargetersProperty = watcher.GetType().GetProperty("PreviousTargeters", InstanceBindings);
        if (previousTargetersProperty?.GetValue(watcher) is IList publicList)
            return publicList;

        var previousProperty = watcher.GetType().GetProperty("Previous", InstanceBindings);
        if (previousProperty?.GetValue(watcher) is IList privateList)
            return privateList;

        var previousField = watcher.GetType().GetField("<Previous>k__BackingField", InstanceBindings);
        return previousField?.GetValue(watcher) as IList;
    }

    private static IEnumerable<object> GetTargeters(object watcher, string propertyName)
    {
        var property = watcher.GetType().GetProperty(propertyName, InstanceBindings);
        if (property?.GetValue(watcher) is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
        {
            if (item != null)
                yield return item;
        }
    }

    private static int GetHistoryLimit(object pluginInstance)
    {
        var config = GetPropertyValue(pluginInstance, "Config");
        if (config == null)
            return 5;

        var property = config.GetType().GetProperty("NumHistory", InstanceBindings);
        if (property?.GetValue(config) is not int numHistory)
            return 5;

        return Math.Clamp(numHistory, 0, 50);
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

    private static uint ReadUInt32Property(object targeter, string propertyName)
    {
        var property = targeter.GetType().GetProperty(propertyName, InstanceBindings);
        return property?.GetValue(targeter) switch
        {
            uint value => value,
            int value when value >= 0 => (uint)value,
            ulong value when value <= uint.MaxValue => (uint)value,
            long value when value >= 0 && value <= uint.MaxValue => (uint)value,
            _ => 0U,
        };
    }

    private static DateTime ReadDateTimeProperty(object targeter, string propertyName)
    {
        var property = targeter.GetType().GetProperty(propertyName, InstanceBindings);
        return property?.GetValue(targeter) switch
        {
            DateTime value => value,
            _ => DateTime.UtcNow,
        };
    }

    private static string ReadNameText(object targeter)
    {
        var property = targeter.GetType().GetProperty("Name", InstanceBindings);
        var nameValue = property?.GetValue(targeter);
        return nameValue switch
        {
            SeString seString => seString.TextValue,
            null => string.Empty,
            _ => nameValue.GetType().GetProperty("TextValue", InstanceBindings)?.GetValue(nameValue)?.ToString() ?? nameValue.ToString() ?? string.Empty,
        };
    }

    private static bool TryCreateSnapshot(object targeter, out PreservedTargeterSnapshot snapshot)
    {
        snapshot = default;

        var gameObjectId = ReadUInt64Property(targeter, "GameObjectId");
        var entityId = ReadUInt32Property(targeter, "EntityId");
        var homeWorldId = ReadUInt32Property(targeter, "HomeWorldId");
        var when = ReadDateTimeProperty(targeter, "When");
        var name = ReadNameText(targeter);
        if (gameObjectId == 0 && string.IsNullOrWhiteSpace(name))
            return false;

        snapshot = new PreservedTargeterSnapshot(name, homeWorldId, entityId, gameObjectId, when);
        return true;
    }

    private static ulong ReadUInt64Property(object targeter, string propertyName)
    {
        var property = targeter.GetType().GetProperty(propertyName, InstanceBindings);
        return property?.GetValue(targeter) switch
        {
            ulong value => value,
            uint value => value,
            long value when value >= 0 => (ulong)value,
            int value when value >= 0 => (ulong)value,
            _ => 0UL,
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

    private static bool TryGetWatcher(object pluginInstance, out object? watcher)
    {
        watcher = GetPropertyValue(pluginInstance, "Watcher");
        return watcher != null;
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

    private readonly record struct PreservedTargeterSnapshot(
        string Name,
        uint HomeWorldId,
        uint EntityId,
        ulong GameObjectId,
        DateTime When);
}
