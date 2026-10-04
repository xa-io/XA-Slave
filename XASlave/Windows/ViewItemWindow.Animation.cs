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
            if (ImGui.Button(animation.Playing ? "Pause Animation" : "Play Animation"))
                preview.SetAnimationPlaying(!animation.Playing);
        }
        finally { ImGui.EndDisabled(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Record one animation cycle, including glow, then loop its frames. New previews start paused.");

        if (animation.FrameCount > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            var frame = animation.FrameIndex + 1;
            if (ImGui.SliderInt("##ViewItemAnimationFrame", ref frame, 1, animation.FrameCount,
                    "Frame %d", ImGuiSliderFlags.AlwaysClamp))
                preview.SeekAnimationFrame(frame - 1, animation.Revision);
            if (ImGui.IsItemActivated()) preview.SetAnimationPlaying(false);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Frame {animation.FrameIndex + 1} of {animation.FrameCount}. Drag to pause on a recorded frame, including its glow.");
        }
        ImGui.TextDisabled(animation.Status);
        ImGui.TextDisabled("Changing the view clears the recording. Play again to capture the new angle.");
    }
}
