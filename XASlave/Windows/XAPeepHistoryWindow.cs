using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace XASlave.Windows;

public sealed class XAPeepHistoryWindow : Window
{
    private readonly Plugin plugin;
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScaleSafe;

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
                "##XAPeepHistoryTable",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, Scale(56f));
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, Scale(125f));
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
        ImGui.TableHeadersRow();

        foreach (var player in trackedPlayers)
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
}
