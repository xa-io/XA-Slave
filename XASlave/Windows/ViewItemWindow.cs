using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed partial class ViewItemWindow : Window
{
    private readonly ViewSingleItemService service;
    private readonly ViewSingleItemFramerKitPreview kitPreview = new();
    private long kitSelectionRevision = -1;
    private bool showHistory;

    internal ViewItemWindow(ViewSingleItemService service)
        : base("View Item###XAViewSingleItem", ImGuiWindowFlags.None)
    {
        this.service = service;
        Size = ImGuiHelpers.ScaledVector2(960, 760);
        SizeCondition = ImGuiCond.Once;
    }

    internal void OpenSearch()
    {
        showCatalogSearch = true;
        showHistory = false;
        IsOpen = true;
    }

    public override void OnClose() => service.Stop();

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = ImGuiHelpers.ScaledVector2(600, 500),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public override void Draw()
    {
        try { DrawContents(); }
        catch (Exception error) { service.ReportPreviewFailure(error); }
    }

    private void DrawContents()
    {
        if (!service.IsEnabled)
        {
            ImGui.TextWrapped("Enable XA Mods > Player Mods > View Single Item to use Find item search or right-click a player or supported item.");
            return;
        }
        if (ImGui.Button("Use current inspection")) service.OpenCurrentInspection();
        ImGui.SameLine();
        if (ImGui.Button("Stop")) service.Stop();
        ImGui.SameLine();
        if (ImGui.Button("History")) showHistory = !showHistory;
        ImGui.SameLine();
        if (ImGui.Button("Find item")) showCatalogSearch = !showCatalogSearch;
        if (showCatalogSearch) DrawCatalogSearch();
        if (showHistory) DrawHistory();
        if (!string.IsNullOrEmpty(service.PlayerName)) ImGui.TextUnformatted(service.PlayerName);
        ImGui.TextWrapped(service.StatusText);
        if (service.Choices.Count == 0) return;

        DrawItemButtons();
        if (service.SelectedKey == 105 && service.FramerKitId is { } kitId)
        {
            if (kitSelectionRevision != service.SelectionRevision)
            {
                kitPreview.Reset();
                kitSelectionRevision = service.SelectionRevision;
            }
            kitPreview.Draw(kitId);
            return;
        }
        var preview = service.Preview;
        if (service.SelectedKey == 106 && preview != null)
        {
            DrawFurniture(preview.Furniture);
            return;
        }
        if (preview != null) DrawAnimationControls(preview);
        if (preview != null && service.SelectedKey is 0 or 1)
        {
            if (ImGui.RadioButton("Sheathe Weapon", !preview.WeaponDrawn)) preview.SetWeaponDrawn(false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shows the weapon's sheathed appearance, then freezes the pose.");
            ImGui.SameLine();
            if (ImGui.RadioButton("Unsheathe Weapon", preview.WeaponDrawn)) preview.SetWeaponDrawn(true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shows the weapon's drawn appearance and supported glow effects, then freezes the pose.");
        }
        if (ImGui.Button("Rotate Left")) preview?.Camera(-0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Rotate Right")) preview?.Camera(0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Above")) preview?.Camera(0, -0.15f, 0, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Below")) preview?.Camera(0, 0.15f, 0, 0, 0);
        if (ImGui.Button("Roll Counterclockwise")) preview?.Camera(0, 0, 0, 0, 0, -0.15f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn the view counterclockwise. Shift + left-drag left or right also rolls the view.");
        ImGui.SameLine();
        if (ImGui.Button("Roll Clockwise")) preview?.Camera(0, 0, 0, 0, 0, 0.15f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn the view clockwise, including sideways or upside down. Shift + left-drag also rolls the view.");
        if (ImGui.Button("Zoom Out")) preview?.Camera(0, 0, 1, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Zoom In")) preview?.Camera(0, 0, -1, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Reset View")) preview?.ResetCamera();
        ImGui.TextDisabled("Drag: rotate / tilt | Shift + drag: roll | Right-drag: pan | Wheel: zoom");
        ImGui.TextDisabled("Drag the title bar to move this window.");

        if (preview == null) return;
        var available = ImGui.GetContentRegionAvail();
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y) || available.X <= 0 || available.Y <= 0) return;
        var size = available;
        var position = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, position + size, ImGui.GetColorU32(ImGuiCol.FrameBg));
        preview.RequestViewport(size);
        // Match the displayed proportions to the applied camera projection. While
        // resizing, retain the previous framing until the new dimensions settle.
        if (preview.TryGetDisplayImage(out var image, out var imageUv))
        {
            var imageSize = FitPreviewImage(size, preview.ViewportSize);
            var imagePosition = position + (size - imageSize) * 0.5f;
            if (imageSize.X > 0 && imageSize.Y > 0)
                drawList.AddImage(image.Handle, imagePosition, imagePosition + imageSize, Vector2.Zero, imageUv);
        }
        // Keep one interactive canvas in both loading and rendered states so
        // dragging captures the mouse instead of initiating a window move.
        ImGui.InvisibleButton("##ViewItemCanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (!hovered && !active) return;
        var io = ImGui.GetIO();
        var delta = io.MouseDelta;
        var leftDrag = active && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        var rotate = leftDrag && !io.KeyShift;
        var roll = leftDrag && io.KeyShift;
        var pan = active && ImGui.IsMouseDragging(ImGuiMouseButton.Right);
        if (!leftDrag && !pan && (!hovered || io.MouseWheel == 0)) return;
        preview.Camera(rotate ? delta.X * 0.01f : 0, rotate ? delta.Y * 0.01f : 0,
            hovered ? -io.MouseWheel : 0, pan ? -delta.X * 0.003f : 0, pan ? delta.Y * 0.003f : 0,
            roll ? delta.X * 0.01f : 0);
    }

    internal static Vector2 FitPreviewImage(Vector2 available, Vector2 pixels)
    {
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y)
            || !float.IsFinite(pixels.X) || !float.IsFinite(pixels.Y)
            || available.X <= 0 || available.Y <= 0 || pixels.X <= 0 || pixels.Y <= 0)
            return Vector2.Zero;
        var scale = Math.Min(available.X / pixels.X, available.Y / pixels.Y);
        return pixels * scale;
    }

    private void DrawItemButtons()
    {
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (150 * ImGuiHelpers.GlobalScale)), 1, 5);
        if (ImGui.BeginTable("##ViewItemCategories", columns, ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                foreach (var choice in service.Choices)
                {
                    ImGui.TableNextColumn();
                    var active = choice.Key == service.SelectedKey;
                    if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
                    try
                    {
                        if (ImGui.Button(ChoiceButtonLabel(choice.Key) + $"##ViewItemChoice{choice.Key}", new Vector2(-1, 0)))
                            service.Select(choice.Key);
                    }
                    finally { if (active) ImGui.PopStyleColor(); }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.IsNullOrEmpty(choice.UnavailableReason)
                            ? choice.Label : choice.Label + "\n" + choice.UnavailableReason);
                }
            }
            finally { ImGui.EndTable(); }
        }
        foreach (var choice in service.Choices)
            if (choice.Key == service.SelectedKey) { ImGui.TextWrapped(choice.Label); break; }
    }

    private static string ChoiceButtonLabel(int key) => key switch
    {
        0 => "Weapon", 1 => "Off Hand", 2 => "Head", 3 => "Body", 4 => "Hands",
        6 => "Legs", 7 => "Feet", 8 => "Earrings", 9 => "Necklace", 10 => "Bracelets",
        11 => "Right Ring", 12 => "Left Ring", 100 => "Hair", 101 => "Facewear",
        102 => "Fashion Accessory", 103 => "Minion", 104 => "Mount", 105 => "Framer's Kit", 106 => "Furniture", _ => "Item",
    };

    private void DrawHistory()
    {
        ImGui.TextWrapped(service.HistoryStatus);
        if (ImGui.Button("Refresh history")) service.RefreshHistory();
        if (ImGui.BeginChild("##ViewItemHistory", new Vector2(0, 150 * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var entry in service.History)
            {
                var stamp = entry.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                var label = entry.World == 0 ? $"{stamp}  {entry.PlayerName}  (Item)"
                    : $"{stamp}  {entry.PlayerName}  (World {entry.World})";
                if (entry.Capture == null) label += " - unavailable";
                if (ImGui.Selectable(label + $"##Capture{entry.Id}")) service.OpenHistory(entry.Id);
                if (entry.Capture == null && ImGui.IsItemHovered()) ImGui.SetTooltip(entry.Error);
            }
        }
        ImGui.EndChild();
    }

}
