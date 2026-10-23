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

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S05F3A5D5B4F5775C"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SAEA7841BB44EB701"));
        if (plugin.WindowRenamer.IsXIVWindowResizerCompatibilityActive)
        {
            ImGui.Spacing();
            if (plugin.WindowRenamer.IsXIVWindowResizerHandleReady)
            {
                ImGui.TextColored(
                    new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                    global::XASlave.Localization.UiLocale.Get("SF34937B96ABE78E4"));
                ImGui.TextDisabled(plugin.WindowRenamer.LocalizedXIVWindowResizerCompatibilityStatusText);
            }
            else
            {
                ImGui.TextColored(
                    new Vector4(1.0f, 0.35f, 0.25f, 1.0f),
                    global::XASlave.Localization.UiLocale.Get("SB64BF3E794F96751"));
                ImGui.TextDisabled(plugin.WindowRenamer.LocalizedXIVWindowResizerCompatibilityStatusText);
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7E3910F9D848BFD2"));
            }
        }
        else if (plugin.WindowRenamer.HasXIVWindowResizerCompatibilityError)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1.0f, 0.35f, 0.25f, 1.0f),
                global::XASlave.Localization.UiLocale.Get("SA9674899392125C3"));
            ImGui.TextDisabled(plugin.WindowRenamer.LocalizedXIVWindowResizerCompatibilityStatusText);
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SB6F61AF9F826AAC2"));
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Enable / Disable --
        var enabled = plugin.Configuration.WindowRenamerEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SD98E6BF2D57101E2"), ref enabled))
        {
            plugin.Configuration.WindowRenamerEnabled = enabled;
            plugin.Configuration.Save();
            plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Settings (always visible so user can configure before enabling) --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S74A883A037BC227F"));
        ImGui.Spacing();

        // Use Process ID checkbox
        var usePid = plugin.Configuration.WindowRenamerUseProcessId;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SF9CD25E170D20C06"), ref usePid))
        {
            plugin.Configuration.WindowRenamerUseProcessId = usePid;
            plugin.Configuration.Save();
            if (enabled)
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S3488A6481F68EB25", Environment.ProcessId));

        ImGui.Spacing();

        var showCurrentCharacter = plugin.Configuration.WindowRenamerShowCurrentCharacter;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SB3A1A7911657A677"), ref showCurrentCharacter))
        {
            plugin.Configuration.WindowRenamerShowCurrentCharacter = showCurrentCharacter;
            plugin.Configuration.Save();
            if (enabled)
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S45C1218E4775EC6E"));

        ImGui.Spacing();

        // Custom title text box
        ImGui.Text(global::XASlave.Localization.UiLocale.Get("SB824A6F02CADB68E"));
        ImGui.SetNextItemWidth(Scale(300f));
        if (ImGui.InputText("##WindowTitle", ref windowRenamerTitleInput, 256))
            plugin.Configuration.WindowRenamerTitle = windowRenamerTitleInput;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            plugin.Configuration.Save();
            if (enabled)
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S84EDAC856391C21A"));

        ImGui.Spacing();

        // Apply button (manual re-apply)
        if (enabled)
        {
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S7F4B7731284A3909")))
            {
                plugin.WindowRenamer.ApplyFromConfig(plugin.Configuration);
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SCB73E9ACE1C62F02")))
            {
                plugin.WindowRenamer.Restore();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S144C25F25BA41346"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- Preview --
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), global::XASlave.Localization.UiLocale.Get("S324B134F57C70C72"));
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
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SC4D45EE54EAEBDFF"));
        else if (!plugin.WindowRenamer.IsCustomTitleApplied)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S4777C62735D82D8E"));
        else if (compatibilityError)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S142135E1D1A483BD"));
        else if (compatibilityActive && plugin.WindowRenamer.IsXIVWindowResizerHandleReady)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S2A4AB99DFA5A6DBD"));
    }
}
