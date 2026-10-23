using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Localization;

/// <summary>Localizes authored captions using native controls, original IDs and existing styles.</summary>
internal static partial class UiNative
{
    internal static string Caption(string label)
    {
        var end = label.IndexOf("##", System.StringComparison.Ordinal);
        return end < 0 ? label : label[..end];
    }

    internal static bool Button(string nativeLabel, Vector2 size = default, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        return ImGui.Button(caption.Label, size);
    }

    internal static bool Checkbox(string nativeLabel, ref bool value, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        return ImGui.Checkbox(caption.Label, ref value);
    }

    internal static bool Selectable(string nativeLabel, bool selected = false, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, Vector2 size = default, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        return ImGui.Selectable(caption.Label, selected, flags, size);
    }

    internal static bool InputInt(string nativeLabel, ref int value, int step = 0, int stepFast = 0, ImU8String format = default, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        return ImGui.InputInt(caption.Label, ref value, step, stepFast, format, flags);
    }

    internal static bool InputFloat(string nativeLabel, ref float value, float step = 0, float stepFast = 0, ImU8String format = default, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.InputFloat(caption.Label, ref value, step, stepFast, format, flags);
    }

    internal static bool InputDouble(string nativeLabel, ref double value, double step = 0, double stepFast = 0, ImU8String format = default, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.InputDouble(caption.Label, ref value, step, stepFast, format, flags);
    }

    internal static bool SliderInt(string nativeLabel, ref int value, int min = 0, int max = 0, ImU8String format = default, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.SliderInt(caption.Label, ref value, min, max, format, flags);
    }

    internal static bool SliderFloat(string nativeLabel, ref float value, float min = 0, float max = 0, ImU8String format = default, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.SliderFloat(caption.Label, ref value, min, max, format, flags);
    }

    internal static bool DragInt(string nativeLabel, ref int value, float speed = 1, int min = 0, int max = 0, ImU8String format = default, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.DragInt(caption.Label, ref value, speed, min, max, format, flags);
    }

    internal static bool DragFloat(string nativeLabel, ref float value, float speed = 1, float min = 0, float max = 0, ImU8String format = default, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.DragFloat(caption.Label, ref value, speed, min, max, format, flags);
    }

    internal static bool InputText(string nativeLabel, ref string value, int maxLength = 512, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.InputText(caption.Label, ref value, maxLength, flags);
    }

    internal static bool InputTextWithHint(string nativeLabel, string hint, ref string value, int maxLength = 512, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.InputTextWithHint(caption.Label, hint, ref value, maxLength, flags);
    }

    internal static bool ColorEdit3(string nativeLabel, ref Vector3 color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.ColorEdit3(caption.Label, ref color, flags);
    }

    internal static bool ColorEdit4(string nativeLabel, ref Vector4 color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.ColorEdit4(caption.Label, ref color, flags);
    }

    internal static bool SmallButton(string nativeLabel, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        return ImGui.SmallButton(caption.Label);
    }

    internal static bool RadioButton(string nativeLabel, bool active)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.RadioButton(caption.Label, active);
    }

    internal static bool TreeNode(string nativeLabel, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        var caption = Caption(nativeLabel);
        var display = UiLocale.T(caption);
        return display == caption
            ? ImGui.TreeNodeEx(nativeLabel, flags)
            : ImGuiP.TreeNodeBehavior(ImGui.GetID(nativeLabel), flags, display);
    }

    internal static bool CollapsingHeader(string nativeLabel, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        => TreeNode(nativeLabel, flags | ImGuiTreeNodeFlags.CollapsingHeader);
}
