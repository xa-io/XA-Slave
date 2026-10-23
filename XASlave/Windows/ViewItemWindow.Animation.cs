using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed partial class ViewItemWindow
{
    private static void DrawAnimationControls(ViewSingleItemPreview preview)
    {
        var animation = preview.Animation;
        ImGui.BeginDisabled(!animation.Ready);
        try
        {
            if (global::XASlave.Localization.UiNative.Button(animation.Playing ? global::XASlave.Localization.UiLocale.Native("S89314C065E7A92A9") : global::XASlave.Localization.UiLocale.Native("S28178E88501D5220")))
                preview.SetAnimationPlaying(!animation.Playing);
        }
        finally { ImGui.EndDisabled(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SB54B7A664A9AF1E3"));

        if (animation.FrameCount > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            var frame = animation.FrameIndex + 1;
            if (ImGui.SliderInt("##ViewItemAnimationFrame", ref frame, 1, animation.FrameCount,
                    global::XASlave.Localization.UiLocale.Get("SD0B50E064A4F8D94").Replace("%", "%%", System.StringComparison.Ordinal) + " %d", ImGuiSliderFlags.AlwaysClamp))
                preview.SeekAnimationFrame(frame - 1, animation.Revision);
            if (ImGui.IsItemActivated()) preview.SetAnimationPlaying(false);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S4DE4C17233F3D3A7", animation.FrameIndex + 1, animation.FrameCount));
        }
        ImGui.TextDisabled(animation.LocalizedStatus);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA59E8FEC41162F01"));
    }
}
