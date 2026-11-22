using Dalamud.Bindings.ImGui;
using XASlave.Localization;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawPvpHotbarsOptions()
    {
        var configuration = plugin.Configuration;
        var detailedLogging = configuration.PvpHotbarsDetailedLoggingEnabled;
        var chatMessages = configuration.PvpHotbarsChatMessagesEnabled;
        var changed = UiNative.Checkbox(UiLocale.Native(UiLocale.Key("Show detailed /xllog diagnostics")), ref detailedLogging);
        changed |= UiNative.Checkbox(UiLocale.Native(UiLocale.Key("Show system chat messages")), ref chatMessages);
        if (changed)
        {
            configuration.PvpHotbarsDetailedLoggingEnabled = detailedLogging;
            configuration.PvpHotbarsChatMessagesEnabled = chatMessages;
            plugin.PvpHotbars.ApplyConfiguration();
            configuration.SaveDeferred();
        }
        ImGui.TextWrapped(UiLocale.T("Both output options default off. Errors remain in /xllog; status and failed-command replies still appear in chat."));
        ImGui.Spacing();
        ImGui.TextWrapped(UiLocale.T("Remembers which bars you show or hide separately in PvE and PvP, restoring them when you switch."));
        ImGui.TextWrapped(UiLocale.T("/xa hotbar 1 toggles bar 1. Add on or off to show or hide it. Works with bars 1-10 while this mod is enabled."));
        ImGui.TextWrapped(UiLocale.T("Choices reset when you disable the mod, log out, or change HUD layout or control mode."));
    }
}
