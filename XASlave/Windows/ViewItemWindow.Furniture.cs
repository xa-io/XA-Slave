using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed partial class ViewItemWindow
{
    private static void DrawFurniture(ViewSingleItemFurniturePreview preview)
    {
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1E8977C24EE39B44"))) preview.Camera(-0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD2F1321248875601"))) preview.Camera(0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S80792346CA2D714D"))) preview.Camera(0, -0.15f, 0, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA53DFFFFB7E57BB6"))) preview.Camera(0, 0.15f, 0, 0, 0);
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC90B88F3B2622C2E"))) preview.Camera(0, 0, 0, 0, 0, -0.15f);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1B353379EAB6ED41"))) preview.Camera(0, 0, 0, 0, 0, 0.15f);
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S24649EE0C7A92FD8"))) preview.Camera(0, 0, 1, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S5A982CF9DC11B4EC"))) preview.Camera(0, 0, -1, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7F19F60F9123C6BD"))) preview.ResetCamera();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S9CCF0E8137768B84"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S49405AA6678140E8"));

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
