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
        ImGui.TextWrapped("Check any installed plugin to protect it. Built-in protections stay selected. Changes apply immediately while Plugin Guard is enabled and are saved for next time.");
        ImGui.TextDisabled($"Additional protected plugins: {cfg.PluginGuardAdditionalPlugins.Count}");
        ImGui.SetNextItemWidth(Scale(260f));
        ImGui.InputTextWithHint("##PluginGuardFilter", "Filter plugins...", ref pluginGuardFilter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##PluginGuard"))
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
            ImGui.TextWrapped("Installed plugin list is temporarily unavailable. Saved selections are retained; refresh to retry.");

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
                ImGui.TextDisabled("(built-in)");
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
            ImGui.TextDisabled(pluginGuardListUnavailable ? "(saved)" : "(not installed)");
        }
        if (visible == 0) ImGui.TextDisabled("No matching plugins.");
    }
}
