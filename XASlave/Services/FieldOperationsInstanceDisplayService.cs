using System;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XASlave.Data;

namespace XASlave.Services;

public sealed class FieldOperationsInstanceDisplayService : IDisposable
{
    private readonly Configuration configuration;
    private readonly ZoneInitObservationService observations;
    private readonly EurekaInstanceIdService eureka;
    private readonly IFramework framework;
    private FieldOperationsIdentity? displayed;
    private FieldOperationsIdentity? pendingAnnouncement;
    private long lastAnnouncedGeneration = -1;
    private uint lastAnnouncedComposite;
    private bool disposed;
    private string territoryLabel = string.Empty;
    private global::XASlave.Localization.UiMessage? territoryLabelMessage;
    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public FieldOperationsInstanceDisplayService(Configuration configuration, ZoneInitObservationService observations, EurekaInstanceIdService eureka, IFramework framework)
    {
        this.configuration = configuration; this.observations = observations; this.eureka = eureka; this.framework = framework;
        observations.Invalidated += Invalidate;
        eureka.FieldOperationsAnnouncementSuffix = AppendToEurekaAnnouncement;
        eureka.FieldOperationsAnnouncementPublished = ConfirmEurekaAnnouncement;
        framework.Update += Update;
    }
    public void ApplyConfiguration()
    {
        if (disposed) return;
        Invalidate();
        if (framework.IsInFrameworkUpdateThread) Refresh();
    }
    private void Invalidate()
    {
        displayed = null;
        pendingAnnouncement = null;
        if (framework.IsInFrameworkUpdateThread) eureka.SetFieldOperationsDtr(configuration.FieldOperationsInstanceDisplayEnabled, string.Empty, false, string.Empty, null);
        else Plugin.ScheduleOnGameThread(() => { if (!disposed) Refresh(); });
    }
    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            Refresh();
            var current = displayed;
            if (current == null || current.Cached || !configuration.FieldOperationsInstanceDisplayChat || eureka.FieldOperationsAnnouncementPending || AlreadyAnnounced(current)) return;
            var text = global::XASlave.Localization.UiMessage.Format("SFDA996155E48861C", global::XASlave.Localization.UiMessage.Capture(ref territoryLabelMessage, territoryLabel), current.Composite.ToString(CultureInfo.InvariantCulture)).Original;
            Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(45).AddText(text).AddUiForegroundOff().Build());
            MarkAnnounced(current);
        }
        catch (Exception error)
        {
            displayed = null; StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SF4C6331A2642B28F"), global::XASlave.Localization.UiMessage.FromException(error)));
            eureka.SetFieldOperationsDtr(true, string.Empty, false, string.Empty, null);
        }
    }
    private unsafe void Refresh()
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        if (!configuration.FieldOperationsInstanceDisplayEnabled)
        {
            displayed = null; StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            eureka.SetFieldOperationsDtr(false, string.Empty, false, string.Empty, null); return;
        }
        var view = observations.Read();
        var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(view.Context.Territory);
        var intendedUse = row?.TerritoryIntendedUse.RowId ?? 0;
        if (intendedUse > byte.MaxValue || !ZoneInitIdentityState.Supported((byte)intendedUse))
        {
            displayed = null; StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S185465CD359E4980"));
            eureka.SetFieldOperationsDtr(false, string.Empty, false, string.Empty, null); return;
        }
        territoryLabel = global::XASlave.Localization.UiMessage.Store(ref territoryLabelMessage, row?.PlaceName.ValueNullable?.Name.ExtractText() is { } gameTerritoryName ? global::XASlave.Localization.UiMessage.Raw(gameTerritoryName) : global::XASlave.Localization.UiMessage.Key("S9820D743FD29145C"));
        var identity = ZoneInitIdentityState.Select(view, (byte)intendedUse);
        if (identity == null && view.CachedEnableAllowed && view.Context.Ready && !view.Context.Replay)
        {
            var replay = ContentsReplayManager.Instance();
            if (replay != null)
            {
                var packet = replay->ZoneInitPacket;
                identity = ZoneInitIdentityState.Select(view, (byte)intendedUse, packet.TerritoryTypeId, packet.ServerId, packet.Instance);
            }
        }
        if (identity == null)
        {
            displayed = null; StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (view.HookAvailable ? global::XASlave.Localization.UiMessage.Key("SBCC9F77CBC612E83") : global::XASlave.Localization.UiMessage.Key("S344031528C5E2E4A")));
            eureka.SetFieldOperationsDtr(true, string.Empty, false, string.Empty, null); return;
        }
        if (displayed != identity) displayed = identity;
        var current = displayed!;
        var provenance = (current.Cached ? global::XASlave.Localization.UiMessage.Key("SDA7494C78F962FAD") : global::XASlave.Localization.UiMessage.Key("S4E526D5145BEE411"));
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S5077DA1D158B33DC", global::XASlave.Localization.UiMessage.Capture(ref territoryLabelMessage, territoryLabel), current.Composite.ToString(CultureInfo.InvariantCulture), provenance));
        var tooltip = global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S6237EF84B44B83A2", current.Server, current.Instance, provenance, current.World, current.DataCenter), global::XASlave.Localization.UiMessage.Raw(eureka.FieldOperationsLegacyTooltip())), global::XASlave.Localization.UiMessage.Key("S9419557CEB7F2BBE"));
        if (current.DataCenter == 0) tooltip = global::XASlave.Localization.UiMessage.Concat(tooltip, global::XASlave.Localization.UiMessage.Key("S05ED8C7F46F6E944"));
        eureka.SetFieldOperationsDtr(true, global::XASlave.Localization.UiMessage.Format("SFB62FE2AE6B2AE6C", global::XASlave.Localization.UiMessage.Capture(ref territoryLabelMessage, territoryLabel), current.Composite.ToString(CultureInfo.InvariantCulture)).Original, configuration.FieldOperationsInstanceDisplayShowInDtr,
            tooltip.Original, interaction => Click(current, interaction));
    }
    private bool AlreadyAnnounced(FieldOperationsIdentity identity) => lastAnnouncedGeneration == identity.Generation && lastAnnouncedComposite == identity.Composite;
    private void MarkAnnounced(FieldOperationsIdentity identity) { lastAnnouncedGeneration = identity.Generation; lastAnnouncedComposite = identity.Composite; }
    private string AppendToEurekaAnnouncement()
    {
        pendingAnnouncement = null;
        if (disposed || !configuration.FieldOperationsInstanceDisplayEnabled || !configuration.FieldOperationsInstanceDisplayChat) return string.Empty;
        Refresh();
        var current = displayed;
        if (current == null || current.Cached || AlreadyAnnounced(current)) return string.Empty;
        pendingAnnouncement = current;
        return global::XASlave.Localization.UiMessage.Format("S2F95824747DF5015", current.Composite.ToString(CultureInfo.InvariantCulture), current.Server, current.Instance).Original;
    }
    private void ConfirmEurekaAnnouncement()
    {
        if (pendingAnnouncement is { } current) MarkAnnounced(current);
        pendingAnnouncement = null;
    }
    private void Click(FieldOperationsIdentity clicked, DtrInteractionEvent interaction)
    {
        // The callback captures the displayed immutable identity; no later identity can be substituted for it.
        void Apply()
        {
            if (disposed || !configuration.FieldOperationsInstanceDisplayEnabled || !configuration.FieldOperationsInstanceDisplayShowInDtr || displayed != clicked) return;
            try
            {
                observations.Refresh(); Refresh();
                if (displayed != clicked) return;
                var text = clicked.Composite.ToString(CultureInfo.InvariantCulture);
                if (interaction.ClickType == MouseClickType.Left)
                {
                    ImGui.SetClipboardText(text);
                    if (!string.Equals(ImGui.GetClipboardText(), text, StringComparison.Ordinal)) { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SD0E5536DB7F2DEE7")); return; }
                    Plugin.ToastGui.ShowQuest(global::XASlave.Localization.UiLocale.Native("S8BDB55AE63F218EA"), new QuestToastOptions { DisplayCheckmark = true });
                }
            }
            catch (Exception error) { StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SB36094A6E57BFC42"), global::XASlave.Localization.UiMessage.FromException(error))); }
        }
        if (framework.IsInFrameworkUpdateThread) Apply(); else Plugin.ScheduleOnGameThread(Apply);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; displayed = null;
        framework.Update -= Update; observations.Invalidated -= Invalidate;
        eureka.FieldOperationsAnnouncementSuffix = null;
        eureka.FieldOperationsAnnouncementPublished = null;
        pendingAnnouncement = null;
        if (framework.IsInFrameworkUpdateThread) eureka.SetFieldOperationsDtr(false, string.Empty, false, string.Empty, null);
        else Plugin.ScheduleOnGameThread(() => eureka.SetFieldOperationsDtr(false, string.Empty, false, string.Empty, null));
    }
}
