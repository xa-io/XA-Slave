using System;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private static readonly string[] CustomMouseRoleLabels = ["Normal", "Inventory", "Window mover", "Interaction", "Chat interaction", "Attack targeting"];
    private readonly FileDialogManager customMouseFileDialog = new();
    private string customMouseFavoriteDraft = string.Empty;
    private string customMouseFavoriteSelection = string.Empty;
    private string customMouseFavoriteMessage = string.Empty;

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
        ImGui.TextWrapped("Custom Mouse restores your previous game cursor settings when disabled. Move onto the game scene to preview; plugin windows keep their own cursor.");
        var changed = ImGui.Checkbox("Keep Active On Click##CustomMouse", ref keepActiveOnClick);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep the cursor visible while left- or right-dragging the camera. Camera controls remain unchanged.");
        changed |= ImGui.Checkbox("Allow Windows Mouse##CustomMouse", ref allowWindowsMouse);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Use your Windows pointer in the game. Game cursor artwork, size, colors and trails are bypassed.");
        using (ImRaii.Disabled(allowWindowsMouse))
        {
            ImGui.TextWrapped(useArtwork ? "Choose a favorite for each cursor role. Size is relative to each file's pixels." : "Size is relative to the game's Normal/Large cursor preset. Move the pointer onto the game scene to preview.");
            ImGui.TextWrapped("Cursor movement is independent of game FPS. Animation follows game updates; optional trails use software rendering.");
            changed |= ImGui.SliderFloat("Size ratio##CustomMouse", ref scale, 0.1f, 5f, "%.2fx");
            changed |= ImGui.ColorEdit4("Base color##CustomMouse", ref baseColor);
            changed |= ImGui.ColorEdit4("Outline color##CustomMouse", ref outlineColor);
            changed |= ImGui.Checkbox("Cursor trails##CustomMouse", ref cursorTrails);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Trails use software rendering and follow game FPS. Turn trails off for independent cursor movement.");
            changed |= ImGui.Checkbox("Use custom artwork##CustomMouse", ref useArtwork);
            if (useArtwork)
            {
                for (var i = 0; i < CustomMouseRoleLabels.Length; i++)
                {
                    var role = (CustomMouseArtworkRole)i;
                    using var roleId = ImRaii.PushId(i);
                    var path = value.GetArtworkPath(role);
                    var emptyLabel = role == CustomMouseArtworkRole.Normal ? "Game cursor" : "Use Normal";
                    var preview = string.IsNullOrEmpty(path) ? emptyLabel : CustomMouseFavoriteLabel(path);
                    var reloadWidth = ImGui.CalcTextSize("Reload").X + ImGui.GetStyle().FramePadding.X * 2;
                    var labelWidth = ImGui.CalcTextSize(CustomMouseRoleLabels[i]).X + ImGui.GetStyle().ItemInnerSpacing.X;
                    ImGui.SetNextItemWidth(Math.Max(Scale(80f), ImGui.GetContentRegionAvail().X - reloadWidth - labelWidth - ImGui.GetStyle().ItemSpacing.X));
                    using (var combo = ImRaii.Combo(CustomMouseRoleLabels[i] + "##CustomMouseAssignment", preview))
                    {
                        if (combo)
                        {
                            if (ImGui.Selectable(emptyLabel + "##Default", string.IsNullOrEmpty(path)))
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
                        ImGui.SetTooltip((string.IsNullOrEmpty(resolved) ? emptyLabel : resolved) + "\n" + plugin.CustomMouse.GetArtworkStatus(role));
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Reload##CustomMouseRole"))
                    {
                        reloadArtwork = true;
                        reloadRole = role;
                        changed = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reload this role's file after changing its contents or restoring a missing file.");
                }
                changed |= ImGui.Checkbox("Animate cursor##CustomMouse", ref animateArtwork);
                ImGui.TextWrapped("Other roles use Normal until you choose a different favorite. Missing files keep the game cursor for that role. PNG files use a top-left click point; animation off uses the first ANI frame.");
            }
        }
        ImGui.Separator();
        DrawCustomMouseFavorites(favorites);
        var resetAppearanceModifierHeld = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!resetAppearanceModifierHeld))
        {
            if (ImGui.Button("Reset appearance##CustomMouse"))
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
            ImGui.SetTooltip("Hold CTRL and click to reset appearance and clear cursor assignments. Your favorites are kept.");
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
        ImGui.TextUnformatted("Cursor favorites");
        ImGui.InputText("Favorite file##CustomMouse", ref customMouseFavoriteDraft, 1024);
        if (ImGui.Button("Browse...##CustomMouseFavorite"))
        {
            customMouseFileDialog.OpenFileDialog("Add a cursor favorite", ".ani,.cur,.png", (accepted, path) =>
            {
                if (!accepted || isDisposed) return;
                customMouseFavoriteDraft = path;
                AddCustomMouseFavorite(path);
            });
        }
        ImGui.SameLine();
        if (ImGui.Button("Add favorite##CustomMouse")) AddCustomMouseFavorite(customMouseFavoriteDraft);
        if (favorites.Paths.Count > 0)
        {
            if (!favorites.Paths.Contains(customMouseFavoriteSelection)) customMouseFavoriteSelection = favorites.Paths[0];
            var removeWidth = ImGui.CalcTextSize("Remove favorite").X + ImGui.GetStyle().FramePadding.X * 2;
            var labelWidth = ImGui.CalcTextSize("Favorites").X + ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(Math.Max(Scale(80f), ImGui.GetContentRegionAvail().X - removeWidth - labelWidth - ImGui.GetStyle().ItemSpacing.X));
            using (var combo = ImRaii.Combo("Favorites##CustomMouseLibrary", CustomMouseFavoriteLabel(customMouseFavoriteSelection)))
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
            if (ImGui.Button("Remove favorite##CustomMouse"))
            {
                if (favorites.Remove(customMouseFavoriteSelection)) plugin.Configuration.Save();
                customMouseFavoriteSelection = string.Empty;
                customMouseFavoriteMessage = "Favorite removed. Current cursor assignments are unchanged.";
            }
        }
        else ImGui.TextDisabled("Add a cursor file, then select it for any role above.");
        if (!string.IsNullOrEmpty(customMouseFavoriteMessage)) ImGui.TextWrapped(customMouseFavoriteMessage);
        ImGui.TextWrapped("Favorites stay saved until you remove them, even if files move or go missing. Resetting appearance or applying a mod preset keeps this library. Removing a favorite keeps any current assignments.");
    }

    private void AddCustomMouseFavorite(string path)
    {
        var normalized = CustomMouseFavoriteLibrary.NormalizePath(path);
        if (string.IsNullOrEmpty(normalized))
        {
            customMouseFavoriteMessage = "Choose a fully qualified local-drive .ani, .cur or .png path of at most 1024 characters.";
            return;
        }
        var favorites = plugin.Configuration.CustomMouseFavorites ??= new() { ExistingAssignmentsImported = true };
        if (favorites.Add(normalized))
        {
            plugin.Configuration.Save();
            customMouseFavoriteMessage = "Favorite added. Select it in a cursor role to use it.";
        }
        else customMouseFavoriteMessage = "That file is already in your favorites.";
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
