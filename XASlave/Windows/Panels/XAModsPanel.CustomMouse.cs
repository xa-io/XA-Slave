using System;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private static readonly string[] CustomMouseRoleLabels = [global::XASlave.Localization.UiLocale.Native("SA7248EEB45EB1CE2"), global::XASlave.Localization.UiLocale.Native("S758F487FE8E9FCC5"), global::XASlave.Localization.UiLocale.Native("SC1F079C379776C39"), global::XASlave.Localization.UiLocale.Native("SB7D2863247E0230F"), global::XASlave.Localization.UiLocale.Native("S22553D44C9DF8C9B"), global::XASlave.Localization.UiLocale.Native("S2ED84B34C15DCC02")];
    private readonly FileDialogManager customMouseFileDialog = new();
    private string customMouseFavoriteDraft = string.Empty;
    private string customMouseFavoriteSelection = string.Empty;
    private string customMouseFavoriteMessage = string.Empty;
    private global::XASlave.Localization.UiMessage? customMouseFavoriteMessageUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);

    private void DrawCustomMouseOptions()
    {
        var value = (plugin.Configuration.CustomMouse ?? new()).Normalized();
        var favorites = plugin.Configuration.CustomMouseFavorites ??= new() { ExistingAssignmentsImported = true };
        favorites.Paths ??= [];
        var scale = value.Scale;
        var baseColor = value.BaseColor;
        var outlineColor = value.OutlineColor;
        var useArtwork = value.UseCustomArtwork;
        var animateArtwork = value.AnimateArtwork;
        var cursorTrails = value.CursorTrails;
        var keepActiveOnClick = value.KeepActiveOnClick;
        var allowWindowsMouse = value.AllowWindowsMouse;
        var reloadArtwork = false;
        CustomMouseArtworkRole? reloadRole = null;
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SAD763FBA35DA3235"));
        var changed = global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SD0E16D81542F93DA") + "##CustomMouse"), ref keepActiveOnClick);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S8F7EF2872F54C697"));
        changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF9663D6C8098968F") + "##CustomMouse"), ref allowWindowsMouse);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SA81FADE0F378E8CE"));
        using (ImRaii.Disabled(allowWindowsMouse))
        {
            ImGui.TextWrapped(useArtwork ? global::XASlave.Localization.UiLocale.Get("S1111241F38259CD3") : global::XASlave.Localization.UiLocale.Get("S1A1F27497F9F5D17"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S60B67F21D699704F"));
            changed |= global::XASlave.Localization.UiNative.SliderFloat((global::XASlave.Localization.UiLocale.Native("SA7E2C23E3C4D8FF9") + "##CustomMouse"), ref scale, 0.1f, 5f, "%.2fx");
            changed |= global::XASlave.Localization.UiNative.ColorEdit4((global::XASlave.Localization.UiLocale.Native("S4144C7E4F13937A8") + "##CustomMouse"), ref baseColor);
            changed |= global::XASlave.Localization.UiNative.ColorEdit4((global::XASlave.Localization.UiLocale.Native("S375B9BF724FC2783") + "##CustomMouse"), ref outlineColor);
            changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S8294BD8E89BBB33B") + "##CustomMouse"), ref cursorTrails);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S229E6A768A697EC0"));
            changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S5477D7869DBB1490") + "##CustomMouse"), ref useArtwork);
            if (useArtwork)
            {
                for (var i = 0; i < CustomMouseRoleLabels.Length; i++)
                {
                    var role = (CustomMouseArtworkRole)i;
                    using var roleId = ImRaii.PushId(i);
                    var path = value.GetArtworkPath(role);
                    var emptyLabel = role == CustomMouseArtworkRole.Normal ? global::XASlave.Localization.UiLocale.Native("S7A60713898D5EDDD") : global::XASlave.Localization.UiLocale.Native("SAFD5FF8E90DE9F65");
                    var preview = string.IsNullOrEmpty(path) ? global::XASlave.Localization.UiLocale.T(emptyLabel) : CustomMouseFavoriteLabel(path);
                    var reloadWidth = ImGui.CalcTextSize(global::XASlave.Localization.UiLocale.Get("SBDC090EC61E3FCFC")).X + ImGui.GetStyle().FramePadding.X * 2;
                    var labelWidth = ImGui.CalcTextSize(global::XASlave.Localization.UiLocale.T(CustomMouseRoleLabels[i])).X + ImGui.GetStyle().ItemInnerSpacing.X;
                    ImGui.SetNextItemWidth(Math.Max(Scale(80f), ImGui.GetContentRegionAvail().X - reloadWidth - labelWidth - ImGui.GetStyle().ItemSpacing.X));
                    using (var combo = global::XASlave.Localization.UiNative.ComboScope(CustomMouseRoleLabels[i] + "##CustomMouseAssignment", preview))
                    {
                        if (combo)
                        {
                            if (global::XASlave.Localization.UiNative.Selectable(emptyLabel + "##Default", string.IsNullOrEmpty(path)))
                            {
                                value.SetArtworkPath(role, string.Empty);
                                changed = true;
                            }
                            for (var favoriteIndex = 0; favoriteIndex < favorites.Paths.Count; favoriteIndex++)
                            {
                                var favorite = favorites.Paths[favoriteIndex];
                                using var favoriteId = ImRaii.PushId(favoriteIndex);
                                var selected = string.Equals(path, favorite, StringComparison.OrdinalIgnoreCase);
                                if (ImGui.Selectable(CustomMouseFavoriteLabel(favorite) + "##Favorite", selected))
                                {
                                    value.SetArtworkPath(role, favorite);
                                    changed = true;
                                }
                                if (ImGui.IsItemHovered()) ImGui.SetTooltip(favorite);
                                if (selected) ImGui.SetItemDefaultFocus();
                            }
                        }
                    }
                    if (ImGui.IsItemHovered())
                    {
                        var resolved = value.ResolveArtworkPath(role);
                        ImGui.SetTooltip((string.IsNullOrEmpty(resolved) ? global::XASlave.Localization.UiLocale.T(emptyLabel) : resolved) + "\n" + plugin.CustomMouse.GetLocalizedArtworkStatus(role));
                    }
                    ImGui.SameLine();
                    if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SBDC090EC61E3FCFC") + "##CustomMouseRole")))
                    {
                        reloadArtwork = true;
                        reloadRole = role;
                        changed = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S91E1A87058F4895D"));
                }
                changed |= global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S529A70E7F2F82AF6") + "##CustomMouse"), ref animateArtwork);
                ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S709507E34967ADBA"));
            }
        }
        ImGui.Separator();
        DrawCustomMouseFavorites(favorites);
        var resetAppearanceModifierHeld = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!resetAppearanceModifierHeld))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S54BA48B060D6C8ED") + "##CustomMouse")))
            {
                value = new();
                scale = value.Scale;
                baseColor = value.BaseColor;
                outlineColor = value.OutlineColor;
                useArtwork = false;
                animateArtwork = true;
                cursorTrails = false;
                keepActiveOnClick = false;
                allowWindowsMouse = false;
                changed = true;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S34C05ACEFC04241A"));
        if (changed)
        {
            value.Scale = scale;
            value.BaseColor = baseColor;
            value.OutlineColor = outlineColor;
            value.UseCustomArtwork = useArtwork;
            value.AnimateArtwork = animateArtwork;
            value.CursorTrails = cursorTrails;
            value.KeepActiveOnClick = keepActiveOnClick;
            value.AllowWindowsMouse = allowWindowsMouse;
            SaveCustomMouseAppearance(value, reloadArtwork, reloadRole);
        }
        // A late file-dialog result changes only the persistent library, never a stale appearance snapshot.
        customMouseFileDialog.Draw();
    }

    private void DrawCustomMouseFavorites(CustomMouseFavoriteLibrary favorites)
    {
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.Get("S8DEB58B8A7AE9E85"));
        global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("SBCE68B9A619A6D95") + "##CustomMouse"), ref customMouseFavoriteDraft, 1024);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SC58A6BD402EFB06C") + "##CustomMouseFavorite")))
        {
            customMouseFileDialog.OpenFileDialog(global::XASlave.Localization.UiLocale.Get("S87DFC54F47A9D555"), ".ani,.cur,.png", (accepted, path) =>
            {
                if (!accepted || isDisposed) return;
                customMouseFavoriteDraft = path;
                AddCustomMouseFavorite(path);
            });
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S65B52E8C6521A21D") + "##CustomMouse"))) AddCustomMouseFavorite(customMouseFavoriteDraft);
        if (favorites.Paths.Count > 0)
        {
            if (!favorites.Paths.Contains(customMouseFavoriteSelection)) customMouseFavoriteSelection = favorites.Paths[0];
            var removeWidth = ImGui.CalcTextSize(global::XASlave.Localization.UiLocale.Get("S5BC0AA08D678C50E")).X + ImGui.GetStyle().FramePadding.X * 2;
            var labelWidth = ImGui.CalcTextSize(global::XASlave.Localization.UiLocale.Get("S7A1F2A83ACA9A081")).X + ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(Math.Max(Scale(80f), ImGui.GetContentRegionAvail().X - removeWidth - labelWidth - ImGui.GetStyle().ItemSpacing.X));
            using (var combo = global::XASlave.Localization.UiNative.ComboScope((global::XASlave.Localization.UiLocale.Native("S7A1F2A83ACA9A081") + "##CustomMouseLibrary"), CustomMouseFavoriteLabel(customMouseFavoriteSelection)))
            {
                if (combo)
                {
                    for (var i = 0; i < favorites.Paths.Count; i++)
                    {
                        var path = favorites.Paths[i];
                        using var favoriteId = ImRaii.PushId(i);
                        if (ImGui.Selectable(CustomMouseFavoriteLabel(path) + "##LibraryFavorite", path == customMouseFavoriteSelection))
                            customMouseFavoriteSelection = path;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
                    }
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(customMouseFavoriteSelection);
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S5BC0AA08D678C50E") + "##CustomMouse")))
            {
                if (favorites.Remove(customMouseFavoriteSelection)) plugin.Configuration.Save();
                customMouseFavoriteSelection = string.Empty;
                customMouseFavoriteMessage = global::XASlave.Localization.UiMessage.Store(ref customMouseFavoriteMessageUiMessage, global::XASlave.Localization.UiMessage.Key("SC625A7DE1EE0150B"));
            }
        }
        else ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SFF09D20E866D8BD6"));
        if (!string.IsNullOrEmpty(customMouseFavoriteMessage)) ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref customMouseFavoriteMessageUiMessage, customMouseFavoriteMessage));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SB994EFFEEF5A5F08"));
    }

    private void AddCustomMouseFavorite(string path)
    {
        var normalized = CustomMouseFavoriteLibrary.NormalizePath(path);
        if (string.IsNullOrEmpty(normalized))
        {
            customMouseFavoriteMessage = global::XASlave.Localization.UiMessage.Store(ref customMouseFavoriteMessageUiMessage, global::XASlave.Localization.UiMessage.Key("S6BD31466FEB90B3A"));
            return;
        }
        var favorites = plugin.Configuration.CustomMouseFavorites ??= new() { ExistingAssignmentsImported = true };
        if (favorites.Add(normalized))
        {
            plugin.Configuration.Save();
            customMouseFavoriteMessage = global::XASlave.Localization.UiMessage.Store(ref customMouseFavoriteMessageUiMessage, global::XASlave.Localization.UiMessage.Key("SDC34AB1E4347596D"));
        }
        else customMouseFavoriteMessage = global::XASlave.Localization.UiMessage.Store(ref customMouseFavoriteMessageUiMessage, global::XASlave.Localization.UiMessage.Key("S398F1B65949D4F0D"));
        customMouseFavoriteSelection = normalized;
    }

    private static string CustomMouseFavoriteLabel(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException) { return path; }
    }

    private void SaveCustomMouseAppearance(CustomMouseSettings value, bool reloadArtwork = false, CustomMouseArtworkRole? reloadRole = null)
    {
        var next = value.Normalized();
        plugin.Configuration.CustomMouse = next;
        plugin.Configuration.Save();
        _ = Plugin.RunOnGameThread(() => plugin.CustomMouse.ApplyConfiguration(next, reloadArtwork, reloadRole), "Custom Mouse appearance");
    }
}
