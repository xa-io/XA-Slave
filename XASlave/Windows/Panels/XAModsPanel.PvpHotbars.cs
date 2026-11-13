using Dalamud.Bindings.ImGui;
using XASlave.Localization;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawPvpHotbarsOptions()
    {
        ImGui.TextWrapped(UiLocale.T("Handles blocked English /hotbar display commands in PvP, including when hotbar 1 is hidden."));
        ImGui.TextWrapped(UiLocale.T("Loading and hotbar readiness requests wait up to 5 seconds. Leaving the territory, logging out or disabling the mod cancels queued commands. Hidden HUDs and cutscenes block changes."));
        ImGui.TextWrapped(UiLocale.T("Use /hotbar display 10 to toggle bar 10, or add on/off. Supports bars 1 through 10. Enabling the monitor does not change any bar until a blocked display command is handled."));
        ImGui.TextWrapped(UiLocale.T("Accepted fallback handles the error without repeating the command. Shown/hidden is reported after the HUD confirms the change; a failed confirmation reports an error. Actions and keybinds stay unchanged."));
        ImGui.TextWrapped(UiLocale.T("Direct command: /xa hotbar 1 toggles bar 1; add on to show it or off to hide it. Supports bars 1–10. Requires this mod enabled, but does not wait for an error or require the main bar visible."));
        ImGui.TextWrapped(UiLocale.T("/xa hotbar on enables monitoring. /xa hotbar off cancels queued commands and restores controlled bars when the HUD is available. Use /xa hotbar 1 on to show the main bar."));
    }
}
