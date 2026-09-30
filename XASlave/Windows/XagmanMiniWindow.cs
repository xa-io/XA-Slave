using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using XASlave.Data;

namespace XASlave.Windows;

internal sealed class XagmanMiniWindow : Window
{
    private readonly Func<XagmanMiniSnapshot> getSnapshot;

    internal XagmanMiniWindow(Func<XagmanMiniSnapshot> getSnapshot)
        : base("Xagman Progress###XASlaveXagmanMini", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.getSnapshot = getSnapshot;
    }

    public override void Draw()
    {
        var snapshot = getSnapshot();
        if (!snapshot.HasRun)
        {
            ImGui.TextUnformatted("Xagman is idle.");
            ImGui.TextDisabled("Start a run from the main Xagman panel.");
            return;
        }

        var role = snapshot.Role == XagmanRole.Tony ? "Tony" : "Franchise Owner";
        var mode = snapshot.OutsideNetworkHelper ? "Outside Network Helper" : "Connected";
        ImGui.TextUnformatted($"{role} / {mode}");
        ImGui.TextUnformatted(snapshot.Running ? snapshot.State : $"Last run: {snapshot.State}");
        if (!string.IsNullOrWhiteSpace(snapshot.Character))
            ImGui.TextUnformatted(snapshot.Character);
        var fraction = snapshot.Total > 0 ? Math.Clamp((float)snapshot.Processed / snapshot.Total, 0f, 1f) : 0f;
        ImGui.ProgressBar(fraction, new Vector2(320f * ImGuiHelpers.GlobalScale, 0),
            $"{snapshot.Processed}/{snapshot.Total} local characters {snapshot.ProgressLabel}");
        if (snapshot.Failed + snapshot.Skipped + snapshot.Unfinished > 0)
            ImGui.TextUnformatted($"{snapshot.Failed} failed / {snapshot.Skipped} skipped / {snapshot.Unfinished} unfinished");
        ImGui.TextUnformatted($"Estimate: {snapshot.Estimate}");
    }
}
