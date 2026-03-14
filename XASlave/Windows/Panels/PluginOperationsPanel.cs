using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private void DrawPluginOperationsTask()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Plugin Operations");
        ImGui.TextDisabled("Configure XA Slave startup window behavior.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var openPluginOnLoad = plugin.Configuration.OpenPluginOnLoad;
        if (ImGui.Checkbox("Open Plugin on Load", ref openPluginOnLoad))
        {
            plugin.Configuration.OpenPluginOnLoad = openPluginOnLoad;
            plugin.Configuration.Save();
        }

        ImGui.TextDisabled("Opens XA Slave when the plugin loads and when the character logs in.");

        ImGui.Spacing();

        var verboseTaskLogging = plugin.Configuration.VerboseTaskLogging;
        if (ImGui.Checkbox("Verbose Task Logging", ref verboseTaskLogging))
        {
            plugin.Configuration.VerboseTaskLogging = verboseTaskLogging;
            plugin.Configuration.Save();
        }

        ImGui.TextDisabled("Off: normal user-facing task logs. On: detailed step timing, relog wait state, and CharacterSafeWait diagnostics.");
    }
}
