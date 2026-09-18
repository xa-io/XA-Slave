using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

// The complete toolbar uses one ImGui input surface, like the mail overlay.
// No toolkit collision list or per-button native input routing is involved.
internal sealed unsafe class FurnitureRestoreControls : IDisposable
{
    internal const float ToolbarWidth = 180, Gap = 6;
    private readonly Func<bool> isCurrent;
    private readonly Action<FurnitureRestoreMode> start;
    private readonly Action cancel, showResults;
    private bool retired, busy, canStart;
    private string status = "Choose a furniture action.";
    internal FurnitureAddonStamp Host { get; }

    internal FurnitureRestoreControls(FurnitureAddonStamp host, Action<FurnitureRestoreMode> start, Action cancel, Action showResults, Func<bool> isCurrent)
    {
        Host = host; this.start = start; this.cancel = cancel; this.showResults = showResults; this.isCurrent = isCurrent;
        if (!CanUseHost()) throw new InvalidOperationException("HousingGoods changed while creating furniture controls.");
    }

    private bool CanUseHost()
    {
        if (retired || !isCurrent()) return false;
        var host = AddonHelper.GetAddon("HousingGoods");
        return host != null && (nint)host == Host.Address && host->Id == Host.Id
            && host->IsVisible && host->IsReady && host->RootNode != null;
    }

    internal bool IsActionEnabled(int action)
        => action switch { 0 or 1 or 2 => !busy && canStart, 3 => busy, 4 => true, _ => false };

    internal void Activate(int action)
    {
        if (!CanUseHost() || !IsActionEnabled(action)) return;
        switch (action)
        {
            case 0: start(FurnitureRestoreMode.PlacedToStoreroom); break;
            case 1: start(FurnitureRestoreMode.PlacedToInventory); break;
            case 2: start(FurnitureRestoreMode.StoredToInventory); break;
            case 3: cancel(); break;
            case 4: showResults(); break;
        }
    }

    // Called by Plugin's existing framework-thread UI draw path.
    internal void Draw()
    {
        if (!CanUseHost()) return;
        var addon = AddonHelper.GetAddon("HousingGoods");
        var host = addon->RootNode;
        var stage = AtkStage.Instance();
        var scale = host->ScaleX;
        if (stage == null || !float.IsFinite(scale) || scale <= 0) return;
        var width = Math.Min(ToolbarWidth * scale, stage->ScreenSize.Width);
        var padding = 4f * scale;
        var spacing = 2f * scale;
        var buttonHeight = Math.Max(22f * scale, ImGui.GetTextLineHeight() + 4f * scale);
        var contentWidth = Math.Max(1, width - padding * 2);
        var textHeight = ImGui.CalcTextSize(status, false, contentWidth).Y;
        var height = padding * 2 + textHeight + spacing * 5 + buttonHeight * 5;
        var hostTop = host->ScreenY;
        var requiredTop = height + Gap * scale;
        if (hostTop < requiredTop)
        {
            addon->SetPosition(addon->X, (short)Math.Clamp(Math.Ceiling(addon->Y + requiredTop - hostTop), short.MinValue, short.MaxValue));
            hostTop = requiredTop;
        }
        var x = Math.Clamp(host->ScreenX + (host->Width * scale - width) / 2, 0, Math.Max(0, stage->ScreenSize.Width - width));
        ImGui.SetNextWindowPos(new Vector2(x, hostTop - requiredTop), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * scale);
        try
        {
            var visible = ImGui.Begin("##XASlaveFurnitureToolbar", flags);
            try
            {
                if (!visible) return;
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + contentWidth);
                try { ImGui.TextUnformatted(status); }
                finally { ImGui.PopTextWrapPos(); }
                Button("Placed -> storeroom", 0, contentWidth, buttonHeight);
                Button("Placed -> inventory", 1, contentWidth, buttonHeight);
                Button("Stored -> inventory", 2, contentWidth, buttonHeight);
                Button("Stop", 3, contentWidth, buttonHeight);
                Button("Per-item results", 4, contentWidth, buttonHeight);
            }
            finally { ImGui.End(); }
        }
        finally { ImGui.PopStyleVar(3); }
    }

    private void Button(string label, int action, float width, float height)
    {
        ImGui.BeginDisabled(!IsActionEnabled(action));
        try
        {
            // Match the Eureka logogram favourites bar's gradient and border,
            // preserving ImGui input and disabled-state handling for every action.
            if (ImGui.InvisibleButton(label, new Vector2(width, height))) Activate(action);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var drawList = ImGui.GetWindowDrawList();
            var alpha = ImGui.GetStyle().Alpha;
            var rounding = ImGui.GetStyle().FrameRounding;
            var top = ImGui.ColorConvertFloat4ToU32(new Vector4(94f / 255f, 93f / 255f, 94f / 255f, alpha));
            var bottom = ImGui.ColorConvertFloat4ToU32(new Vector4(52f / 255f, 51f / 255f, 52f / 255f, alpha));
            var border = ImGui.ColorConvertFloat4ToU32(new Vector4(168f / 255f, 169f / 255f, 168f / 255f, alpha));
            drawList.AddRectFilledMultiColor(min, max, top, top, bottom, bottom);
            drawList.AddRect(min, max, border, rounding, ImDrawFlags.None, 1f);
            if (ImGui.IsItemActive())
                drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.12f * alpha)), rounding);
            else if (ImGui.IsItemHovered())
                drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f * alpha)), rounding);
            var textSize = ImGui.CalcTextSize(label);
            var textPos = new Vector2(min.X + Math.Max(6f, (width - textSize.X) * 0.5f), min.Y + Math.Max(0, (height - textSize.Y) * 0.5f));
            drawList.PushClipRect(min, max, true);
            try { drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), label); }
            finally { drawList.PopClipRect(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        }
        finally { ImGui.EndDisabled(); }
    }

    internal void Update(bool busy, bool canStart, string text)
    {
        if (retired) return;
        this.busy = busy; this.canStart = canStart; status = text;
    }

    internal void StopCallbacks() { retired = true; }
    public void Dispose() => StopCallbacks();
}
