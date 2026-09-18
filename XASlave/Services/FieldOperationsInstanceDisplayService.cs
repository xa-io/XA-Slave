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
    public string StatusText { get; private set; } = "Disabled";

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
            var text = $"[XASlave] {territoryLabel} Server ID: {current.Composite.ToString(CultureInfo.InvariantCulture)}";
            Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(45).AddText(text).AddUiForegroundOff().Build());
            MarkAnnounced(current);
        }
        catch (Exception error)
        {
            displayed = null; StatusText = "Field Operations identity unavailable: " + error.Message;
            eureka.SetFieldOperationsDtr(true, string.Empty, false, string.Empty, null);
        }
    }
    private unsafe void Refresh()
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        if (!configuration.FieldOperationsInstanceDisplayEnabled)
        {
            displayed = null; StatusText = "Disabled";
            eureka.SetFieldOperationsDtr(false, string.Empty, false, string.Empty, null); return;
        }
        var view = observations.Read();
        var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(view.Context.Territory);
        var intendedUse = row?.TerritoryIntendedUse.RowId ?? 0;
        if (intendedUse > byte.MaxValue || !ZoneInitIdentityState.Supported((byte)intendedUse))
        {
            displayed = null; StatusText = "Outside supported Field Operations";
            eureka.SetFieldOperationsDtr(false, string.Empty, false, string.Empty, null); return;
        }
        territoryLabel = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? "Field Operations";
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
            displayed = null; StatusText = view.HookAvailable ? "Waiting for a current-generation zone identity" : "Zone-init observer unavailable";
            eureka.SetFieldOperationsDtr(true, string.Empty, false, string.Empty, null); return;
        }
        if (displayed != identity) displayed = identity;
        var current = displayed!;
        var provenance = current.Cached ? "cached replay-manager snapshot; no fresh entry announcement" : "live zone-init observation";
        StatusText = $"{territoryLabel}: {current.Composite.ToString(CultureInfo.InvariantCulture)} ({provenance})";
        var tooltip = $"Server {current.Server}, instance {current.Instance}; {provenance}. World {current.World}, DC {current.DataCenter}.\n" + eureka.FieldOperationsLegacyTooltip() +
            "\nLeft click: copy this composite ID.";
        if (current.DataCenter == 0) tooltip += "\nCurrent data center unavailable; external link disabled.";
        eureka.SetFieldOperationsDtr(true, $"{territoryLabel}: {current.Composite.ToString(CultureInfo.InvariantCulture)}", configuration.FieldOperationsInstanceDisplayShowInDtr,
            tooltip, interaction => Click(current, interaction));
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
        return $" Server ID: {current.Composite.ToString(CultureInfo.InvariantCulture)} ({current.Server}/{current.Instance}, live).";
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
                    if (!string.Equals(ImGui.GetClipboardText(), text, StringComparison.Ordinal)) { StatusText = "Clipboard write could not be verified"; return; }
                    Plugin.ToastGui.ShowQuest("Copied server ID", new QuestToastOptions { DisplayCheckmark = true });
                }
            }
            catch (Exception error) { StatusText = "Field Operations click failed: " + error.Message; }
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
