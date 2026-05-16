using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class LobbyErrorAutoCloseService : IDisposable
{
    private const string DialogueAddonName = "Dialogue";
    private const int TemporaryMonitorSeconds = 10;
    private const int ClickThrottleMilliseconds = 400;
    private const int NoKillPanelCheckThrottleMilliseconds = 400;
    private const int DialogueVisibilityGapMilliseconds = 1000;
    private const string NoKillInternalName = "NoKillPlugin";
    private const string NoKillDisplayName = "No Kill Plugin";
    private const string NoKillCommandName = "nokill";
    private const string NoKillPanelTitle = "No Kill Plugin Panel";
    private const string TitleMenuAddonName = "_TitleMenu";
    private static readonly BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] DialogueMarkers =
    [
        "90000",
        "90001",
        "90002",
        "90003",
        "90004",
        "90005",
        "90006",
        "90007",
        "2002",
        "3050",
        "3088",
        "3102",
        "5006",
        "Connection with the server was lost.",
        "You are still logged into the game.",
        "Please allow a few moments for the logout process to complete.",
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private bool enabled;
    private bool dialogueListenerSubscribed;
    private bool frameworkSubscribed;
    private bool noKillReflectionFailureLogged;
    private DateTime temporaryMonitorUntilUtc = DateTime.MinValue;
    private DateTime dialogueMonitorUntilUtc = DateTime.MinValue;
    private DateTime lastDialogueSeenUtc = DateTime.MinValue;
    private DateTime lastConfirmAttemptUtc = DateTime.MinValue;
    private DateTime lastNoKillPanelCheckUtc = DateTime.MinValue;

    public LobbyErrorAutoCloseService(IAddonLifecycle addonLifecycle, IFramework framework, IClientState clientState, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.clientState = clientState;
        this.log = log;
    }

    public readonly record struct LobbyMonitorDebugStatus(
        bool MonitorRequested,
        bool DialogueListenerSubscribed,
        bool FrameworkSubscribed,
        bool ClientLoggedIn,
        bool DialogueVisible,
        bool DialogueReady,
        bool DialogueSupported,
        bool DialogueWindowActive,
        double SecondsRemaining,
        bool TitleMenuVisible,
        string DialogueMatch,
        string DialogueText,
        bool ShouldMonitor,
        string Detail);

    public readonly record struct NoKillPluginPanelDebugStatus(
        bool PluginLoaded,
        bool RuntimeInstanceResolved,
        bool ConfigWindowResolved,
        bool VisibilityMemberResolved,
        bool IsVisible,
        string Detail);

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            dialogueMonitorUntilUtc = DateTime.MinValue;
            lastDialogueSeenUtc = DateTime.MinValue;
            UpdateSubscription();
            StatusText = "Disabled";
            return false;
        }

        try
        {
            enabled = true;
            UpdateSubscription();
            StatusText = "Enabled - waits for addon:Dialogue, then monitors lobby/networking errors and NoKill for 10 seconds.";
            return true;
        }
        catch (Exception ex)
        {
            enabled = false;
            temporaryMonitorUntilUtc = DateTime.MinValue;
            UpdateSubscription();
            StatusText = "Unavailable - failed to arm the lobby Dialogue monitor.";
            log.Warning(ex, "[XASlave] Failed to enable Auto Close Lobby Errors.");
            return false;
        }
    }

    public void ArmTemporaryMonitor()
    {
        var nextExpiry = DateTime.UtcNow.AddSeconds(TemporaryMonitorSeconds);
        if (nextExpiry > temporaryMonitorUntilUtc)
            temporaryMonitorUntilUtc = nextExpiry;

        try
        {
            UpdateSubscription();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to arm the temporary lobby Dialogue monitor for Instant Logout.");
        }
    }

    public void Dispose()
    {
        enabled = false;
        temporaryMonitorUntilUtc = DateTime.MinValue;
        dialogueMonitorUntilUtc = DateTime.MinValue;
        UpdateSubscription();
    }

    private bool IsTemporaryMonitorActive()
    {
        return IsTemporaryMonitorActive(DateTime.UtcNow);
    }

    private bool IsTemporaryMonitorActive(DateTime now)
    {
        return now < temporaryMonitorUntilUtc;
    }

    private bool IsDialogueMonitorActive(DateTime now)
    {
        return now < dialogueMonitorUntilUtc;
    }

    private bool IsAnyMonitorWindowActive(DateTime now)
    {
        return IsDialogueMonitorActive(now);
    }

    private void UpdateSubscription()
    {
        var now = DateTime.UtcNow;
        var temporaryMonitorActive = IsTemporaryMonitorActive(now);
        var dialogueMonitorActive = IsDialogueMonitorActive(now);
        var monitorRequested = enabled || temporaryMonitorActive || dialogueMonitorActive;

        var shouldListenForDialogue = monitorRequested;
        if (dialogueListenerSubscribed != shouldListenForDialogue)
        {
            if (shouldListenForDialogue)
                addonLifecycle.RegisterListener(AddonEvent.PreDraw, DialogueAddonName, OnDialogueAddon);
            else
                addonLifecycle.UnregisterListener(OnDialogueAddon);

            dialogueListenerSubscribed = shouldListenForDialogue;
        }

        var shouldRunFramework = IsAnyMonitorWindowActive(now);
        if (frameworkSubscribed == shouldRunFramework)
            return;

        if (shouldRunFramework)
        {
            framework.Update += OnFrameworkUpdate;
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
        }

        frameworkSubscribed = shouldRunFramework;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (!enabled && !IsAnyMonitorWindowActive(now))
        {
            UpdateSubscription();
            return;
        }

        if (!ShouldRunActiveMonitor(now))
        {
            UpdateSubscription();
            return;
        }

        RunActiveMonitorTick(now);
    }

    private void OnDialogueAddon(AddonEvent _, AddonArgs args)
    {
        if (args.Addon.IsNull)
            return;

        var now = DateTime.UtcNow;
        if (!enabled && !IsTemporaryMonitorActive(now) && !IsDialogueMonitorActive(now))
        {
            UpdateSubscription();
            return;
        }

        if (TryGetSupportedDialogue(out var ignoredDialogueMarker, out var ignoredDialogueText))
            ArmDialogueTriggeredMonitor(now);

        RunActiveMonitorTick(now);
    }

    private void RunActiveMonitorTick(DateTime now)
    {
        if (!ShouldRunActiveMonitor(now))
            return;

        var closedNoKillPanel = false;
        if ((now - lastNoKillPanelCheckUtc).TotalMilliseconds >= NoKillPanelCheckThrottleMilliseconds)
        {
            lastNoKillPanelCheckUtc = now;
            closedNoKillPanel = TryCloseNoKillPluginPanel(out var ignoredFrameworkMessage, logWhenUnavailable: false);
        }

        if ((now - lastConfirmAttemptUtc).TotalMilliseconds < ClickThrottleMilliseconds)
            return;

        try
        {
            if (!ShouldConfirmDialogue())
                return;

            if (!AddonHelper.ClickAddonText(DialogueAddonName, "OK"))
                return;

            lastConfirmAttemptUtc = now;
            log.Information(closedNoKillPanel
                ? "[XASlave] Auto-confirmed a disconnect/lobby Dialogue popup and closed the No Kill Plugin Panel."
                : "[XASlave] Auto-confirmed a disconnect/lobby Dialogue popup.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Close Lobby Errors failed while handling the Dialogue popup.");
        }
    }

    private void ArmDialogueTriggeredMonitor(DateTime now)
    {
        var dialogueSeenRecently = (now - lastDialogueSeenUtc).TotalMilliseconds <= DialogueVisibilityGapMilliseconds;
        lastDialogueSeenUtc = now;

        if (dialogueSeenRecently)
            return;

        dialogueMonitorUntilUtc = now.AddSeconds(TemporaryMonitorSeconds);
        UpdateSubscription();
    }

    private static bool ShouldConfirmDialogue()
    {
        return TryGetSupportedDialogue(out _, out _);
    }

    private static bool TryGetSupportedDialogue(out string matchedMarker, out string dialogueText)
    {
        matchedMarker = string.Empty;
        dialogueText = string.Empty;

        if (!AddonHelper.IsAddonReady(DialogueAddonName))
            return false;

        var entries = AddonHelper.GetAddonTextEntries(DialogueAddonName);
        dialogueText = SummarizeDialogueEntries(entries);
        if (!ContainsText(entries, "OK"))
            return false;

        foreach (var marker in DialogueMarkers)
        {
            if (ContainsText(entries, marker))
            {
                matchedMarker = marker;
                return true;
            }
        }

        return false;
    }

    private static bool ContainsText(IReadOnlyList<string> entries, string text)
    {
        foreach (var entry in entries)
        {
            if (entry.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string SummarizeDialogueEntries(IReadOnlyList<string> entries)
    {
        if (entries.Count == 0)
            return "No visible Dialogue text entries.";

        var meaningfulEntries = new List<string>();
        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0)
                continue;

            meaningfulEntries.Add(trimmed);
            if (meaningfulEntries.Count >= 6)
                break;
        }

        return meaningfulEntries.Count == 0
            ? "No visible Dialogue text entries."
            : string.Join(" | ", meaningfulEntries);
    }

    public LobbyMonitorDebugStatus GetLobbyMonitorDebugStatus()
    {
        var now = DateTime.UtcNow;
        return GetLobbyMonitorDebugStatus(now);
    }

    public NoKillPluginPanelDebugStatus GetNoKillPluginPanelDebugStatus()
    {
        TryResolveNoKillConfigWindow(out _, out var status);
        return status;
    }

    public bool TryCloseNoKillPluginPanelForDebug(out string message)
    {
        return TryCloseNoKillPluginPanel(out message, logWhenUnavailable: true);
    }

    private bool TryCloseNoKillPluginPanel(out string message, bool logWhenUnavailable)
    {
        try
        {
            if (!TryResolveNoKillConfigWindow(out var configWindow, out var status))
            {
                message = status.Detail;
                return false;
            }

            if (!status.IsVisible)
            {
                message = status.Detail;
                return false;
            }

            if (configWindow == null
                || (!TrySetBooleanMember(configWindow, "Visible", false)
                    && !TrySetBooleanMember(configWindow, "WindowVisible", false)))
            {
                message = "NoKill config window visibility member could not be set.";
                if (logWhenUnavailable)
                    NoKillReflectionUnavailable(message, null);
                return false;
            }

            noKillReflectionFailureLogged = false;
            message = $"Closed the {NoKillPanelTitle}.";
            log.Information($"[XASlave] Closed the {NoKillPanelTitle} via reflection.");
            return true;
        }
        catch (Exception ex)
        {
            message = $"NoKill panel reflection close failed: {ex.Message}";
            return logWhenUnavailable
                ? NoKillReflectionUnavailable("NoKill panel reflection close failed.", ex)
                : false;
        }
    }

    private bool ShouldRunActiveMonitor(DateTime now)
    {
        return GetLobbyMonitorDebugStatus(now).ShouldMonitor;
    }

    private LobbyMonitorDebugStatus GetLobbyMonitorDebugStatus(DateTime now)
    {
        var temporaryMonitorActive = IsTemporaryMonitorActive(now);
        var dialogueMonitorActive = IsDialogueMonitorActive(now);
        var monitorRequested = enabled || temporaryMonitorActive || dialogueMonitorActive;
        var dialogueVisible = AddonHelper.IsAddonVisible(DialogueAddonName);
        var dialogueReady = AddonHelper.IsAddonReady(DialogueAddonName);
        var dialogueSupported = TryGetSupportedDialogue(out var dialogueMatch, out var dialogueText);
        var titleMenuVisible = AddonHelper.IsAddonVisible(TitleMenuAddonName);
        var secondsRemaining = Math.Max(0, (dialogueMonitorUntilUtc - now).TotalSeconds);
        var shouldMonitor = monitorRequested && dialogueMonitorActive;

        var detail = !monitorRequested
            ? "Close Lobby Errors monitor requested: false."
            : shouldMonitor
                ? $"Monitoring lobby/networking errors and NoKill for {secondsRemaining:0.0}s."
                : dialogueSupported
                    ? $"Supported Dialogue detected ({dialogueMatch}); waiting for active monitor arm."
                    : "Waiting for addon:Dialogue with a supported lobby/networking marker before starting the 10 second monitor.";

        return new LobbyMonitorDebugStatus(
            monitorRequested,
            dialogueListenerSubscribed,
            frameworkSubscribed,
            clientState.IsLoggedIn,
            dialogueVisible,
            dialogueReady,
            dialogueSupported,
            dialogueMonitorActive,
            secondsRemaining,
            titleMenuVisible,
            string.IsNullOrWhiteSpace(dialogueMatch) ? "None" : dialogueMatch,
            dialogueText,
            shouldMonitor,
            detail);
    }

    private bool TryResolveNoKillConfigWindow(out object? configWindow, out NoKillPluginPanelDebugStatus status)
    {
        configWindow = null;

        if (!TryCheckNoKillPluginLoaded(out var pluginLoaded, out var pluginLoadDetail) || !pluginLoaded)
        {
            status = new NoKillPluginPanelDebugStatus(pluginLoaded, false, false, false, false, pluginLoadDetail);
            return false;
        }

        var pluginInstance = TryGetNoKillPluginInstance(skipLoadedCheck: true);
        if (pluginInstance == null)
        {
            status = new NoKillPluginPanelDebugStatus(true, false, false, false, false, "NoKillPlugin is loaded, but its runtime instance could not be resolved.");
            return false;
        }

        var gui = GetMemberValue(pluginInstance, "Gui");
        configWindow = gui == null ? null : GetMemberValue(gui, "ConfigWindow");
        if (configWindow == null)
        {
            status = new NoKillPluginPanelDebugStatus(true, true, false, false, false, "NoKillPlugin runtime instance resolved, but Gui.ConfigWindow could not be resolved.");
            return false;
        }

        if (!TryGetBooleanMember(configWindow, "Visible", out var visible)
            && !TryGetBooleanMember(configWindow, "WindowVisible", out visible))
        {
            status = new NoKillPluginPanelDebugStatus(true, true, true, false, false, "NoKillPlugin Gui.ConfigWindow resolved, but Visible/WindowVisible could not be read.");
            return false;
        }

        status = new NoKillPluginPanelDebugStatus(
            true,
            true,
            true,
            true,
            visible,
            visible
                ? $"{NoKillPanelTitle} active: true."
                : $"{NoKillPanelTitle} active: false.");
        return true;
    }

    private object? TryGetNoKillPluginInstance(bool skipLoadedCheck = false)
    {
        if (!skipLoadedCheck && !IsNoKillPluginLoaded())
            return null;

        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pluginManagerServiceType == null || pluginManagerType == null)
            {
                NoKillReflectionUnavailable("Dalamud plugin-manager reflection types could not be resolved.", null);
                return null;
            }

            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);
            if (pluginManager == null)
            {
                NoKillReflectionUnavailable("Dalamud plugin manager could not be resolved.", null);
                return null;
            }

            var installedPlugins = pluginManager.GetType().GetProperty("InstalledPlugins", InstanceBindings)?.GetValue(pluginManager) as IEnumerable;
            if (installedPlugins == null)
            {
                NoKillReflectionUnavailable("Dalamud installed-plugin collection could not be resolved.", null);
                return null;
            }

            var pluginInstance = TryGetNoKillPluginInstanceFromCollection(installedPlugins);
            if (pluginInstance == null)
                NoKillReflectionUnavailable("NoKillPlugin is loaded, but its runtime instance could not be resolved.", null);

            return pluginInstance;
        }
        catch (Exception ex)
        {
            NoKillReflectionUnavailable("Could not inspect Dalamud's internal plugin manager for NoKillPlugin.", ex);
            return null;
        }
    }

    private bool IsNoKillPluginLoaded()
    {
        return TryCheckNoKillPluginLoaded(out var loaded, out _) && loaded;
    }

    private bool TryCheckNoKillPluginLoaded(out bool loaded, out string detail)
    {
        try
        {
            loaded = IsNoKillPluginLoaded(Plugin.PluginInterface.InstalledPlugins);
            detail = loaded
                ? "NoKillPlugin loaded: true."
                : "NoKillPlugin loaded: false.";
            return true;
        }
        catch (Exception ex)
        {
            loaded = false;
            detail = $"NoKillPlugin availability check failed: {ex.Message}";
            NoKillReflectionUnavailable("NoKillPlugin availability check failed.", ex);
            return false;
        }
    }

    private static bool IsNoKillPluginLoaded(IEnumerable installedPlugins)
    {
        foreach (var pluginState in installedPlugins)
        {
            if (pluginState != null && IsLoaded(pluginState) && IsNoKillPlugin(pluginState))
                return true;
        }

        return false;
    }

    private static object? TryGetNoKillPluginInstanceFromCollection(IEnumerable installedPlugins)
    {
        foreach (var pluginState in installedPlugins)
        {
            if (pluginState == null || !IsLoaded(pluginState) || !IsNoKillPlugin(pluginState))
                continue;

            var pluginType = pluginState.GetType().Name == "LocalDevPlugin"
                ? pluginState.GetType().BaseType
                : pluginState.GetType();
            var instanceField = pluginType?.GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic);
            var instance = instanceField?.GetValue(pluginState);
            if (instance != null)
                return instance;
        }

        return null;
    }

    private bool NoKillReflectionUnavailable(string message, Exception? exception)
    {
        if (noKillReflectionFailureLogged)
            return false;

        noKillReflectionFailureLogged = true;
        if (exception == null)
            log.Warning($"[XASlave] {message}");
        else
            log.Warning(exception, $"[XASlave] {message}");

        return false;
    }

    private static bool IsLoaded(object pluginState)
    {
        return GetBooleanProperty(pluginState, "IsLoaded");
    }

    private static bool IsNoKillPlugin(object pluginState)
    {
        return IsMatchingNoKillPluginName(GetStringProperty(pluginState, "InternalName"))
            || IsMatchingNoKillPluginName(GetStringProperty(pluginState, "Name"));
    }

    private static bool IsMatchingNoKillPluginName(string? value)
    {
        return string.Equals(value, NoKillInternalName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, NoKillDisplayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, NoKillCommandName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetStringProperty(object source, string propertyName)
    {
        return source.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(source)?.ToString();
    }

    private static bool GetBooleanProperty(object source, string propertyName)
    {
        return source.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(source) as bool? ?? false;
    }

    private static object? GetMemberValue(object source, string memberName)
    {
        var sourceType = source.GetType();
        return sourceType.GetProperty(memberName, InstanceBindings)?.GetValue(source)
            ?? sourceType.GetField(memberName, InstanceBindings)?.GetValue(source);
    }

    private static bool TryGetBooleanMember(object source, string memberName, out bool value)
    {
        var sourceType = source.GetType();
        var property = sourceType.GetProperty(memberName, InstanceBindings);
        if (property?.GetValue(source) is bool propertyValue)
        {
            value = propertyValue;
            return true;
        }

        var field = sourceType.GetField(memberName, InstanceBindings);
        if (field?.GetValue(source) is bool fieldValue)
        {
            value = fieldValue;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TrySetBooleanMember(object source, string memberName, bool value)
    {
        var sourceType = source.GetType();
        var property = sourceType.GetProperty(memberName, InstanceBindings);
        if (property?.CanWrite == true && property.PropertyType == typeof(bool))
        {
            property.SetValue(source, value);
            return true;
        }

        var field = sourceType.GetField(memberName, InstanceBindings);
        if (field != null && !field.IsInitOnly && field.FieldType == typeof(bool))
        {
            field.SetValue(source, value);
            return true;
        }

        return false;
    }
}
