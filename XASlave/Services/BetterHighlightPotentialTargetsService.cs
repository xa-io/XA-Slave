using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace XASlave.Services;

public unsafe sealed class BetterHighlightPotentialTargetsService : IDisposable
{
    private const double RuntimeActivationDelaySeconds = 5.0;
    private const double HoverStableDelayMilliseconds = 75.0;
    private const int StableClientFramesRequired = 30;

    public const int DefaultHighlightColor = (int)ObjectHighlightColor.Magenta;

    public static IReadOnlyList<int> SelectableHighlightColors { get; } =
    [
        (int)ObjectHighlightColor.Red,
        (int)ObjectHighlightColor.Green,
        (int)ObjectHighlightColor.Blue,
        (int)ObjectHighlightColor.Orange,
        (int)ObjectHighlightColor.Magenta,
    ];

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private ObjectHighlightColor highlightColor = ObjectHighlightColor.Magenta;
    private ObjectHighlightColor highlightedColor = ObjectHighlightColor.None;
    private nint highlightedAddress;
    private nint pendingHoverAddress;
    private DateTime pendingHoverSinceUtc = DateTime.MinValue;
    private DateTime activationNotBeforeUtc = DateTime.MaxValue;
    private DateTime lastFailureLogUtc = DateTime.MinValue;
    private bool enabled;
    private bool runtimeArmed;
    private bool subscribed;
    private bool clientStateSubscribed;
    private bool hasAppliedHighlight;
    private bool hasFailureStatus;
    private bool nativeUnavailable;
    private bool disposed;
    private int stableClientFrames;

    public BetterHighlightPotentialTargetsService(
        IFramework framework,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IClientState clientState,
        IPluginLog log)
    {
        this.framework = framework;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.clientState = clientState;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool IsStartupArmingPending => enabled
        && !nativeUnavailable
        && clientState.IsLoggedIn
        && !runtimeArmed;

    public static int NormalizeHighlightColor(int colorValue)
    {
        var color = (ObjectHighlightColor)colorValue;
        return IsSelectableHighlightColor(color) ? (int)color : DefaultHighlightColor;
    }

    public static string GetColorLabel(int colorValue)
    {
        var color = (ObjectHighlightColor)NormalizeHighlightColor(colorValue);
        return color switch
        {
            ObjectHighlightColor.Red => "Red",
            ObjectHighlightColor.Green => "Green",
            ObjectHighlightColor.Blue => "Blue",
            ObjectHighlightColor.Orange => "Orange",
            ObjectHighlightColor.Magenta => "Magenta",
            _ => "Magenta",
        };
    }

    public void ApplyConfiguration(int colorValue)
    {
        var nextColor = (ObjectHighlightColor)NormalizeHighlightColor(colorValue);
        if (enabled && runtimeArmed && hasAppliedHighlight && highlightedColor != nextColor)
            ClearHighlightedObjectIfSafe();

        highlightColor = nextColor;

        if (!enabled)
            return;

        RefreshStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (disposed)
            return false;

        if (value == enabled)
            return enabled && !nativeUnavailable;

        if (!value)
        {
            ClearHighlightedObjectIfSafe();
            enabled = false;
            runtimeArmed = false;
            stableClientFrames = 0;
            UpdateClientStateSubscription(false);
            UpdateFrameworkSubscription(false);
            ForgetHighlightedObject();
            StatusText = "Disabled";
            return false;
        }

        if (nativeUnavailable)
        {
            StatusText = "Unavailable - native highlight backend failed and was disabled for this plugin session.";
            return false;
        }

        enabled = true;
        ScheduleRuntimeArming("enabled");
        UpdateClientStateSubscription(true);
        UpdateFrameworkSubscription(true);
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        enabled = false;
        runtimeArmed = false;
        UpdateClientStateSubscription(false);
        UpdateFrameworkSubscription(false);

        // Do not call GameObject.Highlight during plugin disposal. Reload/update/unload
        // can leave hover object addresses stale, and native failures can freeze the client.
        ForgetHighlightedObject();
    }

    private static bool IsSelectableHighlightColor(ObjectHighlightColor color)
        => color is ObjectHighlightColor.Red
            or ObjectHighlightColor.Green
            or ObjectHighlightColor.Blue
            or ObjectHighlightColor.Orange
            or ObjectHighlightColor.Magenta;

    private void UpdateFrameworkSubscription(bool targetState)
    {
        if (subscribed == targetState)
            return;

        if (targetState)
            framework.Update += OnFrameworkUpdate;
        else
            framework.Update -= OnFrameworkUpdate;

        subscribed = targetState;
    }

    private void UpdateClientStateSubscription(bool targetState)
    {
        if (clientStateSubscribed == targetState)
            return;

        if (targetState)
        {
            clientState.Login += OnLogin;
            clientState.Logout += OnLogout;
            clientState.TerritoryChanged += OnTerritoryChanged;
        }
        else
        {
            clientState.Login -= OnLogin;
            clientState.Logout -= OnLogout;
            clientState.TerritoryChanged -= OnTerritoryChanged;
        }

        clientStateSubscribed = targetState;
    }

    private void OnLogin()
    {
        if (!enabled || disposed)
            return;

        ScheduleRuntimeArming("login");
    }

    private void OnLogout(int type, int code)
    {
        if (!enabled || disposed)
            return;

        SuspendRuntime("Enabled - suspended during logout/relog.");
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (!enabled || disposed)
            return;

        ScheduleRuntimeArming("territory change");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled || disposed)
            return;

        if (nativeUnavailable)
        {
            UpdateClientStateSubscription(false);
            UpdateFrameworkSubscription(false);
            return;
        }

        if (!IsClientReady())
        {
            SuspendRuntime("Enabled - waiting for a fully loaded character before touching native highlight state.");
            return;
        }

        var now = DateTime.UtcNow;
        if (activationNotBeforeUtc == DateTime.MaxValue)
            activationNotBeforeUtc = now.AddSeconds(RuntimeActivationDelaySeconds);

        if (now < activationNotBeforeUtc)
        {
            StatusText = $"Arming - waiting for stable login/territory state before native highlight repaint ({Math.Max(0, (activationNotBeforeUtc - now).TotalSeconds):0.0}s).";
            return;
        }

        if (stableClientFrames < StableClientFramesRequired)
        {
            stableClientFrames++;
            StatusText = $"Arming - waiting for stable client frames before native highlight repaint ({stableClientFrames}/{StableClientFramesRequired}).";
            return;
        }

        if (!runtimeArmed)
        {
            runtimeArmed = true;
            RefreshStatusText();
        }

        ApplyCurrentHoverHighlight(now);
    }

    private bool IsClientReady()
        => clientState.IsLoggedIn
            && clientState.TerritoryType != 0
            && objectTable.LocalPlayer != null;

    private void ScheduleRuntimeArming(string reason)
    {
        runtimeArmed = false;
        stableClientFrames = 0;
        activationNotBeforeUtc = DateTime.UtcNow.AddSeconds(RuntimeActivationDelaySeconds);
        ForgetHighlightedObject();
        StatusText = $"Arming - Better Highlight will wait for stable client state after {reason}.";
    }

    private void SuspendRuntime(string statusText)
    {
        runtimeArmed = false;
        stableClientFrames = 0;
        activationNotBeforeUtc = DateTime.MaxValue;
        ForgetHighlightedObject();
        StatusText = statusText;
    }

    private void ApplyCurrentHoverHighlight(DateTime now)
    {
        var hoveredAddress = targetManager.MouseOverTarget?.Address
            ?? targetManager.MouseOverNameplateTarget?.Address
            ?? nint.Zero;

        if (hoveredAddress == nint.Zero)
        {
            ClearHighlightedObjectIfSafe();
            pendingHoverAddress = nint.Zero;
            pendingHoverSinceUtc = DateTime.MinValue;
            return;
        }

        if (hasAppliedHighlight && highlightedAddress != nint.Zero && highlightedAddress != hoveredAddress)
            ClearHighlightedObjectIfSafe();

        if (hoveredAddress != pendingHoverAddress)
        {
            pendingHoverAddress = hoveredAddress;
            pendingHoverSinceUtc = now;
            return;
        }

        if ((now - pendingHoverSinceUtc).TotalMilliseconds < HoverStableDelayMilliseconds)
            return;

        if (hasAppliedHighlight && highlightedAddress == hoveredAddress && highlightedColor == highlightColor)
            return;

        if (TryHighlight(hoveredAddress, highlightColor))
        {
            highlightedAddress = hoveredAddress;
            highlightedColor = highlightColor;
            hasAppliedHighlight = true;
        }
    }

    private void ForgetHighlightedObject()
    {
        highlightedAddress = nint.Zero;
        pendingHoverAddress = nint.Zero;
        pendingHoverSinceUtc = DateTime.MinValue;
        highlightedColor = ObjectHighlightColor.None;
        hasAppliedHighlight = false;
    }

    private void ForgetAppliedHighlight()
    {
        highlightedAddress = nint.Zero;
        highlightedColor = ObjectHighlightColor.None;
        hasAppliedHighlight = false;
    }

    private void ClearHighlightedObjectIfSafe()
    {
        if (!hasAppliedHighlight || highlightedAddress == nint.Zero)
            return;

        if (!runtimeArmed || !IsClientReady())
        {
            ForgetAppliedHighlight();
            return;
        }

        if (!IsCurrentObjectAddress(highlightedAddress))
        {
            ForgetAppliedHighlight();
            return;
        }

        if (TryHighlight(highlightedAddress, ObjectHighlightColor.None))
            ForgetAppliedHighlight();
    }

    private bool IsCurrentObjectAddress(nint address)
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject.Address == address)
                return true;
        }

        return false;
    }

    private bool TryHighlight(nint address, ObjectHighlightColor color)
    {
        if (address == nint.Zero || !runtimeArmed || !IsClientReady())
            return false;

        try
        {
            ((GameObject*)address)->Highlight(color);
            if (hasFailureStatus)
            {
                hasFailureStatus = false;
                RefreshStatusText();
            }

            return true;
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if ((now - lastFailureLogUtc).TotalSeconds >= 5)
            {
                lastFailureLogUtc = now;
                log.Warning(ex, "[XASlave] Better Highlight Potential Targets failed to apply native object highlight color. Disabling native repaint for this plugin session.");
            }

            nativeUnavailable = true;
            enabled = false;
            runtimeArmed = false;
            UpdateClientStateSubscription(false);
            UpdateFrameworkSubscription(false);
            ForgetHighlightedObject();
            StatusText = "Unavailable - native highlight backend failed and was disabled for this plugin session.";
            hasFailureStatus = true;
            return false;
        }
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (nativeUnavailable)
        {
            StatusText = "Unavailable - native highlight backend failed and was disabled for this plugin session.";
            return;
        }

        if (!runtimeArmed)
        {
            StatusText = "Arming - waiting for stable client state before native highlight repaint.";
            return;
        }

        StatusText = $"Enabled - repainting hovered potential targets as {GetColorLabel((int)highlightColor)} after stable hover checks and clearing them on hover loss.";
    }
}
