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
        : base(global::XASlave.Localization.UiLocale.Get("S3906DF9883EAF5F0") + "###XASlaveXagmanMini", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.getSnapshot = getSnapshot;
    }

    public override void PreDraw() => WindowName = global::XASlave.Localization.UiLocale.Get("S3906DF9883EAF5F0") + "###XASlaveXagmanMini";

    public override void Draw()
    {
        var snapshot = getSnapshot();
        if (!snapshot.HasRun)
        {
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.Get("SAF99E2CC3B71E057"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S40FC50EBC71B3FF0"));
            return;
        }

        var role = snapshot.Role == XagmanRole.Tony ? global::XASlave.Localization.UiLocale.Get("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiLocale.Get("SAD5B29877C321F10");
        var mode = snapshot.OutsideNetworkHelper ? global::XASlave.Localization.UiLocale.Get("S590E803E0812A76C") : global::XASlave.Localization.UiLocale.Get("S22965568D22A14EE");
        ImGui.TextUnformatted($"{role} / {mode}");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(snapshot.Running ? snapshot.State.Display : global::XASlave.Localization.UiLocale.FormatKey("S4AFC04ABB1B6102C", snapshot.State.Display));
        ImGui.PopTextWrapPos();
        if (!string.IsNullOrWhiteSpace(snapshot.Character))
            ImGui.TextUnformatted(snapshot.Character);
        var fraction = snapshot.Total > 0 ? Math.Clamp((float)snapshot.Processed / snapshot.Total, 0f, 1f) : 0f;
        ImGui.ProgressBar(fraction, new Vector2(320f * ImGuiHelpers.GlobalScale, 0),
            global::XASlave.Localization.UiLocale.FormatKey("S58C8980BD0DDC860", snapshot.Processed, snapshot.Total, snapshot.ProgressLabel.Display));
        if (snapshot.Failed + snapshot.Skipped + snapshot.Unfinished > 0)
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S8D8731414083CB7F", snapshot.Failed, snapshot.Skipped, snapshot.Unfinished));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S11E98CD46A83A309", snapshot.Estimate.Display));
        ImGui.PopTextWrapPos();
    }
}
