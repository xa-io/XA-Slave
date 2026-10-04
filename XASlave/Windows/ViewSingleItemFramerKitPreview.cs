using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using XASlave.Services;

namespace XASlave.Windows;

// The window owns selection and framing state. TextureProvider owns the wraps.
internal sealed class ViewSingleItemFramerKitPreview
{
    private uint? loadedKit;
    private IReadOnlyList<ViewSingleItemKitArtwork> artwork = Array.Empty<ViewSingleItemKitArtwork>();
    private int selected;
    private string loadError = string.Empty;
    private uint? failedIcon;
    private float zoom = 1;
    private Vector2 pan;

    internal void Reset()
    {
        selected = 0;
        failedIcon = null;
        ResetView();
    }

    private void ResetView()
    {
        zoom = 1;
        pan = Vector2.Zero;
    }

    private void ZoomAt(float steps, Vector2 anchor)
    {
        if (!float.IsFinite(steps) || steps == 0 || !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y)) return;
        var next = Math.Clamp(zoom * MathF.Pow(1.2f, Math.Clamp(steps, -10, 10)), 0.25f, 16);
        // Keep the image point under the cursor fixed as the scale changes.
        pan = anchor + (pan - anchor) * (next / zoom);
        zoom = next;
    }

    internal void Draw(uint kitId)
    {
        if (loadedKit != kitId)
        {
            loadedKit = kitId;
            Reset();
            artwork = Array.Empty<ViewSingleItemKitArtwork>();
            loadError = string.Empty;
            try { artwork = ViewSingleItemFramerKit.Load(kitId); }
            catch (Exception error)
            {
                loadError = "The Framer's Kit artwork could not be loaded. See /xllog for details.";
                Plugin.Log.Warning(error, "[View Item] Framer's Kit artwork lookup failed; kit={Kit}.", kitId);
            }
        }
        if (loadError.Length != 0)
        {
            ImGui.TextWrapped(loadError);
            return;
        }
        if (artwork.Count == 0)
        {
            ImGui.TextWrapped("This kit has no adventurer plate or portrait artwork available to preview.");
            return;
        }

        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (220 * ImGuiHelpers.GlobalScale)), 1, 4);
        if (ImGui.BeginTable("##ViewItemKitComponents", columns, ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                for (var index = 0; index < artwork.Count; index++)
                {
                    ImGui.TableNextColumn();
                    var active = selected == index;
                    if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
                    try
                    {
                        if (ImGui.Button(artwork[index].Label + $"##KitComponent{index}", new Vector2(-1, 0)))
                        {
                            selected = index;
                            failedIcon = null;
                            ResetView();
                        }
                    }
                    finally { if (active) ImGui.PopStyleColor(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(artwork[index].Label);
                }
            }
            finally { ImGui.EndTable(); }
        }
        var item = artwork[selected];
        ImGui.TextWrapped(item.Label);
        if (ImGui.Button("Zoom Out##KitArtwork")) ZoomAt(-1, Vector2.Zero);
        ImGui.SameLine();
        if (ImGui.Button("Zoom In##KitArtwork")) ZoomAt(1, Vector2.Zero);
        ImGui.SameLine();
        if (ImGui.Button("Reset View##KitArtwork")) ResetView();
        ImGui.SameLine();
        ImGui.TextUnformatted($"{zoom * 100:0}% of fit");
        ImGui.TextDisabled("Wheel: zoom | Left or right drag: pan");
        ImGui.TextDisabled("Drag the title bar to move the window.");
        var shared = Plugin.TextureProvider.GetFromGameIcon(item.IconId);
        if (!shared.TryGetWrap(out var texture, out var failure))
        {
            if (failure != null && failedIcon != item.IconId)
            {
                failedIcon = item.IconId;
                Plugin.Log.Warning(failure, "[View Item] Framer's Kit artwork texture failed; kit={Kit}; icon={Icon}.", kitId, item.IconId);
            }
            ImGui.TextWrapped(failure == null ? "Loading artwork..." : "The artwork texture could not be loaded. See /xllog for details.");
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y)
            || available.X <= 0 || available.Y <= 0 || texture.Width <= 0 || texture.Height <= 0) return;
        var start = ImGui.GetCursorScreenPos();
        // Own the canvas input so image dragging cannot move the whole window.
        ImGui.InvisibleButton("##KitArtworkCanvas", available,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        ImGuiP.SetItemUsingMouseWheel();
        var io = ImGui.GetIO();
        if (ImGui.IsItemHovered())
        {
            ZoomAt(io.MouseWheel, io.MousePos - start - available * 0.5f);
        }
        if (ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Left)
            || ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
        {
            var delta = io.MouseDelta;
            if (float.IsFinite(delta.X) && float.IsFinite(delta.Y)) pan += delta;
        }
        var scale = MathF.Min(available.X / texture.Width, available.Y / texture.Height) * zoom;
        var size = new Vector2(texture.Width * scale, texture.Height * scale);
        // Retain a visible edge even after a large pan or a window resize.
        var visible = Vector2.Min(Vector2.Min(size, available) * 0.5f,
            new Vector2(32 * ImGuiHelpers.GlobalScale));
        var limit = (available + size) * 0.5f - visible;
        pan = Vector2.Clamp(pan, -limit, limit);
        var imageStart = start + (available - size) * 0.5f + pan;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(start, start + available, true);
        try
        {
            drawList.AddRectFilled(start, start + available, ImGui.GetColorU32(ImGuiCol.FrameBg));
            drawList.AddImage(texture.Handle, imageStart, imageStart + size);
        }
        finally { drawList.PopClipRect(); }
    }
}
