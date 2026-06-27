using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XASlave.Windows;

public sealed class EurekaLogogramCreatorFavoritesOverlayWindow : Window
{
    private const int MaxButtonsPerRow = 7;
    private const float PlateButtonWidth = 84f;
    private const float ButtonHeight = 22f;
    private const float ButtonRounding = 4f;
    private const float RowSpacing = 4f;

    private readonly Plugin plugin;

    public EurekaLogogramCreatorFavoritesOverlayWindow(Plugin plugin)
        : base(
            "##XASlaveEurekaLogogramCreatorFavoritesOverlay",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        InhibitAtkCollision = true;
    }

    public override void PreDraw()
    {
        if (plugin.EurekaLogogramCreator.TryGetSynthesisOverlayAnchor(out var position))
        {
            var totalRows = GetTotalRows();
            if (totalRows > 1)
            {
                position.Y -= (totalRows - 1) * (ButtonHeight + RowSpacing);
            }

            ImGui.SetNextWindowPos(position, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.0f);
        }
    }

    public override void Draw()
    {
        if (!plugin.EurekaLogogramCreator.TryGetSynthesisOverlayAnchor(out _))
        {
            return;
        }

        var topColor = ImGui.ColorConvertFloat4ToU32(new Vector4(94f / 255f, 93f / 255f, 94f / 255f, 1.0f));
        var bottomColor = ImGui.ColorConvertFloat4ToU32(new Vector4(52f / 255f, 51f / 255f, 52f / 255f, 1.0f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(168f / 255f, 169f / 255f, 168f / 255f, 1.0f));
        var hoverOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 1.0f, 1.0f, 0.08f));
        var activeOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 0.0f, 0.0f, 0.12f));
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var drawList = ImGui.GetWindowDrawList();
        var totalRows = GetTotalRows();

        for (var row = totalRows - 1; row >= 0; row--)
        {
            var rowStart = row * MaxButtonsPerRow;
            var rowEndExclusive = Math.Min(rowStart + MaxButtonsPerRow, plugin.Configuration.FavoritePlates.Count);
            for (var i = rowStart; i < rowEndExclusive; i++)
            {
                var plate = plugin.Configuration.FavoritePlates[i];
                var label = plugin.EurekaLogogramCreator.DescribeFavoritePlateCompact(plate);

                if (i > rowStart)
                {
                    ImGui.SameLine();
                }

                var buttonId = $"##EurekaLogogramCreatorFavoritePlateButton{i}";
                if (ImGui.InvisibleButton(buttonId, new Vector2(PlateButtonWidth, ButtonHeight)))
                {
                    plugin.EurekaLogogramCreator.QueueFavoritePlate(plate);
                }

                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                drawList.AddRectFilledMultiColor(min, max, topColor, topColor, bottomColor, bottomColor);
                drawList.AddRect(min, max, borderColor, ButtonRounding, ImDrawFlags.None, 1.0f);

                if (ImGui.IsItemActive())
                {
                    drawList.AddRectFilled(min, max, activeOverlay, ButtonRounding);
                }
                else if (ImGui.IsItemHovered())
                {
                    drawList.AddRectFilled(min, max, hoverOverlay, ButtonRounding);
                }

                var textSize = ImGui.CalcTextSize(label);
                var textPos = new Vector2(
                    min.X + Math.Max(6f, (PlateButtonWidth - textSize.X) * 0.5f),
                    min.Y + Math.Max(0f, (ButtonHeight - textSize.Y) * 0.5f));
                ImGui.PushClipRect(min, max, true);
                drawList.AddText(textPos, textColor, label);
                ImGui.PopClipRect();

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(string.Join(" / ", plugin.EurekaLogogramCreator.GetFavoritePlateActionNames(plate)));
                }
            }
        }
    }

    private int GetTotalRows()
    {
        var plateCount = plugin.Configuration.FavoritePlates.Count;
        return plateCount == 0 ? 0 : (plateCount + MaxButtonsPerRow - 1) / MaxButtonsPerRow;
    }
}
