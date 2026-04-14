using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private enum ToonModsSection
    {
        GameMods,
        GraphicMods,
        PlayerMods,
        IllegalMods,
        PluginMods,
    }

    private static readonly string[] AutoExpertDeliveryPageLabels = { "Supply Missions", "Provisioning Missions", "Expert Delivery" };
    private string toonModsSearchText = string.Empty;
    private bool toonModsShowOnlyEnabled;
    private string toonModsSavedListName = string.Empty;
    private string toonModsStatus = string.Empty;
    private DateTime toonModsStatusExpiryUtc = DateTime.MinValue;
    private bool toonModsStatusIsError;
    private int xaModsCustomResolutionWidth = 500;
    private int xaModsCustomResolutionHeight = 345;
    private static readonly JsonSerializerOptions toonModsListJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class ToonModsListPackage
    {
        public int SchemaVersion { get; set; } = 1;
        public string ListId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; }
        public List<string> ModKeys { get; set; } = new();
    }

    private bool GetToonModsSectionExpanded(ToonModsSection section)
    {
        return section switch
        {
            ToonModsSection.GameMods => plugin.Configuration.ToonModsGameModsExpanded,
            ToonModsSection.GraphicMods => plugin.Configuration.ToonModsGraphicModsExpanded,
            ToonModsSection.PlayerMods => plugin.Configuration.ToonModsPlayerModsExpanded,
            ToonModsSection.IllegalMods => plugin.Configuration.ToonModsIllegalModsExpanded,
            ToonModsSection.PluginMods => plugin.Configuration.ToonModsPluginModsExpanded,
            _ => true,
        };
    }

    private void SetToonModsSectionExpanded(ToonModsSection section, bool expanded)
    {
        switch (section)
        {
            case ToonModsSection.GameMods:
                if (plugin.Configuration.ToonModsGameModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsGameModsExpanded = expanded;
                break;
            case ToonModsSection.GraphicMods:
                if (plugin.Configuration.ToonModsGraphicModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsGraphicModsExpanded = expanded;
                break;
            case ToonModsSection.PlayerMods:
                if (plugin.Configuration.ToonModsPlayerModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsPlayerModsExpanded = expanded;
                break;
            case ToonModsSection.IllegalMods:
                if (plugin.Configuration.ToonModsIllegalModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsIllegalModsExpanded = expanded;
                break;
            case ToonModsSection.PluginMods:
                if (plugin.Configuration.ToonModsPluginModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsPluginModsExpanded = expanded;
                break;
            default:
                return;
        }

        plugin.Configuration.Save();
    }

    private void DrawXAModsTask()
    {
        var configuration = plugin.Configuration;
        var featureEntries = new List<(ToonModsSection Section, string Label, Action Draw)>();
        var toonModDefinitions = new List<(string Key, Func<bool> GetCurrent, Func<bool, bool> Apply, Action<bool> Store)>();

        void SaveConfiguration()
        {
            configuration.Save();
        }

        void SetToonModsStatus(string message, bool isError = false)
        {
            toonModsStatus = message;
            toonModsStatusIsError = isError;
            toonModsStatusExpiryUtc = DateTime.UtcNow.AddSeconds(8);
        }

        void DrawSectionHeader(string text)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), text);
            ImGui.Separator();
            ImGui.Spacing();
        }

        void DrawHelpMarker(string helpText)
        {
            ImGui.TextDisabled("(?)");
            if (!ImGui.IsItemHovered())
                return;

            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(460f);
            ImGui.TextUnformatted(helpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        void DrawWarningText(string warningText)
        {
            if (string.IsNullOrWhiteSpace(warningText))
                return;

            ImGui.PushTextWrapPos(460f);
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.25f, 1.0f), warningText);
            ImGui.PopTextWrapPos();
        }

        Vector4 GetSpecialRenderBackgroundColor()
        {
            return new Vector4(
                configuration.SpecialRenderModeBackgroundColorR,
                configuration.SpecialRenderModeBackgroundColorG,
                configuration.SpecialRenderModeBackgroundColorB,
                configuration.SpecialRenderModeBackgroundColorA);
        }

        void RestoreSpecialRenderModes()
        {
            var allUiFlags = UIModule.UiFlags.ActionBars
                | UIModule.UiFlags.Chat
                | UIModule.UiFlags.Hud
                | UIModule.UiFlags.Nameplates
                | UIModule.UiFlags.TargetInfo
                | UIModule.UiFlags.Shortcuts;

            plugin.SystemWindowMods.SetSpecialRenderWorldHidden(false, GetSpecialRenderBackgroundColor());
            plugin.SystemWindowMods.SetSpecialRenderUiVisibility(allUiFlags, true);
        }

        bool SetSpecialRenderModesEnabled(bool value)
        {
            if (!value)
                RestoreSpecialRenderModes();

            return value;
        }

        void ApplyBackgroundRenderingConfiguration()
        {
            plugin.SystemWindowMods.SetDisableBackgroundRenderingOnlyWhenMinimized(configuration.DisableBackgroundGameRenderingOnlyWhenMinimized);
            plugin.SystemWindowMods.SetDisableBackgroundRenderingDisableWhenArMultiIsOn(configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn);
        }

        void ApplyAutoHideGameObjectsConfiguration()
        {
            plugin.AutoHideGameObjects.ApplyConfiguration(
                configuration.AutoHideGameObjectsHidePlayer,
                configuration.AutoHideGameObjectsHideUnimportantEnpc,
                configuration.AutoHideGameObjectsHidePet,
                configuration.AutoHideGameObjectsHideChocobo,
                configuration.AutoHideGameObjectsDisableInDuties,
                configuration.AutoHideGameObjectsDisableInIslandSanctuary,
                configuration.AutoHideGameObjectsUseOccultCrescentRules);
        }

        void ApplyPlayerSearchConfiguration()
        {
            plugin.PlayerSearchContextMenu.ApplyConfiguration(
                configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled,
                configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled,
                configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled,
                configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled);
        }

        void ApplyExpertDeliveryConfiguration()
        {
            plugin.AutoUnlockExpertDelivery.ApplyConfiguration(
                configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen,
                configuration.AutoUnlockExpertDeliveryDefaultPage,
                configuration.AutoUnlockExpertDeliverySkipHq,
                configuration.AutoUnlockExpertDeliverySkipMateria,
                configuration.AutoUnlockExpertDeliveryIgnoreSealCap);
        }

        void ApplyTradeRefusalConfiguration()
        {
            plugin.AutoRefuseTrade.ApplyConfiguration(
                configuration.AutoRefuseTradeShowNotification,
                configuration.AutoRefuseTradeSendEcho,
                configuration.AutoRefuseTradeExtraCommands);
        }

        void ApplySightDistanceConfiguration()
        {
            plugin.SightDistance.ApplyConfiguration(
                configuration.CustomSightDistanceMaxDistance,
                configuration.CustomSightDistanceMinDistance,
                configuration.CustomSightDistanceMaxRotation,
                configuration.CustomSightDistanceMinRotation,
                configuration.CustomSightDistanceMaxFoV,
                configuration.CustomSightDistanceMinFoV,
                configuration.CustomSightDistanceFoV,
                configuration.CustomSightDistanceIgnoreCollision);
        }

        void ApplyInfiniteSprintConfiguration()
        {
            configuration.InfiniteSprintDelaySeconds = PlayerModsService.ClampInfiniteSprintDelaySeconds(configuration.InfiniteSprintDelaySeconds);
            plugin.PlayerMods.ApplyInfiniteSprintConfiguration(configuration.InfiniteSprintDelaySeconds);
        }

        void DrawFeatureToggle(
            string label,
            bool currentValue,
            Func<bool, bool> apply,
            Action<bool> store,
            string description,
            string helpText,
            string status,
            string? warningText = null,
            bool requireCtrlShiftToEnable = false,
            System.Action? drawOptions = null)
        {
            var value = currentValue;
            var modifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            var toggled = false;

            if (requireCtrlShiftToEnable && !currentValue && !modifierHeld)
            {
                ImGui.BeginDisabled();
                ImGui.Checkbox(label, ref value);
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(320f);
                    ImGui.TextUnformatted("Hold CTRL + SHIFT to allow changing.");
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
            }
            else
            {
                toggled = ImGui.Checkbox(label, ref value);
            }

            if (toggled)
            {
                var applied = apply(value);
                store(applied);
                SaveConfiguration();
                value = applied;
            }

            ImGui.SameLine(0f, 6f);
            DrawHelpMarker(helpText);

            ImGui.TextDisabled(description);
            ImGui.TextDisabled($"Status: {status}");
            DrawWarningText(warningText ?? string.Empty);

            if (value && drawOptions != null)
            {
                ImGui.Indent();
                drawOptions();
                ImGui.Unindent();
            }

            ImGui.Spacing();
        }

        bool MatchesToonModsSearch(string label, string description, string helpText, string[]? extraTerms = null)
        {
            if (string.IsNullOrWhiteSpace(toonModsSearchText))
                return true;

            var queryTerms = toonModsSearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (queryTerms.Length == 0)
                return true;

            var haystack = $"{label}\n{description}\n{helpText}";
            if (extraTerms is { Length: > 0 })
                haystack = $"{haystack}\n{string.Join('\n', extraTerms)}";

            foreach (var term in queryTerms)
            {
                if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        void AddFeatureEntry(
            ToonModsSection section,
            string label,
            bool currentValue,
            Func<bool, bool> apply,
            Action<bool> store,
            string description,
            string helpText,
            string status,
            string? warningText = null,
            bool requireCtrlShiftToEnable = false,
            string[]? searchTerms = null,
            Action? drawOptions = null)
        {
            if (toonModsShowOnlyEnabled && !currentValue)
                return;

            if (!MatchesToonModsSearch(label, description, helpText, searchTerms))
                return;

            featureEntries.Add((section, label, () => DrawFeatureToggle(label, currentValue, apply, store, description, helpText, status, warningText, requireCtrlShiftToEnable, drawOptions)));
        }

        void AddSavedFeatureEntry(
            ToonModsSection section,
            string key,
            string label,
            Func<bool> getCurrent,
            Func<bool, bool> apply,
            Action<bool> store,
            string description,
            string helpText,
            string status,
            string? warningText = null,
            bool requireCtrlShiftToEnable = false,
            string[]? searchTerms = null,
            Action? drawOptions = null)
        {
            toonModDefinitions.Add((key, getCurrent, apply, store));
            AddFeatureEntry(section, label, getCurrent(), apply, store, description, helpText, status, warningText, requireCtrlShiftToEnable, searchTerms, drawOptions);
        }

        List<string> GetCurrentToonModKeys()
        {
            return toonModDefinitions
                .Where(entry => entry.GetCurrent())
                .Select(entry => entry.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        void SaveCurrentToonModsList()
        {
            var name = toonModsSavedListName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                SetToonModsStatus("XA Mods: enter a list name before saving.", true);
                return;
            }

            var modKeys = GetCurrentToonModKeys();
            var saved = configuration.ToonModsSavedLists.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (saved == null)
            {
                configuration.ToonModsSavedLists.Add(new ToonModSavedList
                {
                    Name = name,
                    ModKeys = modKeys,
                });
            }
            else
            {
                saved.Name = name;
                saved.ModKeys = modKeys;
            }

            SaveConfiguration();
            toonModsSavedListName = name;
            SetToonModsStatus($"XA Mods: saved list '{name}' ({modKeys.Count} mods).");
        }

        void ApplyToonModsList(string title, IEnumerable<string> modKeys)
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "XA Mods Selection" : title.Trim();
            var requestedKeys = modKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var definitionsByKey = toonModDefinitions.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

            DisableAllMods();

            if (requestedKeys.Count == 0)
            {
                toonModsSavedListName = safeTitle;
                SetToonModsStatus($"XA Mods: loaded '{safeTitle}' with all mods disabled.");
                return;
            }

            var appliedCount = 0;
            var unavailableCount = 0;
            var unknownCount = 0;

            foreach (var key in requestedKeys)
            {
                if (!definitionsByKey.TryGetValue(key, out var definition))
                {
                    unknownCount++;
                    continue;
                }

                var applied = definition.Apply(true);
                definition.Store(applied);
                if (applied)
                    appliedCount++;
                else
                    unavailableCount++;
            }

            SaveConfiguration();
            toonModsSavedListName = safeTitle;
            SetToonModsStatus(
                unknownCount > 0 || unavailableCount > 0
                    ? $"XA Mods: loaded '{safeTitle}' ({appliedCount} applied, {unavailableCount} unavailable, {unknownCount} unknown)."
                    : $"XA Mods: loaded '{safeTitle}' ({appliedCount} mods).",
                unknownCount > 0 || unavailableCount > 0);
        }

        void ExportCurrentToonModsList()
        {
            var package = new ToonModsListPackage
            {
                ListId = Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(toonModsSavedListName) ? "XA Mods Selection" : toonModsSavedListName.Trim(),
                ExportedAtUtc = DateTime.UtcNow,
                ModKeys = GetCurrentToonModKeys(),
            };

            ImGui.SetClipboardText(JsonSerializer.Serialize(package, toonModsListJsonOptions));
            SetToonModsStatus($"XA Mods: copied '{package.Title}' JSON ({package.ModKeys.Count} mods) to clipboard.");
        }

        void ImportCurrentToonModsList()
        {
            if (!TryImportToonModsList(ImGui.GetClipboardText(), out var package, out var message))
            {
                SetToonModsStatus(message, true);
                return;
            }

            ApplyToonModsList(package.Title, package.ModKeys);
        }

        bool TryImportToonModsList(string clipboardText, out ToonModsListPackage package, out string message)
        {
            package = new ToonModsListPackage();

            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                message = "XA Mods: clipboard data not supported.";
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(clipboardText);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    message = "XA Mods: clipboard data not supported.";
                    return false;
                }

                if (TryGetToonModsJsonProperty(root, "schemaVersion", out var schemaVersionElement)
                    && schemaVersionElement.ValueKind == JsonValueKind.Number
                    && schemaVersionElement.TryGetInt32(out var schemaVersion))
                {
                    package.SchemaVersion = schemaVersion;
                }

                if (TryGetToonModsJsonProperty(root, "listId", out var listIdElement) && listIdElement.ValueKind == JsonValueKind.String)
                    package.ListId = listIdElement.GetString() ?? string.Empty;

                if (TryGetToonModsJsonProperty(root, "title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                    package.Title = titleElement.GetString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(package.Title)
                    && TryGetToonModsJsonProperty(root, "name", out var nameElement)
                    && nameElement.ValueKind == JsonValueKind.String)
                {
                    package.Title = nameElement.GetString() ?? string.Empty;
                }

                if (!TryGetToonModsJsonProperty(root, "modKeys", out var modKeysElement) || modKeysElement.ValueKind != JsonValueKind.Array)
                {
                    message = "XA Mods: clipboard data not supported.";
                    return false;
                }

                package.ModKeys = modKeysElement
                    .EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString() ?? string.Empty)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .ToList();

                if (string.IsNullOrWhiteSpace(package.Title))
                    package.Title = "Imported XA Mods";

                message = string.Empty;
                return true;
            }
            catch
            {
                message = "XA Mods: clipboard data not supported.";
                return false;
            }
        }

        bool TryGetToonModsJsonProperty(JsonElement root, string name, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        void DrawToonModsSaveListPopup()
        {
            if (!ImGui.BeginPopup("ToonModsSaveListPopup"))
                return;

            ImGui.SetNextItemWidth(220f);
            ImGui.InputTextWithHint("##ToonModsSaveListName", "List name...", ref toonModsSavedListName, 128);
            ImGui.SameLine();
            if (ImGui.Button("Save Current##ToonModsSaveCurrent"))
                SaveCurrentToonModsList();

            ImGui.EndPopup();
        }

        void DrawToonModsLoadListPopup()
        {
            if (!ImGui.BeginPopup("ToonModsLoadListPopup"))
                return;

            if (configuration.ToonModsSavedLists.Count == 0)
            {
                ImGui.TextDisabled("No saved lists.");
                ImGui.EndPopup();
                return;
            }

            foreach (var saved in configuration.ToonModsSavedLists.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList())
            {
                ImGui.TextUnformatted(saved.Name);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Load##ToonModsLoad{saved.Name}"))
                    ApplyToonModsList(saved.Name, saved.ModKeys);

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##ToonModsDelete{saved.Name}"))
                {
                    configuration.ToonModsSavedLists.RemoveAll(entry => entry.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
                    SaveConfiguration();
                    SetToonModsStatus($"XA Mods: deleted list '{saved.Name}'.");
                    ImGui.PopStyleColor();
                    break;
                }

                ImGui.PopStyleColor();
            }

            ImGui.EndPopup();
        }

        List<(ToonModsSection Section, string Label, Action Draw)> GetSortedSectionEntries(ToonModsSection section)
        {
            return featureEntries
                .Where(entry => entry.Section == section)
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        void DrawModSection(ToonModsSection section, string title)
        {
            var sectionEntries = GetSortedSectionEntries(section);
            var visibleCount = sectionEntries.Count;
            if (visibleCount == 0)
                return;

            var searchActive = !string.IsNullOrWhiteSpace(toonModsSearchText);
            if (searchActive)
            {
                DrawSectionHeader($"{title} ({visibleCount})");
                foreach (var entry in sectionEntries)
                    entry.Draw();

                return;
            }

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.9f, 1.0f, 1.0f));
            ImGui.SetNextItemOpen(GetToonModsSectionExpanded(section), ImGuiCond.Always);
            var isOpen = ImGui.CollapsingHeader(title);
            ImGui.PopStyleColor();

            if (isOpen != GetToonModsSectionExpanded(section))
                SetToonModsSectionExpanded(section, isOpen);

            if (!isOpen)
                return;

            ImGui.Indent();
            foreach (var entry in sectionEntries)
                entry.Draw();

            ImGui.Unindent();
        }

        void DisableFeature(Func<bool, bool> apply, Action<bool> store)
        {
            store(apply(false));
        }

        void DisableAllMods()
        {
            foreach (var definition in toonModDefinitions)
                DisableFeature(definition.Apply, definition.Store);

            SaveConfiguration();
        }

        void DrawBackgroundRenderingOptions()
        {
            var onlyWhenMinimized = configuration.DisableBackgroundGameRenderingOnlyWhenMinimized;
            if (ImGui.Checkbox("Only while minimized##DisableBackgroundGameRendering", ref onlyWhenMinimized))
            {
                configuration.DisableBackgroundGameRenderingOnlyWhenMinimized = onlyWhenMinimized;
                ApplyBackgroundRenderingConfiguration();
                SaveConfiguration();
            }

            var disableWhenArMultiIsOn = configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn;
            if (ImGui.Checkbox("Disable when AR Multi is on##DisableBackgroundGameRendering", ref disableWhenArMultiIsOn))
            {
                configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn = disableWhenArMultiIsOn;
                ApplyBackgroundRenderingConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(onlyWhenMinimized
                ? "Alt-tabbed rendering stays active until the window is minimized."
                : "Any inactive window state can pause the DX11 render tick.");
            ImGui.TextDisabled(disableWhenArMultiIsOn
                ? "If AutoRetainer multi-mode is enabled, the render hooks stay armed but frames continue so background processing is not throttled."
                : "AutoRetainer multi-mode does not override the background rendering pause.");
        }

        void DrawAutoHideGameObjectsOptions()
        {
            var changed = false;

            var hidePlayer = configuration.AutoHideGameObjectsHidePlayer;
            if (ImGui.Checkbox("Hide players##AutoHideGameObjects", ref hidePlayer))
            {
                configuration.AutoHideGameObjectsHidePlayer = hidePlayer;
                changed = true;
            }

            var hideUnimportantEnpc = configuration.AutoHideGameObjectsHideUnimportantEnpc;
            if (ImGui.Checkbox("Hide unimportant NPCs##AutoHideGameObjects", ref hideUnimportantEnpc))
            {
                configuration.AutoHideGameObjectsHideUnimportantEnpc = hideUnimportantEnpc;
                changed = true;
            }

            var hidePet = configuration.AutoHideGameObjectsHidePet;
            if (ImGui.Checkbox("Hide pets##AutoHideGameObjects", ref hidePet))
            {
                configuration.AutoHideGameObjectsHidePet = hidePet;
                changed = true;
            }

            var hideChocobo = configuration.AutoHideGameObjectsHideChocobo;
            if (ImGui.Checkbox("Hide chocobos##AutoHideGameObjects", ref hideChocobo))
            {
                configuration.AutoHideGameObjectsHideChocobo = hideChocobo;
                changed = true;
            }

            var disableInDuties = configuration.AutoHideGameObjectsDisableInDuties;
            if (ImGui.Checkbox("Disable in duties##AutoHideGameObjects", ref disableInDuties))
            {
                configuration.AutoHideGameObjectsDisableInDuties = disableInDuties;
                changed = true;
            }

            var disableInIslandSanctuary = configuration.AutoHideGameObjectsDisableInIslandSanctuary;
            if (ImGui.Checkbox("Disable in Island Sanctuary##AutoHideGameObjects", ref disableInIslandSanctuary))
            {
                configuration.AutoHideGameObjectsDisableInIslandSanctuary = disableInIslandSanctuary;
                changed = true;
            }

            var useOccultCrescentRules = configuration.AutoHideGameObjectsUseOccultCrescentRules;
            if (ImGui.Checkbox("Use Occult Crescent rules##AutoHideGameObjects", ref useOccultCrescentRules))
            {
                configuration.AutoHideGameObjectsUseOccultCrescentRules = useOccultCrescentRules;
                changed = true;
            }

            if (changed)
            {
                ApplyAutoHideGameObjectsConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled("Friends, party members, alliance members, icon-marked objects, and your own actor stay visible.");
            ImGui.TextDisabled("Occult Crescent rules keep your current target and dead players visible, then start hiding additional players after the visible count grows.");
        }

        void DrawPlayerSearchOptions()
        {
            var configChanged = false;

            var ffLogsEnabled = configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled;
            if (ImGui.Checkbox("FFLogs##ExpandedPlayerSearch", ref ffLogsEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled = ffLogsEnabled;
                configChanged = true;
            }

            var lodestoneEnabled = configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled;
            if (ImGui.Checkbox("Lodestone##ExpandedPlayerSearch", ref lodestoneEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled = lodestoneEnabled;
                configChanged = true;
            }

            var lalachievementsEnabled = configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled;
            if (ImGui.Checkbox("Lalachievements##ExpandedPlayerSearch", ref lalachievementsEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled = lalachievementsEnabled;
                configChanged = true;
            }

            var openAllEnabled = configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled;
            if (ImGui.Checkbox("Show Open All Enabled##ExpandedPlayerSearch", ref openAllEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled = openAllEnabled;
                configChanged = true;
            }

            if (configChanged)
            {
                ApplyPlayerSearchConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled("At least one provider must stay enabled for the search submenu to appear.");
        }

        void DrawUiVisibilityButtons(string id, string hideLabel, string showLabel, UIModule.UiFlags flags)
        {
            if (ImGui.Button($"{hideLabel}##{id}"))
                plugin.SystemWindowMods.SetSpecialRenderUiVisibility(flags, false);

            ImGui.SameLine();
            if (ImGui.Button($"{showLabel}##{id}"))
                plugin.SystemWindowMods.SetSpecialRenderUiVisibility(flags, true);
        }

        void DrawSpecialRenderModeTools()
        {
            var backgroundColor = GetSpecialRenderBackgroundColor();

            if (ImGui.ColorEdit4("Background color##SpecialRenderModes", ref backgroundColor))
            {
                configuration.SpecialRenderModeBackgroundColorR = backgroundColor.X;
                configuration.SpecialRenderModeBackgroundColorG = backgroundColor.Y;
                configuration.SpecialRenderModeBackgroundColorB = backgroundColor.Z;
                configuration.SpecialRenderModeBackgroundColorA = backgroundColor.W;
                SaveConfiguration();
            }

            if (ImGui.Button("Hide world / keep addons##SpecialRenderModes"))
                plugin.SystemWindowMods.SetSpecialRenderWorldHidden(true, backgroundColor);

            ImGui.SameLine();
            if (ImGui.Button("Restore world##SpecialRenderModes"))
                plugin.SystemWindowMods.SetSpecialRenderWorldHidden(false, backgroundColor);

            ImGui.SameLine();
            if (ImGui.Button("Restore all##SpecialRenderModes"))
                RestoreSpecialRenderModes();

            ImGui.TextDisabled("The background color is used when the world-render fade is forced on.");

            DrawUiVisibilityButtons(
                "SpecialRenderHideAddonsKeepNameplates",
                "Hide addons / keep nameplates",
                "Restore",
                UIModule.UiFlags.ActionBars | UIModule.UiFlags.Chat | UIModule.UiFlags.Hud | UIModule.UiFlags.TargetInfo | UIModule.UiFlags.Shortcuts);

            DrawUiVisibilityButtons(
                "SpecialRenderHideAddonsKeepChat",
                "Hide addons / keep chat",
                "Restore",
                UIModule.UiFlags.ActionBars | UIModule.UiFlags.Nameplates | UIModule.UiFlags.Hud | UIModule.UiFlags.TargetInfo | UIModule.UiFlags.Shortcuts);

            DrawUiVisibilityButtons("SpecialRenderHideChat", "Hide chat log", "Restore", UIModule.UiFlags.Chat);
            DrawUiVisibilityButtons("SpecialRenderHideActionBars", "Hide action bars", "Restore", UIModule.UiFlags.ActionBars);
            DrawUiVisibilityButtons("SpecialRenderHideTargetInfo", "Hide target info", "Restore", UIModule.UiFlags.TargetInfo);
            DrawUiVisibilityButtons("SpecialRenderHideNameplates", "Hide nameplates", "Restore", UIModule.UiFlags.Nameplates);
        }

        void AddCustomResolutionPreset()
        {
            if (!SystemWindowModsService.TryNormalizeCustomResolution(
                    xaModsCustomResolutionWidth,
                    xaModsCustomResolutionHeight,
                    out var width,
                    out var height,
                    out var message))
            {
                SetToonModsStatus($"XA Mods: {message}", true);
                return;
            }

            if (configuration.CustomResolutionPresets.Any(entry => entry.Width == width && entry.Height == height))
            {
                SetToonModsStatus($"XA Mods: custom resolution button {width}x{height} already exists.", true);
                return;
            }

            configuration.CustomResolutionPresets.Add(new XAModResolutionPreset
            {
                Width = width,
                Height = height,
            });
            SaveConfiguration();
            SetToonModsStatus($"XA Mods: added custom resolution button {width}x{height}.");
        }

        void ApplyCustomResolutionFromUi(int width, int height)
        {
            if (plugin.SystemWindowMods.TryApplyCustomResolution(width, height, out var message))
                SetToonModsStatus($"XA Mods: {message}");
            else
                SetToonModsStatus($"XA Mods: {message}", true);
        }

        void DrawCustomResolutionTools()
        {
            if (ImGui.Button("500x345##CustomResolutionExample"))
                ApplyCustomResolutionFromUi(500, 345);

            ImGui.SameLine();
            ImGui.TextDisabled("Example");
            ImGui.TextDisabled("Command: /xa res 500x345");

            var width = xaModsCustomResolutionWidth;
            if (ImGui.InputInt("Width##CustomResolutionPresetWidth", ref width))
                xaModsCustomResolutionWidth = Math.Max(1, width);

            var height = xaModsCustomResolutionHeight;
            if (ImGui.InputInt("Height##CustomResolutionPresetHeight", ref height))
                xaModsCustomResolutionHeight = Math.Max(1, height);

            if (ImGui.Button("Apply typed size##CustomResolutionApply"))
                ApplyCustomResolutionFromUi(xaModsCustomResolutionWidth, xaModsCustomResolutionHeight);

            ImGui.SameLine();
            if (ImGui.Button("Add button##CustomResolutionAdd"))
                AddCustomResolutionPreset();

            if (configuration.CustomResolutionPresets.Count == 0)
            {
                ImGui.TextDisabled("No saved custom resolution buttons yet.");
                return;
            }

            for (var index = 0; index < configuration.CustomResolutionPresets.Count; index++)
            {
                var preset = configuration.CustomResolutionPresets[index];
                var label = $"{preset.Width}x{preset.Height}";

                if (ImGui.Button($"{label}##CustomResolutionPreset{index}"))
                    ApplyCustomResolutionFromUi(preset.Width, preset.Height);

                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##CustomResolutionPresetDelete{index}"))
                {
                    configuration.CustomResolutionPresets.RemoveAt(index);
                    SaveConfiguration();
                    SetToonModsStatus($"XA Mods: removed custom resolution button {label}.");
                    break;
                }
            }
        }

        void DrawLowResolutionOptions()
        {
            var scale = configuration.LowResolutionScale;
            if (ImGui.SliderFloat("3D resolution scale##LowResolution", ref scale, 0.01f, 1.00f, "%.2f"))
            {
                configuration.LowResolutionScale = scale;
                plugin.SystemWindowMods.ApplyLowResolutionConfiguration(scale);
                SaveConfiguration();
            }

            ImGui.TextDisabled("Uses the game's Standard / AMD FSR runtime scale path. If DLSS is active, XA temporarily switches to AMD FSR and restores the previous scaler when disabled. No sharpen override is applied.");
        }

        void DrawSightDistanceSlider(string label, string id, float currentValue, float minimumValue, float maximumValue, string format, Action<float> store)
        {
            var value = currentValue;
            if (!ImGui.SliderFloat($"{label}##{id}", ref value, minimumValue, maximumValue, format))
                return;

            store(value);
            ApplySightDistanceConfiguration();
            SaveConfiguration();
        }

        void ResetSightDistanceDefaults()
        {
            configuration.CustomSightDistanceMaxDistance = 80f;
            configuration.CustomSightDistanceMinDistance = 1.5f;
            configuration.CustomSightDistanceMaxRotation = 1.569f;
            configuration.CustomSightDistanceMinRotation = -1.483530f;
            configuration.CustomSightDistanceMaxFoV = 0.78f;
            configuration.CustomSightDistanceMinFoV = 0.69f;
            configuration.CustomSightDistanceFoV = 0.78f;
            configuration.CustomSightDistanceIgnoreCollision = true;
            ApplySightDistanceConfiguration();
            SaveConfiguration();
        }

        void DrawSightDistanceOptions()
        {
            DrawSightDistanceSlider(
                "Max distance",
                "CustomSightDistanceMaxDistance",
                configuration.CustomSightDistanceMaxDistance,
                Math.Max(configuration.CustomSightDistanceMinDistance, 1f),
                80f,
                "%.1f",
                value => configuration.CustomSightDistanceMaxDistance = value);
            DrawSightDistanceSlider(
                "Min distance",
                "CustomSightDistanceMinDistance",
                configuration.CustomSightDistanceMinDistance,
                0f,
                configuration.CustomSightDistanceMaxDistance,
                "%.1f",
                value => configuration.CustomSightDistanceMinDistance = value);
            DrawSightDistanceSlider(
                "Max rotation",
                "CustomSightDistanceMaxRotation",
                configuration.CustomSightDistanceMaxRotation,
                configuration.CustomSightDistanceMinRotation,
                1.569f,
                "%.3f",
                value => configuration.CustomSightDistanceMaxRotation = value);
            DrawSightDistanceSlider(
                "Min rotation",
                "CustomSightDistanceMinRotation",
                configuration.CustomSightDistanceMinRotation,
                -1.569f,
                configuration.CustomSightDistanceMaxRotation,
                "%.3f",
                value => configuration.CustomSightDistanceMinRotation = value);
            DrawSightDistanceSlider(
                "Max FoV",
                "CustomSightDistanceMaxFoV",
                configuration.CustomSightDistanceMaxFoV,
                configuration.CustomSightDistanceMinFoV,
                3f,
                "%.3f",
                value => configuration.CustomSightDistanceMaxFoV = value);
            DrawSightDistanceSlider(
                "Min FoV",
                "CustomSightDistanceMinFoV",
                configuration.CustomSightDistanceMinFoV,
                0.01f,
                configuration.CustomSightDistanceMaxFoV,
                "%.3f",
                value => configuration.CustomSightDistanceMinFoV = value);
            DrawSightDistanceSlider(
                "Current FoV",
                "CustomSightDistanceFoV",
                configuration.CustomSightDistanceFoV,
                configuration.CustomSightDistanceMinFoV,
                configuration.CustomSightDistanceMaxFoV,
                "%.3f",
                value => configuration.CustomSightDistanceFoV = value);

            var ignoreCollision = configuration.CustomSightDistanceIgnoreCollision;
            if (ImGui.Checkbox("Ignore camera collision##CustomSightDistance", ref ignoreCollision))
            {
                configuration.CustomSightDistanceIgnoreCollision = ignoreCollision;
                ApplySightDistanceConfiguration();
                SaveConfiguration();
            }

            if (ImGui.Button("Reset sight defaults##CustomSightDistance"))
                ResetSightDistanceDefaults();

            ImGui.TextDisabled("These values apply immediately while the camera hooks are active.");
        }

        void DrawInstantLogoutTool()
        {
            if (ImGui.Button("Log out now##InstantLogout") && !plugin.InstantLogout.RequestLogout())
                SetToonModsStatus("XA Mods: Instant Logout did not fire a logout request.", true);

            ImGui.SameLine();
            if (ImGui.Button("Kill game now##InstantLogout") && !plugin.InstantLogout.RequestKillGame())
                SetToonModsStatus("XA Mods: Instant Logout could not start the kill-game flow.", true);

            ImGui.TextDisabled("Commands: /xa logout | /xa killgame");
            ImGui.TextDisabled("Kill game waits for logout to complete, then sends /xlkill.");
        }

        void DrawDozeSitAnywhereTools()
        {
            if (ImGui.Button("Sit now##DozeSitAnywhere") && !plugin.DozeSitAnywhere.RequestSit())
                SetToonModsStatus("XA Mods: Doze & Sit Anywhere could not trigger Sit.", true);

            ImGui.SameLine();
            if (ImGui.Button("Doze now##DozeSitAnywhere") && !plugin.DozeSitAnywhere.RequestDoze())
                SetToonModsStatus("XA Mods: Doze & Sit Anywhere could not trigger Doze.", true);

            ImGui.TextDisabled("Commands: /xa sit, /xa doze");
            ImGui.TextDisabled("Uses the emote-agent seam with the local sit/doze snap overrides instead of chat commands.");
        }

        void DrawInfiniteSprintOptions()
        {
            var delaySeconds = configuration.InfiniteSprintDelaySeconds;
            if (ImGui.SliderFloat(
                    "Sprint delay##InfiniteSprint",
                    ref delaySeconds,
                    PlayerModsService.InfiniteSprintDelaySecondsMinimum,
                    PlayerModsService.InfiniteSprintDelaySecondsMaximum,
                    "%.1f sec"))
            {
                configuration.InfiniteSprintDelaySeconds = delaySeconds;
                ApplyInfiniteSprintConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled("Delay starts when a fresh movement start is detected.");
        }

        void DrawExpertDeliveryOptions()
        {
            var autoSwitchWhenOpen = configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen;
            if (ImGui.Checkbox("Auto switch on window open##AutoUnlockExpertDelivery", ref autoSwitchWhenOpen))
            {
                configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen = autoSwitchWhenOpen;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            ImGui.BeginDisabled(!configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen);
            var selectedPage = Math.Clamp(configuration.AutoUnlockExpertDeliveryDefaultPage, 0, AutoExpertDeliveryPageLabels.Length - 1);
            if (ImGui.BeginCombo("Landing page##AutoUnlockExpertDelivery", AutoExpertDeliveryPageLabels[selectedPage]))
            {
                for (var i = 0; i < AutoExpertDeliveryPageLabels.Length; i++)
                {
                    var isSelected = selectedPage == i;
                    if (ImGui.Selectable(AutoExpertDeliveryPageLabels[i], isSelected))
                    {
                        configuration.AutoUnlockExpertDeliveryDefaultPage = i;
                        ApplyExpertDeliveryConfiguration();
                        SaveConfiguration();
                        selectedPage = i;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.EndDisabled();

            var skipHq = configuration.AutoUnlockExpertDeliverySkipHq;
            if (ImGui.Checkbox("Skip HQ items##AutoUnlockExpertDelivery", ref skipHq))
            {
                configuration.AutoUnlockExpertDeliverySkipHq = skipHq;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            var skipMateria = configuration.AutoUnlockExpertDeliverySkipMateria;
            if (ImGui.Checkbox("Skip items with materia##AutoUnlockExpertDelivery", ref skipMateria))
            {
                configuration.AutoUnlockExpertDeliverySkipMateria = skipMateria;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            var ignoreSealCap = configuration.AutoUnlockExpertDeliveryIgnoreSealCap;
            if (ImGui.Checkbox("Ignore seal max and keep selling##AutoUnlockExpertDelivery", ref ignoreSealCap))
            {
                configuration.AutoUnlockExpertDeliveryIgnoreSealCap = ignoreSealCap;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(ignoreSealCap
                ? "Allows Expert Delivery hand-ins to continue even when the next item would overcap Company Seals. Excess seals will be lost."
                : "Hand-ins stop before the next selected item would exceed the real Company Seal cap.");
        }

        void DrawTradeRefusalOptions()
        {
            var showNotification = configuration.AutoRefuseTradeShowNotification;
            if (ImGui.Checkbox("Show notification##AutoRefuseTrade", ref showNotification))
            {
                configuration.AutoRefuseTradeShowNotification = showNotification;
                ApplyTradeRefusalConfiguration();
                SaveConfiguration();
            }

            var sendEcho = configuration.AutoRefuseTradeSendEcho;
            if (ImGui.Checkbox("Send /echo message##AutoRefuseTrade", ref sendEcho))
            {
                configuration.AutoRefuseTradeSendEcho = sendEcho;
                ApplyTradeRefusalConfiguration();
                SaveConfiguration();
            }

            var extraCommands = configuration.AutoRefuseTradeExtraCommands;
            if (ImGui.InputTextMultiline(
                    "Commands after refusal##AutoRefuseTrade",
                    ref extraCommands,
                    1024,
                    new Vector2(Math.Max(320f, ImGui.GetContentRegionAvail().X), 72f)))
            {
                configuration.AutoRefuseTradeExtraCommands = extraCommands;
                ApplyTradeRefusalConfiguration();
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfiguration();

            ImGui.TextDisabled("Feedback stays local to this client and does not send tells, say, or party chat.");
            ImGui.TextDisabled("Extra commands run locally after XA refuses an incoming trade. Use one command per line.");
        }

        void DrawPeepingTomOptions()
        {
            var preserveHistory = configuration.ForcePeepingTomPreserveHistoryOnLogoutEnabled;
            if (ImGui.Checkbox("Preserve player list on logout##ForcePeepingTom", ref preserveHistory))
            {
                var applied = plugin.PeepingTomIntegration.SetPreserveHistoryOnLogoutEnabled(preserveHistory);
                configuration.ForcePeepingTomPreserveHistoryOnLogoutEnabled = applied;
                SaveConfiguration();
            }

            ImGui.TextDisabled("Keeps an in-memory snapshot of Peeping Tom's visible target list and restores it after the next character login. The snapshot respects Peeping Tom's own history limit and is lost if either plugin unloads.");
        }

        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-allow-multiple-game-instances",
            "Allow Multiple Game Instances",
            () => configuration.AutoAllowMultipleGameInstancesEnabled,
            plugin.SystemWindowMods.SetAllowMultipleGameInstancesEnabled,
            applied => configuration.AutoAllowMultipleGameInstancesEnabled = applied,
            "Clears the client-side single-instance launch lock for this process.",
            "Clears the game's named launch-lock handles inside the current process when the toggle is enabled or when the plugin starts with it already on.",
            plugin.SystemWindowMods.AllowMultipleGameInstancesStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-cancel-login-cooldown",
            "Cancel Login Cooldown",
            () => configuration.AutoCancelLoginCooldownEnabled,
            plugin.SystemWindowMods.SetCancelLoginCooldownEnabled,
            applied => configuration.AutoCancelLoginCooldownEnabled = applied,
            "Clears the temporary character-select login cooldown locally.",
            "Hooks the AgentLobby update path and clears the `TemporaryLocked` gate before and after the original update.",
            plugin.SystemWindowMods.CancelLoginCooldownStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-display-msq-progress",
            "Display MSQ Progress",
            () => configuration.AutoDisplayMsqProgressEnabled,
            plugin.MsqProgressDisplay.SetEnabled,
            applied => configuration.AutoDisplayMsqProgressEnabled = applied,
            "Expands Scenario Tree with remaining main-scenario count and completion percentage.",
            "Waits for the addon nodes to be ready, refreshes on `PostDraw`, resolves the first incomplete MSQ, and rewrites the visible summary locally.",
            plugin.MsqProgressDisplay.StatusText,
            searchTerms: ["Scenario Tree", "main scenario", "remaining", "completion percentage"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-skip-cutscenes",
            "Skip Cutscenes",
            () => configuration.AutoSkipCutscenesEnabled,
            plugin.AutoSkipCutscenes.SetEnabled,
            applied => configuration.AutoSkipCutscenesEnabled = applied,
            "Skips standard cutscenes and clears the skip prompt when available.",
            "Skips standard cutscene prompts, staff roll surfaces, and seen-cutscene checks when the native cutscene hooks are available.",
            plugin.AutoSkipCutscenes.StatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-skip-cutscenes-feeding-chocobo",
            "Skip Cutscenes Feeding Chocobo",
            () => configuration.AutoSkipCutscenesFeedingChocoboEnabled,
            plugin.BuddyFeedCutsceneSkip.SetEnabled,
            applied => configuration.AutoSkipCutscenesFeedingChocoboEnabled = applied,
            "Suppresses the companion feeding cutscene.",
            "Suppresses the buddy feeding scene only. This is a separate surface from normal event cutscene skipping and has no additional configuration.",
            plugin.BuddyFeedCutsceneSkip.StatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "auto-ignore-minimum-window-size",
            "Ignore Minimum Window Size",
            () => configuration.AutoIgnoreMinimumWindowSizeEnabled,
            plugin.SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled,
            applied => configuration.AutoIgnoreMinimumWindowSizeEnabled = applied,
            "Lowers the client minimum window size and re-syncs rendering after restore or maximize.",
            "Lowers the client-side minimum width and height limits, but keeps a guarded 250x200 floor because smaller values have been observed to crash the client. While this toggle is enabled, it also watches the real client size and rearms the game resolution state after external restores or maximizes so rendering can recover cleanly.",
            plugin.SystemWindowMods.IgnoreMinimumWindowSizeStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-hide-unnecessary-popups",
            "Hide Unnecessary Popups",
            () => configuration.AutoHideUnnecessaryPopupsEnabled,
            plugin.PopupCleaner.SetEnabled,
            applied => configuration.AutoHideUnnecessaryPopupsEnabled = applied,
            "Closes tutorial and recommendation popups as they appear.",
            "Closes a fixed set of tutorial and recommendation surfaces as they are drawn, including Play Guide, How To, recommendation, launcher, and achievement-style popups.",
            plugin.PopupCleaner.StatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-prevent-game-exiting-from-lobby-errors",
            "Prevent Game Exiting From Lobby Errors",
            () => configuration.AutoPreventGameExitingFromLobbyErrorsEnabled,
            plugin.SystemWindowMods.SetPreventLobbyExitEnabled,
            applied => configuration.AutoPreventGameExitingFromLobbyErrorsEnabled = applied,
            "Overrides the forced shutdown countdown used by lobby error dialogs.",
            "Overrides the forced shutdown timeout used by the relevant lobby error dialog surface so the client is not auto-closed locally.",
            plugin.SystemWindowMods.PreventLobbyExitStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-close-lobby-errors",
            "Close Lobby Errors",
            () => configuration.AutoCloseLobbyErrorsEnabled,
            plugin.LobbyErrorAutoClose.SetEnabled,
            applied => configuration.AutoCloseLobbyErrorsEnabled = applied,
            "Confirms disconnect and supported stuck-logout lobby Dialogue popups by pressing `OK` automatically.",
            "Monitors the `Dialogue` addon for disconnect/lobby error markers such as `90002`, `3102`, `Connection with the server was lost.`, and `You are still logged into the game.`, then clicks the live `OK` button automatically. `Instant Logout` also arms this same monitor for 10 seconds even when this toggle is off.",
            plugin.LobbyErrorAutoClose.StatusText,
            searchTerms: ["90002", "3102", "Connection with the server was lost.", "You are still logged into the game.", "Dialogue", "OK"]);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "auto-hide-game-objects",
            "Hide Game Objects",
            () => configuration.AutoHideGameObjectsEnabled,
            plugin.AutoHideGameObjects.SetEnabled,
            applied => configuration.AutoHideGameObjectsEnabled = applied,
            "Locally hides selected object categories from view with duty and territory guards.",
            "Hides players, pets, chocobos, or low-value NPCs on the local client while leaving party members, alliance members, friends, marked objects, and your own character visible. The extra options can disable the feature in duties or Island Sanctuary, and can apply the safer Occult Crescent filtering rules.",
            plugin.AutoHideGameObjects.StatusText,
            searchTerms: ["Hide players", "Hide unimportant NPCs", "Hide pets", "Hide chocobos", "Occult Crescent", "Island Sanctuary", "duty"],
            drawOptions: DrawAutoHideGameObjectsOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "custom-resolutions",
            "Custom Resolutions",
            () => configuration.CustomResolutionsEnabled,
            plugin.SystemWindowMods.SetCustomResolutionsEnabled,
            applied => configuration.CustomResolutionsEnabled = applied,
            "Applies saved or typed client sizes from the panel or `/xa res <width>x<height>`.",
            "Lets you apply custom window sizes, use the built-in 500x345 example, and save or remove your own preset buttons. A safety floor stays enforced so unstable sizes are not requested.",
            plugin.SystemWindowMods.CustomResolutionsStatusText,
            searchTerms: ["/xa res", "500x345", "Width", "Height", "Add button", "Delete"],
            drawOptions: DrawCustomResolutionTools);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-skip-dialogue",
            "Skip Dialogue",
            () => configuration.AutoSkipDialogueEnabled,
            plugin.DialogueSkip.SetEnabled,
            applied => configuration.AutoSkipDialogueEnabled = applied,
            "Auto-advances Talk dialogue and the broader native talk surfaces locally.",
            "Auto-clicks the standard `Talk` addon and also hooks the native Talk, SystemTalk, ShortTalk, leve, and guildleve dialogue surfaces when those signatures are available.",
            plugin.DialogueSkip.StatusText,
            searchTerms: ["Talk", "SystemTalk", "ShortTalk", "npc", "dialogue", "conversation", "leve", "guildleve"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "display-actual-queue-position",
            "Display Actual Queue Position",
            () => configuration.DisplayActualQueuePositionEnabled,
            plugin.QueuePositionDisplay.SetEnabled,
            applied => configuration.DisplayActualQueuePositionEnabled = applied,
            "Expands queue displays with position and ETA information.",
            "Shows queue position, elapsed time, and an ETA on supported queue displays.",
            plugin.QueuePositionDisplay.StatusText,
            searchTerms: ["ETA", "elapsed", "queue position", "wait time"]);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "disable-background-game-rendering",
            "Disable Background Rendering",
            () => configuration.DisableBackgroundGameRenderingEnabled,
            plugin.SystemWindowMods.SetDisableBackgroundRenderingEnabled,
            applied => configuration.DisableBackgroundGameRenderingEnabled = applied,
            "Pauses the background DX11 render tick and forces a keep-alive frame periodically.",
            "Pauses most background rendering, lets an occasional keep-alive frame through, and supports minimized-only or AutoRetainer multi-mode exceptions.",
            plugin.SystemWindowMods.DisableBackgroundRenderingStatusText,
            searchTerms: ["Only while minimized", "Disable when AR Multi is on", "AutoRetainer multi-mode", "DX11", "nameplate"],
            drawOptions: DrawBackgroundRenderingOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "low-resolution",
            "Low Resolution",
            () => configuration.LowResolutionEnabled,
            plugin.SystemWindowMods.SetLowResolutionEnabled,
            applied => configuration.LowResolutionEnabled = applied,
            "Forces the live 3D resolution scale below the game's normal UI floor.",
            "Forces the live 3D resolution scale to the slider value between 0.01 and 1.00. If the current runtime scaler is DLSS, XA temporarily switches to AMD FSR while the feature is active and restores the previous mode on disable.",
            plugin.SystemWindowMods.LowResolutionStatusText,
            searchTerms: ["3D resolution scale", "0.01", "1.00", "DLSS", "FSR", "GraphicsRezoScale", "GraphicsRezoUpscaleType"],
            drawOptions: DrawLowResolutionOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "copy-item-name-for-all",
            "Copy Item Name For All",
            () => configuration.CopyItemNameForAllEnabled,
            plugin.CopyItemNameContextMenu.SetEnabled,
            applied => configuration.CopyItemNameForAllEnabled = applied,
            "Adds inventory context-menu actions to copy base or glamour item names.",
            "Adds copy-name actions to supported inventory context menus, including glamour-source resolution when available.",
            plugin.CopyItemNameContextMenu.StatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "special-rendering-modes",
            "Special Rendering Modes",
            () => configuration.SpecialRenderModesEnabled,
            SetSpecialRenderModesEnabled,
            applied => configuration.SpecialRenderModesEnabled = applied,
            "Reveals world fade and UI visibility tools. Turning this off restores all world/UI surfaces.",
            "Shows the world fade and UI visibility tools while enabled. Turning it off restores the world and any hidden UI groups.",
            configuration.SpecialRenderModesEnabled
                ? plugin.SystemWindowMods.SpecialRenderModesStatusText
                : "Disabled",
            searchTerms: [
                "Background color",
                "Hide world / keep addons",
                "Restore all",
                "Hide addons / keep nameplates",
                "Hide addons / keep chat",
                "Hide chat log",
                "Hide action bars",
                "Hide target info",
                "Hide nameplates"],
            drawOptions: DrawSpecialRenderModeTools);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "expanded-player-right-click-menu-search",
            "Expanded Player Right-Click Menu Search",
            () => configuration.ExpandedPlayerRightClickMenuSearchEnabled,
            plugin.PlayerSearchContextMenu.SetEnabled,
            applied => configuration.ExpandedPlayerRightClickMenuSearchEnabled = applied,
            "Adds FFLogs, Lodestone, and Lalachievements shortcuts to player context menus.",
            "Adds a search submenu to supported player context menus. The options below decide which providers appear and whether an Open All shortcut is shown.",
            plugin.PlayerSearchContextMenu.StatusText,
            searchTerms: ["FFLogs", "Lodestone", "Lalachievements", "Open All"],
            drawOptions: DrawPlayerSearchOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "live-anonymous-mode",
            "Live Anonymous Mode",
            () => configuration.LiveAnonymousModeEnabled,
            plugin.NameplatePrivacy.SetAnonymousModeEnabled,
            applied => configuration.LiveAnonymousModeEnabled = applied,
            "Masks visible player nameplates locally.",
            "Masks visible player nameplates locally with generated traveler aliases and removes titles or FC tags from the rewritten plates. This only changes local presentation and does not change server data.",
            plugin.NameplatePrivacy.AnonymousModeStatusText);

        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-expert-delivery",
            "Automate Expert Delivery",
            () => configuration.AutoUnlockExpertDeliveryEnabled,
            plugin.AutoUnlockExpertDelivery.SetEnabled,
            applied => configuration.AutoUnlockExpertDeliveryEnabled = applied,
            "Automates Expert Delivery hand-ins for characters that already have the feature unlocked, with local failure, prompt-classification, repeat-scan, and configurable seal-cap handling.",
            "Automates Expert Delivery hand-ins, handles confirmation prompts, stops when no eligible items remain, and can either respect or ignore the seal cap option below.",
            plugin.AutoUnlockExpertDelivery.StatusText,
            searchTerms: ["Auto switch on window open", "Landing page", "Skip HQ items", "Skip items with materia", "Ignore seal max and keep selling"],
            drawOptions: DrawExpertDeliveryOptions);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "auto-unlock-expert-delivery",
            "Unlock Expert Delivery",
            () => configuration.UnlockExpertDeliveryEnabled,
            plugin.ExpertDeliveryUnlock.SetEnabled,
            applied => configuration.UnlockExpertDeliveryEnabled = applied,
            "Forces a fixed local Grand Company rank floor of 11 so Expert Delivery appears.",
            "Locally spoofs a minimum Grand Company rank of 11 so the Expert Delivery entry appears.",
            plugin.ExpertDeliveryUnlock.StatusText,
            warningText: "DO NOT USE IF YOUR LODESTONE IS NOT SET TO PRIVATE! You take the risk of revealing your character on the leaderboards by using this. If you're not the actual proper rank, it's easy to determine if you're using this.",
            requireCtrlShiftToEnable: true);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-refuse-trade-request",
            "Refuse Trade Request",
            () => configuration.AutoRefuseTradeRequestEnabled,
            plugin.AutoRefuseTrade.SetEnabled,
            applied => configuration.AutoRefuseTradeRequestEnabled = applied,
            "Refuses incoming trade requests automatically.",
            "Refuses incoming trade requests unless this client recently initiated one. The options below control local notifications and any extra local commands that should run after each refusal.",
            plugin.AutoRefuseTrade.StatusText,
            searchTerms: ["Show notification", "Send /echo message", "Commands after refusal"],
            drawOptions: DrawTradeRefusalOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-reveal-undiscovered-areas",
            "Reveal Undiscovered Areas",
            () => configuration.AutoRevealUndiscoveredAreasEnabled,
            plugin.SystemWindowMods.SetRevealUndiscoveredAreasEnabled,
            applied => configuration.AutoRevealUndiscoveredAreasEnabled = applied,
            "Clears local map discovery flags when the map agent refreshes.",
            "Reveals map coverage locally by clearing undiscovered-area flags whenever the map refreshes.",
            plugin.SystemWindowMods.RevealUndiscoveredAreasStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-clear-teleportation-lock",
            "Clear Teleportation Lock",
            () => configuration.AutoClearTeleportationLockEnabled,
            plugin.TeleportLockClear.SetEnabled,
            applied => configuration.AutoClearTeleportationLockEnabled = applied,
            "Suppresses the teleport-lock log and retries Teleport locally.",
            "Suppresses the teleport-lock log message and retries Teleport immediately when that local lock is hit.",
            plugin.TeleportLockClear.StatusText,
            searchTerms: ["1665", "Teleport", "stuck", "log message"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "custom-sight-distance",
            "Custom Sight Distance",
            () => configuration.CustomSightDistanceEnabled,
            plugin.SightDistance.SetEnabled,
            applied => configuration.CustomSightDistanceEnabled = applied,
            "Overrides camera distance, angle, FoV, and optional collision limits.",
            "Lets you adjust camera distance, rotation, field of view, and optional collision handling while the camera hooks are active.",
            plugin.SightDistance.StatusText,
            searchTerms: ["Max distance", "Min distance", "Max rotation", "Min rotation", "Max FoV", "Min FoV", "Current FoV", "Ignore camera collision"],
            drawOptions: DrawSightDistanceOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "doze-sit-anywhere",
            "Doze & Sit Anywhere",
            () => configuration.DozeSitAnywhereEnabled,
            plugin.DozeSitAnywhere.SetEnabled,
            applied => configuration.DozeSitAnywhereEnabled = applied,
            "Lets you trigger Sit and Doze from the panel or `/xa sit` / `/xa doze` without nearby furniture.",
            "Allows Sit and Doze to fire without a nearby bed or chair. The panel buttons and `/xa sit` / `/xa doze` only work while this toggle is enabled.",
            plugin.DozeSitAnywhere.StatusText,
            searchTerms: ["/xa sit", "/xa doze", "sit now", "doze now", "emote", "bed", "chair"],
            drawOptions: DrawDozeSitAnywhereTools);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "infinite-sprint",
            "Infinite Sprint",
            () => configuration.InfiniteSprintEnabled,
            plugin.PlayerMods.SetInfiniteSprintEnabled,
            applied => configuration.InfiniteSprintEnabled = applied,
            "Reapplies Sprint only while the local character is moving, with a configurable movement-start delay.",
            "Reapplies Sprint after it falls off, but only while real movement is detected and after the configured movement-start delay.",
            plugin.PlayerMods.InfiniteSprintStatusText,
            searchTerms: ["Sprint delay", "movement-start delay", "recast delay"],
            drawOptions: DrawInfiniteSprintOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "instant-logout",
            "Instant Logout",
            () => configuration.InstantLogoutEnabled,
            plugin.InstantLogout.SetEnabled,
            applied => configuration.InstantLogoutEnabled = applied,
            "Arms hard logout and enables `/xa logout` and `/xa killgame`.",
            "Uses the native contents-finder request path for hard logout. `/xa killgame` waits for the logout to complete, then sends `/xlkill`. When this toggle is off, the panel buttons are hidden and those commands do nothing.",
            plugin.InstantLogout.StatusText,
            searchTerms: ["logout", "/xa logout", "/xa killgame", "Log out now", "Kill game now"],
            drawOptions: DrawInstantLogoutTool);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "moveable-after-death",
            "Moveable After Death",
            () => configuration.MoveableAfterDeathEnabled,
            plugin.PlayerMods.SetMoveableAfterDeathEnabled,
            applied => configuration.MoveableAfterDeathEnabled = applied,
            "Keeps local movement permission open after death.",
            "Keeps local movement enabled after death.",
            plugin.PlayerMods.MoveableAfterDeathStatusText,
            warningText: "USE AT YOUR OWN RISK! Others will see your dead feckin body moving around, while they die of laughter others might get angry.",
            requireCtrlShiftToEnable: true);

        AddSavedFeatureEntry(
            ToonModsSection.PluginMods,
            "force-peepingtom",
            "Force PeepingTom",
            () => configuration.ForcePeepingTomEnabled,
            plugin.PeepingTomIntegration.SetForceEnabled,
            applied => configuration.ForcePeepingTomEnabled = applied,
            "Keeps Peeping Tom target tracking active in PvP matches.",
            "Keeps Peeping Tom target tracking active in PvP by bypassing its local PvP runtime gate. Peeping Tom still controls what markers or windows it shows.",
            plugin.PeepingTomIntegration.StatusText,
            searchTerms: ["Preserve player list on logout", "history"],
            drawOptions: DrawPeepingTomOptions);

        var enabledXAModsCount = toonModDefinitions.Count(entry => entry.GetCurrent());
        ImGui.TextColored(
            new Vector4(0.4f, 0.8f, 1.0f, 1.0f),
            $"XA QoL Mods: {toonModDefinitions.Count} Total Available | {enabledXAModsCount} Enabled");
        ImGui.TextDisabled("Configure character quality-of-life automation and plugin integrations.");
        ImGui.Spacing();

        const string showOnlyEnabledLabel = "Show Only Enabled";
        const string showAllPluginsLabel = "Show All Plugins";
        var showOnlyEnabledButtonWidth = MathF.Max(
            ImGui.CalcTextSize(showOnlyEnabledLabel).X,
            ImGui.CalcTextSize(showAllPluginsLabel).X)
            + (ImGui.GetStyle().FramePadding.X * 2f);

        if (ImGui.Button(
                $"{(toonModsShowOnlyEnabled ? showAllPluginsLabel : showOnlyEnabledLabel)}##ToonModsShowOnlyEnabled",
                new Vector2(showOnlyEnabledButtonWidth, 0f)))
        {
            toonModsShowOnlyEnabled = !toonModsShowOnlyEnabled;
        }
        ImGui.SameLine();
        if (ImGui.Button("Disable All Mods##ToonModsDisableAll"))
            DisableAllMods();
        ImGui.SameLine();
        if (ImGui.Button("Save List##ToonModsSaveList"))
            ImGui.OpenPopup("ToonModsSaveListPopup");
        ImGui.SameLine();
        if (ImGui.Button("Load List##ToonModsLoadList"))
            ImGui.OpenPopup("ToonModsLoadListPopup");
        ImGui.SameLine();
        if (ImGui.Button("Export##ToonModsExport"))
            ExportCurrentToonModsList();
        ImGui.SameLine();
        if (ImGui.Button("Import##ToonModsImport"))
            ImportCurrentToonModsList();

        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##ToonModsSearch", "Search mod titles, descriptions, and sub-options", ref toonModsSearchText, 256);

        DrawToonModsSaveListPopup();
        DrawToonModsLoadListPopup();

        if (!string.IsNullOrEmpty(toonModsStatus) && DateTime.UtcNow < toonModsStatusExpiryUtc)
            ImGui.TextColored(toonModsStatusIsError ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f) : new Vector4(0.4f, 1.0f, 0.4f, 1.0f), toonModsStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawModSection(ToonModsSection.GameMods, "Game Mods");
        DrawModSection(ToonModsSection.GraphicMods, "Graphic Mods");
        DrawModSection(ToonModsSection.PlayerMods, "Player Mods");
        DrawModSection(ToonModsSection.PluginMods, "Plugin Mods");
        DrawModSection(ToonModsSection.IllegalMods, "Illegal Shit You Shouldn't Use");

        if (featureEntries.Count == 0)
        {
            ImGui.TextDisabled("No XA Mods matched the current filter.");
            ImGui.TextDisabled("Filters apply the search text plus the optional enabled-only toggle.");
        }
    }

}
