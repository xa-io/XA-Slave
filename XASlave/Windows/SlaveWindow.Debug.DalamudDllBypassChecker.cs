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

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SEA1FCD4A259C28DE") + "##xaAbuseDalamudDllBypassRefresh")))
        {
            dalamudDllBypassDiagnostic = DalamudDllBypassDiagnostic.Capture();
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S074DA38A0AD5E422", dalamudDllBypassDiagnostic.Value.OperatorVerdictMessage));
        }

        var diagnostic = dalamudDllBypassDiagnostic.Value;
        var passColor = new Vector4(0.45f, 1.0f, 0.45f, 1.0f);
        var failColor = new Vector4(1.0f, 0.42f, 0.42f, 1.0f);
        var neutralColor = new Vector4(0.82f, 0.82f, 0.82f, 1.0f);
        var warningColor = new Vector4(1.0f, 0.78f, 0.30f, 1.0f);
        var mismatchText = (diagnostic.VersionMismatch switch
        {
            true => global::XASlave.Localization.UiMessage.Key("S85A39AB345D672FF"),
            false => global::XASlave.Localization.UiMessage.Key("S1EA442A134B2A184"),
            null => global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374"),
        }).Display;
        var preLaunchText = (!diagnostic.Classification.BypassInstalled ? global::XASlave.Localization.UiMessage.Key("S5237C96AC1A23B98") : diagnostic.PresentBeforeGameStart switch
            {
                true => global::XASlave.Localization.UiMessage.Key("S85A39AB345D672FF"),
                false => global::XASlave.Localization.UiMessage.Key("S1EA442A134B2A184"),
                null => global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374"),
            }).Display;
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

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SC9BA5B6E7748BB24"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S447923016A98C59D"));
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("SE6777F491CA1BEE1", diagnostic.GameVersionMessage.Display));
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("SB50AABD55AABCDCE", diagnostic.SupportedGameVersionMessage.Display));
        ImGui.TextColored(diagnostic.VersionMismatch == true ? warningColor : neutralColor, global::XASlave.Localization.UiLocale.FormatKey("S349FAED2A3B16C87", mismatchText));
        ImGui.Spacing();

        ImGui.TextColored(classificationColor, global::XASlave.Localization.UiLocale.FormatKey("S9EF664B80B6CC749", diagnostic.Classification.StateLabelMessage.Display));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S47988FCAD5B2293C", diagnostic.Classification.DisplayLabelMessage.Display));
        ImGui.TextColored(diagnostic.Classification.BypassInstalled ? passColor : neutralColor, global::XASlave.Localization.UiLocale.FormatKey("S0095548DC3A449DB", (diagnostic.Classification.BypassInstalled ? global::XASlave.Localization.UiLocale.Get("S85A39AB345D672FF") : global::XASlave.Localization.UiLocale.Get("S1EA442A134B2A184"))));
        ImGui.TextColored(diagnostic.Classification.StaticPatchValid ? passColor : diagnostic.Classification.State == ReviewedLauncherArtifactState.Unknown ? warningColor : neutralColor, global::XASlave.Localization.UiLocale.FormatKey("S5E2D43BF4377DA4B", diagnostic.Classification.StaticPatchResultMessage.Display));
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S0B1B75895C3064F0", diagnostic.Classification.PatchFamilyMessage.Display));
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("SD50DF72BDB7FCC61", preLaunchText));
        ImGui.TextColored(verdictColor, global::XASlave.Localization.UiLocale.FormatKey("SBAC8D8C81DC12C2F", diagnostic.OperatorVerdictMessage.Display));
        ImGui.Spacing();

        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SCCB487E0F5B2B8FA", (diagnostic.LauncherDllFound ? diagnostic.LauncherDllPath : global::XASlave.Localization.UiLocale.Get("SE3EBAA16DD9D9B9F"))));
        if (!string.IsNullOrWhiteSpace(diagnostic.LauncherDllSha256))
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S4E2AD981EDB268E9", diagnostic.LauncherDllSha256));
        if (!string.IsNullOrWhiteSpace(diagnostic.DalamudVersionMetadataPath))
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S3D1A3BF28EEA8E11", diagnostic.DalamudVersionMetadataPath));

        if (diagnostic.Classification.StaticPatchValid)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S18F84058B195A40E"));
            foreach (var seam in DalamudDllBypassDiagnostic.ReviewedPatchSeams)
                ImGui.TextDisabled($"  - {seam}");
        }

        ImGui.TextWrapped(diagnostic.DetailMessage.Display);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S01B8861C25E969B2", diagnostic.CapturedUtc.ToLocalTime()));
    }
}
