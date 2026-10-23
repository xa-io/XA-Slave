using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string pluginGuardFilter = string.Empty;
    private (string InternalName, string Name)[] pluginGuardInstalled = [];
    private long pluginGuardRefreshAt;
    private bool pluginGuardListUnavailable;

    private void SavePluginGuardSelection()
    {
        var cfg = plugin.Configuration;
        cfg.PluginGuardAdditionalPlugins = PluginGuardService.NormalizeAdditionalPlugins(cfg.PluginGuardAdditionalPlugins);
        plugin.PluginGuard.ApplyConfiguration(cfg.PluginGuardAdditionalPlugins);
        cfg.Save();
    }

    private void DrawPluginGuardOptions()
    {
        var cfg = plugin.Configuration;
        cfg.PluginGuardAdditionalPlugins ??= new();
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF39CA49AAD50294C"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SF7DF33D1750BB553", cfg.PluginGuardAdditionalPlugins.Count));
        ImGui.SetNextItemWidth(Scale(260f));
        ImGui.InputTextWithHint("##PluginGuardFilter", global::XASlave.Localization.UiLocale.Get("SFC791C78AD23347B"), ref pluginGuardFilter, 128);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S0E91610117029A62") + "##PluginGuard")))
            pluginGuardRefreshAt = 0;

        if (Environment.TickCount64 >= pluginGuardRefreshAt)
        {
            pluginGuardRefreshAt = Environment.TickCount64 + 5000;
            try
            {
                // Copy values only; do not retain host plugin objects or change their state.
                pluginGuardInstalled = Plugin.PluginInterface.InstalledPlugins
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.InternalName))
                    .Select(entry => (entry.InternalName, entry.Name))
                    .DistinctBy(entry => entry.InternalName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                pluginGuardListUnavailable = false;
            }
            catch
            {
                // Retain the last list and saved selections if host enumeration is temporarily unavailable.
                pluginGuardListUnavailable = true;
            }
        }

        if (pluginGuardListUnavailable)
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SC38F212D3C71CF5A"));

        var selected = new HashSet<string>(cfg.PluginGuardAdditionalPlugins, StringComparer.OrdinalIgnoreCase);
        using var child = ImRaii.Child("##PluginGuardPluginList", new Vector2(0f, Scale(240f)), true);
        if (!child) return;

        var visible = 0;
        foreach (var entry in pluginGuardInstalled)
        {
            if (!entry.Name.Contains(pluginGuardFilter, StringComparison.OrdinalIgnoreCase)
                && !entry.InternalName.Contains(pluginGuardFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            visible++;
            var builtIn = PluginGuardService.IsDefaultProtected(entry.InternalName);
            var protect = builtIn || selected.Contains(entry.InternalName);
            using (ImRaii.Disabled(builtIn))
            {
                if (ImGui.Checkbox($"##PluginGuardSelect{entry.InternalName}", ref protect))
                {
                    if (protect)
                        cfg.PluginGuardAdditionalPlugins.Add(entry.InternalName);
                    else
                        cfg.PluginGuardAdditionalPlugins.RemoveAll(name => name.Equals(entry.InternalName, StringComparison.OrdinalIgnoreCase));
                    SavePluginGuardSelection();
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(entry.InternalName);
            if (builtIn)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S711606EB29D7026B"));
            }
        }

        var installedNames = new HashSet<string>(pluginGuardInstalled.Select(entry => entry.InternalName), StringComparer.OrdinalIgnoreCase);
        foreach (var name in cfg.PluginGuardAdditionalPlugins.ToArray())
        {
            if (installedNames.Contains(name) || !name.Contains(pluginGuardFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            visible++;
            var protect = true;
            if (ImGui.Checkbox($"##PluginGuardMissing{name}", ref protect))
            {
                cfg.PluginGuardAdditionalPlugins.RemoveAll(entry => entry.Equals(name, StringComparison.OrdinalIgnoreCase));
                SavePluginGuardSelection();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            ImGui.TextDisabled(pluginGuardListUnavailable ? global::XASlave.Localization.UiLocale.Get("S7FA4EBDE64BB063C") : global::XASlave.Localization.UiLocale.Get("S57C74A8A4F4791EE"));
        }
        if (visible == 0) ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA78C7366AD44DC8B"));
    }
}
