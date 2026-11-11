using Dalamud.Bindings.ImGui;
using XASlave.Localization;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawPvpHotbarsOptions()
    {
        ImGui.TextWrapped(UiLocale.T("While enabled, monitor English chat errors for blocked /hotbar display commands in PvP. You must be logged in with the main _ActionBar visible."));
        ImGui.TextWrapped(UiLocale.T("Use /hotbar display 10 to toggle bar 10, or add on/off. Supports bars 1 through 10. Enabling the monitor does not change any bar until a blocked display command is handled."));
        ImGui.TextWrapped(UiLocale.T("Accepted fallback handles the error without repeating the command. Shown/hidden is reported after the HUD confirms the change; a failed confirmation reports an error. Actions and keybinds stay unchanged."));
        ImGui.TextWrapped(UiLocale.T("Direct command: /xa hotbar 1 toggles bar 1; add on to show it or off to hide it. Supports bars 1–10. Requires this mod enabled, but does not wait for an error or require the main bar visible."));
        ImGui.TextWrapped(UiLocale.T("/xa hotbar on enables monitoring; /xa hotbar off disables it and shows bars still controlled by this mod. Recovery waits while the HUD is unavailable; repeat off to retry a failed recovery. Use /xa hotbar 1 on to recover a hidden main bar, since error monitoring requires it visible."));
    }
}
