using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// Window Renamer panel - partial class split from SlaveWindow.
/// Renames the FFXIV game window title with enable/disable toggle,
/// custom title text box, and optional process ID prefix.
/// </summary>
public partial class SlaveWindow
{
    private string windowRenamerTitleInput = string.Empty;
    private bool windowRenamerInitialized;

    // -----------------------------------------------
    //  Task: Window Renamer
    // -----------------------------------------------
    private void DrawWindowRenamerTask()
    {
        // One-time init: sync local input buffer with persisted config
        if (!windowRenamerInitialized)
        {
            windowRenamerTitleInput = plugin.Configuration.WindowRenamerTitle;
            windowRenamerInitialized = true;
        }

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Window Renamer");
        ImGui.TextDisabled("Renames the FFXIV game window title. Takes effect on plugin load when enabled.");
        if (plugin.WindowRenamer.HasXIVWindowResizerCompatibilityRefreshError)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1.0f, 0.35f, 0.25f, 1.0f),
                "XA could not finish an XIVWindowResizer plugin-state refresh.");
            ImGui.TextDisabled("XA attempted the native title for safety. Use Apply Now to recheck the loaded-plugin state.");
        }
        else if (plugin.WindowRenamer.IsXIVWindowResizerCompatibilityActive)
        {
            ImGui.Spacing();
            if (plugin.WindowRenamer.IsXIVWindowResizerNativeTitleConfirmed)
            {
                ImGui.TextColored(
                    new Vector4(1.0f, 0.75f, 0.2f, 1.0f),
                    $"Paused for XIVWindowResizer compatibility. The live title is confirmed as \"{XASlave.Services.WindowRenamerService.NativeGameWindowTitle}\".");
                ImGui.TextDisabled("Your saved Window Renamer settings will reapply automatically when XIVWindowResizer unloads.");
            }
            else
            {
                ImGui.TextColored(
                    new Vector4(1.0f, 0.35f, 0.25f, 1.0f),
                    "XIVWindowResizer compatibility needs attention: XA could not confirm the native window title.");
                ImGui.TextDisabled("The custom title remains paused. Use Apply Now to retry after the game window is available.");
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Enable / Disable --
        var enabled = plugin.Configuration.WindowRenamerEnabled;
        if (ImGui.Checkbox("Enable Window Renamer", ref enabled))
        {
            plugin.Configuration.WindowRenamerEnabled = enabled;
            plugin.Configuration.Save();
            plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Settings (always visible so user can configure before enabling) --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Settings");
        ImGui.Spacing();

        // Use Process ID checkbox
        var usePid = plugin.Configuration.WindowRenamerUseProcessId;
        if (ImGui.Checkbox("Use Process ID prefix", ref usePid))
        {
            plugin.Configuration.WindowRenamerUseProcessId = usePid;
            plugin.Configuration.Save();
            if (enabled)
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled($"Prepends \"{Environment.ProcessId} - \" to the window title.");

        ImGui.Spacing();

        var showCurrentCharacter = plugin.Configuration.WindowRenamerShowCurrentCharacter;
        if (ImGui.Checkbox("Show Current Character", ref showCurrentCharacter))
        {
            plugin.Configuration.WindowRenamerShowCurrentCharacter = showCurrentCharacter;
            plugin.Configuration.Save();
            if (enabled)
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled("Appends the currently logged-in character name after the title and refreshes on login/logout.");

        ImGui.Spacing();

        // Custom title text box
        ImGui.Text("Window Title:");
        ImGui.SetNextItemWidth(Scale(300f));
        if (ImGui.InputText("##WindowTitle", ref windowRenamerTitleInput, 256))
        {
            plugin.Configuration.WindowRenamerTitle = windowRenamerTitleInput;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemDeactivatedAfterEdit() && enabled)
        {
            plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled("Leave blank to use the default \"FINAL FANTASY XIV\".");

        ImGui.Spacing();

        // Apply button (manual re-apply)
        if (enabled)
        {
            var controlsCompatibilityActive = plugin.WindowRenamer.IsXIVWindowResizerCompatibilityActive;
            var controlsCompatibilityRefreshError = plugin.WindowRenamer.HasXIVWindowResizerCompatibilityRefreshError;
            if (ImGui.Button("Apply Now"))
            {
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
            }
            ImGui.SameLine();
            if (ImGui.Button("Restore Default"))
            {
                if (controlsCompatibilityActive || controlsCompatibilityRefreshError)
                    plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
                else
                    plugin.WindowRenamer.Restore();
            }
            if (ImGui.IsItemHovered())
            {
                var tooltip = controlsCompatibilityRefreshError
                    ? "Retries the loaded-plugin check and native-title compatibility state."
                    : controlsCompatibilityActive
                        ? plugin.WindowRenamer.IsXIVWindowResizerNativeTitleConfirmed
                            ? "XIVWindowResizer compatibility already has the exact native title confirmed."
                            : "Retries the native-title restore and compatibility confirmation."
                        : "Temporarily restores \"FINAL FANTASY XIV\". Will re-apply on next plugin load if enabled.";
                ImGui.SetTooltip(tooltip);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Preview --
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Preview");
        ImGui.Spacing();

        var previewTitle = string.IsNullOrWhiteSpace(windowRenamerTitleInput)
            ? "FINAL FANTASY XIV"
            : windowRenamerTitleInput;
        previewTitle = plugin.WindowRenamer.BuildPreviewTitle(previewTitle, usePid, showCurrentCharacter);

        var compatibilityActive = plugin.WindowRenamer.IsXIVWindowResizerCompatibilityActive;
        var compatibilityRefreshError = plugin.WindowRenamer.HasXIVWindowResizerCompatibilityRefreshError;
        var previewColor = compatibilityRefreshError
            ? new Vector4(1.0f, 0.35f, 0.25f, 1.0f)
            : compatibilityActive
            ? plugin.WindowRenamer.IsXIVWindowResizerNativeTitleConfirmed
                ? new Vector4(1.0f, 0.75f, 0.2f, 1.0f)
                : new Vector4(1.0f, 0.35f, 0.25f, 1.0f)
            : enabled
                ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f)
                : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
        ImGui.TextColored(previewColor, $"\"{previewTitle}\"");

        if (!enabled)
            ImGui.TextDisabled("(disabled - enable to apply)");
        else if (compatibilityRefreshError)
            ImGui.TextDisabled("(preview only - plugin-state refresh failed; use Apply Now)");
        else if (compatibilityActive)
            ImGui.TextDisabled("(preview only - paused until XIVWindowResizer unloads)");
    }
}
