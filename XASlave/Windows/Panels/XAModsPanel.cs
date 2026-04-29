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
        PluginMods,
        EurekaMods,
        IllegalMods,
    }

    private static readonly string[] AutoExpertDeliveryPageLabels = { "Supply Missions", "Provisioning Missions", "Expert Delivery" };
    private static readonly EurekaInstanceIdService.EurekaZone[] EurekaZoneOptions =
    {
        EurekaInstanceIdService.EurekaZone.Anemos,
        EurekaInstanceIdService.EurekaZone.Pagos,
        EurekaInstanceIdService.EurekaZone.Pyros,
        EurekaInstanceIdService.EurekaZone.Hydatos,
    };
    private string toonModsSearchText = string.Empty;
    private bool toonModsShowOnlyEnabled;
    private string toonModsSavedListName = string.Empty;
    private string toonModsStatus = string.Empty;
    private DateTime toonModsStatusExpiryUtc = DateTime.MinValue;
    private bool toonModsStatusIsError;
    private int xaModsCustomResolutionWidth = 500;
    private int xaModsCustomResolutionHeight = 345;
    private static readonly JsonSerializerOptions toonModsListJsonOptions = ToonModsPresetSerialization.JsonOptions;

    private bool GetToonModsSectionExpanded(ToonModsSection section)
    {
        return section switch
        {
            ToonModsSection.GameMods => plugin.Configuration.ToonModsGameModsExpanded,
            ToonModsSection.GraphicMods => plugin.Configuration.ToonModsGraphicModsExpanded,
            ToonModsSection.PlayerMods => plugin.Configuration.ToonModsPlayerModsExpanded,
            ToonModsSection.PluginMods => plugin.Configuration.ToonModsPluginModsExpanded,
            ToonModsSection.EurekaMods => plugin.Configuration.ToonModsEurekaExpanded,
            ToonModsSection.IllegalMods => plugin.Configuration.ToonModsIllegalModsExpanded,
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
            case ToonModsSection.PluginMods:
                if (plugin.Configuration.ToonModsPluginModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsPluginModsExpanded = expanded;
                break;
            case ToonModsSection.EurekaMods:
                if (plugin.Configuration.ToonModsEurekaExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsEurekaExpanded = expanded;
                break;
            case ToonModsSection.IllegalMods:
                if (plugin.Configuration.ToonModsIllegalModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsIllegalModsExpanded = expanded;
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
            ImGui.PushTextWrapPos(Scale(460f));
            ImGui.TextUnformatted(helpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        void DrawWarningText(string warningText)
        {
            if (string.IsNullOrWhiteSpace(warningText))
                return;

            ImGui.PushTextWrapPos(Scale(460f));
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

        void RestoreSpecialRenderModes(bool clearStoredUiToggles = false)
        {
            plugin.RestoreSpecialRenderModes(clearStoredUiToggles);
        }

        void ApplySpecialRenderModesConfiguration()
        {
            plugin.ApplySpecialRenderModesConfiguration();
        }

        bool SetSpecialRenderModesEnabled(bool value)
        {
            return plugin.SetSpecialRenderModesEnabled(value);
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

        void ApplyUnlockExpertDeliveryConfiguration()
        {
            configuration.UnlockExpertDeliveryForcedRankFloor = ExpertDeliveryUnlockService.NormalizeForcedRankFloor(configuration.UnlockExpertDeliveryForcedRankFloor);
            plugin.ExpertDeliveryUnlock.ApplyConfiguration(configuration.UnlockExpertDeliveryForcedRankFloor);
        }

        string GetUnlockExpertDeliveryRankLabel(int rank)
        {
            return ExpertDeliveryUnlockService.GetForcedRankFloorLabel(rank);
        }

        void ApplyAutoLeaveDutyConfiguration()
        {
            configuration.AutoLeaveDutyDelaySeconds = AutoLeaveDutyService.ClampDelaySeconds(configuration.AutoLeaveDutyDelaySeconds);
            plugin.AutoLeaveDuty.ApplyConfiguration(configuration.AutoLeaveDutyDelaySeconds);
        }

        void ApplyBailoutEscMenuConfiguration()
        {
            configuration.BailoutEscMenuSeconds = EscMenuBailoutService.NormalizeTimeoutSeconds(configuration.BailoutEscMenuSeconds);
            plugin.EscMenuBailout.ApplyConfiguration(configuration.BailoutEscMenuSeconds);
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

        void ApplyPopupCleanerConfiguration()
        {
            plugin.PopupCleaner.ApplyConfiguration(configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled);
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
            System.Action? drawOptions = null,
            bool showOptionsWhenDisabled = false)
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
                    ImGui.PushTextWrapPos(Scale(320f));
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

            if ((value || showOptionsWhenDisabled) && drawOptions != null)
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
            Action? drawOptions = null,
            bool showOptionsWhenDisabled = false)
        {
            if (toonModsShowOnlyEnabled && !currentValue)
                return;

            if (!MatchesToonModsSearch(label, description, helpText, searchTerms))
                return;

            featureEntries.Add((section, label, () => DrawFeatureToggle(label, currentValue, apply, store, description, helpText, status, warningText, requireCtrlShiftToEnable, drawOptions, showOptionsWhenDisabled)));
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
            Action? drawOptions = null,
            bool showOptionsWhenDisabled = false)
        {
            toonModDefinitions.Add((key, getCurrent, apply, store));
            AddFeatureEntry(section, label, getCurrent(), apply, store, description, helpText, status, warningText, requireCtrlShiftToEnable, searchTerms, drawOptions, showOptionsWhenDisabled);
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
            var modSettings = plugin.CaptureXAModSettingsForKeys(modKeys);
            var saved = configuration.ToonModsSavedLists.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (saved == null)
            {
                configuration.ToonModsSavedLists.Add(new ToonModSavedList
                {
                    Name = name,
                    ModKeys = modKeys,
                    ModSettings = modSettings,
                });
            }
            else
            {
                saved.Name = name;
                saved.ModKeys = modKeys;
                saved.ModSettings = modSettings;
            }

            SaveConfiguration();
            toonModsSavedListName = name;
            SetToonModsStatus($"XA Mods: saved list '{name}' ({modKeys.Count} mods).");
        }

        void ApplyToonModsList(string title, IEnumerable<string> modKeys, IReadOnlyDictionary<string, JsonElement>? modSettings = null)
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "XA Mods Selection" : title.Trim();
            toonModsSavedListName = safeTitle;
            var success = plugin.ApplySavedXAModsPreset(safeTitle, modKeys, modSettings, out var message);
            SetToonModsStatus($"XA Mods: {message}", !success);
        }

        void ExportCurrentToonModsList()
        {
            var modKeys = GetCurrentToonModKeys();
            var package = new ToonModsListPackage
            {
                ListId = Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(toonModsSavedListName) ? "XA Mods Selection" : toonModsSavedListName.Trim(),
                ExportedAtUtc = DateTime.UtcNow,
                ModKeys = modKeys,
                ModSettings = plugin.CaptureXAModSettingsForKeys(modKeys),
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

            ApplyToonModsList(package.Title, package.ModKeys, package.ModSettings);
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

                if (TryGetToonModsJsonProperty(root, "modSettings", out var modSettingsElement)
                    && modSettingsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in modSettingsElement.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(property.Name))
                            continue;

                        package.ModSettings[property.Name] = property.Value.Clone();
                    }
                }

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

            ImGui.SetNextItemWidth(Scale(220f));
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
                    ApplyToonModsList(saved.Name, saved.ModKeys, saved.ModSettings);

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

        void DrawHideUnnecessaryPopupsOptions()
        {
            var hideHowToNotice = configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled;
            if (ImGui.Checkbox("Also hide HowToNotice##HideUnnecessaryPopups", ref hideHowToNotice))
            {
                configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled = hideHowToNotice;
                ApplyPopupCleanerConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(hideHowToNotice
                ? "HowToNotice tutorial prompts are included in the popup cleaner list."
                : "HowToNotice stays visible unless this extra checkbox is turned on.");
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

        void DrawSpecialRenderToggle(string label, string id, bool currentValue, Action<bool> store, string blockedEnableMessage = "")
        {
            var value = currentValue;
            var disableEnablePath = !currentValue && !string.IsNullOrWhiteSpace(blockedEnableMessage);
            if (disableEnablePath)
                ImGui.BeginDisabled();

            var changed = ImGui.Checkbox($"{label}##{id}", ref value);

            if (disableEnablePath)
                ImGui.EndDisabled();

            if (disableEnablePath && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(blockedEnableMessage);

            if (!changed)
                return;

            store(value);
            ApplySpecialRenderModesConfiguration();
            SaveConfiguration();
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
            {
                RestoreSpecialRenderModes(clearStoredUiToggles: true);
                SaveConfiguration();
            }

            ImGui.TextDisabled("The background color is used when the world-render fade is forced on.");
            ImGui.TextDisabled("These UI toggles are stored while Special Rendering Modes is enabled. Restore all clears them.");
            if (!plugin.CanEnableSpecialRenderHideChat(out var hideChatBlockedMessage))
                ImGui.TextDisabled(hideChatBlockedMessage);

            DrawSpecialRenderToggle(
                "Hide addons / keep nameplates",
                "SpecialRenderHideAddonsKeepNameplates",
                configuration.SpecialRenderHideAddonsKeepNameplatesEnabled,
                value => configuration.SpecialRenderHideAddonsKeepNameplatesEnabled = value);

            DrawSpecialRenderToggle(
                "Hide addons / keep chat",
                "SpecialRenderHideAddonsKeepChat",
                configuration.SpecialRenderHideAddonsKeepChatEnabled,
                value => configuration.SpecialRenderHideAddonsKeepChatEnabled = value);

            DrawSpecialRenderToggle(
                "Hide chat",
                "SpecialRenderHideChat",
                configuration.SpecialRenderHideChatEnabled,
                value => configuration.SpecialRenderHideChatEnabled = value,
                hideChatBlockedMessage);
            DrawSpecialRenderToggle(
                "Hide action bars",
                "SpecialRenderHideActionBars",
                configuration.SpecialRenderHideActionBarsEnabled,
                value => configuration.SpecialRenderHideActionBarsEnabled = value);
            DrawSpecialRenderToggle(
                "Hide target info",
                "SpecialRenderHideTargetInfo",
                configuration.SpecialRenderHideTargetInfoEnabled,
                value => configuration.SpecialRenderHideTargetInfoEnabled = value);
            DrawSpecialRenderToggle(
                "Hide nameplates",
                "SpecialRenderHideNameplates",
                configuration.SpecialRenderHideNameplatesEnabled,
                value => configuration.SpecialRenderHideNameplatesEnabled = value);
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
            var logoutActionsAllowed = plugin.CanTriggerLogoutActions(out var blockedMessage);
            if (!logoutActionsAllowed)
                ImGui.BeginDisabled();

            if (ImGui.Button("Log out now##InstantLogout"))
            {
                if (!plugin.TryRequestLogoutAction(out var message))
                    SetToonModsStatus($"XA Mods: {message}", true);
                else
                    SetToonModsStatus("XA Mods: Triggered XA hard logout.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Kill game now##InstantLogout"))
            {
                if (!plugin.TryRequestKillGameAction(out var message))
                    SetToonModsStatus($"XA Mods: {message}", true);
                else
                    SetToonModsStatus("XA Mods: Triggered XA kill-game flow.");
            }

            if (!logoutActionsAllowed)
                ImGui.EndDisabled();

            ImGui.TextDisabled("Commands: /xa logout | /xa killgame");
            ImGui.TextDisabled("Kill game waits for logout to complete, then sends /xlkill.");
            if (!logoutActionsAllowed)
                ImGui.TextDisabled(blockedMessage);
        }

        void DrawBailoutEscMenuOptions()
        {
            if (!configuration.BailoutEscMenuEnabled)
                return;

            var timeoutOptions = EscMenuBailoutService.TimeoutOptions;
            var timeoutIndex = Array.IndexOf(timeoutOptions, configuration.BailoutEscMenuSeconds);
            if (timeoutIndex < 0)
            {
                configuration.BailoutEscMenuSeconds = EscMenuBailoutService.NormalizeTimeoutSeconds(configuration.BailoutEscMenuSeconds);
                timeoutIndex = Array.IndexOf(timeoutOptions, configuration.BailoutEscMenuSeconds);
            }

            ImGui.SetNextItemWidth(Scale(180f));
            if (ImGui.SliderInt("Timeout##BailoutEscMenu", ref timeoutIndex, 0, timeoutOptions.Length - 1, $"{timeoutOptions[timeoutIndex]} sec"))
            {
                configuration.BailoutEscMenuSeconds = timeoutOptions[timeoutIndex];
                ApplyBailoutEscMenuConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled("Closes addon:SystemMenu locally if it stays open past the selected timer.");
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
            ImGui.TextDisabled("Turn on the master toggle first, then use the panel buttons or add Sit / Doze titlebar favourites from Plugin Operations.");
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

        void DrawAutoLeaveDutyOptions()
        {
            var delaySeconds = configuration.AutoLeaveDutyDelaySeconds;
            if (ImGui.SliderInt(
                    "Leave after##AutoLeaveDuty",
                    ref delaySeconds,
                    AutoLeaveDutyService.DelaySecondsMinimum,
                    AutoLeaveDutyService.DelaySecondsMaximum,
                    "%d sec"))
            {
                configuration.AutoLeaveDutyDelaySeconds = delaySeconds;
                ApplyAutoLeaveDutyConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled("After duty completion, XA waits this long before opening the duty menu and confirming Leave Duty.");
        }

        void DrawEurekaInstanceIdOptions()
        {
            DrawEurekaInstanceIdSharedDisplayOptions("EurekaInstanceId");

            if (ImGui.Button("Open Eureka Instance Hunter##EurekaInstanceId"))
                OpenEurekaInstanceHunterTask();

            ImGui.TextDisabled("`Instance ID` here is the shared global trigger for the live Eureka ID surface and optional DTR entry.");
            ImGui.TextDisabled("The actual Rodney scanner now lives in `Field Operations` -> `Eureka Instance Hunter`, including the zone rows, baselines, `Start` / `Stop`, leave-duty delay, and alert settings.");
            ImGui.TextDisabled(plugin.EurekaInstanceId.StatusText);
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

        void DrawUnlockExpertDeliveryOptions()
        {
            var selectedRank = ExpertDeliveryUnlockService.NormalizeForcedRankFloor(configuration.UnlockExpertDeliveryForcedRankFloor);
            if (ImGui.BeginCombo("GC rank floor##UnlockExpertDelivery", GetUnlockExpertDeliveryRankLabel(selectedRank)))
            {
                for (var rank = ExpertDeliveryUnlockService.MinForcedRankFloor; rank <= ExpertDeliveryUnlockService.MaxForcedRankFloor; rank++)
                {
                    var isSelected = selectedRank == rank;
                    if (ImGui.Selectable(GetUnlockExpertDeliveryRankLabel(rank), isSelected))
                    {
                        configuration.UnlockExpertDeliveryForcedRankFloor = rank;
                        ApplyUnlockExpertDeliveryConfiguration();
                        SaveConfiguration();
                        selectedRank = rank;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.TextDisabled("Rank 0 leaves the real rank untouched. Higher values use the named GC-rank labels from your reference list and only spoof upward when needed.");
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
            if (ImGui.Checkbox("Send /e message##AutoRefuseTrade", ref sendEcho))
            {
                configuration.AutoRefuseTradeSendEcho = sendEcho;
                ApplyTradeRefusalConfiguration();
                SaveConfiguration();
            }

            var extraCommands = configuration.AutoRefuseTradeExtraCommands;
            if (ImGui.InputTextMultiline(
                    "Echo lines / commands after refusal##AutoRefuseTrade",
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
            ImGui.TextDisabled("Plain lines are sent locally as /e. Slash-prefixed lines still run as commands.");
            ImGui.TextDisabled("Use <trader> or <target> for the incoming trader's first and last name.");
        }

        void DrawXAPeepOptions()
        {
            if (ImGui.Button(plugin.XAPeepWindow.IsOpen ? "Hide XA Peep Window" : "Show XA Peep Window"))
                plugin.ToggleXAPeepUi();

            ImGui.SameLine();
            if (ImGui.Button(plugin.XAPeepHistoryWindow.IsOpen ? "Hide History" : "Show History"))
                plugin.ToggleXAPeepHistoryUi();

            ImGui.SameLine();
            var clearXaPeepHistoryModifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            if (!clearXaPeepHistoryModifierHeld)
                ImGui.BeginDisabled();
            if (ImGui.Button("Clear XA Peep History"))
                plugin.XAPeep.ClearHistory();
            if (!clearXaPeepHistoryModifierHeld)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Press and hold CTRL + SHIFT to allow clearing.");
            }

            var autoOpenWindow = configuration.XAPeepAutoOpenWindowOnPluginLoad;
            if (ImGui.Checkbox("Auto open XA Peep on plugin load##XAPeep", ref autoOpenWindow))
            {
                configuration.XAPeepAutoOpenWindowOnPluginLoad = autoOpenWindow;
                SaveConfiguration();
            }

            var logParty = configuration.XAPeepLogParty;
            if (ImGui.Checkbox("Log party members##XAPeep", ref logParty))
            {
                configuration.XAPeepLogParty = logParty;
                SaveConfiguration();
            }

            var logAlliance = configuration.XAPeepLogAlliance;
            if (ImGui.Checkbox("Log alliance members##XAPeep", ref logAlliance))
            {
                configuration.XAPeepLogAlliance = logAlliance;
                SaveConfiguration();
            }

            var logPlayersInCombat = configuration.XAPeepLogInCombat;
            if (ImGui.Checkbox("Log players in combat##XAPeep", ref logPlayersInCombat))
            {
                configuration.XAPeepLogInCombat = logPlayersInCombat;
                SaveConfiguration();
            }

            var showCardWhenTargeted = configuration.XAPeepDisplayLineWhenTargetingMe;
            if (ImGui.Checkbox("Show card when targeted##XAPeep", ref showCardWhenTargeted))
            {
                configuration.XAPeepDisplayLineWhenTargetingMe = showCardWhenTargeted;
                SaveConfiguration();
            }

            var showTargeterLine = configuration.XAPeepShowTargeterLine;
            if (ImGui.Checkbox("Show Targeter's Line##XAPeep", ref showTargeterLine))
            {
                configuration.XAPeepShowTargeterLine = showTargeterLine;
                SaveConfiguration();
            }

            var targeterLineColor = configuration.XAPeepTargeterLineColor;
            if (ImGui.ColorEdit4("Targeter line color##XAPeep", ref targeterLineColor))
            {
                configuration.XAPeepTargeterLineColor = targeterLineColor;
                SaveConfiguration();
            }

            var showTargeterDot = configuration.XAPeepShowTargeterDot;
            if (ImGui.Checkbox("Show targeter's dot##XAPeep", ref showTargeterDot))
            {
                configuration.XAPeepShowTargeterDot = showTargeterDot;
                SaveConfiguration();
            }

            var targeterDotColor = configuration.XAPeepTargeterDotColor;
            if (ImGui.ColorEdit4("Targeter dot color##XAPeep", ref targeterDotColor))
            {
                configuration.XAPeepTargeterDotColor = targeterDotColor;
                SaveConfiguration();
            }

            var targeterDotSize = Math.Clamp(configuration.XAPeepTargeterDotSize, 1f, 15f);
            if (ImGui.SliderFloat("Targeter dot size##XAPeep", ref targeterDotSize, 1f, 15f, "%.1f"))
            {
                configuration.XAPeepTargeterDotSize = Math.Clamp(targeterDotSize, 1f, 15f);
                SaveConfiguration();
            }

            var showTargetersCard = configuration.XAPeepShowTargetersCard;
            if (ImGui.Checkbox("Show targeters card##XAPeep", ref showTargetersCard))
            {
                configuration.XAPeepShowTargetersCard = showTargetersCard;
                SaveConfiguration();
            }

            var showCenterNotification = configuration.XAPeepShowCenterNotification;
            if (ImGui.Checkbox("Show center-screen notification##XAPeep", ref showCenterNotification))
            {
                configuration.XAPeepShowCenterNotification = showCenterNotification;
                SaveConfiguration();
            }

            var showChatNotification = configuration.XAPeepShowChatNotification;
            if (ImGui.Checkbox("Print chat notification##XAPeep", ref showChatNotification))
            {
                configuration.XAPeepShowChatNotification = showChatNotification;
                SaveConfiguration();
            }

            var soundEffectId = XAPeepData.ClampSoundEffectId(configuration.XAPeepSoundEffectId);
            if (ImGui.BeginCombo("Sound##XAPeep", XAPeepData.GetSoundEffectLabel(soundEffectId)))
            {
                for (var i = 0; i <= XAPeepData.MaxSoundEffectId; i++)
                {
                    var selected = soundEffectId == i;
                    if (ImGui.Selectable(XAPeepData.GetSoundEffectLabel(i), selected))
                    {
                        configuration.XAPeepSoundEffectId = i;
                        configuration.XAPeepPlaySound = i > 0;
                        SaveConfiguration();
                        if (i > 0)
                            plugin.XAPeep.PlayConfiguredSoundPreview();

                        soundEffectId = i;
                    }

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            var soundVolumePercent = Math.Clamp(configuration.XAPeepSoundVolume, 0f, 1f) * 100f;
            if (ImGui.SliderFloat("Alert volume##XAPeep", ref soundVolumePercent, 0f, 100f, "%.0f%%"))
            {
                configuration.XAPeepSoundVolume = Math.Clamp(soundVolumePercent / 100f, 0f, 1f);
                SaveConfiguration();
            }

            if (ImGui.IsItemDeactivatedAfterEdit() && XAPeepData.ClampSoundEffectId(configuration.XAPeepSoundEffectId) > 0)
                plugin.XAPeep.PlayConfiguredSoundPreview();
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
            "Waits for the addon nodes to be ready, refreshes on `PostDraw`, resolves the first incomplete MSQ, and rewrites the visible summary with all-MSQ completion progress from Lumina quest data.",
            plugin.MsqProgressDisplay.StatusText,
            searchTerms: ["Scenario Tree", "main scenario", "remaining", "completion percentage"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "target-command-fix",
            "Fix /target Command",
            () => configuration.TargetCommandFixEnabled,
            plugin.TargetCommandFix.SetEnabled,
            applied => configuration.TargetCommandFixEnabled = applied,
            "Selects the closest targetable actor when the game's `/target` command cannot resolve a visible player or NPC name.",
            "Mirrors the SimpleTweaks `/target` fix by watching failed target-name errors and choosing the closest matching targetable actor from the object table. XA automation also uses the same direct lookup before falling back to the game command.",
            plugin.TargetCommandFix.StatusText,
            searchTerms: ["/target", "SimpleTweaks", "target fix", "Rodney", "NPC", "player"]);
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
            "Lowers the client-side minimum width and height limits, but keeps a guarded 250x200 floor because smaller values have been observed to crash the client. While this toggle is enabled, XA watches the real client size after restore or maximize operations and clamps undersized results back up so rendering can recover cleanly without subclassing the game window.",
            plugin.SystemWindowMods.IgnoreMinimumWindowSizeStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-hide-unnecessary-popups",
            "Hide Unnecessary Popups",
            () => configuration.AutoHideUnnecessaryPopupsEnabled,
            plugin.PopupCleaner.SetEnabled,
            applied => configuration.AutoHideUnnecessaryPopupsEnabled = applied,
            "Closes tutorial and recommendation popups as they appear.",
            "Closes a fixed set of tutorial and recommendation surfaces as they are drawn, including Play Guide, How To, recommendation, launcher, and achievement-style popups. Use the subsetting below if you also want XA to hide `HowToNotice`.",
            plugin.PopupCleaner.StatusText,
            searchTerms: ["HowToNotice", "HowTo", "PlayGuide", "RecommendList", "AchievementInfo"],
            drawOptions: DrawHideUnnecessaryPopupsOptions);
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
            "Monitors the `Dialogue` addon for disconnect/lobby error markers such as `3088`, `5006`, `90002`, `3102`, `Connection with the server was lost.`, and `You are still logged into the game.`, then clicks the live `OK` button automatically. `Instant Logout` also arms this same monitor for 10 seconds even when this toggle is off.",
            plugin.LobbyErrorAutoClose.StatusText,
            searchTerms: ["3088", "5006", "90002", "3102", "Connection with the server was lost.", "You are still logged into the game.", "Dialogue", "OK"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "bailout-esc-menu",
            "Bailout ESC Menu",
            () => configuration.BailoutEscMenuEnabled,
            plugin.EscMenuBailout.SetEnabled,
            applied => configuration.BailoutEscMenuEnabled = applied,
            "Monitors addon:SystemMenu and force-closes it if it sits open too long.",
            "Watches `addon:SystemMenu` on the live client. If the ESC / System menu stays open longer than the selected timer, XA Slave closes it locally through the same direct addon close path used by the debug test button.",
            plugin.EscMenuBailout.StatusText,
            searchTerms: ["SystemMenu", "ESC menu", "escape menu", "close system menu", "timeout", "bailout"],
            drawOptions: DrawBailoutEscMenuOptions);
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
            "Shows the world fade and UI visibility tools while enabled. Turning it off restores the world and any hidden UI groups. `Hide Chat` is blocked while AutoRetainer Multi Mode is active so chat does not disappear during AR multi sessions.",
            configuration.SpecialRenderModesEnabled
                ? plugin.SystemWindowMods.SpecialRenderModesStatusText
                : "Disabled",
            searchTerms: [
                "Background color",
                "Hide world / keep addons",
                "Restore all",
                "Hide addons / keep nameplates",
                "Hide addons / keep chat",
                "Hide chat",
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
            "Masks visible player nameplates locally with deterministic `Firstname Lastname` aliases.",
            "Masks visible player nameplates locally with deterministic CLI/programming aliases such as `CLI Programming`, and removes titles or FC tags from the rewritten plates. The alias choice is keyed from the original character name and world so it stays stable across redraws. This only changes local presentation and does not change server data.",
            plugin.NameplatePrivacy.AnonymousModeStatusText);

        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "anti-afk",
            "Anti-AFK",
            () => configuration.AntiAfkEnabled,
            plugin.AntiAfk.SetEnabled,
            applied => configuration.AntiAfkEnabled = applied,
            "Resets the local AFK timer every 2 minutes so this client stays ahead of the game's 10-minute idle kick path.",
            "Keeps the local AFK timer fresh on a 2-minute cadence while enabled. XA only touches the local idle timer; it does not send chat or movement packets.",
            plugin.AntiAfk.StatusText,
            searchTerms: ["afk", "idle", "kick", "timer"]);
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
            "Lets you choose the local Grand Company rank floor XA spoofs so Expert Delivery can appear at the selected threshold.",
            "Locally spoofs the selected minimum Grand Company rank so the Expert Delivery entry can appear. Use the dropdown below to choose the floor XA returns.",
            plugin.ExpertDeliveryUnlock.StatusText,
            warningText: "DO NOT USE IF YOUR LODESTONE IS NOT SET TO PRIVATE! You take the risk of revealing your character on the leaderboards by using this. If you're not the actual proper rank, it's easy to determine if you're using this.",
            requireCtrlShiftToEnable: true,
            searchTerms: ["GC rank floor", "Grand Company rank", "rank 0", "rank 19", "Storm Captain", "Storm Champion"],
            drawOptions: DrawUnlockExpertDeliveryOptions,
            showOptionsWhenDisabled: true);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-refuse-trade-request",
            "Refuse Trade Request",
            () => configuration.AutoRefuseTradeRequestEnabled,
            plugin.AutoRefuseTrade.SetEnabled,
            applied => configuration.AutoRefuseTradeRequestEnabled = applied,
            "Refuses incoming trade requests automatically.",
            "Refuses incoming trade requests unless this client recently initiated one. The options below control local notifications plus custom /e lines or slash commands that should run after each refusal.",
            plugin.AutoRefuseTrade.StatusText,
            searchTerms: ["Show notification", "Send /e message", "Echo lines", "Commands after refusal", "<trader>", "<target>"],
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
            "auto-leave-duty",
            "Auto Leave Duty",
            () => configuration.AutoLeaveDutyEnabled,
            plugin.AutoLeaveDuty.SetEnabled,
            applied => configuration.AutoLeaveDutyEnabled = applied,
            "Leaves a completed duty automatically after combat and blocking duty UI states clear.",
            "Watches for duty completion, waits the selected delay, then opens the duty menu and confirms Leave Duty once combat, cutscene, and occupied-state blockers are gone. This is meant for normal completed-duty cleanup and does not force an early exit.",
            plugin.AutoLeaveDuty.StatusText,
            searchTerms: ["completed duty", "leave duty", "instance", "dungeon", "raid", "delay", "1-10 sec", "duty menu"],
            drawOptions: DrawAutoLeaveDutyOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-merge",
            "Auto Merge",
            () => configuration.AutoMergeEnabled,
            plugin.AutoMerge.SetEnabled,
            applied => configuration.AutoMergeEnabled = applied,
            "Merges incomplete main-bag stacks after opening the inventory.",
            "Watches for the main inventory window to open, then walks incomplete normal or HQ bag stacks together until the mergeable stacks settle.",
            plugin.AutoMerge.StatusText,
            searchTerms: ["inventory", "stacks", "merge", "bags"]);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "quick-return",
            "Instant Return",
            () => configuration.QuickReturnEnabled,
            plugin.QuickReturn.SetEnabled,
            applied => configuration.QuickReturnEnabled = applied,
            "Skips Return cast/cooldown, leaves the in-game confirmation prompt manual, and stays off in PvP.",
            "Hooks the native Return action so XA can fire the fast return command directly and leave or disband party first when the current party state would otherwise block that path. The in-game confirmation prompt is left for the user.",
            plugin.QuickReturn.StatusText,
            searchTerms: ["Return", "instant return", "instance return", "cast", "cooldown", "instant", "PvP", "party", "disband", "leave party", "manual confirm"],
            requireCtrlShiftToEnable: true);
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
            "Allows Sit and Doze to fire without a nearby bed or chair. The panel buttons and `/xa sit` / `/xa doze` only work while this toggle is enabled, and the same actions can also be added as titlebar favourites in Plugin Operations.",
            plugin.DozeSitAnywhere.StatusText,
            searchTerms: ["/xa sit", "/xa doze", "sit now", "doze now", "titlebar favourite", "emote", "bed", "chair"],
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
            ToonModsSection.IllegalMods,
            "instant-logout",
            "Instant Logout",
            () => configuration.InstantLogoutEnabled,
            plugin.InstantLogout.SetEnabled,
            applied => configuration.InstantLogoutEnabled = applied,
            "Arms hard logout and enables `/xa logout` and `/xa killgame`.",
            "Uses the native contents-finder request path for hard logout. `/xa killgame` waits for the logout to complete, then sends `/xlkill`. When this toggle is off, the panel buttons are hidden and those commands do nothing. Logout and kill-game actions are also blocked while Special Rendering Modes is actively hiding chat.",
            plugin.InstantLogout.StatusText,
            searchTerms: ["logout", "/xa logout", "/xa killgame", "Log out now", "Kill game now"],
            requireCtrlShiftToEnable: true,
            drawOptions: DrawInstantLogoutTool);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "item-commands",
            "Item Commands",
            () => configuration.ItemCommandsEnabled,
            plugin.ItemCommands.SetEnabled,
            applied => configuration.ItemCommandsEnabled = applied,
            "Adds `/xa equip <itemId>`.",
            "Adds an XA-routed item-equip command: `/xa equip <itemId>` equips from the main inventory or armory chest.",
            plugin.ItemCommands.StatusText,
            searchTerms: ["/xa equip", "equip item id", "armory"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "xa-peep",
            "XA Peep",
            () => configuration.XAPeepEnabled,
            plugin.XAPeep.SetEnabled,
            applied => configuration.XAPeepEnabled = applied,
            "XA target tracker with a small cached list and full history window.",
            "Tracks players targeting you in all areas, including PvP, keeps the small XA Peep list and the separate history window available through logout, records cumulative per-player counts in XA Slave's local database, and can show purple cards, lines, dots, center-screen notifications, prefixed chat notifications, and selectable XA alert sounds that still play even if the game's own sound channel is muted. XA Peep can be filtered to skip party, alliance, or in-combat players, can auto-open its compact window on plugin load, and lets you lock or unlock window resizing from the title bar. Use `/xa peep` to open the small window or `/xa peep on|off` to toggle tracking from chat.",
            plugin.XAPeep.StatusText,
            searchTerms: ["Show card when targeted", "Show Targeter's Line", "Show targeter's dot", "targeter line color", "targeter dot color", "targeter dot size", "Show targeters card", "Show center-screen notification", "Print chat notification", "is targeting you", "Log party members", "Log alliance members", "Log players in combat", "Auto open XA Peep on plugin load", "lock", "resize", "/xa peep", "history", "PvP", "sound", "window"],
            drawOptions: DrawXAPeepOptions,
            showOptionsWhenDisabled: true);
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
            "anonymize-character-lists",
            "Anonymize Character Lists",
            () => configuration.GlobalCharacterListAnonymizeEnabled,
            enabled =>
            {
                configuration.GlobalCharacterListAnonymizeEnabled = enabled;
                return enabled;
            },
            applied => configuration.GlobalCharacterListAnonymizeEnabled = applied,
            "Forces screenshot-safe aliases in XA Slave character-list tables.",
            "Applies deterministic CLI/programming/world aliases to XA Slave character-list tables and duplicate summaries. Every task-list `Anonymize` checkbox writes to this same shared global setting, so turning it on in one list carries across the others. Because it is a normal XA Mod, it can also be added as a titlebar favourite.",
            configuration.GlobalCharacterListAnonymizeEnabled
                ? "Enabled - character-list tables and duplicate summaries use deterministic aliases for screenshot-safe local views."
                : "Disabled",
            searchTerms: ["screenshot", "character list", "character lists", "anonymize", "privacy", "titlebar favourite"]);
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
            searchTerms: ["PvP", "Peeping Tom"]);
        AddSavedFeatureEntry(
            ToonModsSection.EurekaMods,
            "eureka-instance-id",
            "Instance ID",
            () => configuration.EurekaInstanceIdEnabled,
            plugin.EurekaInstanceId.SetEnabled,
            applied => configuration.EurekaInstanceIdEnabled = applied,
            "Shows the live Eureka instance ID while you are inside Anemos, Pagos, Pyros, or Hydatos.",
            "Turn this on to enable the live Eureka instance surface and the optional DTR entry. The actual Rodney farming loop now lives in `Field Operations` -> `Eureka Instance Hunter`, where XA can scan any mix of `Anemos`, `Pagos`, `Pyros`, and `Hydatos`, use per-zone baselines, leave duplicate runs through the duty menu with a configurable delay, run CharacterSafeWait in Kugane before Rodney interaction, and stop once a selected zone lands on a different instance.",
            plugin.EurekaInstanceId.StatusText,
            searchTerms: ["Eureka", "instance", "DTR", "server bar", "Field Operations", "Eureka Instance Hunter", "Rodney", "Anemos", "Pagos", "Pyros", "Hydatos", "farming"],
            drawOptions: DrawEurekaInstanceIdOptions,
            showOptionsWhenDisabled: true);

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

        ImGui.SetNextItemWidth(Math.Max(Scale(220f), ImGui.GetContentRegionAvail().X));
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
        DrawModSection(ToonModsSection.EurekaMods, "Eureka");
        DrawModSection(ToonModsSection.IllegalMods, "Illegal Shit You Shouldn't Use");

        if (featureEntries.Count == 0)
        {
            ImGui.TextDisabled("No XA Mods matched the current filter.");
            ImGui.TextDisabled("Filters apply the search text plus the optional enabled-only toggle.");
        }
    }

}
