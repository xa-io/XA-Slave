using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Data;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawXagmanGreenValueForecasts(Configuration cfg)
    {
        if (!HasXagmanGreenValueSelectors(cfg.XagmanItems))
            return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.55f, 1f), "Green Gear Value Forecast");
        ImGui.TextDisabled(
            "GC Seals and FC Credits / Rank Progress use one physical green-item pool; the two rows are not additive.");
        if (cfg.XagmanOutsideNetworkHelper)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.55f, 0.25f, 1f),
                "Unavailable in Outside Network Helper. Use connected peer-managed Xagman.");
            return;
        }

        var localSnapshot = xagmanGreenValueScanCache?.Snapshot;
        var activeCharacter = !string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? xagmanActiveCharacter
            : MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        var selectors = cfg.XagmanItems
            .Where(item => IsXagmanGreenValueSelector(item.SelectorKind))
            .Select(item => item.SelectorKind)
            .Distinct()
            .OrderBy(selector => selector)
            .ToList();

        if (ImGui.BeginTable(
                "xagmanGreenValueForecastTable",
                5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Metric");
            ImGui.TableSetupColumn("Current");
            ImGui.TableSetupColumn("Goal");
            ImGui.TableSetupColumn("Shortage");
            ImGui.TableSetupColumn("Tony safe supply");
            ImGui.TableHeadersRow();

            foreach (var selector in selectors)
            {
                long currentScaled2;
                long targetScaled2;
                long shortageScaled2;
                long tonySupplyScaled2;
                bool currentKnown;
                bool targetKnown;
                bool tonySupplyKnown;

                if (cfg.XagmanRole == XagmanRole.Tony)
                {
                    var peerSnapshots = plugin.XagmanPeers.Peers
                        .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
                        .Where(peer => peer.XagmanEnabled)
                        .Where(IsXagmanPeerInCurrentRunPhase)
                        .Where(peer => IsXagmanPeerFresh(peer, 15.0))
                        .Where(peer => peer.GreenValueProtocolRevision == XagmanGreenValueProtocolRevision)
                        .Select(peer => peer.GreenValueSnapshot)
                        .Where(snapshot => snapshot?.Complete == true)
                        .Select(snapshot => snapshot!)
                        .ToList();
                    currentScaled2 = peerSnapshots.Sum(snapshot =>
                        GetXagmanGreenMetricScaled2(snapshot, selector));
                    targetScaled2 = peerSnapshots.Sum(snapshot =>
                        GetXagmanGreenTargetMetricScaled2(snapshot, selector));
                    shortageScaled2 = peerSnapshots.Sum(snapshot => Math.Max(
                        0,
                        GetXagmanGreenTargetMetricScaled2(snapshot, selector)
                            - GetXagmanGreenMetricScaled2(snapshot, selector)));
                    tonySupplyScaled2 = GetXagmanGreenDropboxMetricScaled2(localSnapshot, selector);
                    currentKnown = peerSnapshots.Count > 0;
                    targetKnown = peerSnapshots.Count > 0;
                    tonySupplyKnown = localSnapshot?.Complete == true;
                }
                else
                {
                    var resolved = ResolveXagmanItemsForOwner(
                        cfg.XagmanItems,
                        activeCharacter,
                        out var skippedUnknownConditionalGroup);
                    var effective = resolved
                        .FirstOrDefault(item => item.SelectorKind == selector);
                    targetKnown = effective != null || !skippedUnknownConditionalGroup;
                    targetScaled2 = Math.Max(0L, effective?.Quantity ?? 0) * 2L;
                    currentScaled2 = localSnapshot?.Complete == true
                        ? GetXagmanGreenMetricScaled2(localSnapshot, selector)
                        : 0;
                    currentKnown = localSnapshot?.Complete == true;
                    shortageScaled2 = Math.Max(0, targetScaled2 - currentScaled2);
                    var tonyPeer = GetXagmanLiveTonyPeer();
                    var tonySnapshot = tonyPeer?.GreenValueSnapshot;
                    tonySupplyKnown = tonyPeer != null
                        && IsXagmanPeerFresh(tonyPeer, 15.0)
                        && tonyPeer.GreenValueProtocolRevision == XagmanGreenValueProtocolRevision
                        && tonySnapshot?.Complete == true;
                    tonySupplyScaled2 = tonySupplyKnown
                        ? GetXagmanGreenDropboxMetricScaled2(tonySnapshot, selector)
                        : 0;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetXagmanGreenValueSelectorName(selector));
                ImGui.TableNextColumn();
                if (currentKnown)
                    ImGui.TextUnformatted(FormatXagmanScaled2(currentScaled2));
                else
                    ImGui.TextDisabled("Unknown");
                ImGui.TableNextColumn();
                if (targetKnown)
                    ImGui.TextUnformatted(FormatXagmanScaled2(targetScaled2));
                else
                    ImGui.TextDisabled("Unknown");
                ImGui.TableNextColumn();
                if (currentKnown && targetKnown)
                    ImGui.TextUnformatted(FormatXagmanScaled2(shortageScaled2));
                else
                    ImGui.TextDisabled("Unknown");
                ImGui.TableNextColumn();
                if (tonySupplyKnown)
                    ImGui.TextUnformatted(FormatXagmanScaled2(tonySupplyScaled2));
                else
                    ImGui.TextDisabled("Unknown");
            }

            ImGui.EndTable();
        }

        if (localSnapshot == null)
        {
            ImGui.TextDisabled("Waiting for the next presence-tick inventory scan.");
        }
        else if (!localSnapshot.Complete)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.45f, 0.35f, 1f),
                $"Fail closed: {localSnapshot.Error}");
        }
        else
        {
            var age = Math.Max(0, (DateTime.UtcNow - localSnapshot.GeneratedAtUtc).TotalSeconds);
            ImGui.TextDisabled(
                $"Safe held items: {localSnapshot.SafeItemCount.ToString("N0", CultureInfo.InvariantCulture)}; " +
                $"Dropbox-selectable: {localSnapshot.DropboxSafeItemCount.ToString("N0", CultureInfo.InvariantCulture)}; " +
                $"excluded: {localSnapshot.ExcludedItemCount.ToString("N0", CultureInfo.InvariantCulture)}; " +
                $"blocked keys: {localSnapshot.BlockedKeyCount.ToString("N0", CultureInfo.InvariantCulture)}; " +
                $"scan age: {age:0}s.");
        }
    }

    private static long GetXagmanGreenDropboxMetricScaled2(
        XagmanGreenValueSnapshot? snapshot,
        XagmanItemSelectorKind selector)
    {
        if (snapshot?.Complete != true)
            return 0;
        return selector switch
        {
            XagmanItemSelectorKind.GreenItemGcSeals => Math.Max(0, snapshot.DropboxGcSealsScaled2),
            XagmanItemSelectorKind.GreenItemFcCreditsRankProgress => Math.Max(0, snapshot.DropboxFcCreditsScaled2),
            _ => 0,
        };
    }

    private static long GetXagmanGreenTargetMetricScaled2(
        XagmanGreenValueSnapshot snapshot,
        XagmanItemSelectorKind selector)
    {
        return selector switch
        {
            XagmanItemSelectorKind.GreenItemGcSeals => Math.Max(0, snapshot.GcSealsTargetScaled2),
            XagmanItemSelectorKind.GreenItemFcCreditsRankProgress => Math.Max(0, snapshot.FcCreditsTargetScaled2),
            _ => 0,
        };
    }

    private XagmanGreenValueSnapshot BuildXagmanGreenValuePresenceSnapshot(
        XagmanGreenValueSnapshot source,
        string activeCharacter)
    {
        var snapshot = new XagmanGreenValueSnapshot
        {
            GeneratedAtUtc = source.GeneratedAtUtc,
            Revision = source.Revision,
            Complete = source.Complete,
            Error = source.Error,
            GcSealsScaled2 = source.GcSealsScaled2,
            FcCreditsScaled2 = source.FcCreditsScaled2,
            DropboxGcSealsScaled2 = source.DropboxGcSealsScaled2,
            DropboxFcCreditsScaled2 = source.DropboxFcCreditsScaled2,
            SafeItemCount = source.SafeItemCount,
            DropboxSafeItemCount = source.DropboxSafeItemCount,
            ExcludedItemCount = source.ExcludedItemCount,
            BlockedKeyCount = source.BlockedKeyCount,
        };
        foreach (var item in ResolveXagmanItemsForOwner(
                     plugin.Configuration.XagmanItems,
                     activeCharacter))
        {
            var targetScaled2 = Math.Max(0L, item.Quantity) * 2L;
            switch (item.SelectorKind)
            {
                case XagmanItemSelectorKind.GreenItemGcSeals:
                    snapshot.GcSealsTargetScaled2 = targetScaled2;
                    break;
                case XagmanItemSelectorKind.GreenItemFcCreditsRankProgress:
                    snapshot.FcCreditsTargetScaled2 = targetScaled2;
                    break;
            }
        }
        return snapshot;
    }
}
