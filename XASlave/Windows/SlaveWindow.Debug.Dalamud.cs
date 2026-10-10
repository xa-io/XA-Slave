using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private DalamudBranchSnapshot? dalamudBranchSnapshot;

    private void DrawXaAbuseDalamud()
    {
        if (ImGui.Button("Dalamud Branch##xaAbuseDalamudBranch"))
        {
            dalamudBranchSnapshot = DalamudBranchDiagnostic.Capture(Plugin.PluginInterface);
            SetDebugResult($"Dalamud Branch: {dalamudBranchSnapshot.Branch}. {dalamudBranchSnapshot.PolicyMessage}");
        }

        var snapshot = dalamudBranchSnapshot;
        if (snapshot is null)
        {
            ImGui.TextDisabled("Click Dalamud Branch to inspect this running session.");
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Results##xaAbuseDalamudBranchCopy"))
        {
            try
            {
                ImGui.SetClipboardText(snapshot.ToReport());
                SetDebugResult("Dalamud Branch results copied to clipboard.");
            }
            catch (Exception error)
            {
                SetDebugResult($"Could not copy Dalamud Branch results: {error.Message}");
            }
        }

        var resultColor = snapshot.IsRelease
            ? new Vector4(0.45f, 1.0f, 0.45f, 1.0f)
            : snapshot.Kind == DalamudBranchKind.Unknown
                ? new Vector4(1.0f, 0.78f, 0.30f, 1.0f)
                : new Vector4(1.0f, 0.42f, 0.42f, 1.0f);

        ImGui.TextUnformatted($"Dalamud Branch: {snapshot.Branch}");
        ImGui.TextWrapped($"Raw track: {(snapshot.RawBranch is null ? "<null>" : $"\"{snapshot.RawBranch}\"")}");
        ImGui.TextUnformatted($"Classification: {snapshot.Kind}");
        ImGui.TextDisabled("Release-only policy preview:");
        ImGui.TextColored(resultColor, snapshot.PolicyMessage);
        ImGui.Spacing();
        ImGui.TextUnformatted($"Dalamud version: {snapshot.DalamudVersion}");
        ImGui.TextWrapped($"Loaded ClientStructs: {snapshot.ClientStructsVersion}");
        ImGui.TextWrapped($"ClientStructs Git hash: {snapshot.ClientStructsGitHash}");
        ImGui.TextWrapped(snapshot.Detail);
        ImGui.TextDisabled($"Captured: {snapshot.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        ImGui.TextDisabled("Diagnostic only; no plugin is blocked or unloaded.");
        ImGui.TextWrapped("Relaunch the game after changing the launcher branch, then capture again.");
    }
}
