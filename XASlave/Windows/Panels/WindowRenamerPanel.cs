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
        else if (!ImGui.IsAnyItemActive()
                 && !windowRenamerTitleInput.Equals(plugin.Configuration.WindowRenamerTitle, StringComparison.Ordinal))
        {
            windowRenamerTitleInput = plugin.Configuration.WindowRenamerTitle;
        }

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Window Renamer");
        ImGui.TextDisabled("Renames the FFXIV game window title. Takes effect on plugin load when enabled.");
        if (plugin.WindowRenamer.IsXIVWindowResizerCompatibilityActive)
        {
            ImGui.Spacing();
            if (plugin.WindowRenamer.IsXIVWindowResizerHandleReady)
            {
                ImGui.TextColored(
                    new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                    "XIVWindowResizer compatibility bridge is active; the custom title remains live.");
                ImGui.TextDisabled(plugin.WindowRenamer.XIVWindowResizerCompatibilityStatusText);
            }
            else
            {
                ImGui.TextColored(
                    new Vector4(1.0f, 0.35f, 0.25f, 1.0f),
                    "XIVWindowResizer compatibility could not be confirmed; the custom title remains live.");
                ImGui.TextDisabled(plugin.WindowRenamer.XIVWindowResizerCompatibilityStatusText);
                ImGui.TextDisabled("Use Apply Now to retry. XIVWindowResizer's first resize may fail until its handle is ready.");
            }
        }
        else if (plugin.WindowRenamer.HasXIVWindowResizerCompatibilityError)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1.0f, 0.35f, 0.25f, 1.0f),
                "XA could not confirm the loaded-plugin state; the custom title remains live.");
            ImGui.TextDisabled(plugin.WindowRenamer.XIVWindowResizerCompatibilityStatusText);
            ImGui.TextDisabled("Use Apply Now to retry the compatibility check.");
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
            plugin.Configuration.WindowRenamerTitle = windowRenamerTitleInput;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            plugin.Configuration.Save();
            if (enabled)
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled("Leave blank to use the default \"FINAL FANTASY XIV\".");

        ImGui.Spacing();

        // Apply button (manual re-apply)
        if (enabled)
        {
            if (ImGui.Button("Apply Now"))
            {
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
            }
            ImGui.SameLine();
            if (ImGui.Button("Restore Default"))
            {
                plugin.WindowRenamer.Restore();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Temporarily restores \"FINAL FANTASY XIV\". Use Apply Now or reload XA to reapply the saved title.");
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
        var compatibilityError = plugin.WindowRenamer.HasXIVWindowResizerCompatibilityError;
        var previewColor = compatibilityError
            ? new Vector4(1.0f, 0.35f, 0.25f, 1.0f)
            : compatibilityActive
            ? plugin.WindowRenamer.IsXIVWindowResizerHandleReady
                ? new Vector4(1.0f, 0.75f, 0.2f, 1.0f)
                : new Vector4(1.0f, 0.35f, 0.25f, 1.0f)
            : enabled
                ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f)
                : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
        ImGui.TextColored(previewColor, $"\"{previewTitle}\"");

        if (!enabled)
            ImGui.TextDisabled("(disabled - enable to apply)");
        else if (!plugin.WindowRenamer.IsCustomTitleApplied)
            ImGui.TextDisabled("(saved preview - live title is temporarily restored; use Apply Now to reapply)");
        else if (compatibilityError)
            ImGui.TextDisabled("(live custom title - XIVWindowResizer compatibility needs attention)");
        else if (compatibilityActive && plugin.WindowRenamer.IsXIVWindowResizerHandleReady)
            ImGui.TextDisabled("(live custom title - XIVWindowResizer handle ready)");
    }
}
