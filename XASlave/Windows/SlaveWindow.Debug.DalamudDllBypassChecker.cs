using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private DalamudDllBypassDiagnosticSnapshot? dalamudDllBypassDiagnostic;

    private void DrawXaAbuseDalamudDllBypassChecker()
    {
        if (dalamudDllBypassDiagnostic is null)
            dalamudDllBypassDiagnostic = DalamudDllBypassDiagnostic.Capture();

        if (ImGui.Button("Refresh Bypass Status##xaAbuseDalamudDllBypassRefresh"))
        {
            dalamudDllBypassDiagnostic = DalamudDllBypassDiagnostic.Capture();
            SetDebugResult($"Dalamud DLL bypass status refreshed: {dalamudDllBypassDiagnostic.Value.OperatorVerdict}");
        }

        var diagnostic = dalamudDllBypassDiagnostic.Value;
        var passColor = new Vector4(0.45f, 1.0f, 0.45f, 1.0f);
        var failColor = new Vector4(1.0f, 0.42f, 0.42f, 1.0f);
        var neutralColor = new Vector4(0.82f, 0.82f, 0.82f, 1.0f);
        var warningColor = new Vector4(1.0f, 0.78f, 0.30f, 1.0f);
        var mismatchText = diagnostic.VersionMismatch switch
        {
            true => "Yes",
            false => "No",
            null => "Unknown",
        };
        var preLaunchText = !diagnostic.Classification.BypassInstalled
            ? "Not applicable"
            : diagnostic.PresentBeforeGameStart switch
            {
                true => "Yes",
                false => "No",
                null => "Unknown",
            };
        var classificationColor = diagnostic.Classification.State switch
        {
            ReviewedLauncherArtifactState.KnownPatched => passColor,
            ReviewedLauncherArtifactState.KnownDefault => neutralColor,
            _ => warningColor,
        };
        var verdictColor = diagnostic.OperatorVerdict.StartsWith("PASS", StringComparison.Ordinal)
            ? passColor
            : diagnostic.OperatorVerdict.StartsWith("NOT NEEDED", StringComparison.Ordinal)
                ? neutralColor
                : diagnostic.OperatorVerdict.StartsWith("UNRESOLVED", StringComparison.Ordinal)
                    || diagnostic.OperatorVerdict.StartsWith("UNKNOWN", StringComparison.Ordinal)
                    ? warningColor
                    : failColor;

        ImGui.TextDisabled("Local path, file metadata, exact SHA-256, and loaded-Dalamud version metadata only.");
        ImGui.TextDisabled("The launcher DLL is never loaded or executed, and this panel performs no network access.");
        ImGui.TextUnformatted($"Running FFXIV version: {diagnostic.GameVersion}");
        ImGui.TextUnformatted($"Loaded Dalamud SupportedGameVer: {diagnostic.SupportedGameVersion}");
        ImGui.TextColored(diagnostic.VersionMismatch == true ? warningColor : neutralColor, $"Version mismatch requires bypass today: {mismatchText}");
        ImGui.Spacing();

        ImGui.TextColored(classificationColor, $"Classification: {diagnostic.Classification.StateLabel}");
        ImGui.TextWrapped($"Detected artifact: {diagnostic.Classification.DisplayLabel}");
        ImGui.TextColored(diagnostic.Classification.BypassInstalled ? passColor : neutralColor, $"Bypass installed: {(diagnostic.Classification.BypassInstalled ? "Yes" : "No")}");
        ImGui.TextColored(diagnostic.Classification.StaticPatchValid ? passColor : diagnostic.Classification.State == ReviewedLauncherArtifactState.Unknown ? warningColor : neutralColor, $"Five-method static patch result: {diagnostic.Classification.StaticPatchResult}");
        ImGui.TextUnformatted($"Patch family: {diagnostic.Classification.PatchFamily}");
        ImGui.TextUnformatted($"Present before this game launch (timestamp evidence): {preLaunchText}");
        ImGui.TextColored(verdictColor, $"Operator verdict: {diagnostic.OperatorVerdict}");
        ImGui.Spacing();

        ImGui.TextWrapped($"Installed launcher DLL: {(diagnostic.LauncherDllFound ? diagnostic.LauncherDllPath : "Not found")}");
        if (!string.IsNullOrWhiteSpace(diagnostic.LauncherDllSha256))
            ImGui.TextWrapped($"SHA-256: {diagnostic.LauncherDllSha256}");
        if (!string.IsNullOrWhiteSpace(diagnostic.DalamudVersionMetadataPath))
            ImGui.TextWrapped($"Loaded version metadata: {diagnostic.DalamudVersionMetadataPath}");

        if (diagnostic.Classification.StaticPatchValid)
        {
            ImGui.TextDisabled("Exact reviewed five-method seam set certified by this hash:");
            foreach (var seam in DalamudDllBypassDiagnostic.ReviewedPatchSeams)
                ImGui.TextDisabled($"  - {seam}");
        }

        ImGui.TextWrapped(diagnostic.Detail);
        ImGui.TextDisabled($"Captured: {diagnostic.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }
}
