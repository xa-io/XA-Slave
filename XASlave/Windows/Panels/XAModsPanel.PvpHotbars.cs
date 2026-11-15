using Dalamud.Bindings.ImGui;
using XASlave.Localization;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawPvpHotbarsOptions()
    {
        ImGui.TextWrapped(UiLocale.T("Handles blocked English /hotbar display commands in PvP, including when hotbar 1 is hidden."));
        ImGui.TextWrapped(UiLocale.T("Loading and hotbar readiness requests wait up to 5 seconds. Leaving the territory, logging out or disabling the mod cancels queued commands. Hidden HUDs and cutscenes block changes."));
        ImGui.TextWrapped(UiLocale.T("Native commands: /hotbar display 10 toggles bar 10; add on/off to show or hide it. Supports bars 1 through 10."));
        ImGui.TextWrapped(UiLocale.T("Accepted fallback handles the error without repeating the command. Shown/hidden is reported after the HUD confirms the change; a failed confirmation reports an error. Actions and keybinds stay unchanged."));
        ImGui.TextWrapped("/xa hotbar <1-10> [on|off|toggle]");
        ImGui.TextWrapped(UiLocale.T("Directly show, hide or toggle a hotbar in any zone."));
        ImGui.TextWrapped(UiLocale.T("Enable this mod first. Omit the state or use toggle to switch; on shows; off hides. Visibility is reported after HUD confirmation."));
        ImGui.TextWrapped(UiLocale.T("Remembers separate hotbar visibility settings for PvE and PvP and restores the active mode after loading. Both modes start from the current HUD visibility; later changes affect only the active mode. Memory clears when disabled, logged out, or changing HUD layout or control mode."));
        ImGui.TextWrapped(UiLocale.T("/xa hotbar on enables direct commands and PvP error monitoring. /xa hotbar off cancels queued commands and shows bars still controlled by the mod when the HUD is available. Repeat off to retry recovery. Omit on/off for status."));
    }
}
