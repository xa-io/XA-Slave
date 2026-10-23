using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Bindings.ImGui;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private IReadOnlyDictionary<string, (decimal Tanks, decimal Kits)> xagmanSubmarineRates = SubmarineBuildSupply.GetEffectiveRates(null);
    private string xagmanRateBuild = string.Empty;
    private string xagmanRateTanks = string.Empty;
    private string xagmanRateKits = string.Empty;
    private bool xagmanRateEditing;
    private string xagmanRateStatus = string.Empty;
    private global::XASlave.Localization.UiMessage? xagmanRateStatusUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);

    private void DrawXagmanSubmarineRates(Configuration cfg)
    {
        ImGui.Spacing();
        if (!global::XASlave.Localization.UiNative.CollapsingHeader((global::XASlave.Localization.UiLocale.Native("S78A35E557F11430C") + "##xagmanSubmarineRates")))
            return;

        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SE05F497DFD1AD017"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S397D1FA83F1A4746"));
        var locked = xagmanRunning || IsXagmanTaskRunnerActive();
        if (locked)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S4849A62D1B433258"));

        using (var table = ImRaii.Table("XagmanSubmarineRates", 5,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                   ScaledVector(0f, 240f)))
        {
            if (table)
            {
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBDD254B65BAAC169"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S10377E8B282E9ED4"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S7E0C4310A409A15C"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBB5CCC73DA57F8B2"));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFF8059DC6752AFDD"));
                ImGui.TableSetupScrollFreeze(0, 1);
                global::XASlave.Localization.UiNative.TableHeadersRow();
                foreach (var (build, rate) in xagmanSubmarineRates.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray())
                {
                    var isDefault = SubmarineBuildSupply.IsDefaultBuild(build);
                    var hasOverride = cfg.SubmarineBuildConsumptionRates?.Any(entry =>
                        SubmarineBuildSupply.TryNormalizeBuild(entry.Key, out var code) && code == build
                        && entry.Value != null && SubmarineBuildSupply.IsValidRate(entry.Value.TanksPerDay, entry.Value.KitsPerDay)) == true;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(build);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rate.Tanks.ToString(CultureInfo.InvariantCulture));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rate.Kits.ToString(CultureInfo.InvariantCulture));
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(isDefault ? hasOverride ? global::XASlave.Localization.UiLocale.Get("S43BC0F5FC035108F") : global::XASlave.Localization.UiLocale.Get("S21B111CBFE6E8FCA") : global::XASlave.Localization.UiLocale.Get("S494CA78F7374E46F"));
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(locked))
                    {
                        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S464C4FFD019E1E96") + $"##xagmanRateEdit{build}")))
                        {
                            xagmanRateBuild = build;
                            xagmanRateTanks = rate.Tanks.ToString(CultureInfo.InvariantCulture);
                            xagmanRateKits = rate.Kits.ToString(CultureInfo.InvariantCulture);
                            xagmanRateEditing = true;
                            xagmanRateStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanRateStatusUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
                        }
                        if (hasOverride)
                        {
                            ImGui.SameLine();
                            if (global::XASlave.Localization.UiNative.SmallButton($"{(isDefault ? global::XASlave.Localization.UiLocale.Native("SDAEE7606B339F3C3") : global::XASlave.Localization.UiLocale.Native("SC3812FC4ACB861D5"))}##xagmanRateRemove{build}"))
                            {
                                var updated = CopyXagmanSubmarineRatesWithout(cfg, build);
                                if (SaveXagmanSubmarineRates(cfg, updated))
                                    ClearXagmanRateEditor();
                            }
                        }
                    }
                }
            }
        }

        // Keep invalid manual entries visible and removable instead of silently using them in arithmetic.
        var invalidEntries = cfg.SubmarineBuildConsumptionRates?.Where(entry =>
            !SubmarineBuildSupply.TryNormalizeBuild(entry.Key, out _) || entry.Value == null
            || !SubmarineBuildSupply.IsValidRate(entry.Value.TanksPerDay, entry.Value.KitsPerDay)).ToArray();
        if (invalidEntries != null && invalidEntries.Length > 0)
        {
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S7105195C37F4F5CA"));
            for (var i = 0; i < invalidEntries.Length; i++)
            {
                ImGui.TextUnformatted(invalidEntries[i].Key);
                ImGui.SameLine();
                using (ImRaii.Disabled(locked))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("SF83F23B276F593A8") + $"##xagmanRateInvalid{i}"))
                        && cfg.SubmarineBuildConsumptionRates is { } savedRates)
                    {
                        var updated = new Dictionary<string, SubmarineConsumptionRate>(savedRates, StringComparer.Ordinal);
                        updated.Remove(invalidEntries[i].Key);
                        SaveXagmanSubmarineRates(cfg, updated);
                    }
                }
            }
        }

        using (ImRaii.Disabled(locked))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S7AE43CCEC3613FCC") + "##xagmanRateNew")))
            {
                ClearXagmanRateEditor();
                xagmanRateStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanRateStatusUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
            }
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SAF365286926A4FD2"));
            using (ImRaii.Disabled(xagmanRateEditing))
            {
                ImGui.SetNextItemWidth(Scale(180f));
                global::XASlave.Localization.UiNative.InputTextWithHint((global::XASlave.Localization.UiLocale.Native("SBDD254B65BAAC169") + "##xagmanRateBuild"), global::XASlave.Localization.UiLocale.Get("S2D0E868F3266C2C2"), ref xagmanRateBuild, 16);
            }
            ImGui.SetNextItemWidth(Scale(180f));
            global::XASlave.Localization.UiNative.InputTextWithHint((global::XASlave.Localization.UiLocale.Native("SCD4EEC6B2C3E39DB") + "##xagmanRateTanks"), global::XASlave.Localization.UiLocale.Get("S6A4CD68EB1ED4DF1"), ref xagmanRateTanks, 40);
            ImGui.SetNextItemWidth(Scale(180f));
            global::XASlave.Localization.UiNative.InputTextWithHint((global::XASlave.Localization.UiLocale.Native("SE602A7E98E7B2E1C") + "##xagmanRateKits"), global::XASlave.Localization.UiLocale.Get("S92B5F17EC1AD5C67"), ref xagmanRateKits, 40);

            var buildValid = SubmarineBuildSupply.TryNormalizeBuild(xagmanRateBuild, out var build);
            var tanksValid = SubmarineBuildSupply.TryParseRate(xagmanRateTanks, out var tanks);
            var kitsValid = SubmarineBuildSupply.TryParseRate(xagmanRateKits, out var kits);
            var duplicate = !xagmanRateEditing && buildValid && xagmanSubmarineRates.ContainsKey(build);
            if (duplicate)
                ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S14A6C721797C5290"));
            else if (!buildValid && xagmanRateBuild.Length > 0)
                ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S4D502D1DBECF533A"));
            else if ((!tanksValid && xagmanRateTanks.Length > 0) || (!kitsValid && xagmanRateKits.Length > 0))
                ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SDC5B2F29704BF487"));

            using (ImRaii.Disabled(!buildValid || !tanksValid || !kitsValid || duplicate))
            {
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S8526829BE7CDAA15") + "##xagmanRateSave")) && buildValid && tanksValid && kitsValid && !duplicate && !locked)
                {
                    var updated = CopyXagmanSubmarineRatesWithout(cfg, build);
                    updated[build] = new SubmarineConsumptionRate { TanksPerDay = tanks, KitsPerDay = kits };
                    if (SaveXagmanSubmarineRates(cfg, updated))
                        ClearXagmanRateEditor();
                }
            }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SFDDCFDF8D5F404E1") + "##xagmanRateCancel")))
                ClearXagmanRateEditor();
        }
        if (!string.IsNullOrEmpty(xagmanRateStatus))
            ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref xagmanRateStatusUiMessage, xagmanRateStatus));
    }

    private static Dictionary<string, SubmarineConsumptionRate> CopyXagmanSubmarineRatesWithout(Configuration cfg, string build)
    {
        var updated = new Dictionary<string, SubmarineConsumptionRate>(StringComparer.Ordinal);
        if (cfg.SubmarineBuildConsumptionRates != null)
        {
            foreach (var entry in cfg.SubmarineBuildConsumptionRates)
            {
                if (!SubmarineBuildSupply.TryNormalizeBuild(entry.Key, out var code) || code != build)
                    updated[entry.Key] = entry.Value;
            }
        }
        return updated;
    }

    private bool SaveXagmanSubmarineRates(Configuration cfg, Dictionary<string, SubmarineConsumptionRate> updated)
    {
        if (xagmanRunning || IsXagmanTaskRunnerActive())
            return false;
        var previous = cfg.SubmarineBuildConsumptionRates;
        try
        {
            cfg.SubmarineBuildConsumptionRates = updated;
            cfg.Save();
        }
        catch (Exception ex)
        {
            cfg.SubmarineBuildConsumptionRates = previous;
            Plugin.Log.Error(ex, "[Xagman] Could not save submarine build rates.");
            xagmanRateStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanRateStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S606803E990CBC4CA"));
            return false;
        }
        xagmanSubmarineRates = SubmarineBuildSupply.GetEffectiveRates(updated);
        xagmanRateStatus = global::XASlave.Localization.UiMessage.Store(ref xagmanRateStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S777B0AF4D16C4548"));
        return true;
    }

    private void ClearXagmanRateEditor()
    {
        xagmanRateBuild = xagmanRateTanks = xagmanRateKits = string.Empty;
        xagmanRateEditing = false;
    }
}
