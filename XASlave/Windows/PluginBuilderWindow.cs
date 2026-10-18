using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XASlave.Services;

namespace XASlave.Windows;

internal sealed class PluginBuilderWindow : Window
{
    private const int MaxControls = 64;
    private readonly PluginBuilderWindowDefinition definition;
    private readonly PluginBuilderService service;
    private bool editing;
    private bool drawnSinceUpdate;
    private Guid selectedPageId;
    private string newPageTitle = string.Empty;
    private string sourcePlugin = string.Empty;
    private PluginBuilderControlDefinition? draft;
    private Guid? replacingControlId;
    private string sourceFilter = string.Empty;
    private PluginBuilderCatalog? choicesCatalog;
    private IReadOnlyList<PluginBuilderSource> orderedSources = Array.Empty<PluginBuilderSource>();

    internal PluginBuilderWindow(PluginBuilderWindowDefinition definition, PluginBuilderService service)
        : base(VisibleTitle(definition.Title) + "###XAPluginBuilder" + definition.Id.ToString("N"))
    {
        this.definition = definition;
        this.service = service;
        Size = new Vector2(740, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = definition.OpenOnLoad;
        EnsureSelectedPage();
    }

    internal void Open(bool edit)
    {
        editing = edit;
        IsOpen = true;
        BringToFront();
    }

    internal void SetOpen(bool open)
    {
        IsOpen = open;
        if (open) BringToFront();
        else service.SetWindowOpen(definition.Id, false);
    }

    internal void UpdateSamplingVisibility()
    {
        // A window omitted by the host, closed, collapsed, or being edited does not keep polling.
        EnsureSelectedPage();
        service.SetWindowPage(definition.Id, selectedPageId);
        service.SetWindowOpen(definition.Id, IsOpen && !editing && drawnSinceUpdate);
        drawnSinceUpdate = false;
    }

    public override void OnClose() => service.SetWindowOpen(definition.Id, false);

    public override void PreDraw()
    {
        WindowName = VisibleTitle(definition.Title) + "###XAPluginBuilder" + definition.Id.ToString("N");
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 240) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public override void Draw()
    {
        drawnSinceUpdate = true;
        EnsureSelectedPage();
        DrawToolbar();
        ImGui.Separator();
        if (definition.ShowSidebar)
        {
            var width = Math.Min(definition.SidebarWidth * ImGuiHelpers.GlobalScale, Math.Max(100 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.4f));
            using (var sidebar = ImRaii.Child("##pages", new Vector2(width, 0), true))
            {
                if (sidebar) DrawPageSelection();
            }
            ImGui.SameLine();
            using (var content = ImRaii.Child("##pageContent", Vector2.Zero, false))
            {
                if (content) DrawBody();
            }
        }
        else
        {
            if (definition.Pages.Count > 1)
            {
                using var pages = ImRaii.Combo("Page", CurrentPageTitle());
                if (pages) DrawPageSelection();
            }
            DrawBody();
        }
    }

    private void DrawBody()
    {
        if (editing) DrawEditor();
        else DrawControls();
    }

    private void EnsureSelectedPage()
    {
        if (!definition.Pages.Any(page => page.Id == selectedPageId)) selectedPageId = definition.Pages.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    private string CurrentPageTitle() => definition.Pages.FirstOrDefault(page => page.Id == selectedPageId)?.Title ?? "Overview";

    private void SelectPage(Guid pageId)
    {
        selectedPageId = pageId;
        service.SetWindowPage(definition.Id, selectedPageId);
    }

    private void DrawPageSelection()
    {
        foreach (var page in definition.Pages)
        {
            using var id = ImRaii.PushId(page.Id.ToString("N"));
            if (ImGui.Selectable(VisibleTitle(page.Title), page.Id == selectedPageId)) SelectPage(page.Id);
        }
    }

    private void DrawToolbar()
    {
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Refresh##page")) RefreshCurrentPage();
        var editLabel = editing ? "Done" : "Edit";
        var editWidth = ImGui.CalcTextSize(editLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X, rightEdge - editWidth));
        if (ImGui.Button(editLabel + "##mode"))
        {
            editing = !editing;
            service.SetWindowOpen(definition.Id, false);
        }
    }

    private void RefreshCurrentPage()
    {
        foreach (var control in definition.Controls)
            if (control.PageId == selectedPageId && control.Kind == PluginBuilderControlKind.Info
                && control.Source.CanRead && CanUse(control))
                service.QueueRead(definition.Id, control.Id);
    }

    private bool CanUse(PluginBuilderControlDefinition control)
        => service.IsAvailable(control.Source) && control.Source.UnsupportedReason.Length == 0
            && (!control.Source.RequiresReflectionConfirmation || control.ReflectionReadConfirmed);

    private void DrawWindowSettings()
    {
        using var settings = ImRaii.TreeNode("Window settings");
        if (!settings) return;
        var changed = false;
        var title = definition.Title;
        if (ImGui.InputText("Window title", ref title, 128)) { definition.Title = title; changed = true; }
        var openOnLoad = definition.OpenOnLoad;
        if (ImGui.Checkbox("Open on plugin load", ref openOnLoad)) { definition.OpenOnLoad = openOnLoad; changed = true; }
        var timestamps = definition.ShowTimestamps;
        if (ImGui.Checkbox("Show timestamps", ref timestamps)) { definition.ShowTimestamps = timestamps; changed = true; }
        var sidebar = definition.ShowSidebar;
        if (ImGui.Checkbox("Show page sidebar", ref sidebar)) { definition.ShowSidebar = sidebar; changed = true; }
        if (sidebar)
        {
            var width = definition.SidebarWidth;
            if (ImGui.InputInt("Sidebar width", ref width)) { definition.SidebarWidth = Math.Clamp(width, 100, 400); changed = true; }
        }
        if (changed) service.Save();
    }

    private void DrawPageSettings()
    {
        using var pages = ImRaii.TreeNode("Manage pages");
        if (!pages) return;
        var page = definition.Pages.FirstOrDefault(item => item.Id == selectedPageId);
        if (page != null)
        {
            var title = page.Title;
            if (ImGui.InputText("Page title", ref title, 64)) { page.Title = title; service.Save(); }
            var index = definition.Pages.IndexOf(page);
            using (ImRaii.Disabled(index == 0))
                if (ImGui.SmallButton("Move page up")) { definition.Pages.RemoveAt(index); definition.Pages.Insert(index - 1, page); service.Save(); }
            ImGui.SameLine();
            using (ImRaii.Disabled(index >= definition.Pages.Count - 1))
                if (ImGui.SmallButton("Move page down")) { definition.Pages.Remove(page); definition.Pages.Insert(index + 1, page); service.Save(); }
            using (ImRaii.Disabled(definition.Pages.Count <= 1))
                if (ImGui.SmallButton("Remove page"))
                {
                    var fallback = definition.Pages.First(item => item.Id != page.Id);
                    foreach (var control in definition.Controls.Where(item => item.PageId == page.Id)) control.PageId = fallback.Id;
                    if (draft?.PageId == page.Id) draft.PageId = fallback.Id;
                    definition.Pages.Remove(page);
                    SelectPage(fallback.Id);
                    service.Save();
                }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move this page's widgets to the first remaining page.");
        }
        ImGui.InputTextWithHint("##newPage", "New page title", ref newPageTitle, 64);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newPageTitle) || definition.Pages.Count >= 32))
            if (ImGui.Button("Add page"))
            {
                var added = new PluginBuilderPageDefinition { Title = newPageTitle.Trim() };
                definition.Pages.Add(added);
                newPageTitle = string.Empty;
                SelectPage(added.Id);
                service.Save();
            }
    }

    private void DrawEditor()
    {
        DrawWindowSettings();
        DrawPageSettings();
        ImGui.Separator();
        ImGui.TextUnformatted(CurrentPageTitle());
        using (ImRaii.Disabled(definition.Controls.Count >= MaxControls))
        {
            if (ImGui.Button("Add Info Panel")) BeginDraft(PluginBuilderControlKind.Info);
            ImGui.SameLine();
            if (ImGui.Button("Add Action Button")) BeginDraft(PluginBuilderControlKind.Action);
        }
        if (definition.Controls.Count >= MaxControls) ImGui.TextDisabled("This window has reached its 64-widget limit.");
        if (draft != null) DrawDraft();
        ImGui.Separator();

        var remove = -1;
        var moveFrom = -1;
        var moveTo = -1;
        for (var index = 0; index < definition.Controls.Count; index++)
        {
            var control = definition.Controls[index];
            if (control.PageId != selectedPageId) continue;
            using var id = ImRaii.PushId(control.Id.ToString("N"));
            ImGui.TextUnformatted($"{index + 1}. {control.Kind}: {control.Label}");
            var previous = definition.Controls.FindLastIndex(index, item => item.Id != control.Id && item.PageId == selectedPageId);
            var next = definition.Controls.FindIndex(index + 1, item => item.PageId == selectedPageId);
            using (ImRaii.Disabled(previous < 0))
                if (ImGui.SmallButton("Up")) { moveFrom = index; moveTo = previous; }
            ImGui.SameLine();
            using (ImRaii.Disabled(next < 0))
                if (ImGui.SmallButton("Down")) { moveFrom = index; moveTo = next; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) remove = index;
            ImGui.SameLine();
            if (ImGui.SmallButton("Change source")) BeginDraft(control.Kind, control);
            if (!service.IsAvailable(control.Source)) ImGui.TextWrapped("Source unavailable. Choose Change source to scan or reconnect it.");
            using (var settings = ImRaii.TreeNode("Settings##widget"))
            {
                if (settings)
                {
                    if (DrawControlSettings(control)) service.Save();
                }
            }
            ImGui.Separator();
        }
        if (remove >= 0)
        {
            if (definition.Controls[remove].Id == replacingControlId) { draft = null; replacingControlId = null; }
            definition.Controls.RemoveAt(remove);
            service.Save();
        }
        else if (moveFrom >= 0 && moveTo >= 0 && moveTo < definition.Controls.Count)
        {
            var control = definition.Controls[moveFrom];
            definition.Controls.RemoveAt(moveFrom);
            definition.Controls.Insert(moveTo, control);
            service.Save();
        }
        if (!definition.Controls.Any(control => control.PageId == selectedPageId) && draft == null) ImGui.TextDisabled("Add a panel or button, then press Done.");
    }

    private void BeginDraft(PluginBuilderControlKind kind, PluginBuilderControlDefinition? replacement = null)
    {
        replacingControlId = replacement?.Id;
        draft = new PluginBuilderControlDefinition
        {
            Kind = kind, Label = replacement?.Label ?? string.Empty,
            PageId = replacement?.PageId ?? selectedPageId,
            SameLine = replacement?.SameLine ?? (kind == PluginBuilderControlKind.Action
                && definition.Controls.LastOrDefault(control => control.PageId == selectedPageId)?.Kind == PluginBuilderControlKind.Action),
            Width = replacement?.Width ?? 0,
        };
        if (replacement != null) draft.Id = replacement.Id;
        sourceFilter = string.Empty;
        sourcePlugin = replacement?.Source.PluginName ?? service.GetWindowPluginNames(definition).FirstOrDefault()
            ?? service.PluginNames.FirstOrDefault() ?? string.Empty;
        choicesCatalog = null;
        orderedSources = Array.Empty<PluginBuilderSource>();
        if (!service.IsScanning && sourcePlugin.Length > 0 && service.GetCatalog(sourcePlugin) == null) service.Scan(sourcePlugin);
    }

    private void DrawDraft()
    {
        var control = draft;
        if (control == null) return;
        using var draftId = ImRaii.PushId("newBuilderWidget");
        ImGui.Separator();
        ImGui.TextUnformatted(replacingControlId != null ? "Replace widget source"
            : control.Kind == PluginBuilderControlKind.Info ? "New information panel" : "New action button");
        using (var plugins = ImRaii.Combo("Source plugin", sourcePlugin.Length > 0 ? sourcePlugin : "Select a plugin"))
        {
            if (plugins)
                foreach (var name in service.PluginNames)
                    if (ImGui.Selectable(name, sourcePlugin == name))
                    {
                        sourcePlugin = name;
                        control.Source = new();
                        control.Arguments.Clear();
                        sourceFilter = string.Empty;
                        if (!service.IsScanning && service.GetCatalog(name) == null) service.Scan(name);
                    }
        }
        if (ImGui.SmallButton("Refresh plugins")) service.RefreshPlugins();
        ImGui.SameLine();
        using (ImRaii.Disabled(service.IsScanning || sourcePlugin.Length == 0))
            if (ImGui.SmallButton("Scan sources")) service.Scan(sourcePlugin);
        var catalog = service.GetCatalog(sourcePlugin);
        if (!ReferenceEquals(choicesCatalog, catalog))
        {
            choicesCatalog = catalog;
            orderedSources = catalog?.PluginName == sourcePlugin
                ? catalog.Sources.OrderBy(source => (int)source.Kind).ThenBy(source => source.Label, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<PluginBuilderSource>();
        }
        if (catalog == null)
        {
            ImGui.TextWrapped("Select a loaded plugin and scan its sources.");
            ImGui.TextWrapped(service.Status);
        }
        else ImGui.TextDisabled("Choose IPC first, then commands or reviewed reflection.");
        ImGui.InputTextWithHint("##sourceFilter", "Search IPC, commands or reflection sources", ref sourceFilter, 160);
        var matches = 0;
        using (var list = ImRaii.Child("##builderSources", new Vector2(0, 170 * ImGuiHelpers.GlobalScale), true))
        if (list)
        {
            foreach (var source in orderedSources)
            {
                if (!source.Label.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase)
                    && !source.Channel.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase)
                    && !source.DeclaringType.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase)) continue;
                matches++;
                if (matches > 100) continue;
                var supported = control.Kind == PluginBuilderControlKind.Info ? source.CanRead : source.CanExecute;
                supported &= source.UnsupportedReason.Length == 0;
                using var sourceId = ImRaii.PushId(source.Id);
                using (ImRaii.Disabled(!supported))
                    if (ImGui.Selectable(VisibleTitle(SourceLabel(source)), control.Source.Id == source.Id)) SelectSource(control, source);
                if (!supported)
                    ImGui.TextDisabled(source.UnsupportedReason.Length > 0 ? source.UnsupportedReason
                        : control.Kind == PluginBuilderControlKind.Info ? "Available as an action only." : "Available as information only.");
            }
            if (matches == 0) ImGui.TextDisabled("No matching sources.");
        }
        if (matches > 100) ImGui.TextDisabled($"Showing 100 of {matches} sources; narrow the search.");
        if (control.Source.Id.Length > 0) DrawControlSettings(control);
        var supportedSelection = control.Source.Id.Length > 0
            && (control.Kind == PluginBuilderControlKind.Info ? control.Source.CanRead : control.Source.CanExecute)
            && control.Source.UnsupportedReason.Length == 0
            && (!control.Source.RequiresReflectionConfirmation || control.ReflectionReadConfirmed)
            && catalog?.PluginName == control.Source.PluginName
            && catalog.ModuleVersionId == control.Source.ModuleVersionId;
        using (ImRaii.Disabled(!supportedSelection || string.IsNullOrWhiteSpace(control.Label)
            || replacingControlId == null && definition.Controls.Count >= MaxControls))
        {
            if (ImGui.Button(replacingControlId == null ? "Add to window" : "Replace source"))
            {
                var existingIndex = replacingControlId is { } id
                    ? definition.Controls.FindIndex(existing => existing.Id == id) : -1;
                if (existingIndex >= 0) definition.Controls[existingIndex] = control;
                else if (replacingControlId == null) definition.Controls.Add(control);
                service.Save();
                draft = null;
                replacingControlId = null;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { draft = null; replacingControlId = null; }
    }

    private static void SelectSource(PluginBuilderControlDefinition control, PluginBuilderSource source)
    {
        if (string.IsNullOrWhiteSpace(control.Label) || control.Label == control.Source.Label) control.Label = source.Label;
        control.Source = CloneSource(source);
        control.Arguments = source.ParameterTypes.Select(type => new PluginBuilderArgument
        {
            Value = type.Split(',')[0] == "System.Boolean" ? "false" : string.Empty,
        }).ToList();
        control.ReflectionReadConfirmed = false;
        control.AutoRefreshSeconds = 0;
        control.MemberPath = string.Empty;
    }

    private bool DrawControlSettings(PluginBuilderControlDefinition control)
    {
        var changed = false;
        var label = control.Label;
        if (ImGui.InputText("Label", ref label, 128)) { control.Label = label; changed = true; }
        var page = definition.Pages.FirstOrDefault(item => item.Id == control.PageId);
        using (var pages = ImRaii.Combo("Page", page?.Title ?? "Overview"))
        {
            if (pages)
                foreach (var candidate in definition.Pages)
                {
                    using var pageId = ImRaii.PushId(candidate.Id.ToString("N"));
                    if (ImGui.Selectable(VisibleTitle(candidate.Title), candidate.Id == control.PageId))
                    {
                        control.PageId = candidate.Id;
                        changed = true;
                    }
                }
        }
        var sameLine = control.SameLine;
        if (ImGui.Checkbox("Place beside previous widget", ref sameLine)) { control.SameLine = sameLine; changed = true; }
        var width = control.Width;
        if (ImGui.InputInt("Width (0 = automatic)", ref width)) { control.Width = Math.Clamp(width, 0, 1200); changed = true; }
        ImGui.TextWrapped(SourceLabel(control.Source));
        ImGui.TextDisabled("Provided by " + control.Source.PluginName);
        if (control.Source.UnsupportedReason.Length > 0) ImGui.TextWrapped(control.Source.UnsupportedReason);
        if (control.Source.RequiresReflectionConfirmation)
        {
            var confirmed = control.ReflectionReadConfirmed;
            if (ImGui.Checkbox("Allow this selected reflection source", ref confirmed))
            {
                control.ReflectionReadConfirmed = confirmed;
                changed = true;
            }
            ImGui.TextWrapped("Reflection accesses the selected loaded member. Methods and property getters can have side effects.");
        }
        for (var index = 0; index < control.Arguments.Count; index++)
        {
            using var id = ImRaii.PushId(index);
            var argument = control.Arguments[index];
            var name = index < control.Source.ParameterNames.Count ? control.Source.ParameterNames[index] : "Argument " + (index + 1);
            var type = index < control.Source.ParameterTypes.Count ? control.Source.ParameterTypes[index] : string.Empty;
            var shortType = type.Split(',')[0];
            ImGui.TextWrapped(name + " : " + shortType);
            var binding = argument.UseCurrentCharacterId ? 1 : argument.UseCurrentCharacterName ? 2 : 0;
            var bindingNames = new[] { "Fixed value", "Current character CID", "Current character name" };
            using (var combo = ImRaii.Combo("Value source", bindingNames[binding]))
            if (combo)
                for (var option = 0; option < bindingNames.Length; option++)
                {
                    using var disabled = ImRaii.Disabled(option == 1 && shortType != "System.UInt64"
                        || option == 2 && shortType != "System.String");
                    if (ImGui.Selectable(bindingNames[option], binding == option))
                    {
                        argument.UseCurrentCharacterId = option == 1;
                        argument.UseCurrentCharacterName = option == 2;
                        changed = true;
                    }
                }
            if (!argument.UseCurrentCharacterId && !argument.UseCurrentCharacterName)
            {
                var value = argument.Value;
                if (shortType == "System.Boolean")
                {
                    var boolean = bool.TryParse(value, out var parsed) && parsed;
                    if (ImGui.Checkbox("Value", ref boolean)) { argument.Value = boolean ? "true" : "false"; changed = true; }
                }
                else if (ImGui.InputText("Value", ref value, 2048)) { argument.Value = value; changed = true; }
            }
            else ImGui.TextDisabled("Resolved when used; requires a logged-in character and a compatible parameter type.");
        }
        if (control.Kind == PluginBuilderControlKind.Info)
        {
            var path = control.MemberPath;
            if (ImGui.InputText("Value field (optional)", ref path, 256)) { control.MemberPath = path; changed = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Enter a returned field, key or list index, using dots for nested values. Leave blank to display the returned value.");
            var refresh = control.AutoRefreshSeconds;
            if (ImGui.InputInt("Refresh seconds (0 = manual)", ref refresh))
            {
                control.AutoRefreshSeconds = Math.Clamp(refresh, 0, 300);
                changed = true;
            }
            ImGui.TextWrapped("Enable repeat refresh only for a source you know is safe to read repeatedly. Return types do not establish whether a call changes plugin state.");
        }
        return changed;
    }

    private void DrawControls()
    {
        if (!definition.Controls.Any(control => control.PageId == selectedPageId))
        {
            ImGui.TextDisabled("Press Edit to add panels or buttons to this page.");
            return;
        }
        var hasPrevious = false;
        foreach (var control in definition.Controls)
        {
            if (control.PageId != selectedPageId) continue;
            if (control.SameLine && hasPrevious) ImGui.SameLine();
            using var id = ImRaii.PushId(control.Id.ToString("N"));
            var availableWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
            var wantedWidth = control.Width > 0 ? control.Width * ImGuiHelpers.GlobalScale
                : control.Kind == PluginBuilderControlKind.Info ? 240 * ImGuiHelpers.GlobalScale
                : ImGui.CalcTextSize(VisibleTitle(control.Label)).X + ImGui.GetStyle().FramePadding.X * 2;
            if (control.SameLine && hasPrevious && wantedWidth > availableWidth)
            {
                ImGui.NewLine();
                availableWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
            }
            var width = Math.Min(wantedWidth, availableWidth);
            using (ImRaii.Group())
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
                try { DrawControl(control, width); }
                finally { ImGui.PopTextWrapPos(); }
                if (control.Width > 0 || control.Kind == PluginBuilderControlKind.Info) ImGui.Dummy(new Vector2(width, 0));
            }
            hasPrevious = true;
        }
    }

    private void DrawControl(PluginBuilderControlDefinition control, float width)
    {
        var canUse = CanUse(control);
        if (control.Kind == PluginBuilderControlKind.Info)
        {
            ImGui.TextUnformatted(control.Label);
            using (ImRaii.Disabled(!canUse || !control.Source.CanRead))
                if (ImGui.SmallButton("Refresh##widget")) service.QueueRead(definition.Id, control.Id);
        }
        else
        {
            using (ImRaii.Disabled(!canUse || !control.Source.CanExecute))
                if (ImGui.Button(VisibleTitle(control.Label) + "##action", new Vector2(width, 0))) service.QueueAction(definition.Id, control.Id);
        }
        var sample = service.GetSample(definition.Id, control.Id);
        if (!canUse) ImGui.TextUnformatted("Unavailable. Edit to reconnect.");
        else if (control.Kind == PluginBuilderControlKind.Info)
            ImGui.TextUnformatted(sample.Text.Length > 0 ? sample.Text : "No value yet.");
        else if (sample.Status == "Invoked")
        {
            if (ImGui.IsItemHovered() && sample.Text.Length > 0) ImGui.SetTooltip(sample.Text);
        }
        if (sample.Status != "Observed" && sample.Status != "Invoked" && sample.Status != "Not read")
        {
            ImGui.TextUnformatted(sample.Status);
            if (control.Kind == PluginBuilderControlKind.Action && sample.Text.Length > 0) ImGui.TextUnformatted(sample.Text);
        }
        if (definition.ShowTimestamps && sample.TimestampUtc is { } timestamp)
            ImGui.TextDisabled("Updated " + timestamp.ToLocalTime().ToString("HH:mm:ss"));
    }

    private static string SourceLabel(PluginBuilderSource source)
    {
        var prefix = source.Kind switch
        {
            PluginBuilderSourceKind.IpcFunction or PluginBuilderSourceKind.IpcAction => "IPC",
            PluginBuilderSourceKind.Command => "Command",
            _ => "Reflection",
        };
        return prefix + " · " + source.Label;
    }

    private static string VisibleTitle(string value)
        => string.IsNullOrWhiteSpace(value) ? "Plugin Window" : value.Replace("#", "＃", StringComparison.Ordinal);

    private static PluginBuilderSource CloneSource(PluginBuilderSource source) => new()
    {
        Id = source.Id,
        Label = source.Label,
        Kind = source.Kind,
        PluginName = source.PluginName,
        ModuleVersionId = source.ModuleVersionId,
        Channel = source.Channel,
        DeclaringType = source.DeclaringType,
        MetadataToken = source.MetadataToken,
        Static = source.Static,
        FieldPath = new(source.FieldPath),
        ParameterTypes = new(source.ParameterTypes),
        ParameterNames = new(source.ParameterNames),
        ReturnType = source.ReturnType,
        CanRead = source.CanRead,
        CanExecute = source.CanExecute,
        UnsupportedReason = source.UnsupportedReason,
    };
}
