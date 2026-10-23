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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S03F020CBB71754CC");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        Subscribe();
        enabled = true;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF35868DF2FDC9DEA"));
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
                || !addon->AtkUnitBase.IsReady)
                return;

            if (!NativeArrayAccess.TryGetAtkUInt(&addon->AtkUnitBase, 7, out var commenceState) ||
                commenceState != 0)
                return;

            lastAddonAddress = addonAddress;
            lastAttemptUtc = now;
            addon->AtkUnitBase.FireCallbackInt(8);
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S419FF2B2D75C0B7A", DateTime.Now));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Duty Commence failed while handling ContentsFinderConfirm.");
        }
    }
}
