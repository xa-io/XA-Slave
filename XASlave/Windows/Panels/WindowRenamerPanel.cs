using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// Window Renamer panel — partial class split from SlaveWindow.
/// Renames the FFXIV game window title with enable/disable toggle,
/// custom title text box, and optional process ID prefix.
/// </summary>
public partial class SlaveWindow
{
    private string windowRenamerTitleInput = string.Empty;
    private bool windowRenamerInitialized;

    // ───────────────────────────────────────────────
    //  Task: Window Renamer
    // ───────────────────────────────────────────────
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
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Enable / Disable ──
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

        // ── Settings (always visible so user can configure before enabling) ──
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

        // Custom title text box
        ImGui.Text("Window Title:");
        ImGui.SetNextItemWidth(300);
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
                ImGui.SetTooltip("Temporarily restores \"FINAL FANTASY XIV\". Will re-apply on next plugin load if enabled.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Preview ──
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Preview");
        ImGui.Spacing();

        var previewTitle = string.IsNullOrWhiteSpace(windowRenamerTitleInput)
            ? "FINAL FANTASY XIV"
            : windowRenamerTitleInput;
        if (usePid)
            previewTitle = $"{Environment.ProcessId} - {previewTitle}";

        ImGui.TextColored(
            enabled ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
            $"\"{previewTitle}\"");

        if (!enabled)
            ImGui.TextDisabled("(disabled — enable to apply)");
    }
}
