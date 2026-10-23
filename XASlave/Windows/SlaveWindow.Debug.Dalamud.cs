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
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SB0F3BF84C8626357") + "##xaAbuseDalamudBranch")))
        {
            dalamudBranchSnapshot = DalamudBranchDiagnostic.Capture(Plugin.PluginInterface);
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SFC6D4153C4C9FB3C", dalamudBranchSnapshot.BranchMessage, dalamudBranchSnapshot.PolicyMessagePresentation));
        }

        var snapshot = dalamudBranchSnapshot;
        if (snapshot is null)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S264BF144BC1419EB"));
            return;
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S1C014404C4A13162") + "##xaAbuseDalamudBranchCopy")))
        {
            try
            {
                ImGui.SetClipboardText(snapshot.ToReport());
                SetDebugResult(global::XASlave.Localization.UiMessage.Key("S908C530B5ABF97C2"));
            }
            catch (Exception error)
            {
                SetDebugResult(global::XASlave.Localization.UiMessage.Format("SE0F3B435D49F7E04", global::XASlave.Localization.UiMessage.FromException(error)));
            }
        }

        var resultColor = snapshot.IsRelease
            ? new Vector4(0.45f, 1.0f, 0.45f, 1.0f)
            : snapshot.Kind == DalamudBranchKind.Unknown
                ? new Vector4(1.0f, 0.78f, 0.30f, 1.0f)
                : new Vector4(1.0f, 0.42f, 0.42f, 1.0f);

        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S589A7936CD13DC10", snapshot.BranchMessage.Display));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SCF01AFEA016DB750", (snapshot.RawBranch is null ? global::XASlave.Localization.UiLocale.Get("S6A699266C344AA19") : $"\"{snapshot.RawBranch}\"")));
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S9EF664B80B6CC749", snapshot.KindMessage.Display));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3B8B77A38D7A0C8A"));
        ImGui.TextColored(resultColor, snapshot.PolicyMessagePresentation.Display);
        ImGui.Spacing();
        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("SB6954682431D257A", snapshot.DalamudVersionMessage.Display));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S76D717CA4C92A3E7", snapshot.ClientStructsVersionMessage.Display));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SA978D35205CEB9B5", snapshot.ClientStructsGitHashMessage.Display));
        ImGui.TextWrapped(snapshot.DetailMessage.Display);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S01B8861C25E969B2", snapshot.CapturedUtc.ToLocalTime()));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SB1A27A83EEF6147E"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S411ED7F4615B4591"));
    }
}
