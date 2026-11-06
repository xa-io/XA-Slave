using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Data;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string autoFoodSearch = string.Empty;
    private UiMessage autoFoodSettingsMessage = UiMessage.Raw(string.Empty);

    private void SaveAutoFoodConsumeSettings(AutoFoodConsumeSettings candidate)
    {
        var cfg = plugin.Configuration;
        var previous = cfg.AutoFoodConsumeSettings;
        var normalized = candidate.Normalized();
        if (previous != null && previous.ItemId == normalized.ItemId && previous.HighQuality == normalized.HighQuality
            && previous.OnlyInSanctuary == normalized.OnlyInSanctuary && previous.OnlyInDuty == normalized.OnlyInDuty
            && previous.RefreshThresholdSeconds == normalized.RefreshThresholdSeconds)
            return;
        try
        {
            // The service keeps its prior detached settings until persistence succeeds.
            cfg.AutoFoodConsumeSettings = normalized;
            cfg.Save();
        }
        catch (Exception ex)
        {
            cfg.AutoFoodConsumeSettings = previous ?? new();
            autoFoodSettingsMessage = UiMessage.Format(UiLocale.Key("Could not save food settings: {0}"), UiMessage.FromException(ex));
            return;
        }
        plugin.AutoFoodConsume.ApplyConfiguration(normalized);
        autoFoodSettingsMessage = UiMessage.Raw(string.Empty);
    }

    private void DrawAutoFoodConsumeOptions()
    {
        var draft = (plugin.Configuration.AutoFoodConsumeSettings ?? new()).Normalized();
        if (!plugin.Configuration.AutoFoodConsumeEnabled)
            ImGui.TextWrapped(plugin.AutoFoodConsume.StatusTextMessage.Display);
        var changed = false;
        AutoFoodChoice? selected = null;
        try
        {
            if (plugin.AutoFoodConsume.TryGetFood(draft.ItemId, out var resolved))
                selected = resolved;
            var preview = selected?.Name ?? (draft.ItemId == 0
                ? UiMessage.Authored("No food selected.").Display
                : UiMessage.Format(UiLocale.Key("Selected food is unavailable (item {0})."), draft.ItemId).Display);
            ImGui.SetNextItemWidth(Scale(340f));
            using (var combo = UiNative.ComboScope(UiLocale.Native(UiLocale.Key("Food")) + "##AutoFoodItem", preview))
            {
                if (combo)
                {
                    ImGui.SetNextItemWidth(Scale(300f));
                    ImGui.InputTextWithHint("##AutoFoodSearch", UiLocale.Get("SA0241849A87B9A67"), ref autoFoodSearch, 128);
                    var query = autoFoodSearch.Trim();
                    using var child = ImRaii.Child("##AutoFoodChoices", new Vector2(Scale(340f), Scale(240f)), false);
                    if (child)
                    {
                        var count = 0;
                        foreach (var food in plugin.AutoFoodConsume.GetFoods())
                        {
                            if (query.Length != 0 && !food.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                && !food.ItemId.ToString().Contains(query, StringComparison.Ordinal))
                                continue;
                            count++;
                            if (ImGui.Selectable($"{food.Name} ({food.ItemId})##AutoFood{food.ItemId}", draft.ItemId == food.ItemId))
                            {
                                draft.ItemId = food.ItemId;
                                if (!food.CanBeHq) draft.HighQuality = false;
                                selected = food;
                                changed = true;
                            }
                        }
                        if (count == 0)
                            ImGui.TextDisabled(UiMessage.Authored("No matching meals.").Display);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            autoFoodSettingsMessage = UiMessage.Format(UiLocale.Key("Food data could not be loaded: {0}"), UiMessage.FromException(ex));
        }

        if (draft.ItemId != 0)
        {
            ImGui.SameLine();
            if (UiNative.SmallButton(UiLocale.Native("S83B12C2216EFB4FD") + "##AutoFoodClear"))
            {
                draft.ItemId = 0;
                draft.HighQuality = false;
                selected = null;
                changed = true;
            }
        }
        var hq = draft.HighQuality;
        using (ImRaii.Disabled(selected == null || !selected.CanBeHq))
        {
            if (UiNative.Checkbox(UiLocale.Native(UiLocale.Key("Use HQ food")) + "##AutoFoodQuality", ref hq))
            {
                draft.HighQuality = hq;
                changed = true;
            }
        }
        if (selected != null)
            ImGui.TextUnformatted(UiMessage.Format(UiLocale.Key("Selected food: {0} ({1})."), selected.Name,
                UiMessage.Key(draft.HighQuality ? "SA688D83AE8586526" : "SACF8F7780683D2D4")).Display);
        ImGui.TextWrapped(UiMessage.Authored("Only the selected quality is used; no NQ/HQ substitution.").Display);

        var sanctuary = draft.OnlyInSanctuary;
        if (UiNative.Checkbox(UiLocale.Native(UiLocale.Key("Only in sanctuaries")) + "##AutoFoodSanctuary", ref sanctuary))
        {
            draft.OnlyInSanctuary = sanctuary;
            changed = true;
        }
        var duty = draft.OnlyInDuty;
        if (UiNative.Checkbox(UiLocale.Native(UiLocale.Key("Only in duties")) + "##AutoFoodDuty", ref duty))
        {
            draft.OnlyInDuty = duty;
            changed = true;
        }
        ImGui.TextWrapped(UiMessage.Authored("Neither option: any eligible location. Both options: a sanctuary or a duty.").Display);

        var threshold = draft.RefreshThresholdSeconds;
        ImGui.SetNextItemWidth(Scale(240f));
        if (UiNative.SliderInt(UiLocale.Native(UiLocale.Key("Refresh below (seconds)")) + "##AutoFoodThreshold",
                ref threshold, 0, 300, "%d", ImGuiSliderFlags.AlwaysClamp))
        {
            draft.RefreshThresholdSeconds = threshold;
            changed = true;
        }
        ImGui.TextWrapped(UiMessage.Authored("0 refreshes only after Well Fed expires. A different active food is never replaced.").Display);
        if (changed)
            SaveAutoFoodConsumeSettings(draft);
        if (autoFoodSettingsMessage.Original.Length != 0)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), autoFoodSettingsMessage.Display);
        }
    }
}
