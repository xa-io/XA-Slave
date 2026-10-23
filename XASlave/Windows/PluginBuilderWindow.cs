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
        : base(VisibleTitle(definition.Title, localizeFallback: true) + "###XAPluginBuilder" + definition.Id.ToString("N"))
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
        WindowName = VisibleTitle(definition.Title, localizeFallback: true) + "###XAPluginBuilder" + definition.Id.ToString("N");
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
                using var pages = global::XASlave.Localization.UiNative.ComboScope(global::XASlave.Localization.UiLocale.Native("S0A30A815D67D7DD2"), CurrentPageTitle());
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

    private string CurrentPageTitle() => definition.Pages.FirstOrDefault(page => page.Id == selectedPageId)?.Title ?? global::XASlave.Localization.UiLocale.Get("SD4B1EA5708DD5329");

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
            if (global::XASlave.Localization.UiNative.Selectable(VisibleTitle(page.Title), page.Id == selectedPageId, display: VisibleTitle(page.Title, localizeFallback: true))) SelectPage(page.Id);
        }
    }

    private void DrawToolbar()
    {
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0E91610117029A62") + "##page"))) RefreshCurrentPage();
        var editLabel = editing ? global::XASlave.Localization.UiLocale.Native("S11A6767D5674C7E4") : global::XASlave.Localization.UiLocale.Native("S464C4FFD019E1E96");
        var editWidth = ImGui.CalcTextSize(global::XASlave.Localization.UiLocale.T(editLabel)).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X, rightEdge - editWidth));
        if (global::XASlave.Localization.UiNative.Button(editLabel + "##mode"))
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
        using var settings = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("S11EC92789D80A212"));
        if (!settings) return;
        var changed = false;
        var title = definition.Title;
        if (global::XASlave.Localization.UiNative.InputText(global::XASlave.Localization.UiLocale.Native("S534837B3B824387D"), ref title, 128)) { definition.Title = title; changed = true; }
        var openOnLoad = definition.OpenOnLoad;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SF9DA3CCF916B9B89"), ref openOnLoad)) { definition.OpenOnLoad = openOnLoad; changed = true; }
        var timestamps = definition.ShowTimestamps;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S7ECD3D57E2EB6FF5"), ref timestamps)) { definition.ShowTimestamps = timestamps; changed = true; }
        var sidebar = definition.ShowSidebar;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S835B3FBE5AF46E1F"), ref sidebar)) { definition.ShowSidebar = sidebar; changed = true; }
        if (sidebar)
        {
            var width = definition.SidebarWidth;
            if (global::XASlave.Localization.UiNative.InputInt(global::XASlave.Localization.UiLocale.Native("SC65C56269E5F3513"), ref width)) { definition.SidebarWidth = Math.Clamp(width, 100, 400); changed = true; }
        }
        if (changed) service.Save();
    }

    private void DrawPageSettings()
    {
        using var pages = global::XASlave.Localization.UiNative.TreeScope(global::XASlave.Localization.UiLocale.Native("S102CD0C01BBDCEA6"));
        if (!pages) return;
        var page = definition.Pages.FirstOrDefault(item => item.Id == selectedPageId);
        if (page != null)
        {
            var title = page.Title;
            if (global::XASlave.Localization.UiNative.InputText(global::XASlave.Localization.UiLocale.Native("S02660FFE391C0147"), ref title, 64)) { page.Title = title; service.Save(); }
            var index = definition.Pages.IndexOf(page);
            using (ImRaii.Disabled(index == 0))
                if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("SDFF3C0D94EB9F7B4"))) { definition.Pages.RemoveAt(index); definition.Pages.Insert(index - 1, page); service.Save(); }
            ImGui.SameLine();
            using (ImRaii.Disabled(index >= definition.Pages.Count - 1))
                if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S1B2E761405D39D89"))) { definition.Pages.Remove(page); definition.Pages.Insert(index + 1, page); service.Save(); }
            using (ImRaii.Disabled(definition.Pages.Count <= 1))
                if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S5C9F97796D9C9CA6")))
                {
                    var fallback = definition.Pages.First(item => item.Id != page.Id);
                    foreach (var control in definition.Controls.Where(item => item.PageId == page.Id)) control.PageId = fallback.Id;
                    if (draft?.PageId == page.Id) draft.PageId = fallback.Id;
                    definition.Pages.Remove(page);
                    SelectPage(fallback.Id);
                    service.Save();
                }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S9051B1C67127CF1C"));
        }
        ImGui.InputTextWithHint("##newPage", global::XASlave.Localization.UiLocale.Get("S2950D57D492E8986"), ref newPageTitle, 64);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newPageTitle) || definition.Pages.Count >= 32))
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SAE764A4A4D8BB1E9")))
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
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S8B4C9C06DC11AA32"))) BeginDraft(PluginBuilderControlKind.Info);
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S801BF80AB2A7F7E5"))) BeginDraft(PluginBuilderControlKind.Action);
        }
        if (definition.Controls.Count >= MaxControls) ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S759560112BAC8D2A"));
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
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("SF8227E4FA1686AB1", index + 1, ControlKindLabel(control.Kind), control.Label));
            var previous = definition.Controls.FindLastIndex(index, item => item.Id != control.Id && item.PageId == selectedPageId);
            var next = definition.Controls.FindIndex(index + 1, item => item.PageId == selectedPageId);
            using (ImRaii.Disabled(previous < 0))
                if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S55490A4BF3A8E8CB"))) { moveFrom = index; moveTo = previous; }
            ImGui.SameLine();
            using (ImRaii.Disabled(next < 0))
                if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("SB86D11AF79188E58"))) { moveFrom = index; moveTo = next; }
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("SC3812FC4ACB861D5"))) remove = index;
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S34A6A9B07B12EBB9"))) BeginDraft(control.Kind, control);
            if (!service.IsAvailable(control.Source)) ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S835960A78D1FBC12"));
            using (var settings = global::XASlave.Localization.UiNative.TreeScope((global::XASlave.Localization.UiLocale.Native("S74A883A037BC227F") + "##widget")))
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
        if (!definition.Controls.Any(control => control.PageId == selectedPageId) && draft == null) ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S2AA5A71BE8B4F8B0"));
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
        ImGui.TextUnformatted(replacingControlId != null ? global::XASlave.Localization.UiLocale.Get("SDF9B348B4FF0D9E5")
            : control.Kind == PluginBuilderControlKind.Info ? global::XASlave.Localization.UiLocale.Get("S8C52470B47D4B5AF") : global::XASlave.Localization.UiLocale.Get("S0E4F208986447A3B"));
        using (var plugins = global::XASlave.Localization.UiNative.ComboScope(global::XASlave.Localization.UiLocale.Native("S8716343FA721CC69"), sourcePlugin.Length > 0 ? sourcePlugin : global::XASlave.Localization.UiLocale.Get("SCF0666C237C15903")))
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
        if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S0986F19F7A29E4C0"))) service.RefreshPlugins();
        ImGui.SameLine();
        using (ImRaii.Disabled(service.IsScanning || sourcePlugin.Length == 0))
            if (global::XASlave.Localization.UiNative.SmallButton(global::XASlave.Localization.UiLocale.Native("S0BA39332052B93E6"))) service.Scan(sourcePlugin);
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
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF052AD7CD0C1049C"));
            ImGui.TextWrapped(service.LocalizedStatus);
        }
        else ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S92D51F4EC29A8A30"));
        ImGui.InputTextWithHint("##sourceFilter", global::XASlave.Localization.UiLocale.Get("S119699E93228EC34"), ref sourceFilter, 160);
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
                    if (global::XASlave.Localization.UiNative.Selectable(VisibleTitle(SourceLabel(source)), control.Source.Id == source.Id, display: VisibleTitle(SourceLabel(source, localize: true)))) SelectSource(control, source);
                if (!supported)
                    ImGui.TextDisabled(source.UnsupportedReason.Length > 0 ? source.LocalizedUnsupportedReason
                        : control.Kind == PluginBuilderControlKind.Info ? global::XASlave.Localization.UiLocale.Get("S624538466129957F") : global::XASlave.Localization.UiLocale.Get("SDEEBED90243387EF"));
            }
            if (matches == 0) ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SCC7ECBB106C96673"));
        }
        if (matches > 100) ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S3DE698B4264D1491", matches));
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
            if (global::XASlave.Localization.UiNative.Button(replacingControlId == null ? global::XASlave.Localization.UiLocale.Native("S8D9FDB7A7562AE2D") : global::XASlave.Localization.UiLocale.Native("S6C3109BEEE945E34")))
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
        if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3"))) { draft = null; replacingControlId = null; }
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
        if (global::XASlave.Localization.UiNative.InputText(global::XASlave.Localization.UiLocale.Native("S0E66373F45DCF3DD"), ref label, 128)) { control.Label = label; changed = true; }
        var page = definition.Pages.FirstOrDefault(item => item.Id == control.PageId);
        using (var pages = global::XASlave.Localization.UiNative.ComboScope(global::XASlave.Localization.UiLocale.Native("S0A30A815D67D7DD2"), page?.Title ?? global::XASlave.Localization.UiLocale.Get("SD4B1EA5708DD5329")))
        {
            if (pages)
                foreach (var candidate in definition.Pages)
                {
                    using var pageId = ImRaii.PushId(candidate.Id.ToString("N"));
                    if (global::XASlave.Localization.UiNative.Selectable(VisibleTitle(candidate.Title), candidate.Id == control.PageId, display: VisibleTitle(candidate.Title, localizeFallback: true)))
                    {
                        control.PageId = candidate.Id;
                        changed = true;
                    }
                }
        }
        var sameLine = control.SameLine;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SC44FBB83C74F6B66"), ref sameLine)) { control.SameLine = sameLine; changed = true; }
        var width = control.Width;
        if (global::XASlave.Localization.UiNative.InputInt(global::XASlave.Localization.UiLocale.Native("S0D0F9EB47BF21670"), ref width)) { control.Width = Math.Clamp(width, 0, 1200); changed = true; }
        ImGui.TextWrapped(SourceLabel(control.Source, localize: true));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S35328694F67B2F24", control.Source.PluginName));
        if (control.Source.UnsupportedReason.Length > 0) ImGui.TextWrapped(control.Source.LocalizedUnsupportedReason);
        if (control.Source.RequiresReflectionConfirmation)
        {
            var confirmed = control.ReflectionReadConfirmed;
            if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SD0D3BB3CB10E7F21"), ref confirmed))
            {
                control.ReflectionReadConfirmed = confirmed;
                changed = true;
            }
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SDD4254188B3031D2"));
        }
        for (var index = 0; index < control.Arguments.Count; index++)
        {
            using var id = ImRaii.PushId(index);
            var argument = control.Arguments[index];
            var name = index < control.Source.ParameterNames.Count ? control.Source.ParameterNames[index] : global::XASlave.Localization.UiLocale.FormatKey("SF629367797FE82E5", index + 1);
            var type = index < control.Source.ParameterTypes.Count ? control.Source.ParameterTypes[index] : string.Empty;
            var shortType = type.Split(',')[0];
            ImGui.TextWrapped(name + " : " + shortType);
            var binding = argument.UseCurrentCharacterId ? 1 : argument.UseCurrentCharacterName ? 2 : 0;
            var bindingNames = new[] { global::XASlave.Localization.UiLocale.Native("SE1B164E3CD3D0E5E"), global::XASlave.Localization.UiLocale.Native("S446403B3CC284FF9"), global::XASlave.Localization.UiLocale.Native("S45940AC335FC57E7") };
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(global::XASlave.Localization.UiLocale.Native("S980B10E3E81D06B1"), global::XASlave.Localization.UiLocale.T(bindingNames[binding])))
            if (combo)
                for (var option = 0; option < bindingNames.Length; option++)
                {
                    using var disabled = ImRaii.Disabled(option == 1 && shortType != "System.UInt64"
                        || option == 2 && shortType != "System.String");
                    if (global::XASlave.Localization.UiNative.Selectable(bindingNames[option], binding == option))
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
                    if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ref boolean)) { argument.Value = boolean ? "true" : "false"; changed = true; }
                }
                else if (global::XASlave.Localization.UiNative.InputText(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ref value, 2048)) { argument.Value = value; changed = true; }
            }
            else ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S0C3BFFCD5B609BA9"));
        }
        if (control.Kind == PluginBuilderControlKind.Info)
        {
            var path = control.MemberPath;
            if (global::XASlave.Localization.UiNative.InputText(global::XASlave.Localization.UiLocale.Native("S66D45366A4E2EEC9"), ref path, 256)) { control.MemberPath = path; changed = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S23C309F419F94EBF"));
            var refresh = control.AutoRefreshSeconds;
            if (global::XASlave.Localization.UiNative.InputInt(global::XASlave.Localization.UiLocale.Native("S22CDC0C4E429EE54"), ref refresh))
            {
                control.AutoRefreshSeconds = Math.Clamp(refresh, 0, 300);
                changed = true;
            }
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S3275C4CACB8A2C39"));
        }
        return changed;
    }

    private void DrawControls()
    {
        if (!definition.Controls.Any(control => control.PageId == selectedPageId))
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SCF2E5AFC3AAE0B11"));
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
                : ImGui.CalcTextSize(VisibleTitle(control.Label, localizeFallback: true)).X + ImGui.GetStyle().FramePadding.X * 2;
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
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S0E91610117029A62") + "##widget"))) service.QueueRead(definition.Id, control.Id);
        }
        else
        {
            using (ImRaii.Disabled(!canUse || !control.Source.CanExecute))
                if (global::XASlave.Localization.UiNative.Button(VisibleTitle(control.Label) + "##action", new Vector2(width, 0), display: VisibleTitle(control.Label, localizeFallback: true))) service.QueueAction(definition.Id, control.Id);
        }
        var sample = service.GetSample(definition.Id, control.Id);
        if (!canUse) ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.Get("S253FA2B06264A582"));
        else if (control.Kind == PluginBuilderControlKind.Info)
            ImGui.TextUnformatted(sample.Text.Length > 0 ? sample.LocalizedText : global::XASlave.Localization.UiLocale.Get("SE2843C9ACE38E933"));
        else if (sample.Status == "Invoked")
        {
            if (ImGui.IsItemHovered() && sample.Text.Length > 0) ImGui.SetTooltip(sample.LocalizedText);
        }
        if (sample.Status != "Observed" && sample.Status != "Invoked" && sample.Status != "Not read")
        {
            ImGui.TextUnformatted(sample.LocalizedStatus);
            if (control.Kind == PluginBuilderControlKind.Action && sample.Text.Length > 0) ImGui.TextUnformatted(sample.LocalizedText);
        }
        if (definition.ShowTimestamps && sample.TimestampUtc is { } timestamp)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S38A186BB32684C56", timestamp.ToLocalTime().ToString("HH:mm:ss")));
    }

    private static string SourceLabel(PluginBuilderSource source, bool localize = false)
    {
        var prefix = source.Kind switch
        {
            PluginBuilderSourceKind.IpcFunction or PluginBuilderSourceKind.IpcAction => global::XASlave.Localization.UiLocale.Native("S331B8EE48FD11AF7"),
            PluginBuilderSourceKind.Command => global::XASlave.Localization.UiLocale.Native("S713166971D730F81"),
            _ => global::XASlave.Localization.UiLocale.Native("S7538C2135B030E4E"),
        };
        return (localize ? global::XASlave.Localization.UiLocale.T(prefix) : prefix) + " · " + source.Label;
    }

    private static string VisibleTitle(string value, bool localizeFallback = false)
        => string.IsNullOrWhiteSpace(value) ? (localizeFallback ? global::XASlave.Localization.UiLocale.Get("SAF04A4017BD7799F") : global::XASlave.Localization.UiLocale.Native("SAF04A4017BD7799F")) : value.Replace("#", "＃", StringComparison.Ordinal);

    private static string ControlKindLabel(PluginBuilderControlKind kind) => kind switch
    {
        PluginBuilderControlKind.Info => global::XASlave.Localization.UiLocale.Get("S170322A32F3C35B2"),
        PluginBuilderControlKind.Action => global::XASlave.Localization.UiLocale.Get("S64CFF1319D2FD2CB"),
        _ => kind.ToString(),
    };

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
        UnsupportedMessage = source.UnsupportedMessage,
    };
}
