using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class PopupCleanerService : IDisposable
{
    private static readonly HashSet<string> AddonNames =
    [
        "_NotificationCircleBook",
        "AchievementInfo",
        "RecommendList",
        "PlayGuide",
        "HowTo",
        "WebLauncher",
        "LicenseViewer"
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;

    private bool enabled;
    private bool registered;

    public PopupCleanerService(IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
    }

    public bool IsEnabled => enabled;

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unregister();
            StatusText = "Disabled";
            return false;
        }

        Register();
        enabled = true;
        StatusText = "Enabled - common popups are auto-closed.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unregister();
    }

    private void Register()
    {
        if (registered)
            return;

        addonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonNames, OnAddon);
        registered = true;
    }

    private void Unregister()
    {
        if (!registered)
            return;

        addonLifecycle.UnregisterListener(OnAddon);
        registered = false;
    }

    private void OnAddon(AddonEvent _, AddonArgs args)
    {
        if (!enabled)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->RootNode == null)
                return;

            addon->RootNode->ToggleVisibility(false);
            addon->Close(false);
            addon->FireCloseCallback();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Popup cleaner failed while closing an addon.");
        }
    }
}
