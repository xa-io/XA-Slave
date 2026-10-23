using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class PopupCleanerService : IDisposable
{
    private static readonly string[] BaseAddonNames =
    [
        "_NotificationCircleBook",
        "AchievementInfo",
        "RecommendList",
        "PlayGuide",
        "HowTo",
        "WebLauncher",
        "LicenseViewer",
    ];

    private static readonly string[] AddonNamesWithHowToNotice =
    [
        ..BaseAddonNames,
        "HowToNotice",
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;

    private bool enabled;
    private bool registered;
    private bool hideHowToNotice;

    public PopupCleanerService(IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
    }

    public bool IsEnabled => enabled;
    public bool HideHowToNotice => hideHowToNotice;

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public void ApplyConfiguration(bool hideHowToNotice)
    {
        if (this.hideHowToNotice == hideHowToNotice)
        {
            UpdateStatusText();
            return;
        }

        this.hideHowToNotice = hideHowToNotice;
        if (registered)
        {
            Unregister();
            Register();
        }

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
            Unregister();
            UpdateStatusText();
            return false;
        }

        Register();
        enabled = true;
        UpdateStatusText();
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

        addonLifecycle.RegisterListener(AddonEvent.PreDraw, hideHowToNotice ? AddonNamesWithHowToNotice : BaseAddonNames, OnAddon);
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

    private void UpdateStatusText()
    {
        if (!enabled)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (hideHowToNotice ? global::XASlave.Localization.UiMessage.Key("SFD8801E845E0A752") : global::XASlave.Localization.UiMessage.Key("SCFC29D5E94845FDE")));
    }
}
