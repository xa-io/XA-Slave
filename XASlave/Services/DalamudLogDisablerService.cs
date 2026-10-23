using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

// Suppresses a named plugin's log output to Dalamud (/xllog window AND the on-disk log file).
//
// Dalamud gives each plugin a scoped IPluginLog implemented by Dalamud.Logging.ScopedPluginLogService.
// That service builds a Serilog sub-logger gated by MinimumLevel.ControlledBy(levelSwitch); its public
// MinimumLogLevel property simply moves that switch. Setting the switch above Fatal makes the plugin's
// logger drop everything before it reaches the shared Serilog pipeline, so nothing is written to the log
// window or the file. The instance lives in the plugin's ServiceScope (ServiceScopeImpl.scopeCreatedObjects,
// a ConcurrentDictionary<Type, Task<object>>); we read the cached instance so there are no creation side effects.
public sealed class DalamudLogDisablerService : IDisposable
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly PropertyInfo? minimumLogLevelProperty;
    private readonly Type? logEventLevelType;
    private readonly int maxDefinedLevel;

    // Serilog LogEventLevel is a single threshold (Verbose=0 .. Fatal=5); the switch keeps events whose
    // level >= this value. So "keep Warning/Error/Fatal, drop Info/Debug/Verbose" is minimumKeptLevel=Warning.
    // maxDefinedLevel+1 is a sentinel above Fatal that drops everything (full mute).
    private int minimumKeptLevelInt;
    private object? suppressLevelValue;

    private readonly Dictionary<string, SuppressionRecord> suppressed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> blockedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool enabled;
    private bool subscribed;
    private DateTime nextReconcileUtc = DateTime.MinValue;
    private bool reflectionUnavailableLogged;
    private int lastSuppressedCount;

    public DalamudLogDisablerService(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;

        minimumLogLevelProperty = typeof(IPluginLog).GetProperty("MinimumLogLevel", InstanceFlags);
        logEventLevelType = minimumLogLevelProperty?.PropertyType;
        maxDefinedLevel = logEventLevelType is { IsEnum: true }
            ? Enum.GetValues(logEventLevelType).Cast<object>().Select(Convert.ToInt32).DefaultIfEmpty(5).Max()
            : 5;
        minimumKeptLevelInt = BlockAllLevel;
        RecomputeSuppressLevel();
    }

    // The value that drops every standard level (one above Fatal).
    public int BlockAllLevel => maxDefinedLevel + 1;

    public int MinimumKeptLevel => minimumKeptLevelInt;

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    // Options for the UI: each kept-level threshold plus the full-mute sentinel, most-permissive first.
    public IReadOnlyList<(int Value, string Label, string Blocked)> GetLevelOptions()
        => GetLevelOptionMessages().Select(option => (option.Value, option.Label.Original, option.Blocked.Original)).ToArray();

    internal IReadOnlyList<(int Value, global::XASlave.Localization.UiMessage Label, global::XASlave.Localization.UiMessage Blocked)> GetLevelOptionMessages()
    {
        if (logEventLevelType is not { IsEnum: true })
            return [(BlockAllLevel, global::XASlave.Localization.UiMessage.Key("SFB180217165A7FC1"), global::XASlave.Localization.UiMessage.Key("SA0A44ED8CFC32B7E"))];

        var levels = Enumerable.Range(0, maxDefinedLevel + 1)
            .Select(value => (Value: value, Name: Enum.GetName(logEventLevelType, Enum.ToObject(logEventLevelType, value)) is { } name ? global::XASlave.Localization.UiMessage.Raw(name) : global::XASlave.Localization.UiMessage.Format("S06ED7C0B2BB7A3AF", value)))
            .ToList();

        var options = new List<(int Value, global::XASlave.Localization.UiMessage Label, global::XASlave.Localization.UiMessage Blocked)>();
        // Keep-from-level thresholds (skip Verbose=0, which would mute nothing).
        for (var value = 1; value <= maxDefinedLevel; value++)
        {
            var name = levels[value].Name;
            var blocked = levels.Where(l => l.Value < value).Select(l => l.Name);
            options.Add((value, global::XASlave.Localization.UiMessage.Format("SA5CAD227D19BD90A", name), global::XASlave.Localization.UiMessage.Join(", ", blocked)));
        }

        options.Add((BlockAllLevel, global::XASlave.Localization.UiMessage.Key("S598E2E9FA2E9C2AD"), global::XASlave.Localization.UiMessage.Key("SA0A44ED8CFC32B7E")));
        return options;
    }

    public IReadOnlyList<(string InternalName, string Name)> GetLoadedPlugins()
    {
        var result = new List<(string InternalName, string Name)>();
        try
        {
            foreach (var localPlugin in EnumerateLocalPlugins())
            {
                if (!GetBool(localPlugin, "IsLoaded"))
                    continue;

                var internalName = GetString(localPlugin, "InternalName");
                var name = GetString(localPlugin, "Name");
                if (string.IsNullOrWhiteSpace(internalName) && string.IsNullOrWhiteSpace(name))
                    continue;

                result.Add((internalName, string.IsNullOrWhiteSpace(name) ? internalName : name));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Dalamud Log Disabler could not enumerate loaded plugins.");
        }

        return result
            .GroupBy(entry => entry.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ApplyConfiguration(IEnumerable<string>? blocked, int minimumKeptLevel)
    {
        blockedNames = blocked == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(blocked.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);

        minimumKeptLevelInt = Math.Clamp(minimumKeptLevel, 0, BlockAllLevel);
        RecomputeSuppressLevel();

        if (enabled)
        {
            nextReconcileUtc = DateTime.MinValue;
            Reconcile();
        }
    }

    private void RecomputeSuppressLevel()
    {
        suppressLevelValue = logEventLevelType is { IsEnum: true }
            ? Enum.ToObject(logEventLevelType, Math.Clamp(minimumKeptLevelInt, 0, BlockAllLevel))
            : null;
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            UpdateStatusText();
            return enabled;
        }

        if (!value)
        {
            enabled = false;
            RestoreAll();
            Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        if (minimumLogLevelProperty == null || suppressLevelValue == null)
        {
            enabled = false;
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SBA613C85370DFC58"));
            if (!reflectionUnavailableLogged)
            {
                reflectionUnavailableLogged = true;
                log.Warning("[XASlave] Dalamud Log Disabler could not resolve IPluginLog.MinimumLogLevel; the mod cannot function on this Dalamud build.");
            }

            return false;
        }

        enabled = true;
        Subscribe();
        nextReconcileUtc = DateTime.MinValue;
        Reconcile();
        UpdateStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        RestoreAll();
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        framework.Update += OnFrameworkUpdate;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        framework.Update -= OnFrameworkUpdate;
        subscribed = false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextReconcileUtc)
            return;

        nextReconcileUtc = DateTime.UtcNow.Add(ReconcileInterval);
        Reconcile();
    }

    private void Reconcile()
    {
        if (!enabled)
            return;

        try
        {
            var loadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suppressedCount = 0;

            foreach (var localPlugin in EnumerateLocalPlugins())
            {
                if (!GetBool(localPlugin, "IsLoaded"))
                    continue;

                var internalName = GetString(localPlugin, "InternalName");
                var name = GetString(localPlugin, "Name");
                var key = string.IsNullOrWhiteSpace(internalName) ? name : internalName;
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                loadedKeys.Add(key);
                var isBlocked = IsBlocked(internalName, name);

                if (isBlocked)
                {
                    if (SuppressPlugin(localPlugin, key))
                        suppressedCount++;
                }
                else if (suppressed.ContainsKey(key))
                {
                    RestorePlugin(localPlugin, key);
                }
            }

            // Drop stale records for plugins that are no longer loaded (their scope is gone; nothing to restore).
            foreach (var staleKey in suppressed.Keys.Where(k => !loadedKeys.Contains(k)).ToList())
                suppressed.Remove(staleKey);

            lastSuppressedCount = suppressedCount;
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            if (!reflectionUnavailableLogged)
            {
                reflectionUnavailableLogged = true;
                log.Warning(ex, "[XASlave] Dalamud Log Disabler failed while reconciling plugin log levels.");
            }
        }
    }

    private bool SuppressPlugin(object localPlugin, string key)
    {
        var logService = TryGetPluginLogService(localPlugin);
        if (logService == null)
            return false;

        try
        {
            var currentLevel = minimumLogLevelProperty!.GetValue(logService);
            var currentInt = currentLevel != null ? Convert.ToInt32(currentLevel) : 0;
            var desiredInt = Convert.ToInt32(suppressLevelValue!);

            var tracked = suppressed.TryGetValue(key, out var record)
                && record.Service.TryGetTarget(out var trackedService)
                && ReferenceEquals(trackedService, logService);

            if (tracked)
            {
                // Enforce the desired threshold: re-apply if the keep-level changed or the plugin reset it.
                if (currentInt != desiredInt)
                    minimumLogLevelProperty.SetValue(logService, suppressLevelValue);

                return true;
            }

            // New or reloaded instance: capture its real level before we change it. If it is already sitting at
            // our full-mute sentinel we cannot know the original, so record null and restore a default later.
            var originalLevel = currentInt >= BlockAllLevel ? null : currentLevel;
            if (currentInt != desiredInt)
                minimumLogLevelProperty.SetValue(logService, suppressLevelValue);

            suppressed[key] = new SuppressionRecord(new WeakReference<object>(logService), originalLevel);
            log.Information($"[XASlave] Dalamud Log Disabler set '{key}' minimum log level to {DescribeLevel(desiredInt)}.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Dalamud Log Disabler failed to set the log level for '{key}'.");
            return false;
        }
    }

    private string DescribeLevel(int value)
    {
        if (logEventLevelType is not { IsEnum: true })
            return value.ToString();

        return value > maxDefinedLevel
            ? "block all"
            : Enum.GetName(logEventLevelType, Enum.ToObject(logEventLevelType, value)) ?? value.ToString();
    }

    private void RestorePlugin(object localPlugin, string key)
    {
        if (!suppressed.TryGetValue(key, out var record))
            return;

        suppressed.Remove(key);
        var logService = (record.Service.TryGetTarget(out var trackedService) ? trackedService : null) ?? TryGetPluginLogService(localPlugin);
        RestoreLevel(logService, record.OriginalLevel, key);
    }

    private void RestoreAll()
    {
        foreach (var (key, record) in suppressed.ToList())
        {
            var logService = record.Service.TryGetTarget(out var trackedService) ? trackedService : null;
            RestoreLevel(logService, record.OriginalLevel, key);
        }

        suppressed.Clear();
        lastSuppressedCount = 0;
    }

    private void RestoreLevel(object? logService, object? originalLevel, string key)
    {
        if (logService == null || minimumLogLevelProperty == null)
            return;

        try
        {
            // Fall back to Debug (Dalamud's default for downloaded plugins) if the original was never captured.
            var restoreTo = originalLevel ?? (logEventLevelType != null ? Enum.ToObject(logEventLevelType, 1) : null);
            if (restoreTo != null)
                minimumLogLevelProperty.SetValue(logService, restoreTo);

            log.Information($"[XASlave] Dalamud Log Disabler restored Dalamud logging for '{key}'.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Dalamud Log Disabler failed to restore logging for '{key}'.");
        }
    }

    private bool IsBlocked(string internalName, string name)
    {
        return (!string.IsNullOrWhiteSpace(internalName) && blockedNames.Contains(internalName.Trim()))
               || (!string.IsNullOrWhiteSpace(name) && blockedNames.Contains(name.Trim()));
    }

    private object? TryGetPluginLogService(object localPlugin)
    {
        try
        {
            var scope = localPlugin.GetType().GetProperty("ServiceScope", InstanceFlags)?.GetValue(localPlugin);
            var scopeObjects = scope?.GetType().GetField("scopeCreatedObjects", InstanceFlags)?.GetValue(scope);
            if (scopeObjects is not IDictionary dictionary)
                return null;

            foreach (var value in dictionary.Values)
            {
                if (value is not Task task || !task.IsCompletedSuccessfully)
                    continue;

                var result = task.GetType().GetProperty("Result", InstanceFlags)?.GetValue(task);
                if (result is IPluginLog pluginLog)
                    return pluginLog;
            }

            return null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Dalamud Log Disabler failed to resolve a plugin's log service.");
            return null;
        }
    }

    private IEnumerable<object> EnumerateLocalPlugins()
    {
        var pluginManager = GetPluginManager();
        if (pluginManager == null)
            return [];

        var installed = pluginManager.GetType().GetProperty("InstalledPlugins", InstanceFlags)?.GetValue(pluginManager) as IEnumerable;
        if (installed == null)
            return [];

        var list = new List<object>();
        foreach (var localPlugin in installed)
        {
            if (localPlugin != null)
                list.Add(localPlugin);
        }

        return list;
    }

    private object? GetPluginManager()
    {
        try
        {
            var dalamudAssembly = typeof(IFramework).Assembly;
            var serviceGenericType = dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: false);
            var pluginManagerType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: false);
            if (serviceGenericType == null || pluginManagerType == null)
                return null;

            var closedServiceType = serviceGenericType.MakeGenericType(pluginManagerType);
            var getNullable = closedServiceType.GetMethod("GetNullable", StaticFlags);
            if (getNullable != null)
            {
                var parameters = getNullable.GetParameters();
                if (parameters.Length == 0)
                    return getNullable.Invoke(null, null);

                var propagationMode = Enum.Parse(parameters[0].ParameterType, "None");
                return getNullable.Invoke(null, [propagationMode]);
            }

            return closedServiceType.GetMethod("Get", StaticFlags, Type.EmptyTypes)?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Dalamud Log Disabler could not resolve Dalamud's PluginManager.");
            return null;
        }
    }

    private static bool GetBool(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceFlags)?.GetValue(instance) is true;
    }

    private static string GetString(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceFlags)?.GetValue(instance)?.ToString() ?? string.Empty;
    }

    private void UpdateStatusText()
    {
        if (!enabled)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        if (blockedNames.Count == 0)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SB56B93DC2B05B6ED"));
            return;
        }

        var scope = (minimumKeptLevelInt > maxDefinedLevel ? global::XASlave.Localization.UiMessage.Key("SB795E4CBD05D5C3B") : global::XASlave.Localization.UiMessage.Format("S28800508464175F0", DescribeLevel(minimumKeptLevelInt)));
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S91F9346CBD138FB9", scope, blockedNames.Count, lastSuppressedCount));
    }

    private readonly record struct SuppressionRecord(WeakReference<object> Service, object? OriginalLevel);
}
