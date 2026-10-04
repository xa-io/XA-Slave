using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed partial class ViewItemWindow
{
    private static void DrawFurniture(ViewSingleItemFurniturePreview preview)
    {
        if (ImGui.Button("Rotate Left")) preview.Camera(-0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Rotate Right")) preview.Camera(0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Above")) preview.Camera(0, -0.15f, 0, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Below")) preview.Camera(0, 0.15f, 0, 0, 0);
        if (ImGui.Button("Roll Counterclockwise")) preview.Camera(0, 0, 0, 0, 0, -0.15f);
        ImGui.SameLine();
        if (ImGui.Button("Roll Clockwise")) preview.Camera(0, 0, 0, 0, 0, 0.15f);
        if (ImGui.Button("Zoom Out")) preview.Camera(0, 0, 1, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Zoom In")) preview.Camera(0, 0, -1, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Reset View")) preview.ResetCamera();
        ImGui.TextDisabled("Drag: rotate / tilt | Shift + drag: roll | Right-drag: pan | Wheel: zoom");
        ImGui.TextDisabled("Static model and textures; housing animations, dyes and effects are not included.");

        var size = ImGui.GetContentRegionAvail();
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0) return;
        preview.SetViewport(size);
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(ImGuiCol.FrameBg));
        draw.PushClipRect(origin, origin + size, true);
        try
        {
            if (preview.TryGetImage(out var texture))
            {
                // A resize can finish before the next worker image; contain the
                // old image until then instead of stretching its proportions.
                var scale = Math.Min(size.X / texture.Width, size.Y / texture.Height);
                var imageSize = new Vector2(texture.Width, texture.Height) * scale;
                var topLeft = origin + (size - imageSize) * 0.5f;
                draw.AddImage(texture.Handle, topLeft, topLeft + imageSize);
            }
        }
        finally { draw.PopClipRect(); }
        ImGui.InvisibleButton("##ViewFurnitureCanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (!hovered && !active) return;
        ImGuiP.SetItemUsingMouseWheel();
        var io = ImGui.GetIO();
        var delta = io.MouseDelta;
        var left = active && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        var right = active && ImGui.IsMouseDragging(ImGuiMouseButton.Right);
        preview.Camera(left && !io.KeyShift ? delta.X * 0.01f : 0,
            left && !io.KeyShift ? delta.Y * 0.01f : 0, hovered ? -io.MouseWheel : 0,
            right ? delta.X / size.X : 0, right ? delta.Y / size.Y : 0,
            left && io.KeyShift ? delta.X * 0.01f : 0);
    }
}
