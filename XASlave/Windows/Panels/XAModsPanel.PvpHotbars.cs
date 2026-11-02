using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawPvpHotbarsOptions()
    {
        ImGui.TextWrapped("While enabled, monitor English chat errors for blocked /hotbar display commands in PvP. You must be logged in with the main _ActionBar visible.");
        ImGui.TextWrapped("Use /hotbar display 10 to toggle bar 10, or add on/off. Supports bars 1 through 10. Enabling the monitor does not change any bar until a blocked display command is handled.");
        ImGui.TextWrapped("Successful fallback handles the error without repeating the command. Actions and keybinds stay unchanged.");
        ImGui.TextWrapped("Direct command: /xa hotbar 1 toggles bar 1; add on to show it or off to hide it. Supports bars 1–10. Requires this mod enabled, but does not wait for an error or require the main bar visible.");
        ImGui.TextWrapped("/xa hotbar on enables monitoring; /xa hotbar off disables it and shows all bars controlled by this mod. Use off to recover if you hide the main bar, since monitoring requires it to be visible.");
    }
}
