using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
namespace XASlave.Services;

public unsafe sealed class AutoDutyCommenceService : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private bool enabled;
    private bool subscribed;
    private DateTime lastAttemptUtc = DateTime.MinValue;
    private nint lastAddonAddress;

    public AutoDutyCommenceService(IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = "Disabled";
            return false;
        }

        Subscribe();
        enabled = true;
        StatusText = "Enabled - Duty Commence is clicked automatically when the confirmation prompt is ready.";
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

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinderConfirm", OnContentsFinderConfirm);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ContentsFinderConfirm", OnContentsFinderConfirm);
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnContentsFinderConfirm);
        subscribed = false;
    }

    private void OnContentsFinderConfirm(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var addonAddress = args.Addon.Address;
            if (addonAddress == nint.Zero)
                return;

            var now = DateTime.UtcNow;
            if (addonAddress == lastAddonAddress && (now - lastAttemptUtc).TotalMilliseconds < 250)
                return;

            var addon = (AddonContentsFinderConfirm*)addonAddress;
            if (addon == null
                || !addon->AtkUnitBase.IsVisible
                || !addon->AtkUnitBase.IsReady
                || addon->AtkUnitBase.AtkValues == null
                || addon->AtkUnitBase.AtkValuesCount <= 7)
                return;

            if (addon->AtkUnitBase.AtkValues[7].UInt != 0)
                return;

            lastAddonAddress = addonAddress;
            lastAttemptUtc = now;
            addon->AtkUnitBase.FireCallbackInt(8);
            LastActionText = $"Last action: clicked Duty Commence at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Duty Commence failed while handling ContentsFinderConfirm.");
        }
    }
}
