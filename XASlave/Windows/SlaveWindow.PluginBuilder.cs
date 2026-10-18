using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private PluginBuilderService? pluginBuilderService;
    private readonly Dictionary<Guid, PluginBuilderWindow> pluginBuilderWindows = new();
    private readonly Dictionary<Guid, (bool Open, bool? Edit)> pluginBuilderPendingOpen = new();
    private readonly HashSet<string> pluginBuilderPendingScans = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> pluginBuilderCommandDrafts = new();
    private string pluginBuilderWindowTitle = "Plugin Window";

    internal void InitializePluginBuilder(PluginBuilderService service)
    {
        pluginBuilderService = service;
        service.RefreshPlugins();
        UpdatePluginBuilderWindows();
    }

    // Called from Framework.Update, never while WindowSystem.Draw is enumerating its windows.
    private void UpdatePluginBuilderWindows()
    {
        var service = pluginBuilderService;
        if (isDisposed || service == null) return;
        foreach (var id in pluginBuilderWindows.Keys.ToArray())
        {
            if (service.Windows.Any(definition => definition.Id == id)) continue;
            service.SetWindowOpen(id, false);
            plugin.WindowSystem.RemoveWindow(pluginBuilderWindows[id]);
            pluginBuilderWindows.Remove(id);
            pluginBuilderPendingOpen.Remove(id);
        }
        foreach (var definition in service.Windows)
        {
            if (!pluginBuilderWindows.TryGetValue(definition.Id, out var window))
            {
                window = new PluginBuilderWindow(definition, service);
                pluginBuilderWindows.Add(definition.Id, window);
                plugin.WindowSystem.AddWindow(window);
                if (definition.OpenOnLoad) QueuePluginBuilderScans(definition);
            }
            if (pluginBuilderPendingOpen.Remove(definition.Id, out var pending))
            {
                if (pending.Open && pending.Edit is { } edit) window.Open(edit);
                else window.SetOpen(pending.Open);
            }
            window.UpdateSamplingVisibility();
        }
        if (!service.IsScanning && pluginBuilderPendingScans.Count > 0)
        {
            var name = pluginBuilderPendingScans.First();
            pluginBuilderPendingScans.Remove(name);
            service.Scan(name);
        }
    }

    private void DrawPluginBuilderPanel()
    {
        var service = pluginBuilderService;
        ImGui.TextUnformatted("Plugin Builder");
        ImGui.TextWrapped("Build your own windows with pages, information panels and buttons from your favourite plugins.");
        ImGui.TextWrapped("VERY EARLY ACCES, THIS MIGHT CRASH, ERROR, LOOK SUPER UGLY");
        if (service == null)
        {
            ImGui.TextDisabled("Plugin Builder is unavailable.");
            return;
        }

        ImGui.InputText("Window title##builder", ref pluginBuilderWindowTitle, 128);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(pluginBuilderWindowTitle) || service.Windows.Count >= 32))
        {
            if (ImGui.Button("Create saved window##builder"))
            {
                var created = service.CreateWindow(pluginBuilderWindowTitle.Trim());
                if (created != null) pluginBuilderPendingOpen[created.Id] = (true, true);
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Saved windows");
        if (service.Windows.Count == 0)
        {
            ImGui.TextDisabled("Create a window, then add panels and buttons in its editor.");
            return;
        }
        Guid? remove = null;
        using (var table = ImRaii.Table("##pluginBuilderWindows", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            if (!table) return;
            ImGui.TableSetupColumn("Window");
            ImGui.TableSetupColumn("Plugins");
            ImGui.TableSetupColumn("Commands");
            ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthFixed, 190 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var definition in service.Windows)
            {
                using var id = ImRaii.PushId(definition.Id.ToString("N"));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(definition.Title);
                ImGui.TextDisabled($"{definition.Controls.Count} widgets");
                ImGui.TableNextColumn();
                var names = service.GetWindowPluginNames(definition);
                ImGui.TextWrapped(names.Count == 0 ? "Add sources in Edit" : string.Join(", ", names));
                ImGui.TableNextColumn();
                DrawPluginBuilderCommand(definition, service);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Open")) OpenPluginBuilderWindow(definition, edit: false);
                ImGui.SameLine();
                if (ImGui.SmallButton("Edit")) OpenPluginBuilderWindow(definition, edit: true);
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete")) remove = definition.Id;
            }
        }
        if (remove is { } removed)
        {
            service.SetWindowOpen(removed, false);
            pluginBuilderPendingOpen.Remove(removed);
            pluginBuilderCommandDrafts.Remove(removed);
            service.Windows.RemoveAll(definition => definition.Id == removed);
            service.Save();
        }
    }

    private void OpenPluginBuilderWindow(PluginBuilderWindowDefinition definition, bool edit)
    {
        if (pluginBuilderWindows.TryGetValue(definition.Id, out var window)) window.Open(edit);
        else pluginBuilderPendingOpen[definition.Id] = (true, edit);
        QueuePluginBuilderScans(definition);
    }

    private void DrawPluginBuilderCommand(PluginBuilderWindowDefinition definition, PluginBuilderService service)
    {
        if (!pluginBuilderCommandDrafts.TryGetValue(definition.Id, out var command)) command = definition.CommandName;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##windowCommand", "Optional name", ref command, 64))
        {
            pluginBuilderCommandDrafts[definition.Id] = command;
            if (PluginBuilderCommandRules.ValidateName(service.Windows, definition.Id, command, out _))
            {
                definition.CommandName = command;
                service.Save();
            }
        }
        if (!PluginBuilderCommandRules.ValidateName(service.Windows, definition.Id, command, out var error))
            ImGui.TextWrapped(error);
        else if (definition.CommandName.Length > 0)
            ImGui.TextDisabled("/xa win " + definition.CommandName);
        else ImGui.TextDisabled("Letters, numbers, _ or -; no spaces.");
    }

    internal bool TryTogglePluginBuilderWindow(string name, out string error)
    {
        error = string.Empty;
        var service = pluginBuilderService;
        if (isDisposed || service == null) { error = "Plugin Builder is unavailable."; return false; }
        if (!PluginBuilderCommandRules.TryFindWindow(service.Windows, name, out var definition, out error) || definition == null) return false;
        if (pluginBuilderWindows.TryGetValue(definition.Id, out var window))
        {
            window.SetOpen(!window.IsOpen);
            if (window.IsOpen) QueuePluginBuilderScans(definition);
        }
        else
        {
            var current = pluginBuilderPendingOpen.TryGetValue(definition.Id, out var pending)
                ? pending.Open : definition.OpenOnLoad;
            pluginBuilderPendingOpen[definition.Id] = (!current, pending.Edit);
            if (!current) QueuePluginBuilderScans(definition);
        }
        return true;
    }

    private void QueuePluginBuilderScans(PluginBuilderWindowDefinition definition)
    {
        var service = pluginBuilderService;
        if (service == null) return;
        foreach (var name in service.GetWindowPluginNames(definition))
            if (service.GetCatalog(name) == null) pluginBuilderPendingScans.Add(name);
    }

    private void DisposePluginBuilder()
    {
        foreach (var pair in pluginBuilderWindows)
        {
            pluginBuilderService?.SetWindowOpen(pair.Key, false);
            plugin.WindowSystem.RemoveWindow(pair.Value);
        }
        pluginBuilderWindows.Clear();
        pluginBuilderPendingOpen.Clear();
        pluginBuilderPendingScans.Clear();
        pluginBuilderCommandDrafts.Clear();
        pluginBuilderService = null;
    }
}
