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
        : base(global::XASlave.Localization.UiLocale.Get("SEF7E5322A926044B") + "###XAViewSingleItem", ImGuiWindowFlags.None)
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
        WindowName = global::XASlave.Localization.UiLocale.Get("SEF7E5322A926044B") + "###XAViewSingleItem";
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
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SBB0DF7AF96BC40EC"));
            return;
        }
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SAFA6A38A0534E12E"))) service.OpenCurrentInspection();
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SCAE7D57BC067A514"))) service.Stop();
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S0E76960093379060"))) showHistory = !showHistory;
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S15CB4F141211A50F"))) showCatalogSearch = !showCatalogSearch;
        if (showCatalogSearch) DrawCatalogSearch();
        if (showHistory) DrawHistory();
        if (!string.IsNullOrEmpty(service.PlayerName)) ImGui.TextUnformatted(service.PlayerName);
        ImGui.TextWrapped(service.LocalizedStatusText);
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
            if (global::XASlave.Localization.UiNative.RadioButton(global::XASlave.Localization.UiLocale.Native("SA8C767779B6DE601"), !preview.WeaponDrawn)) preview.SetWeaponDrawn(false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SB945D411AA9BE1B4"));
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.RadioButton(global::XASlave.Localization.UiLocale.Native("S028F8B9BD94F5F80"), preview.WeaponDrawn)) preview.SetWeaponDrawn(true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S80D53AFFF9CE844F"));
        }
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1E8977C24EE39B44"))) preview?.Camera(-0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SD2F1321248875601"))) preview?.Camera(0.15f, 0, 0, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S80792346CA2D714D"))) preview?.Camera(0, -0.15f, 0, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SA53DFFFFB7E57BB6"))) preview?.Camera(0, 0.15f, 0, 0, 0);
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC90B88F3B2622C2E"))) preview?.Camera(0, 0, 0, 0, 0, -0.15f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S47CBE59ACC20EF01"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S1B353379EAB6ED41"))) preview?.Camera(0, 0, 0, 0, 0, 0.15f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SCA08E0F5181F7D6E"));
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S24649EE0C7A92FD8"))) preview?.Camera(0, 0, 1, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S5A982CF9DC11B4EC"))) preview?.Camera(0, 0, -1, 0, 0);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7F19F60F9123C6BD"))) preview?.ResetCamera();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S9CCF0E8137768B84"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S6B0ADEA6943EAAC4"));

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
                        if (global::XASlave.Localization.UiNative.Button(ChoiceButtonLabel(choice.Key) + $"##ViewItemChoice{choice.Key}", new Vector2(-1, 0)))
                            service.Select(choice.Key);
                    }
                    finally { if (active) ImGui.PopStyleColor(); }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.IsNullOrEmpty(choice.UnavailableReason)
                            ? choice.LocalizedLabel : choice.LocalizedLabel + "\n" + choice.LocalizedUnavailableReason);
                }
            }
            finally { ImGui.EndTable(); }
        }
        foreach (var choice in service.Choices)
            if (choice.Key == service.SelectedKey) { ImGui.TextWrapped(choice.LocalizedLabel); break; }
    }

    private static string ChoiceButtonLabel(int key) => key switch
    {
        0 => global::XASlave.Localization.UiLocale.Native("S1710782C5D15B913"), 1 => global::XASlave.Localization.UiLocale.Native("SBFD173C2F222B066"), 2 => global::XASlave.Localization.UiLocale.Native("S9BB0AACC6D51F140"), 3 => global::XASlave.Localization.UiLocale.Native("S6CCAA6415B5EE449"), 4 => global::XASlave.Localization.UiLocale.Native("S6B7204778686145F"),
        6 => global::XASlave.Localization.UiLocale.Native("S38557594D0FCE7FE"), 7 => global::XASlave.Localization.UiLocale.Native("SC59E96ABC48597B1"), 8 => global::XASlave.Localization.UiLocale.Native("SA532E980AE2F48EB"), 9 => global::XASlave.Localization.UiLocale.Native("S6A88445BBF65F756"), 10 => global::XASlave.Localization.UiLocale.Native("SA9A8BED2FC5B6067"),
        11 => global::XASlave.Localization.UiLocale.Native("SDBC12EA1A8F44940"), 12 => global::XASlave.Localization.UiLocale.Native("S3A3780EAF4A4B1FA"), 100 => global::XASlave.Localization.UiLocale.Native("SABABED4823AB79D2"), 101 => global::XASlave.Localization.UiLocale.Native("SAF854AC129CA6190"),
        102 => global::XASlave.Localization.UiLocale.Native("SE896611DD4474B59"), 103 => global::XASlave.Localization.UiLocale.Native("S2904BFF43BC4B7FE"), 104 => global::XASlave.Localization.UiLocale.Native("SD5605821C44BA5CB"), 105 => global::XASlave.Localization.UiLocale.Native("S2C5C087FBC66991D"), 106 => global::XASlave.Localization.UiLocale.Native("SC50440E0E41148B9"), _ => global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"),
    };

    private void DrawHistory()
    {
        ImGui.TextWrapped(service.LocalizedHistoryStatus);
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S70F3D21749CCB8DA"))) service.RefreshHistory();
        if (ImGui.BeginChild("##ViewItemHistory", new Vector2(0, 150 * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var entry in service.History)
            {
                var stamp = entry.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                var label = entry.World == 0 ? $"{stamp}  {entry.PlayerName}  (Item)"
                    : $"{stamp}  {entry.PlayerName}  (World {entry.World})";
                if (entry.Capture == null) label += " - unavailable";
                var display = entry.World == 0 ? global::XASlave.Localization.UiLocale.FormatKey("SDF55C71EBF46A8B1", stamp, entry.LocalizedPlayerName)
                    : global::XASlave.Localization.UiLocale.FormatKey("SA29B42C3DECD5FF9", stamp, entry.LocalizedPlayerName, entry.World);
                if (entry.Capture == null) display += global::XASlave.Localization.UiLocale.Get("SF908C382BE86A0B8");
                if (global::XASlave.Localization.UiNative.Selectable(label + $"##Capture{entry.Id}", display: display)) service.OpenHistory(entry.Id);
                if (entry.Capture == null && ImGui.IsItemHovered()) ImGui.SetTooltip(entry.LocalizedError);
            }
        }
        ImGui.EndChild();
    }

}
