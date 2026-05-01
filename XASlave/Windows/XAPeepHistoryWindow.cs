using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using XASlave.Data;

namespace XASlave.Windows;

public sealed class XAPeepHistoryWindow : Window
{
    private readonly Plugin plugin;
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScaleSafe;
    private XAPeepHistorySortColumn sortColumn = XAPeepHistorySortColumn.LastSeen;
    private bool sortDescending = true;
    private int tableOpenSerial;

    public XAPeepHistoryWindow(Plugin plugin)
        : base("XA Peep History###XAPeepHistoryWindow", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        Size = ScaledVector(520f, 320f);
        SizeCondition = ImGuiCond.FirstUseEver;
        UpdateSizeConstraints(UiScaleSafe);
    }

    public override void PreDraw()
    {
        UpdateSizeConstraints(UiScale);
    }

    public override void OnOpen()
    {
        sortColumn = XAPeepHistorySortColumn.LastSeen;
        sortDescending = true;
        tableOpenSerial++;

        if (plugin.Configuration.XAPeepHistoryWindowOpen)
            return;

        plugin.Configuration.XAPeepHistoryWindowOpen = true;
        plugin.Configuration.Save();
    }

    public override void OnClose()
    {
        if (!plugin.Configuration.XAPeepHistoryWindowOpen)
            return;

        plugin.Configuration.XAPeepHistoryWindowOpen = false;
        plugin.Configuration.Save();
    }

    public override void Draw()
    {
        var clearHistoryModifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        if (!clearHistoryModifierHeld)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Clear"))
            plugin.XAPeep.ClearHistory();
        if (!clearHistoryModifierHeld)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Press and hold CTRL + SHIFT to allow clearing.");
        }

        ImGui.Spacing();

        var trackedPlayers = plugin.XAPeep.GetTrackedPlayers(500);
        if (trackedPlayers.Count == 0)
        {
            ImGui.TextDisabled("No XA Peep history recorded yet.");
            return;
        }

        if (!ImGui.BeginTable(
                $"##XAPeepHistoryTable{tableOpenSerial}",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable,
                new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, Scale(56f));
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, Scale(125f));
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
        ImGui.TableHeadersRow();

        ApplyTableSortSpecs();
        var sortedPlayers = SortTrackedPlayers(trackedPlayers);

        foreach (var player in sortedPlayers)
        {
            ImGui.TableNextRow();
            if (!player.IsLive)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(player.TotalTargetCount.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(player.DisplayName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatTimestamp(player.LastSeenUtc));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatDurationSeconds(player.TotalTargetDurationSeconds));

            if (!player.IsLive)
                ImGui.PopStyleColor();
        }

        ImGui.EndTable();
    }

    private void ApplyTableSortSpecs()
    {
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty)
            sortSpecs.SpecsDirty = false;

        if (sortSpecs.SpecsCount <= 0)
            return;

        unsafe
        {
            var spec = sortSpecs.Specs;
            sortColumn = spec.ColumnIndex switch
            {
                0 => XAPeepHistorySortColumn.Count,
                1 => XAPeepHistorySortColumn.Player,
                2 => XAPeepHistorySortColumn.LastSeen,
                3 => XAPeepHistorySortColumn.Total,
                _ => XAPeepHistorySortColumn.LastSeen,
            };
            sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
        }
    }

    private List<XAPeepTrackedPlayerView> SortTrackedPlayers(List<XAPeepTrackedPlayerView> trackedPlayers)
    {
        var sortedPlayers = new List<XAPeepTrackedPlayerView>(trackedPlayers);
        sortedPlayers.Sort((left, right) =>
        {
            var compare = sortColumn switch
            {
                XAPeepHistorySortColumn.Count => left.TotalTargetCount.CompareTo(right.TotalTargetCount),
                XAPeepHistorySortColumn.Player => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase),
                XAPeepHistorySortColumn.LastSeen => left.LastSeenUtc.CompareTo(right.LastSeenUtc),
                XAPeepHistorySortColumn.Total => left.TotalTargetDurationSeconds.CompareTo(right.TotalTargetDurationSeconds),
                _ => left.LastSeenUtc.CompareTo(right.LastSeenUtc),
            };

            if (compare == 0)
                compare = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (compare == 0)
                compare = string.Compare(left.PlayerKey, right.PlayerKey, StringComparison.OrdinalIgnoreCase);

            return sortDescending ? -compare : compare;
        });

        return sortedPlayers;
    }

    private static string FormatDurationSeconds(double durationSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0d, durationSeconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private static string FormatTimestamp(DateTime timestampUtc)
    {
        return timestampUtc == DateTime.MinValue
            ? "-"
            : timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private void UpdateSizeConstraints(float scale)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f * scale, 220f * scale),
            MaximumSize = new Vector2(960f * scale, 720f * scale),
        };
    }

    private static float Scale(float value)
        => value * UiScale;

    private static Vector2 ScaledVector(float x, float y)
        => ImGuiHelpers.ScaledVector2(x, y);

    private enum XAPeepHistorySortColumn
    {
        Count,
        Player,
        LastSeen,
        Total,
    }
}
