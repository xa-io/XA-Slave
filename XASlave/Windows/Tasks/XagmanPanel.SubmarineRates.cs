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

    private void DrawXagmanSubmarineRates(Configuration cfg)
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Submarine Build Rates##xagmanSubmarineRates"))
            return;

        ImGui.TextWrapped("Daily consumption per submarine. Defaults assume rank 90, route OJ, and 24- or 36-hour resend intervals. Enter your own rates for other routes or schedules.");
        ImGui.TextWrapped("Saved rates update Restock estimates and Restock Sub Farmers. Existing Shared Item List quantities stay unchanged; apply the preset again to recalculate them.");
        var locked = xagmanRunning || IsXagmanTaskRunnerActive();
        if (locked)
            ImGui.TextDisabled("Stop Xagman before changing build rates.");

        using (var table = ImRaii.Table("XagmanSubmarineRates", 5,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                   ScaledVector(0f, 240f)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Build");
                ImGui.TableSetupColumn("Tanks / day");
                ImGui.TableSetupColumn("Repair materials / day");
                ImGui.TableSetupColumn("Rate source");
                ImGui.TableSetupColumn("Actions");
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
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
                    ImGui.TextDisabled(isDefault ? hasOverride ? "Override" : "Default" : "Custom");
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(locked))
                    {
                        if (ImGui.SmallButton($"Edit##xagmanRateEdit{build}"))
                        {
                            xagmanRateBuild = build;
                            xagmanRateTanks = rate.Tanks.ToString(CultureInfo.InvariantCulture);
                            xagmanRateKits = rate.Kits.ToString(CultureInfo.InvariantCulture);
                            xagmanRateEditing = true;
                            xagmanRateStatus = string.Empty;
                        }
                        if (hasOverride)
                        {
                            ImGui.SameLine();
                            if (ImGui.SmallButton($"{(isDefault ? "Reset" : "Remove")}##xagmanRateRemove{build}"))
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
            ImGui.TextWrapped("Some saved entries have invalid build codes or rates and are ignored. Remove them below, then add corrected values.");
            for (var i = 0; i < invalidEntries.Length; i++)
            {
                ImGui.TextUnformatted(invalidEntries[i].Key);
                ImGui.SameLine();
                using (ImRaii.Disabled(locked))
                {
                    if (ImGui.SmallButton($"Remove invalid##xagmanRateInvalid{i}")
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
            if (ImGui.Button("Add New Build##xagmanRateNew"))
            {
                ClearXagmanRateEditor();
                xagmanRateStatus = string.Empty;
            }
            ImGui.TextWrapped("Build order: Hull, Stern, Bow, Bridge. Use S, U, W, C or Y, with + after modified parts; WSUC++ also means all four parts are modified.");
            using (ImRaii.Disabled(xagmanRateEditing))
            {
                ImGui.SetNextItemWidth(Scale(180f));
                ImGui.InputTextWithHint("Build##xagmanRateBuild", "e.g. SSSS", ref xagmanRateBuild, 16);
            }
            ImGui.SetNextItemWidth(Scale(180f));
            ImGui.InputTextWithHint("Tanks per day##xagmanRateTanks", "e.g. 9.0", ref xagmanRateTanks, 40);
            ImGui.SetNextItemWidth(Scale(180f));
            ImGui.InputTextWithHint("Repair materials per day##xagmanRateKits", "e.g. 1.67", ref xagmanRateKits, 40);

            var buildValid = SubmarineBuildSupply.TryNormalizeBuild(xagmanRateBuild, out var build);
            var tanksValid = SubmarineBuildSupply.TryParseRate(xagmanRateTanks, out var tanks);
            var kitsValid = SubmarineBuildSupply.TryParseRate(xagmanRateKits, out var kits);
            var duplicate = !xagmanRateEditing && buildValid && xagmanSubmarineRates.ContainsKey(build);
            if (duplicate)
                ImGui.TextWrapped("That build already exists. Choose Edit on its row to change its rates.");
            else if (!buildValid && xagmanRateBuild.Length > 0)
                ImGui.TextWrapped("Enter exactly four valid submarine parts in build order.");
            else if ((!tanksValid && xagmanRateTanks.Length > 0) || (!kitsValid && xagmanRateKits.Length > 0))
                ImGui.TextWrapped("Rates must be positive numbers no greater than 2147483647. Use a dot for decimals.");

            using (ImRaii.Disabled(!buildValid || !tanksValid || !kitsValid || duplicate))
            {
                if (ImGui.Button("Save Build Rates##xagmanRateSave") && buildValid && tanksValid && kitsValid && !duplicate && !locked)
                {
                    var updated = CopyXagmanSubmarineRatesWithout(cfg, build);
                    updated[build] = new SubmarineConsumptionRate { TanksPerDay = tanks, KitsPerDay = kits };
                    if (SaveXagmanSubmarineRates(cfg, updated))
                        ClearXagmanRateEditor();
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel Edit##xagmanRateCancel"))
                ClearXagmanRateEditor();
        }
        if (!string.IsNullOrEmpty(xagmanRateStatus))
            ImGui.TextWrapped(xagmanRateStatus);
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
            xagmanRateStatus = "Could not save build rates. Previous settings kept; check the plugin log.";
            return false;
        }
        xagmanSubmarineRates = SubmarineBuildSupply.GetEffectiveRates(updated);
        xagmanRateStatus = "Build rates saved to XA Slave's user settings.";
        return true;
    }

    private void ClearXagmanRateEditor()
    {
        xagmanRateBuild = xagmanRateTanks = xagmanRateKits = string.Empty;
        xagmanRateEditing = false;
    }
}
