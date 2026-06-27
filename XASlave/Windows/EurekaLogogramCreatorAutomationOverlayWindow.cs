using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XASlave.Windows;

public sealed class EurekaLogogramCreatorAutomationOverlayWindow : Window
{
    private const float CancelButtonWidth = 160f;
    private const float CancelButtonHeight = 24f;
    private const float ButtonRounding = 0f;

    private readonly Plugin plugin;

    public EurekaLogogramCreatorAutomationOverlayWindow(Plugin plugin)
        : base(
            "##XASlaveEurekaLogogramCreatorAutomationOverlay",
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
        if (plugin.EurekaLogogramCreator.TryGetSynthesisAutomationOverlayAnchor(out var position))
        {
            ImGui.SetNextWindowPos(position, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.0f);
        }
    }

    public override void Draw()
    {
        if (!plugin.EurekaLogogramCreator.HasActiveOrQueuedAutoLogoAction
            || !plugin.EurekaLogogramCreator.TryGetSynthesisAutomationOverlayAnchor(out _))
        {
            return;
        }

        var topColor = ImGui.ColorConvertFloat4ToU32(new Vector4(140f / 255f, 50f / 255f, 50f / 255f, 1.0f));
        var bottomColor = ImGui.ColorConvertFloat4ToU32(new Vector4(84f / 255f, 26f / 255f, 26f / 255f, 1.0f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(192f / 255f, 132f / 255f, 132f / 255f, 1.0f));
        var hoverOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 1.0f, 1.0f, 0.08f));
        var activeOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 0.0f, 0.0f, 0.18f));
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var drawList = ImGui.GetWindowDrawList();
        const string label = "Cancel Auto Logo Action";

        if (ImGui.InvisibleButton("##CancelEurekaLogogramCreatorAutoLogoAction", new Vector2(CancelButtonWidth, CancelButtonHeight)))
        {
            plugin.EurekaLogogramCreator.CancelAutoLogoAction();
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
            min.X + Math.Max(6f, (CancelButtonWidth - textSize.X) * 0.5f),
            min.Y + Math.Max(0f, (CancelButtonHeight - textSize.Y) * 0.5f));
        ImGui.PushClipRect(min, max, true);
        drawList.AddText(textPos, textColor, label);
        ImGui.PopClipRect();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Stops the current Auto Logo Action run and clears all queued plates.");
        }
    }
}
