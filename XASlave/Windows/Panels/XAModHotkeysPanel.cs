using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string xaHotkeySearch = string.Empty;
    private string? xaHotkeyEditorKey;
    private uint xaHotkeyEditorPopupId;
    private UiMessage xaHotkeyEditorStatus = UiMessage.Raw(string.Empty);
    private bool xaHotkeyEditorError;
    private bool xaHotkeyCapturePending;
    private bool xaHotkeyCaptureNeedsFocus;
    private UiMessage xaHotkeyOperationsStatus = UiMessage.Raw(string.Empty);
    private bool xaHotkeyOperationsError;

    private void DrawPluginOpsXAModHotkeys()
    {
        if (!UiNative.CollapsingHeader(UiLocale.Native("SC15F1530D219F94A")))
            return;

        using var sectionId = ImRaii.PushId("PluginOperationsXAModHotkeys");
        var service = plugin.XAModHotkeys;
        var enabled = plugin.Configuration.XAModHotkeysEnabled;
        if (UiNative.Checkbox(UiLocale.Native("S9E0BEC5DC44EA1D7"), ref enabled))
            xaHotkeyOperationsError = !service.TrySetGlobalEnabled(enabled, out xaHotkeyOperationsStatus);
        ImGui.TextWrapped(UiLocale.Get("S503ED79C2E4C31B4"));
        DrawXAModHotkeyMessage(xaHotkeyOperationsStatus, xaHotkeyOperationsError);
        if (!string.Equals(service.StatusMessage.Original, xaHotkeyOperationsStatus.Original, StringComparison.Ordinal))
            DrawXAModHotkeyMessage(service.StatusMessage, service.StatusIsError);
        ImGui.SetNextItemWidth(-1f);
        UiNative.InputTextWithHint("##XAModHotkeySearch", UiLocale.Get("S6352BA5342074B06"), ref xaHotkeySearch, 256);

        var terms = xaHotkeySearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var targets = plugin.GetTitleBarFavXAModInfos()
            .Where(target => terms.All(term => target.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || target.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || target.ScopeLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
                || UiLocale.T(target.DisplayName).Contains(term, StringComparison.OrdinalIgnoreCase)
                || UiLocale.T(target.ScopeLabel).Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(target => UiLocale.T(target.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (targets.Length == 0)
            ImGui.TextDisabled(UiLocale.Get("SD6572BD2E77380CE"));
        else
        {
            using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, ScaledVector(6f, 5f));
            var shortcutWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.3f, Scale(105f), Scale(150f));
            var enabledWidth = Math.Max(ImGui.GetFrameHeight(), ImGui.CalcTextSize(UiLocale.Get("S92C1CDFDF4CB9CF6")).X);
            if (ImGui.BeginTable("XAModHotkeyTargets", 3, ImGuiTableFlags.SizingStretchProp
                | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
            {
                try
                {
                    UiNative.TableSetupColumn(UiLocale.Native("SDCD1D5223F73B3A9"), ImGuiTableColumnFlags.WidthStretch);
                    UiNative.TableSetupColumn(UiLocale.Native("S6A6485AF02CAB90B"), ImGuiTableColumnFlags.WidthFixed, shortcutWidth);
                    UiNative.TableSetupColumn(UiLocale.Native("S92C1CDFDF4CB9CF6"), ImGuiTableColumnFlags.WidthFixed, enabledWidth);
                    UiNative.TableHeadersRow();
                    foreach (var target in targets)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextWrapped(UiLocale.T(target.DisplayName));
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]))
                            ImGui.TextWrapped(UiLocale.T(target.ScopeLabel));
                        ImGui.TableNextColumn();
                        DrawXAModHotkeyShortcut(target.Key, target.DisplayName, showBinding: true);
                        ImGui.TableNextColumn();
                        using var targetId = ImRaii.PushId(target.Key);
                        var binding = service.GetBinding(target.Key);
                        var bindingEnabled = binding?.Enabled == true;
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight()) * 0.5f));
                        using (ImRaii.Disabled(binding == null))
                        {
                            if (UiNative.Checkbox(UiLocale.Native("S92C1CDFDF4CB9CF6"), ref bindingEnabled, display: string.Empty))
                                xaHotkeyOperationsError = !service.TrySetBindingEnabled(target.Key, bindingEnabled, out xaHotkeyOperationsStatus);
                        }
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip($"{UiLocale.T(target.DisplayName)}: {UiLocale.Get("S92C1CDFDF4CB9CF6")}");
                    }
                }
                finally { ImGui.EndTable(); }
            }
        }

        ImGui.Spacing();
    }

    private void DrawXAModHotkeyShortcut(string modKey, string label, bool showBinding = false)
    {
        using var targetId = ImRaii.PushId(modKey);
        var service = plugin.XAModHotkeys;
        var binding = service.GetBinding(modKey);
        var chord = binding == null ? UiLocale.Get("S13075C2336114CD6") : XAModHotkeyService.FormatChord(binding);
        var popupId = ImGui.GetID("XAModHotkeyEditor");
        var editClicked = showBinding
            ? UiNative.Button(UiLocale.Native("S6A6485AF02CAB90B"), new Vector2(-1f, 0f), display: chord)
            : UiNative.SmallButton(UiLocale.Native("S6A6485AF02CAB90B"));
        if (editClicked)
        {
            service.EndEditing();
            xaHotkeyEditorKey = modKey;
            xaHotkeyEditorPopupId = popupId;
            xaHotkeyEditorStatus = UiMessage.Raw(string.Empty);
            xaHotkeyEditorError = false;
            xaHotkeyCapturePending = false;
            ImGui.OpenPopup("XAModHotkeyEditor");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(chord);

        if (showBinding)
        {
            if (service.GetBindingIssue(modKey) is { } issue)
                DrawXAModHotkeyMessage(issue, true);
        }

        using var popup = ImRaii.Popup("XAModHotkeyEditor");
        if (!popup)
        {
            if (xaHotkeyEditorPopupId == popupId && string.Equals(xaHotkeyEditorKey, modKey, StringComparison.Ordinal))
            {
                service.EndEditing();
                xaHotkeyEditorPopupId = 0;
                xaHotkeyEditorKey = null;
                xaHotkeyCapturePending = false;
            }
            return;
        }

        service.RefreshEditorHeartbeat();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + Scale(420f));
        ImGui.TextUnformatted(UiLocale.T(label));
        ImGui.Separator();
        DrawXAModHotkeyEditor(modKey);
    }

    private void DrawXAModHotkeyEditor(string modKey)
    {
        var service = plugin.XAModHotkeys;
        // Capture belongs to the service. Consuming a draft only edits the binding; it never toggles a mod.
        if (service.TryTakeCapturedBinding(out var captured))
        {
            xaHotkeyCapturePending = false;
            if (string.Equals(captured.ModKey, modKey, StringComparison.OrdinalIgnoreCase))
                xaHotkeyEditorError = !service.TrySaveBinding(captured, out xaHotkeyEditorStatus);
            else
                service.CancelCapture();
        }

        if (xaHotkeyCapturePending && !service.IsCapturing)
        {
            xaHotkeyCapturePending = false;
            xaHotkeyEditorStatus = service.StatusMessage;
            xaHotkeyEditorError = service.StatusIsError;
        }

        var binding = service.GetBinding(modKey);
        ImGui.TextUnformatted(binding == null ? UiLocale.Get("S13075C2336114CD6") : XAModHotkeyService.FormatChord(binding));
        var enabled = binding?.Enabled == true;
        using (ImRaii.Disabled(binding == null))
        {
            if (UiNative.Checkbox(UiLocale.Native("S92C1CDFDF4CB9CF6"), ref enabled))
                xaHotkeyEditorError = !service.TrySetBindingEnabled(modKey, enabled, out xaHotkeyEditorStatus);
        }

        ImGui.TextWrapped(UiLocale.Get("S22CA9D816ED9D7D3"));

        if (XAModHotkeyService.RequiresCtrlShift(modKey))
            ImGui.TextWrapped(UiLocale.Get("S6EF93CA7306690C1"));

        var capturing = service.IsCapturing && string.Equals(service.CaptureModKey, modKey, StringComparison.OrdinalIgnoreCase);
        if (capturing)
        {
            ImGui.TextWrapped(UiLocale.Get("S0A7466B20A742807"));
            ImGui.TextWrapped(UiLocale.Get("S5ED6F609248A45FB"));
            DrawXAModHotkeyCaptureInput();
            if (UiNative.Button(UiLocale.Native("S19766ED6CCB2F4A3")))
            {
                service.CancelCapture();
                xaHotkeyCapturePending = false;
                xaHotkeyEditorStatus = service.StatusMessage;
                xaHotkeyEditorError = service.StatusIsError;
            }
        }
        else
        {
            if (UiNative.Button(UiLocale.Native("S355ABB3D587E5D5D")))
            {
                xaHotkeyCapturePending = service.BeginCapture(modKey, out xaHotkeyEditorStatus);
                xaHotkeyCaptureNeedsFocus = xaHotkeyCapturePending;
                xaHotkeyEditorError = !xaHotkeyCapturePending;
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(binding == null))
            {
                if (UiNative.Button(UiLocale.Native("S83B12C2216EFB4FD")))
                    xaHotkeyEditorError = !service.TryRemoveBinding(modKey, out xaHotkeyEditorStatus);
            }
            // BeginCapture must have an owner before the after-window Draw observer runs.
            if (xaHotkeyCapturePending)
                DrawXAModHotkeyCaptureInput();
        }

        if (service.GetBindingIssue(modKey) is { } issue)
            DrawXAModHotkeyMessage(issue, true);
        DrawXAModHotkeyMessage(xaHotkeyCapturePending ? service.StatusMessage : xaHotkeyEditorStatus,
            xaHotkeyCapturePending ? service.StatusIsError : xaHotkeyEditorError);
    }

    private void DrawXAModHotkeyCaptureInput()
    {
        if (xaHotkeyCaptureNeedsFocus)
        {
            ImGui.SetKeyboardFocusHere();
            xaHotkeyCaptureNeedsFocus = false;
        }
        ImGui.SetNextItemWidth(Scale(300f));
        var captureText = string.Empty;
        // A writable field requests WantTextInput, which Dalamud uses to intercept game keys.
        // Reject characters so assignment cannot insert text, including clipboard contents.
        ImGui.InputTextWithHint("##XAModHotkeyCapture", UiLocale.Get("S0A7466B20A742807"), ref captureText, 64,
            ImGuiInputTextFlags.AllowTabInput | ImGuiInputTextFlags.CallbackCharFilter | ImGuiInputTextFlags.NoUndoRedo,
            static (scoped ref ImGuiInputTextCallbackData data) => 1);
        plugin.XAModHotkeys.ObserveCaptureInput(ImGui.IsItemActive());
    }

    private static void DrawXAModHotkeyMessage(UiMessage message, bool error)
    {
        if (message.Original.Length == 0)
            return;
        using var color = ImRaii.PushColor(ImGuiCol.Text, error ? new Vector4(1f, 0.45f, 0.4f, 1f) : new Vector4(0.7f, 0.85f, 0.7f, 1f));
        ImGui.TextWrapped(message.Display);
    }
}
