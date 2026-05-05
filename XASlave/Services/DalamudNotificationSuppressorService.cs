using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class DalamudNotificationSuppressorOptions
{
    public bool HideAll { get; init; }
    public bool HideDalamudUpdates { get; init; }
    public bool HidePluginLifecycle { get; init; }
    public bool HidePluginErrors { get; init; }
    public bool HideModManagerAlerts { get; init; }
    public bool HideSuccessInfo { get; init; }
    public bool HideWarningsErrors { get; init; }

    public bool HasAnyCategory =>
        HideAll
        || HideDalamudUpdates
        || HidePluginLifecycle
        || HidePluginErrors
        || HideModManagerAlerts
        || HideSuccessInfo
        || HideWarningsErrors;

    public string BuildCategorySummary()
    {
        if (HideAll)
            return "all notifications";

        var categories = new List<string>();
        if (HideDalamudUpdates)
            categories.Add("updates");
        if (HidePluginLifecycle)
            categories.Add("plugin lifecycle");
        if (HidePluginErrors)
            categories.Add("plugin errors");
        if (HideModManagerAlerts)
            categories.Add("mod manager alerts");
        if (HideSuccessInfo)
            categories.Add("success/info");
        if (HideWarningsErrors)
            categories.Add("warnings/errors");

        return categories.Count == 0
            ? "no categories selected"
            : string.Join(", ", categories);
    }
}

public sealed class DalamudNotificationSuppressorService : IDisposable
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string[] UpdateNeedles =
    [
        "auto-update",
        "auto update",
        "dalamud update",
        "dalamud updater",
        "plugin update",
        "plugin updates",
        "updates available",
        "update available",
        "updates installed",
        "updates complete",
        "updates failed",
        "checking for updates",
        "finished updating",
        "restart to update",
    ];

    private static readonly string[] PluginErrorNeedles =
    [
        "plugin error",
        "plugin errors",
        "plugin failed",
        "plugin reload failed",
        "plugin load failed",
        "plugin unload failed",
        "failed to load plugin",
        "failed to reload plugin",
        "could not be loaded",
        "could not be reloaded",
        "load error",
        "load errors",
        "reload error",
        "reload errors",
        "creating errors",
        "has crashed",
        "dev plugin",
    ];

    private static readonly string[] PluginLifecycleNeedles =
    [
        "plugin installed",
        "plugin uninstalled",
        "plugin enabled",
        "plugin disabled",
        "plugin loaded",
        "plugin reloaded",
        "plugin unloaded",
        "installed plugin",
        "enabled plugin",
        "disabled plugin",
        "loaded plugin",
        "reloaded plugin",
        "unloaded plugin",
    ];

    private static readonly string[] ModManagerNeedles =
    [
        "penumbra",
        "glamourer",
        "mare synchronos",
        "customize+",
        "customize plus",
        "textools",
        "tex tools",
        "mod manager",
        "mods failed to load",
        "mod failed to load",
        "one or more mods failed",
        "failed to load mods",
        "failed to load mod",
    ];

    private static readonly string[] ErrorishNeedles =
    [
        "error",
        "failed",
        "failure",
        "exception",
        "crash",
        "crashed",
        "could not",
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private DalamudNotificationSuppressorOptions options = new();
    private object? notificationManager;
    private FieldInfo? activeNotificationsField;
    private FieldInfo? pendingNotificationsField;
    private MethodInfo? dismissNowMethod;
    private MethodInfo? disposeInternalMethod;
    private PropertyInfo? titleProperty;
    private PropertyInfo? contentProperty;
    private PropertyInfo? minimizedTextProperty;
    private PropertyInfo? typeProperty;
    private PropertyInfo? dismissReasonProperty;
    private PropertyInfo? initiatorStringProperty;
    private MethodInfo? pendingTryTakeMethod;
    private MethodInfo? pendingAddMethod;
    private bool enabled;
    private bool subscribed;
    private bool hasLoggedReflectionFailure;
    private DateTime nextReflectionRetryUtc = DateTime.MinValue;
    private int suppressedCount;

    public DalamudNotificationSuppressorService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No notifications hidden yet.";
    public int SuppressedCount => suppressedCount;

    public void ApplyConfiguration(DalamudNotificationSuppressorOptions options)
    {
        this.options = options;
        UpdateStatusText();
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
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        UpdateStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        pluginInterface.UiBuilder.Draw += OnDraw;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        pluginInterface.UiBuilder.Draw -= OnDraw;
        subscribed = false;
    }

    private void OnDraw()
    {
        if (!enabled || !options.HasAnyCategory)
            return;

        if (!EnsureNotificationAccess())
            return;

        try
        {
            var hiddenCount = SuppressPendingNotifications() + SuppressActiveNotifications();
            if (hiddenCount <= 0)
                return;

            suppressedCount += hiddenCount;
            LastActionText = $"Last action: hid {hiddenCount} Dalamud notification(s) at {DateTime.Now:HH:mm:ss}.";
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            StatusText = "Enabled - suppression failed; check /xllog.";
            log.Warning(ex, "[XASlave] Dalamud Notifications Suck failed while hiding notifications.");
        }
    }

    private int SuppressActiveNotifications()
    {
        if (notificationManager == null || activeNotificationsField?.GetValue(notificationManager) is not IList activeNotifications)
            return 0;

        var hiddenCount = 0;
        for (var i = activeNotifications.Count - 1; i >= 0; i--)
        {
            var notification = activeNotifications[i];
            if (notification == null || !ShouldSuppress(notification))
                continue;

            DismissAndDispose(notification);
            activeNotifications.RemoveAt(i);
            hiddenCount++;
        }

        return hiddenCount;
    }

    private int SuppressPendingNotifications()
    {
        if (notificationManager == null)
            return 0;

        var pendingNotifications = pendingNotificationsField?.GetValue(notificationManager);
        if (pendingNotifications == null || pendingTryTakeMethod == null || pendingAddMethod == null)
            return 0;

        var hiddenCount = 0;
        var retainedNotifications = new List<object>();
        while (TryTakePendingNotification(pendingNotifications, out var notification))
        {
            if (notification == null)
                continue;

            if (ShouldSuppress(notification))
            {
                DismissAndDispose(notification);
                hiddenCount++;
                continue;
            }

            retainedNotifications.Add(notification);
        }

        foreach (var notification in retainedNotifications)
            pendingAddMethod.Invoke(pendingNotifications, [notification]);

        return hiddenCount;
    }

    private bool TryTakePendingNotification(object pendingNotifications, out object? notification)
    {
        var parameters = new object?[] { null };
        var result = pendingTryTakeMethod?.Invoke(pendingNotifications, parameters) is true;
        notification = result ? parameters[0] : null;
        return result;
    }

    private void DismissAndDispose(object notification)
    {
        dismissNowMethod?.Invoke(notification, null);
        disposeInternalMethod?.Invoke(notification, null);
    }

    private bool ShouldSuppress(object notification)
    {
        if (dismissReasonProperty?.GetValue(notification) != null)
            return false;

        var snapshot = CreateSnapshot(notification);
        if (options.HideAll)
            return true;

        if (options.HideModManagerAlerts && IsModManagerAlert(snapshot))
            return true;

        if (options.HidePluginErrors && IsPluginError(snapshot))
            return true;

        if (options.HideDalamudUpdates && IsUpdateAlert(snapshot))
            return true;

        if (options.HidePluginLifecycle && IsPluginLifecycleAlert(snapshot))
            return true;

        if (options.HideSuccessInfo && IsSuccessOrInfo(snapshot))
            return true;

        return options.HideWarningsErrors && IsWarningOrError(snapshot);
    }

    private NotificationSnapshot CreateSnapshot(object notification)
    {
        var title = ReadStringProperty(notification, titleProperty);
        var content = ReadStringProperty(notification, contentProperty);
        var minimizedText = ReadStringProperty(notification, minimizedTextProperty);
        var initiator = ReadStringProperty(notification, initiatorStringProperty);
        var type = ReadStringProperty(notification, typeProperty);
        var searchText = $"{title}\n{content}\n{minimizedText}\n{initiator}".ToLowerInvariant();

        return new NotificationSnapshot(title, content, minimizedText, initiator, type, searchText);
    }

    private static string ReadStringProperty(object source, PropertyInfo? property)
    {
        try
        {
            return property?.GetValue(source)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsUpdateAlert(NotificationSnapshot snapshot)
        => ContainsAny(snapshot.SearchText, UpdateNeedles);

    private static bool IsPluginError(NotificationSnapshot snapshot)
        => ContainsAny(snapshot.SearchText, PluginErrorNeedles)
           || (snapshot.SearchText.Contains("plugin", StringComparison.Ordinal)
               && ContainsAny(snapshot.SearchText, ErrorishNeedles));

    private static bool IsPluginLifecycleAlert(NotificationSnapshot snapshot)
        => ContainsAny(snapshot.SearchText, PluginLifecycleNeedles)
           && !ContainsAny(snapshot.SearchText, ErrorishNeedles);

    private static bool IsModManagerAlert(NotificationSnapshot snapshot)
        => ContainsAny(snapshot.SearchText, ModManagerNeedles);

    private static bool IsSuccessOrInfo(NotificationSnapshot snapshot)
        => snapshot.Type.Equals("Success", StringComparison.OrdinalIgnoreCase)
           || snapshot.Type.Equals("Info", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarningOrError(NotificationSnapshot snapshot)
        => snapshot.Type.Equals("Warning", StringComparison.OrdinalIgnoreCase)
           || snapshot.Type.Equals("Error", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string haystack, IEnumerable<string> needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool EnsureNotificationAccess()
    {
        if (notificationManager != null
            && activeNotificationsField != null
            && pendingNotificationsField != null
            && dismissNowMethod != null)
            return true;

        if (DateTime.UtcNow < nextReflectionRetryUtc)
            return false;

        nextReflectionRetryUtc = DateTime.UtcNow.AddSeconds(5);

        try
        {
            var dalamudAssembly = typeof(IFramework).Assembly;
            var managerType = dalamudAssembly.GetType("Dalamud.Interface.ImGuiNotification.Internal.NotificationManager", throwOnError: false);
            var serviceGenericType = dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: false);
            if (managerType == null || serviceGenericType == null)
                return ReflectionUnavailable("Dalamud NotificationManager service type could not be resolved.", null);

            var manager = GetDalamudServiceInstance(serviceGenericType.MakeGenericType(managerType));
            if (manager == null)
            {
                StatusText = "Enabled - waiting for Dalamud NotificationManager.";
                return false;
            }

            var activeField = managerType.GetField("notifications", InstanceFlags);
            var pendingField = managerType.GetField("pendingNotifications", InstanceFlags);
            var activeType = activeField?.FieldType.GetGenericArguments().FirstOrDefault()
                             ?? pendingField?.FieldType.GetGenericArguments().FirstOrDefault();
            if (activeField == null || pendingField == null || activeType == null)
                return ReflectionUnavailable("Dalamud notification storage fields could not be resolved.", null);

            notificationManager = manager;
            activeNotificationsField = activeField;
            pendingNotificationsField = pendingField;
            dismissNowMethod = activeType.GetMethod("DismissNow", InstanceFlags, Type.EmptyTypes);
            disposeInternalMethod = activeType.GetMethod("DisposeInternal", InstanceFlags);
            titleProperty = activeType.GetProperty("Title", InstanceFlags);
            contentProperty = activeType.GetProperty("Content", InstanceFlags);
            minimizedTextProperty = activeType.GetProperty("MinimizedText", InstanceFlags);
            typeProperty = activeType.GetProperty("Type", InstanceFlags);
            dismissReasonProperty = activeType.GetProperty("DismissReason", InstanceFlags);
            initiatorStringProperty = activeType.GetProperty("InitiatorString", InstanceFlags);

            var pendingInstance = pendingNotificationsField.GetValue(notificationManager);
            pendingTryTakeMethod = pendingInstance?.GetType().GetMethod("TryTake", InstanceFlags);
            pendingAddMethod = pendingInstance?.GetType().GetMethod("Add", InstanceFlags);

            if (dismissNowMethod == null || contentProperty == null || pendingTryTakeMethod == null || pendingAddMethod == null)
                return ReflectionUnavailable("Dalamud notification accessors could not be resolved.", null);

            UpdateStatusText();
            return true;
        }
        catch (Exception ex)
        {
            return ReflectionUnavailable("Dalamud notification reflection failed.", ex);
        }
    }

    private static object? GetDalamudServiceInstance(Type closedServiceType)
    {
        var getNullable = closedServiceType.GetMethod("GetNullable", StaticFlags);
        if (getNullable != null)
        {
            var parameters = getNullable.GetParameters();
            if (parameters.Length == 0)
                return getNullable.Invoke(null, null);

            var propagationMode = Enum.Parse(parameters[0].ParameterType, "None");
            return getNullable.Invoke(null, [propagationMode]);
        }

        var get = closedServiceType.GetMethod("Get", StaticFlags, Type.EmptyTypes);
        return get?.Invoke(null, null);
    }

    private bool ReflectionUnavailable(string message, Exception? exception)
    {
        StatusText = "Enabled - Dalamud notification internals are unavailable.";
        LastActionText = message;
        if (!hasLoggedReflectionFailure)
        {
            hasLoggedReflectionFailure = true;
            if (exception == null)
                log.Warning($"[XASlave] Dalamud Notifications Suck unavailable: {message}");
            else
                log.Warning(exception, $"[XASlave] Dalamud Notifications Suck unavailable: {message}");
        }

        return false;
    }

    private void UpdateStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!options.HasAnyCategory)
        {
            StatusText = "Enabled - no notification categories selected.";
            return;
        }

        StatusText = $"Enabled - hiding {options.BuildCategorySummary()}. Hidden so far: {suppressedCount}.";
    }

    private sealed record NotificationSnapshot(
        string Title,
        string Content,
        string MinimizedText,
        string Initiator,
        string Type,
        string SearchText);
}
