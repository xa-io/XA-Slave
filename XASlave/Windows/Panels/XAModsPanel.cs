using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using XASlave.Data;
using XASlave.Localization;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private enum ToonModsSection
    {
        GameMods,
        UiMods,
        GraphicMods,
        PlayerMods,
        PluginMods,
        EurekaMods,
        IllegalMods,
    }

    private sealed record XAModCatalogueEntry(
        ToonModsSection Section,
        string Label,
        string SearchHaystack,
        Func<bool> GetCurrent,
        Action Draw);

    private static readonly string[] AutoExpertDeliveryPageLabels = { UiLocale.Native("S06F6143A39676057"), UiLocale.Native("S2FCF7E6BBAEC6128"), UiLocale.Native("S13887C7442815B2B") };
    private static readonly EurekaInstanceIdService.EurekaZone[] EurekaZoneOptions =
    {
        EurekaInstanceIdService.EurekaZone.Anemos,
        EurekaInstanceIdService.EurekaZone.Pagos,
        EurekaInstanceIdService.EurekaZone.Pyros,
        EurekaInstanceIdService.EurekaZone.Hydatos,
    };
    private static readonly (ToonModsSection Section, string Title)[] ToonModsSectionOrder =
    {
        (ToonModsSection.GameMods, UiLocale.Native("S21D4DD0AB4C3AE52")),
        (ToonModsSection.UiMods, UiLocale.Native("S374ADB0F115F2717")),
        (ToonModsSection.GraphicMods, UiLocale.Native("S29D926CC18BA302F")),
        (ToonModsSection.PlayerMods, UiLocale.Native("S4FD5B97AAA9976CF")),
        (ToonModsSection.PluginMods, UiLocale.Native("S1C1183BF04995217")),
        (ToonModsSection.EurekaMods, UiLocale.Native("S4DA75721A5F544BC")),
        (ToonModsSection.IllegalMods, UiLocale.Native("SF18B6FBE0208AC7E")),
    };
    private string toonModsSearchText = string.Empty;
    private string toonModsCachedSearchText = string.Empty;
    private string[] toonModsLowerQueryTerms = [];
    private bool toonModsShowOnlyEnabled;
    private string toonModsSavedListName = string.Empty;
    private string toonModsStatus = string.Empty;
    private global::XASlave.Localization.UiMessage? toonModsStatusUiMessage;
    private DateTime toonModsStatusExpiryUtc = DateTime.MinValue;
    private bool toonModsStatusIsError;
    private int xaModsCustomResolutionWidth = 500;
    private int xaModsCustomResolutionHeight = 345;
    private string xaModsFieldEntryQuery = string.Empty;
    private string dalamudLogDisablerAddInput = string.Empty;
    private string dalamudLogDisablerFilter = string.Empty;
    private string notifyWhenFriendIsNearPatternInput = string.Empty;
    private readonly Dictionary<ToonModsSection, (float StartY, float EndY, string Title)> toonModsSectionScrollRanges = new();
    private readonly List<XAModCatalogueEntry> xaModCatalogue = [];
    private readonly List<(string Key, Func<bool> GetCurrent, Func<bool, bool> Apply, Action<bool> Store)> xaModDefinitions = [];
    private Dictionary<ToonModsSection, XAModCatalogueEntry[]> xaModCatalogueBySection = [];
    private readonly Dictionary<XAModCatalogueEntry, bool> xaModCurrentStateFrame = [];
    private readonly Dictionary<XAModCatalogueEntry, bool> xaModVisibilityFrame = [];
    private IReadOnlyList<ToonModSavedList>? toonModsSortedSavedLists;
    private bool xaModCatalogueBuilt;
    private sealed record PendingXAModToggle(string Label, bool Value);
    private PendingXAModToggle? xaModPendingToggle;

    private bool GetDisplayedXAModValue(string label, bool currentValue)
    {
        var pending = System.Threading.Volatile.Read(ref xaModPendingToggle);
        return pending != null && pending.Label == label ? pending.Value : currentValue;
    }

    private float toonModsSectionsScrollY;
    private static readonly JsonSerializerOptions toonModsListJsonOptions = ToonModsPresetSerialization.JsonOptions;

    private bool GetToonModsSectionExpanded(ToonModsSection section)
    {
        return section switch
        {
            ToonModsSection.GameMods => plugin.Configuration.ToonModsGameModsExpanded,
            ToonModsSection.UiMods => plugin.Configuration.ToonModsUiModsExpanded,
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
            case ToonModsSection.UiMods:
                if (plugin.Configuration.ToonModsUiModsExpanded == expanded)
                    return;

                plugin.Configuration.ToonModsUiModsExpanded = expanded;
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

        plugin.Configuration.SaveDeferred();
    }

    private void DrawXAModsTask()
    {
        var configuration = plugin.Configuration;
        var featureEntries = xaModCatalogue;
        var toonModDefinitions = xaModDefinitions;
        var buildCatalogue = !xaModCatalogueBuilt;
        if (buildCatalogue)
        {
            featureEntries.Clear();
            toonModDefinitions.Clear();
            xaModCatalogueBySection.Clear();
        }

        if (!string.Equals(toonModsCachedSearchText, toonModsSearchText, StringComparison.Ordinal))
        {
            toonModsCachedSearchText = toonModsSearchText;
            toonModsLowerQueryTerms = toonModsSearchText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => term.ToLowerInvariant())
                .ToArray();
        }

        void SaveConfiguration()
        {
            configuration.SaveDeferred();
        }

        void SetToonModsStatus(global::XASlave.Localization.UiMessage message, bool isError = false)
        {
            toonModsStatus = global::XASlave.Localization.UiMessage.Store(ref toonModsStatusUiMessage, message);
            toonModsStatusIsError = isError;
            toonModsStatusExpiryUtc = DateTime.UtcNow.AddSeconds(8);
        }

        void DrawSectionHeader(string text)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), UiLocale.T(text));
            ImGui.Separator();
            ImGui.Spacing();
        }

        void DrawHelpMarker(string helpText)
        {
            ImGui.TextDisabled("(?)");
            if (!ImGui.IsItemHovered())
                return;

            using (ImRaii.Tooltip())
            using (ImRaii.TextWrapPos(Scale(460f)))
                ImGui.TextUnformatted(UiLocale.T(helpText));
        }

        void DrawWarningText(string warningText)
        {
            if (string.IsNullOrWhiteSpace(warningText))
                return;

            using (ImRaii.TextWrapPos(Scale(460f)))
                ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.25f, 1.0f), UiLocale.T(warningText));
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

        void ApplyCustomTimestampFormatConfiguration()
        {
            configuration.CustomTimestampFormat = ChatTimestampFormatService.NormalizeFormat(configuration.CustomTimestampFormat);
            plugin.ChatTimestampFormat.ApplyConfiguration(configuration.CustomTimestampFormat);
        }

        void ApplyAutoSkipCutscenesConfiguration()
        {
            plugin.AutoSkipCutscenes.ApplyConfiguration(configuration);
            plugin.BuddyFeedCutsceneSkip.SetEnabled(configuration.AutoSkipCutscenesFeedingChocoboEnabled);
        }

        void ApplyBetterHighlightPotentialTargetsConfiguration()
        {
            plugin.ApplyBetterHighlightPotentialTargetsConfiguration(save: false);
        }

        void ApplyShowTravelerWorldNamesConfiguration()
        {
            plugin.NameplatePrivacy.ApplyShowTravelerWorldNamesConfiguration(
                configuration.ShowTravelerWorldNamesDisableInDuties,
                configuration.ShowTravelerWorldNamesAddSpacer);
        }

        bool SetHideNameplateStatusIconsEnabled(bool value)
        {
            plugin.NameplatePrivacy.ApplyStatusIconConfiguration(configuration.HiddenNameplateStatusIds);
            return plugin.NameplatePrivacy.SetHideStatusIconsEnabled(value);
        }

        void ApplyShowTitlesAsPlayernamesConfiguration()
        {
            plugin.NameplatePrivacy.ApplyShowTitlesAsPlayernamesConfiguration(
                configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled);
        }

        bool SetShowTravelerWorldNamesEnabled(bool value)
        {
            ApplyShowTravelerWorldNamesConfiguration();
            return plugin.NameplatePrivacy.SetShowTravelerWorldNamesEnabled(value);
        }

        bool SetShowTitlesAsPlayernamesEnabled(bool value)
        {
            ApplyShowTitlesAsPlayernamesConfiguration();
            return plugin.NameplatePrivacy.SetShowTitlesAsPlayernamesEnabled(value);
        }

        void ApplyAutoHideGameObjectsConfiguration()
        {
            plugin.NormalizeAutoHideGameObjectsDistances();
            plugin.AutoHideGameObjects.ApplyConfiguration(
                configuration.AutoHideGameObjectsHidePlayer,
                configuration.AutoHideGameObjectsHideUnimportantEnpc,
                configuration.AutoHideGameObjectsHidePet,
                configuration.AutoHideGameObjectsHideChocobo,
                configuration.AutoHideGameObjectsDisableInDuties,
                configuration.AutoHideGameObjectsDisableInIslandSanctuary,
                configuration.AutoHideGameObjectsUseOccultCrescentRules,
                configuration.AutoHideGameObjectsHideBeasts,
                configuration.AutoHideGameObjectsHideFashionAccessories,
                configuration.AutoHideGameObjectsHideOwnBeast,
                configuration.AutoHideGameObjectsHideFriends,
                configuration.AutoHideGameObjectsHidePartyAllianceMembers,
                usePlayerDistance: configuration.AutoHideGameObjectsUsePlayerDistance,
                honorSanctuaryMinimums: configuration.AutoHideGameObjectsHonorSanctuaryMinimums,
                sanctuaryDistance: configuration.AutoHideGameObjectsSanctuaryDistance,
                maxDistance: configuration.AutoHideGameObjectsMaxDistance);
        }

        void ApplyAutoDisplayNetworkLatencyConfiguration()
        {
            plugin.ApplyAutoDisplayNetworkLatencyConfiguration();
        }

        void ApplyNotifyWhenFriendIsNearConfiguration()
        {
            plugin.ApplyNotifyWhenFriendIsNearConfiguration(legacyEdit: true);
        }

        void ApplyAlertWhenTypingInCombatConfiguration()
        {
            plugin.ApplyAlertWhenTypingInCombatConfiguration();
        }

        void ApplyBetterCastBarConfiguration()
        {
            plugin.ApplyBetterCastBarConfiguration();
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
                configuration.AutoUnlockExpertDeliveryIgnoreSealCap,
                configuration.AutoUnlockExpertDeliverySpeedProfile,
                configuration.AutoUnlockExpertDeliveryItemScope,
                configuration.AutoUnlockExpertDeliveryUseArProtection,
                configuration.AutoUnlockExpertDeliveryProtectedItemIds,
                configuration.AutoUnlockExpertDeliveryCloseOnCompletion,
                configuration.AutoUnlockExpertDeliveryNotifyOutcome,
                configuration.AutoUnlockExpertDeliveryRunCompletionCommand,
                configuration.AutoUnlockExpertDeliveryCompletionCommand);
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

        void ApplyTeleportHelperConfiguration()
        {
            plugin.TeleportHelper.ApplyConfiguration(configuration.TeleportHelperSelectYes);
        }

        void ApplySightDistanceConfiguration()
        {
            NormalizeSightDistanceConfiguration();
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

        void NormalizeSightDistanceConfiguration()
        {
            static float ClampFinite(float value, float minimum, float maximum, float fallback)
                => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);

            configuration.CustomSightDistanceMaxDistance = ClampFinite(configuration.CustomSightDistanceMaxDistance, 1f, 80f, 80f);
            configuration.CustomSightDistanceMinDistance = ClampFinite(configuration.CustomSightDistanceMinDistance, 0f, configuration.CustomSightDistanceMaxDistance, 1.5f);
            configuration.CustomSightDistanceMaxRotation = ClampFinite(configuration.CustomSightDistanceMaxRotation, -1.569f, 1.569f, 1.569f);
            configuration.CustomSightDistanceMinRotation = ClampFinite(configuration.CustomSightDistanceMinRotation, -1.569f, configuration.CustomSightDistanceMaxRotation, -1.483530f);
            configuration.CustomSightDistanceMaxFoV = ClampFinite(configuration.CustomSightDistanceMaxFoV, 0.01f, 3f, 0.78f);
            configuration.CustomSightDistanceMinFoV = ClampFinite(configuration.CustomSightDistanceMinFoV, 0.01f, configuration.CustomSightDistanceMaxFoV, 0.69f);
            configuration.CustomSightDistanceFoV = ClampFinite(configuration.CustomSightDistanceFoV, configuration.CustomSightDistanceMinFoV, configuration.CustomSightDistanceMaxFoV, configuration.CustomSightDistanceMaxFoV);
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

        void ApplyDalamudNotificationsSuckConfiguration()
        {
            plugin.ApplyDalamudNotificationsSuckConfiguration(save: false);
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
            var value = GetDisplayedXAModValue(label, currentValue);
            var modifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            var toggled = false;

            var enableBlocked = requireCtrlShiftToEnable && !value && !modifierHeld;
            using (ImRaii.Disabled(enableBlocked))
                toggled = UiNative.Checkbox(label, ref value);

            if (enableBlocked)
            {
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    using (ImRaii.Tooltip())
                    using (ImRaii.TextWrapPos(Scale(320f)))
                        ImGui.TextUnformatted(UiLocale.Get("SBD6F4557C0132C9C"));
                }
            }

            if (toggled)
            {
                // Keep the requested value visible across frames until the game-thread
                // mutation reports its result. The configuration remains authoritative.
                var pending = new PendingXAModToggle(label, value);
                var previous = System.Threading.Interlocked.CompareExchange(ref xaModPendingToggle, pending, null);
                var queueMessage = global::XASlave.Localization.UiMessage.Key("SFEFEE9C8F72267C3");
                var queued = previous == null && plugin.QueueXAModToggleMessage(
                    label,
                    value,
                    apply,
                    store,
                    (success, resultMessage) =>
                    {
                        System.Threading.Interlocked.CompareExchange(ref xaModPendingToggle, null, pending);
                        SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", resultMessage), !success);
                    },
                    out queueMessage);
                if (!queued)
                    System.Threading.Interlocked.CompareExchange(ref xaModPendingToggle, null, pending);
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", queueMessage), !queued);
            }

            ImGui.SameLine(0f, 6f);
            DrawHelpMarker(helpText);

            if (value)
            {
                ImGui.TextDisabled(UiLocale.T(description));
                ImGui.TextDisabled(UiLocale.FormatKey("S390591EA8DC1971A", status));
                DrawWarningText(warningText ?? string.Empty);

                if (drawOptions != null)
                {
                    ImGui.Indent();
                    drawOptions();
                    ImGui.Unindent();
                }
            }

            ImGui.Spacing();
        }

        static string BuildToonModsSearchHaystack(string label, string description, string helpText, string[]? extraTerms)
        {
            var haystack = $"{label}\n{description}\n{helpText}";
            if (extraTerms is { Length: > 0 })
                haystack = $"{haystack}\n{string.Join('\n', extraTerms)}";

            return haystack.ToLowerInvariant();
        }

        bool MatchesToonModsSearch(string searchHaystack)
        {
            foreach (var term in toonModsLowerQueryTerms)
            {
                if (!searchHaystack.Contains(term, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        void AddFeatureEntry(
            ToonModsSection section,
            string label,
            Func<bool> getCurrent,
            Func<bool, bool> apply,
            Action<bool> store,
            string description,
            string helpText,
            Func<string> getStatus,
            string? warningText = null,
            bool requireCtrlShiftToEnable = false,
            string[]? searchTerms = null,
            Action? drawOptions = null)
        {
            var searchHaystack = BuildToonModsSearchHaystack(label, description, helpText, searchTerms);
            featureEntries.Add(new XAModCatalogueEntry(
                section,
                label,
                searchHaystack,
                getCurrent,
                () => DrawFeatureToggle(
                    label,
                    getCurrent(),
                    apply,
                    store,
                    description,
                    helpText,
                    getStatus(),
                    warningText,
                    requireCtrlShiftToEnable,
                    drawOptions)));
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
            Func<string> getStatus,
            string? warningText = null,
            bool requireCtrlShiftToEnable = false,
            string[]? searchTerms = null,
            Action? drawOptions = null)
        {
            toonModDefinitions.Add((key, getCurrent, apply, store));
            AddFeatureEntry(section, label, getCurrent, apply, store, description, helpText, getStatus, warningText, requireCtrlShiftToEnable, searchTerms, drawOptions);
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
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SBFBF70522A84EDDA"), true);
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

            configuration.Save();
            toonModsSortedSavedLists = null;
            toonModsSavedListName = name;
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S253C399FBE534FBD", name, modKeys.Count));
        }

        void ApplyToonModsList(string title, IEnumerable<string> modKeys, IReadOnlyDictionary<string, JsonElement>? modSettings = null)
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "XA Mods Selection" : title.Trim();
            toonModsSavedListName = safeTitle;
            var queued = plugin.ApplySavedXAModsPresetMessage(
                safeTitle,
                modKeys,
                modSettings,
                (success, resultMessage) => SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", resultMessage), !success),
                out var queueMessage);
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", queueMessage), !queued);
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
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S9BC71D831AA90FB9", package.Title, package.ModKeys.Count));
        }

        void ExportSavedToonModsList(ToonModSavedList saved)
        {
            var package = new ToonModsListPackage
            {
                ListId = Guid.NewGuid().ToString("N"),
                Title = saved.Name,
                ExportedAtUtc = DateTime.UtcNow,
                ModKeys = saved.ModKeys,
                ModSettings = saved.ModSettings,
            };

            ImGui.SetClipboardText(JsonSerializer.Serialize(package, toonModsListJsonOptions));
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S3760DDEA005969FB", package.Title, package.ModKeys.Count));
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

        bool TryImportToonModsList(string clipboardText, out ToonModsListPackage package, out global::XASlave.Localization.UiMessage message)
        {
            const int maxPresetCharacters = 512 * 1024;
            const int supportedSchemaVersion = 2;
            package = new ToonModsListPackage();

            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                message = global::XASlave.Localization.UiMessage.Key("SC7025247BE6A4F86");
                return false;
            }

            if (clipboardText.Length > maxPresetCharacters)
            {
                message = global::XASlave.Localization.UiMessage.Format("S64FC7795D2FE1207", clipboardText.Length, maxPresetCharacters);
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(clipboardText);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    message = global::XASlave.Localization.UiMessage.Key("SC7025247BE6A4F86");
                    return false;
                }

                if (TryGetToonModsJsonProperty(root, "schemaVersion", out var schemaVersionElement)
                    && schemaVersionElement.ValueKind == JsonValueKind.Number
                    && schemaVersionElement.TryGetInt32(out var schemaVersion))
                {
                    package.SchemaVersion = schemaVersion;
                }

                if (package.SchemaVersion <= 0 || package.SchemaVersion > supportedSchemaVersion)
                {
                    message = global::XASlave.Localization.UiMessage.Format("S6E675EDC8C0167C1", package.SchemaVersion, supportedSchemaVersion);
                    return false;
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
                    message = global::XASlave.Localization.UiMessage.Key("SC7025247BE6A4F86");
                    return false;
                }

                package.ModKeys = modKeysElement
                    .EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString() ?? string.Empty)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var unknownKeys = package.ModKeys.Where(key => !plugin.IsKnownXAModKey(key)).Take(6).ToList();
                if (unknownKeys.Count > 0)
                {
                    message = global::XASlave.Localization.UiMessage.Format("SA84872392B69F91D", string.Join(", ", unknownKeys));
                    return false;
                }

                if (TryGetToonModsJsonProperty(root, "modSettings", out var modSettingsElement)
                    && modSettingsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in modSettingsElement.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(property.Name) || !plugin.IsKnownXAModKey(property.Name))
                            continue;

                        package.ModSettings[property.Name] = property.Value.Clone();
                    }
                }

                if (string.IsNullOrWhiteSpace(package.Title))
                    package.Title = "Imported XA Mods";

                message = global::XASlave.Localization.UiMessage.Raw(string.Empty);
                return true;
            }
            catch
            {
                message = global::XASlave.Localization.UiMessage.Key("SC7025247BE6A4F86");
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
            using var popup = ImRaii.Popup("ToonModsSaveListPopup");
            if (!popup)
                return;

            ImGui.SetNextItemWidth(Scale(220f));
            UiNative.InputTextWithHint("##ToonModsSaveListName", UiLocale.Get("SA3C674C0BAE2100E"), ref toonModsSavedListName, 128);
            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("SC53FE13AB16A705F")))
                SaveCurrentToonModsList();

        }

        void DrawToonModsLoadListPopup()
        {
            using var popup = ImRaii.Popup("ToonModsLoadListPopup");
            if (!popup)
                return;

            if (configuration.ToonModsSavedLists.Count == 0)
            {
                ImGui.TextDisabled(UiLocale.Get("SB908027C027C465D"));
                return;
            }

            toonModsSortedSavedLists ??= configuration.ToonModsSavedLists
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var saved in toonModsSortedSavedLists)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S0E4327EE7CC93C1D"), saved.Name)))
                    {
                        configuration.ToonModsSavedLists.RemoveAll(entry => entry.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
                        configuration.Save();
                        toonModsSortedSavedLists = null;
                        SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SC083F2ECBB753190", saved.Name));
                        break;
                    }
                }
                ImGui.SameLine();
                ImGui.TextDisabled(UiLocale.FormatKey("SEC813698BE55A9C1", saved.ModKeys.Count));
                ImGui.SameLine();
                if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S970F252A41668B8E"), saved.Name)))
                    ApplyToonModsList(saved.Name, saved.ModKeys, saved.ModSettings);

                ImGui.SameLine();
                if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S72E48C7E7C481D78"), saved.Name)))
                    ExportSavedToonModsList(saved);

                ImGui.SameLine();
                ImGui.TextUnformatted(saved.Name);
            }
        }

        IReadOnlyList<XAModCatalogueEntry> GetSortedSectionEntries(ToonModsSection section)
        {
            return xaModCatalogueBySection.TryGetValue(section, out var entries)
                ? entries
                : Array.Empty<XAModCatalogueEntry>();
        }

        bool GetToonModEntryCurrent(XAModCatalogueEntry entry)
        {
            if (xaModCurrentStateFrame.TryGetValue(entry, out var current))
                return current;

            current = GetDisplayedXAModValue(entry.Label, entry.GetCurrent());
            xaModCurrentStateFrame[entry] = current;
            return current;
        }

        bool IsToonModEntryVisible(XAModCatalogueEntry entry)
        {
            if (xaModVisibilityFrame.TryGetValue(entry, out var visible))
                return visible;

            visible = (!toonModsShowOnlyEnabled || GetToonModEntryCurrent(entry))
                && MatchesToonModsSearch(entry.SearchHaystack);
            xaModVisibilityFrame[entry] = visible;
            return visible;
        }

        void DrawModSection(ToonModsSection section, string title)
        {
            var sectionEntries = GetSortedSectionEntries(section);
            var visibleCount = 0;
            foreach (var entry in sectionEntries)
            {
                if (IsToonModEntryVisible(entry))
                    visibleCount++;
            }
            if (visibleCount == 0)
                return;

            var searchActive = !string.IsNullOrWhiteSpace(toonModsSearchText);
            if (searchActive)
            {
                DrawSectionHeader($"{UiLocale.T(title)} ({visibleCount.ToString(UiLocale.Culture)})");
                foreach (var entry in sectionEntries)
                {
                    if (IsToonModEntryVisible(entry))
                        entry.Draw();
                }

                return;
            }

            var sectionStartY = ImGui.GetCursorPosY();

            ImGui.Spacing();
            bool isOpen;
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.6f, 0.9f, 1.0f, 1.0f)))
            {
                ImGui.SetNextItemOpen(GetToonModsSectionExpanded(section), ImGuiCond.Always);
                isOpen = UiNative.CollapsingHeader(title);
            }

            if (isOpen != GetToonModsSectionExpanded(section))
                SetToonModsSectionExpanded(section, isOpen);

            if (!isOpen)
            {
                toonModsSectionScrollRanges[section] = (sectionStartY, ImGui.GetCursorPosY(), title);
                return;
            }

            ImGui.Indent();
            foreach (var entry in sectionEntries)
            {
                if (IsToonModEntryVisible(entry))
                    entry.Draw();
            }

            ImGui.Unindent();
            toonModsSectionScrollRanges[section] = (sectionStartY, ImGui.GetCursorPosY(), title);
        }

        (ToonModsSection Section, string Title)? GetStickyToonModsSection()
        {
            if (!string.IsNullOrWhiteSpace(toonModsSearchText) || toonModsSectionsScrollY <= 0f)
                return null;

            var headerHeight = ImGui.GetFrameHeightWithSpacing();
            foreach (var (section, title) in ToonModsSectionOrder)
            {
                if (!GetToonModsSectionExpanded(section))
                    continue;

                if (!toonModsSectionScrollRanges.TryGetValue(section, out var range))
                    continue;

                if (toonModsSectionsScrollY >= range.StartY + headerHeight
                    && toonModsSectionsScrollY < range.EndY - headerHeight)
                    return (section, title);
            }

            return null;
        }

        void DrawStickyToonModsSectionHeader()
        {
            var stickySection = GetStickyToonModsSection();
            if (stickySection is not { } sticky)
                return;

            using (var colors = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.12f, 0.24f, 0.30f, 1.0f)))
            {
                colors.Push(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.34f, 0.42f, 1.0f));
                colors.Push(ImGuiCol.ButtonActive, new Vector4(0.10f, 0.20f, 0.26f, 1.0f));
                if (UiNative.Button($"{sticky.Title}  ^##ToonModsStickySection{sticky.Section}", new Vector2(ImGui.GetContentRegionAvail().X, 0f), display: UiLocale.T(sticky.Title) + "  ^"))
                    SetToonModsSectionExpanded(sticky.Section, false);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(UiLocale.FormatKey("S61EB872370E11914", UiLocale.T(sticky.Title)));
        }

        void DisableAllMods()
        {
            var queued = plugin.QueueDisableAllXAModsMessage(
                (success, resultMessage) => SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", resultMessage), !success),
                out var queueMessage);
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", queueMessage), !queued);
        }

        void DrawBackgroundRenderingOptions()
        {
            var onlyWhenMinimized = configuration.DisableBackgroundGameRenderingOnlyWhenMinimized;
            if (UiNative.Checkbox(UiLocale.Native("S80CE5516FA40E79D"), ref onlyWhenMinimized))
            {
                configuration.DisableBackgroundGameRenderingOnlyWhenMinimized = onlyWhenMinimized;
                ApplyBackgroundRenderingConfiguration();
                SaveConfiguration();
            }

            var disableWhenArMultiIsOn = configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn;
            if (UiNative.Checkbox(UiLocale.Native("S6DE3D972285AD240"), ref disableWhenArMultiIsOn))
            {
                configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn = disableWhenArMultiIsOn;
                ApplyBackgroundRenderingConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(onlyWhenMinimized
                ? UiLocale.Get("SA93172537012F10C")
                : UiLocale.Get("S6E82A663293A0C42"));
            ImGui.TextDisabled(disableWhenArMultiIsOn
                ? UiLocale.Get("S967775BD584B5A90")
                : UiLocale.Get("S61147AA8A0F6554A"));
        }

        void DrawAutoHideGameObjectsOptions()
        {
            var changed = false;

            var hideFriends = configuration.AutoHideGameObjectsHideFriends;
            if (UiNative.Checkbox(UiLocale.Native("S69FE5811D3D851E9"), ref hideFriends))
            {
                configuration.AutoHideGameObjectsHideFriends = hideFriends;
                changed = true;
            }

            var hidePartyAllianceMembers = configuration.AutoHideGameObjectsHidePartyAllianceMembers;
            if (UiNative.Checkbox(UiLocale.Native("S6A9088294DABB311"), ref hidePartyAllianceMembers))
            {
                configuration.AutoHideGameObjectsHidePartyAllianceMembers = hidePartyAllianceMembers;
                changed = true;
            }

            var hidePlayer = configuration.AutoHideGameObjectsHidePlayer;
            if (UiNative.Checkbox(UiLocale.Native("S05C8EADFB598A02C"), ref hidePlayer))
            {
                configuration.AutoHideGameObjectsHidePlayer = hidePlayer;
                changed = true;
            }

            var usePlayerDistance = configuration.AutoHideGameObjectsUsePlayerDistance;
            if (UiNative.Checkbox(UiLocale.Native("S5529023589FAD36C"), ref usePlayerDistance))
            {
                configuration.AutoHideGameObjectsUsePlayerDistance = usePlayerDistance;
                changed = true;
            }

            using (ImRaii.Disabled(!usePlayerDistance))
            {
                var honorSanctuaryMinimums = configuration.AutoHideGameObjectsHonorSanctuaryMinimums;
                if (UiNative.Checkbox(UiLocale.Native("SBBA90A4C63473F93"), ref honorSanctuaryMinimums))
                {
                    configuration.AutoHideGameObjectsHonorSanctuaryMinimums = honorSanctuaryMinimums;
                    changed = true;
                }

                var sanctuaryDistance = configuration.AutoHideGameObjectsSanctuaryDistance;
                var maxDistance = configuration.AutoHideGameObjectsMaxDistance;
                AutoHideGameObjectsService.NormalizePlayerDistances(ref sanctuaryDistance, ref maxDistance);
                var distanceChanged = false;
                using (ImRaii.Disabled(!honorSanctuaryMinimums))
                {
                    distanceChanged |= UiNative.SliderFloat(
                        UiLocale.Native("SD6C38ADF38BFC7C4"),
                        ref sanctuaryDistance,
                        AutoHideGameObjectsService.MinimumPlayerDistance,
                        maxDistance,
                        "%.1f",
                        ImGuiSliderFlags.AlwaysClamp);
                }

                distanceChanged |= UiNative.SliderFloat(
                    UiLocale.Native("SE9BDD043948C1D2B"),
                    ref maxDistance,
                    AutoHideGameObjectsService.MinimumPlayerDistance,
                    AutoHideGameObjectsService.MaximumPlayerDistance,
                    "%.1f",
                    ImGuiSliderFlags.AlwaysClamp);
                if (distanceChanged)
                {
                    AutoHideGameObjectsService.NormalizePlayerDistances(ref sanctuaryDistance, ref maxDistance);
                    configuration.AutoHideGameObjectsSanctuaryDistance = sanctuaryDistance;
                    configuration.AutoHideGameObjectsMaxDistance = maxDistance;
                    changed = true;
                }
            }

            using (ImRaii.TextWrapPos(0f))
            {
                ImGui.TextDisabled(UiLocale.Get("S932923F99012D401"));
                ImGui.TextDisabled(UiLocale.Get("S89D1DF8BAC8B65BF"));
                ImGui.TextDisabled(UiLocale.Get("SE9413A40C3DA2A20"));
            }

            var hideUnimportantEnpc = configuration.AutoHideGameObjectsHideUnimportantEnpc;
            if (UiNative.Checkbox(UiLocale.Native("SD9F9831AE05B008F"), ref hideUnimportantEnpc))
            {
                configuration.AutoHideGameObjectsHideUnimportantEnpc = hideUnimportantEnpc;
                changed = true;
            }

            var hidePet = configuration.AutoHideGameObjectsHidePet;
            if (UiNative.Checkbox(UiLocale.Native("S494BC26EF3290B33"), ref hidePet))
            {
                configuration.AutoHideGameObjectsHidePet = hidePet;
                changed = true;
            }

            var hideFashionAccessories = configuration.AutoHideGameObjectsHideFashionAccessories;
            if (UiNative.Checkbox(UiLocale.Native("S79CBE223D6C58B3E"), ref hideFashionAccessories))
            {
                configuration.AutoHideGameObjectsHideFashionAccessories = hideFashionAccessories;
                changed = true;
            }

            var hideChocobo = configuration.AutoHideGameObjectsHideChocobo;
            if (UiNative.Checkbox(UiLocale.Native("SBF4B8AC50C306B22"), ref hideChocobo))
            {
                configuration.AutoHideGameObjectsHideChocobo = hideChocobo;
                changed = true;
            }

            var hideBeasts = configuration.AutoHideGameObjectsHideBeasts;
            if (UiNative.Checkbox(UiLocale.Native("SD4D1624A74D3FFD1"), ref hideBeasts))
            {
                configuration.AutoHideGameObjectsHideBeasts = hideBeasts;
                changed = true;
            }

            var hideOwnBeast = configuration.AutoHideGameObjectsHideOwnBeast;
            if (UiNative.Checkbox(UiLocale.Native("S26E59ED999A1B224"), ref hideOwnBeast))
            {
                configuration.AutoHideGameObjectsHideOwnBeast = hideOwnBeast;
                changed = true;
            }

            var disableInDuties = configuration.AutoHideGameObjectsDisableInDuties;
            if (UiNative.Checkbox(UiLocale.Native("SD7A4151FDBE01D7D"), ref disableInDuties))
            {
                configuration.AutoHideGameObjectsDisableInDuties = disableInDuties;
                changed = true;
            }

            var disableInIslandSanctuary = configuration.AutoHideGameObjectsDisableInIslandSanctuary;
            if (UiNative.Checkbox(UiLocale.Native("S6BC797000B0A3B62"), ref disableInIslandSanctuary))
            {
                configuration.AutoHideGameObjectsDisableInIslandSanctuary = disableInIslandSanctuary;
                changed = true;
            }

            var useOccultCrescentRules = configuration.AutoHideGameObjectsUseOccultCrescentRules;
            if (UiNative.Checkbox(UiLocale.Native("SD1D9FD870EA09651"), ref useOccultCrescentRules))
            {
                configuration.AutoHideGameObjectsUseOccultCrescentRules = useOccultCrescentRules;
                changed = true;
            }

            if (changed)
            {
                ApplyAutoHideGameObjectsConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S72A970DB68C3429B"));
            ImGui.TextDisabled(UiLocale.Get("S992948DFF69349AA"));
        }

        void DrawHideUnnecessaryPopupsOptions()
        {
            var hideHowToNotice = configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled;
            if (UiNative.Checkbox(UiLocale.Native("SEC0AAB8E283BFD17"), ref hideHowToNotice))
            {
                configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled = hideHowToNotice;
                ApplyPopupCleanerConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(hideHowToNotice
                ? UiLocale.Get("SD5B48271CB9E810C")
                : UiLocale.Get("S94142A849C3B5CB0"));
        }

        void DrawPlayerSearchOptions()
        {
            var configChanged = false;

            var ffLogsEnabled = configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled;
            if (UiNative.Checkbox(UiLocale.Native("S6865DF30AFAB7774"), ref ffLogsEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled = ffLogsEnabled;
                configChanged = true;
            }

            var lodestoneEnabled = configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled;
            if (UiNative.Checkbox(UiLocale.Native("S306F1AF001729448"), ref lodestoneEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled = lodestoneEnabled;
                configChanged = true;
            }

            var lalachievementsEnabled = configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled;
            if (UiNative.Checkbox(UiLocale.Native("SD808F93F8B522EEC"), ref lalachievementsEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled = lalachievementsEnabled;
                configChanged = true;
            }

            var openAllEnabled = configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled;
            if (UiNative.Checkbox(UiLocale.Native("S41EB6B588355959D"), ref openAllEnabled))
            {
                configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled = openAllEnabled;
                configChanged = true;
            }

            if (configChanged)
            {
                ApplyPlayerSearchConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S2D3EC73A636A212F"));
        }

        void DrawSpecialRenderToggle(string label, string id, bool currentValue, Action<bool> store, string blockedEnableMessage = "")
        {
            var value = currentValue;
            var disableEnablePath = !currentValue && !string.IsNullOrWhiteSpace(blockedEnableMessage);
            bool changed;
            using (ImRaii.Disabled(disableEnablePath))
                changed = UiNative.Checkbox($"{label}##{id}", ref value);

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
            var worldFadeAvailable = plugin.SystemWindowMods.IsSpecialRenderWorldFadeAvailable;

            void DrawWorldFadeButton(string label, bool hidden)
            {
                using (ImRaii.Disabled(!worldFadeAvailable))
                {
                    if (UiNative.Button(label))
                        plugin.SystemWindowMods.SetSpecialRenderWorldHidden(hidden, backgroundColor);
                }

                if (!worldFadeAvailable)
                {
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(UiLocale.Get("S6D6CF7B9F3456430"));
                }
            }

            if (UiNative.ColorEdit4(UiLocale.Native("S32DCE8B0B811F578"), ref backgroundColor))
            {
                configuration.SpecialRenderModeBackgroundColorR = backgroundColor.X;
                configuration.SpecialRenderModeBackgroundColorG = backgroundColor.Y;
                configuration.SpecialRenderModeBackgroundColorB = backgroundColor.Z;
                configuration.SpecialRenderModeBackgroundColorA = backgroundColor.W;
                SaveConfiguration();
            }

            DrawWorldFadeButton(UiLocale.Native("SDCA4FE3BECDCC506"), true);
            ImGui.SameLine();
            DrawWorldFadeButton(UiLocale.Native("S44C2C9AD06C5C149"), false);

            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("SDF02843E4E8A476E")))
            {
                RestoreSpecialRenderModes(clearStoredUiToggles: true);
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S223A57AA3F849F74"));
            ImGui.TextDisabled(UiLocale.Get("SA33D91AF9B79CE42"));
            if (!plugin.CanEnableSpecialRenderHideChat(out var hideChatBlockedMessage))
                ImGui.TextDisabled(hideChatBlockedMessage);

            DrawSpecialRenderToggle(
                UiLocale.Native("S6ECB725DD7371264"),
                "SpecialRenderHideAddonsKeepNameplates",
                configuration.SpecialRenderHideAddonsKeepNameplatesEnabled,
                value => configuration.SpecialRenderHideAddonsKeepNameplatesEnabled = value);

            DrawSpecialRenderToggle(
                UiLocale.Native("S87AF1E4B608FFC77"),
                "SpecialRenderHideAddonsKeepChat",
                configuration.SpecialRenderHideAddonsKeepChatEnabled,
                value => configuration.SpecialRenderHideAddonsKeepChatEnabled = value);

            DrawSpecialRenderToggle(
                UiLocale.Native("S1ED9F9BF080A444B"),
                "SpecialRenderHideChat",
                configuration.SpecialRenderHideChatEnabled,
                value => configuration.SpecialRenderHideChatEnabled = value,
                hideChatBlockedMessage);
            DrawSpecialRenderToggle(
                UiLocale.Native("S0F9816C6DFF380D2"),
                "SpecialRenderHideActionBars",
                configuration.SpecialRenderHideActionBarsEnabled,
                value => configuration.SpecialRenderHideActionBarsEnabled = value);
            DrawSpecialRenderToggle(
                UiLocale.Native("S6268111E849ED78F"),
                "SpecialRenderHideTargetInfo",
                configuration.SpecialRenderHideTargetInfoEnabled,
                value => configuration.SpecialRenderHideTargetInfoEnabled = value);
            DrawSpecialRenderToggle(
                UiLocale.Native("S1A218F4BA3356A8D"),
                "SpecialRenderHideNameplates",
                configuration.SpecialRenderHideNameplatesEnabled,
                value => configuration.SpecialRenderHideNameplatesEnabled = value);
        }

        void AddCustomResolutionPreset()
        {
            if (!SystemWindowModsService.TryNormalizeCustomResolutionMessage(
                    xaModsCustomResolutionWidth,
                    xaModsCustomResolutionHeight,
                    out var width,
                    out var height,
                    out var message))
            {
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", message), true);
                return;
            }

            if (configuration.CustomResolutionPresets.Any(entry => entry.Width == width && entry.Height == height))
            {
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD8DCFE61B7BEB9FF", width, height), true);
                return;
            }

            configuration.CustomResolutionPresets.Add(new XAModResolutionPreset
            {
                Width = width,
                Height = height,
            });
            SaveConfiguration();
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S6B34DF1D9EDDF1A6", width, height));
        }

        void ApplyCustomResolutionFromUi(int width, int height)
        {
            if (plugin.SystemWindowMods.TryApplyCustomResolutionMessage(width, height, out var message))
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", message));
            else
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", message), true);
        }

        void DrawCustomResolutionTools()
        {
            if (UiNative.Button(UiLocale.Native("S891818785CBDABC7")))
                ApplyCustomResolutionFromUi(500, 345);

            ImGui.SameLine();
            ImGui.TextDisabled(UiLocale.Get("SD029F87E3D80F8FD"));
            ImGui.TextDisabled(UiLocale.Get("SD9C91FDD9FE152FD"));

            var width = xaModsCustomResolutionWidth;
            if (UiNative.InputInt(UiLocale.Native("S782A6F069C5A7CD2"), ref width))
                xaModsCustomResolutionWidth = Math.Max(1, width);

            var height = xaModsCustomResolutionHeight;
            if (UiNative.InputInt(UiLocale.Native("S3E51D8D6829873D6"), ref height))
                xaModsCustomResolutionHeight = Math.Max(1, height);

            if (UiNative.Button(UiLocale.Native("SB202019549F765D4")))
                ApplyCustomResolutionFromUi(xaModsCustomResolutionWidth, xaModsCustomResolutionHeight);

            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("SF62E4092F9B3F60B")))
                AddCustomResolutionPreset();

            if (configuration.CustomResolutionPresets.Count == 0)
            {
                ImGui.TextDisabled(UiLocale.Get("SD3B3A462129E128F"));
                return;
            }

            for (var index = 0; index < configuration.CustomResolutionPresets.Count; index++)
            {
                var preset = configuration.CustomResolutionPresets[index];
                var label = $"{preset.Width}x{preset.Height}";

                if (ImGui.Button($"{label}##CustomResolutionPreset{index}"))
                    ApplyCustomResolutionFromUi(preset.Width, preset.Height);

                ImGui.SameLine();
                if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S30EF15AFF51A4EA1"), index)))
                {
                    configuration.CustomResolutionPresets.RemoveAt(index);
                    SaveConfiguration();
                    SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD0BB407B83C874DA", label));
                    break;
                }
            }
        }

        void DrawLowResolutionOptions()
        {
            var scale = configuration.LowResolutionScale;
            if (UiNative.SliderFloat(UiLocale.Native("SD6475E6A19A2FB4E"), ref scale, 0.01f, 1.00f, "%.2f", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.LowResolutionScale = scale;
                plugin.SystemWindowMods.ApplyLowResolutionConfiguration(scale);
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S8F7A4E7F2A1D2024"));
        }

        void SetCustomTimestampFormat(string format)
        {
            configuration.CustomTimestampFormat = ChatTimestampFormatService.NormalizeFormat(format);
            ApplyCustomTimestampFormatConfiguration();
            SaveConfiguration();
        }

        void DrawTimestampFormatPreset(string format, string id)
        {
            if (ImGui.Button($"{format}##{id}"))
                SetCustomTimestampFormat(format);
        }

        void DrawCustomTimestampFormatOptions()
        {
            var format = configuration.CustomTimestampFormat;
            ImGui.SetNextItemWidth(Scale(180f));
            if (UiNative.InputText(UiLocale.Native("S3D95B2F54F33E254"), ref format, 80))
                SetCustomTimestampFormat(format);

            DrawTimestampFormatPreset("[HH:mm]", "CustomTimestampFormatPresetMinutes24");
            ImGui.SameLine();
            DrawTimestampFormatPreset("[HH:mm:ss]", "CustomTimestampFormatPresetSeconds24");
            ImGui.SameLine();
            DrawTimestampFormatPreset("[hh:mm:ss tt]", "CustomTimestampFormatPresetSeconds12");

            ImGui.TextDisabled(UiLocale.Get("S696F5C73EAE5B0D8"));
            ImGui.TextDisabled(UiLocale.Get("SC28CF008CA8FCDB3"));
            ImGui.TextDisabled(UiLocale.Get("S974BE55A66AAE664"));
            ImGui.TextDisabled(UiLocale.Get("S046BF94C5DFEA465"));
        }

        void DrawSightDistanceSlider(string label, string id, float currentValue, float minimumValue, float maximumValue, string format, Action<float> store)
        {
            var value = currentValue;
            if (!UiNative.SliderFloat($"{label}##{id}", ref value, minimumValue, maximumValue, format, ImGuiSliderFlags.AlwaysClamp))
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
                UiLocale.Native("S8753F5D076CC3B48"),
                "CustomSightDistanceMaxDistance",
                configuration.CustomSightDistanceMaxDistance,
                Math.Max(configuration.CustomSightDistanceMinDistance, 1f),
                80f,
                "%.1f",
                value => configuration.CustomSightDistanceMaxDistance = value);
            DrawSightDistanceSlider(
                UiLocale.Native("S0072D6D44865D309"),
                "CustomSightDistanceMinDistance",
                configuration.CustomSightDistanceMinDistance,
                0f,
                configuration.CustomSightDistanceMaxDistance,
                "%.1f",
                value => configuration.CustomSightDistanceMinDistance = value);
            DrawSightDistanceSlider(
                UiLocale.Native("S101D9B08FB4AC0D0"),
                "CustomSightDistanceMaxRotation",
                configuration.CustomSightDistanceMaxRotation,
                configuration.CustomSightDistanceMinRotation,
                1.569f,
                "%.3f",
                value => configuration.CustomSightDistanceMaxRotation = value);
            DrawSightDistanceSlider(
                UiLocale.Native("S190864AD445D2D56"),
                "CustomSightDistanceMinRotation",
                configuration.CustomSightDistanceMinRotation,
                -1.569f,
                configuration.CustomSightDistanceMaxRotation,
                "%.3f",
                value => configuration.CustomSightDistanceMinRotation = value);
            DrawSightDistanceSlider(
                UiLocale.Native("S2F5285BF919A6518"),
                "CustomSightDistanceMaxFoV",
                configuration.CustomSightDistanceMaxFoV,
                configuration.CustomSightDistanceMinFoV,
                3f,
                "%.3f",
                value => configuration.CustomSightDistanceMaxFoV = value);
            DrawSightDistanceSlider(
                UiLocale.Native("S18A68838BBEA25C6"),
                "CustomSightDistanceMinFoV",
                configuration.CustomSightDistanceMinFoV,
                0.01f,
                configuration.CustomSightDistanceMaxFoV,
                "%.3f",
                value => configuration.CustomSightDistanceMinFoV = value);
            DrawSightDistanceSlider(
                UiLocale.Native("S4FA79DCBCC3593CC"),
                "CustomSightDistanceFoV",
                configuration.CustomSightDistanceFoV,
                configuration.CustomSightDistanceMinFoV,
                configuration.CustomSightDistanceMaxFoV,
                "%.3f",
                value => configuration.CustomSightDistanceFoV = value);

            var ignoreCollision = configuration.CustomSightDistanceIgnoreCollision;
            if (UiNative.Checkbox(UiLocale.Native("SE734FDB3AF1B2EF2"), ref ignoreCollision))
            {
                configuration.CustomSightDistanceIgnoreCollision = ignoreCollision;
                ApplySightDistanceConfiguration();
                SaveConfiguration();
            }

            if (UiNative.Button(UiLocale.Native("S9699C33F4A1C0A10")))
                ResetSightDistanceDefaults();

            ImGui.TextDisabled(UiLocale.Get("S7B794EC9149EFC1F"));
        }

        void DrawInstantLogoutTool()
        {
            var logoutActionsAllowed = plugin.CanTriggerLogoutActionsMessage(out var blockedMessage);
            using (ImRaii.Disabled(!logoutActionsAllowed))
            {
                if (UiNative.Button(UiLocale.Native("S1AE0C2BCABE8B166")))
                {
                    if (!plugin.TryRequestLogoutActionMessage(out var message))
                        SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", message), true);
                    else
                        SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S6B474533AA612627"));
                }

                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("S1B17DFD3CA1835D7")))
                {
                    if (!plugin.TryRequestKillGameActionMessage(out var message))
                        SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SD5D18F9DF2FB2D99", message), true);
                    else
                        SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SB9523EC1E480D6E0"));
                }
            }

            ImGui.TextDisabled(UiLocale.Get("S5EAB021C60F74AE6"));
            ImGui.TextDisabled(UiLocale.Get("S35206B39C87B8243"));
            if (!logoutActionsAllowed)
                ImGui.TextDisabled(blockedMessage.Display);
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
            if (UiNative.SliderInt(UiLocale.Native("S50FB63569C358246"), ref timeoutIndex, 0, timeoutOptions.Length - 1, $"{timeoutOptions[timeoutIndex]} sec", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.BailoutEscMenuSeconds = timeoutOptions[timeoutIndex];
                ApplyBailoutEscMenuConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S7C8A9D678D1B5900"));
        }

        void DrawDozeSitAnywhereTools()
        {
            if (UiNative.Button(UiLocale.Native("S0FABECAD2637DCE9")) && !plugin.DozeSitAnywhere.RequestSit())
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SE2C9A8C5201983E0"), true);

            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("S98F82F396B185BD2")) && !plugin.DozeSitAnywhere.RequestDoze())
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S443E62814F6C3E45"), true);

            ImGui.TextDisabled(UiLocale.Get("S4A2288D4655AEEDE"));
            ImGui.TextDisabled(UiLocale.Get("SAE4D6C6F5402236F"));
            ImGui.TextDisabled(UiLocale.Get("S3A4582ECB469B8BF"));
        }

        void DrawInfiniteSprintOptions()
        {
            var delaySeconds = configuration.InfiniteSprintDelaySeconds;
            if (UiNative.SliderFloat(
                    UiLocale.Native("S98ED47DB8D3E6747"),
                    ref delaySeconds,
                    PlayerModsService.InfiniteSprintDelaySecondsMinimum,
                    PlayerModsService.InfiniteSprintDelaySecondsMaximum,
                    "%.1f sec",
                    ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.InfiniteSprintDelaySeconds = delaySeconds;
                ApplyInfiniteSprintConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S356647600E746BF1"));
        }

        void DrawAutoLeaveDutyOptions()
        {
            var delaySeconds = configuration.AutoLeaveDutyDelaySeconds;
            if (UiNative.SliderInt(
                    UiLocale.Native("SAA20A9A10EDABBD7"),
                    ref delaySeconds,
                    AutoLeaveDutyService.DelaySecondsMinimum,
                    AutoLeaveDutyService.DelaySecondsMaximum,
                    "%d sec",
                    ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.AutoLeaveDutyDelaySeconds = delaySeconds;
                ApplyAutoLeaveDutyConfiguration();
                SaveConfiguration();
            }

            ImGui.TextDisabled(UiLocale.Get("S94D9CBE26A0C046B"));
        }

        void DrawAutoDisplayIdsOptions()
        {
            DrawAutoDisplayIdsCheckbox(
                UiLocale.Native("S74476D92CDA24A22"),
                configuration.AutoDisplayIdsShowItemId,
                value => configuration.AutoDisplayIdsShowItemId = value);

            DrawAutoDisplayIdsCheckbox(
                UiLocale.Native("S5A42B72E51CCA3A3"),
                configuration.AutoDisplayIdsShowActionId,
                value => configuration.AutoDisplayIdsShowActionId = value);

            DrawAutoDisplayIdsCheckbox(
                UiLocale.Native("S1F29611D260F9E57"),
                configuration.AutoDisplayIdsShowTargetDataId,
                value => configuration.AutoDisplayIdsShowTargetDataId = value);

            DrawAutoDisplayIdsCheckbox(
                UiLocale.Native("SFBCCB664499697C7"),
                configuration.AutoDisplayIdsShowWeatherId,
                value => configuration.AutoDisplayIdsShowWeatherId = value);

            DrawAutoDisplayIdsCheckbox(
                UiLocale.Native("SD4C1F7D5951643CB"),
                configuration.AutoDisplayIdsShowZoneInfo,
                value => configuration.AutoDisplayIdsShowZoneInfo = value);

            ImGui.TextDisabled(UiLocale.FormatKey("SBAF8ED046F5FE126", plugin.AutoDisplayIds.CurrentZoneId));
            ImGui.TextDisabled(UiLocale.FormatKey("SEC1C8BDD3DFAFB05", plugin.AutoDisplayIds.CurrentMapId));
            ImGui.TextDisabled(UiLocale.FormatKey("S5BF465ADB7FD4580", plugin.AutoDisplayIds.CurrentWeatherId));
            ImGui.TextDisabled(UiLocale.FormatKey("SFC21828FA4793D7D", plugin.AutoDisplayIds.CurrentTargetDataId));
            ImGui.TextDisabled(UiLocale.FormatKey("S709ED67B581B83FA", plugin.AutoDisplayIds.LastActionOriginalId, plugin.AutoDisplayIds.LastActionResolvedId));
            ImGui.TextDisabled(UiLocale.FormatKey("S11A03971A7C4A297", (configuration.AutoDisplayIdsEnabled && configuration.AutoDisplayIdsShowItemId ? plugin.TooltipItemId.LocalizedStatusText : UiLocale.Get("S75081B593D15CF6E"))));
        }

        void DrawAutoDisplayIdsCheckbox(string label, bool value, Action<bool> applyValue)
        {
            var updatedValue = value;
            if (!UiNative.Checkbox(label, ref updatedValue))
                return;

            applyValue(updatedValue);
            plugin.ApplyAutoDisplayIdsConfiguration();
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SE1A0A113E2B7A88B"));
        }

        void DrawAutoDisplayNetworkLatencyOptions()
        {
            var format = configuration.AutoDisplayNetworkLatencyFormat;
            if (UiNative.InputTextWithHint(UiLocale.Native("S0A600DFC3B47B7B0"), UiLocale.Get("S93770BE8F2015278"), ref format, 128))
            {
                configuration.AutoDisplayNetworkLatencyFormat = format;
                ApplyAutoDisplayNetworkLatencyConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S668D0CE830EA158F"));
            }

            ImGui.TextDisabled(UiLocale.FormatKey("SC009622F93A61D17", (plugin.AutoDisplayNetworkLatency.CurrentPing < 0 ? "--" : plugin.AutoDisplayNetworkLatency.CurrentPing)));
            ImGui.TextDisabled(UiLocale.FormatKey("S47B6926B52390DD5", plugin.AutoDisplayNetworkLatency.AveragePing, plugin.AutoDisplayNetworkLatency.MinimumPing, plugin.AutoDisplayNetworkLatency.MaximumPing));
            ImGui.TextDisabled(UiLocale.FormatKey("SE1B48E3188D89A2C", plugin.AutoDisplayNetworkLatency.LossRate));
            ImGui.TextDisabled(UiLocale.FormatKey("SC53649DC5F1A5F76", plugin.AutoDisplayNetworkLatency.LocalizedServerEndpoint));
            ImGui.TextDisabled(plugin.AutoDisplayNetworkLatency.LocalizedLastActionText);
        }

        void DrawNotifyWhenFriendIsNearOptions()
        {
            UiNative.InputTextWithHint(
                UiLocale.Native("S97F37C6766B3A455"),
                UiLocale.Get("S8A84E86265E0B1F4"),
                ref notifyWhenFriendIsNearPatternInput,
                128);
            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("S8D2866C268A291B7")))
            {
                var pattern = notifyWhenFriendIsNearPatternInput.Trim();
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S1E0B18D1F8CC70B5"), true);
                }
                else if (configuration.NotifyWhenFriendIsNearPatterns.Any(existing => existing.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SDA889C18AFBD7462"), true);
                }
                else
                {
                    configuration.NotifyWhenFriendIsNearPatterns.Add(pattern);
                    notifyWhenFriendIsNearPatternInput = string.Empty;
                    ApplyNotifyWhenFriendIsNearConfiguration();
                    SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S0D40DCC46ADCD082", pattern));
                }
            }

            var cooldown = configuration.NotifyWhenFriendIsNearCooldownSeconds;
            if (UiNative.InputInt(UiLocale.Native("SADD31FA1E383C111"), ref cooldown))
            {
                configuration.NotifyWhenFriendIsNearCooldownSeconds = cooldown;
                ApplyNotifyWhenFriendIsNearConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SAE017EB38519E7DF", configuration.NotifyWhenFriendIsNearCooldownSeconds));
            }

            ImGui.TextDisabled(UiLocale.Get("SFF0165A9379A0BB0"));
            ImGui.TextDisabled(UiLocale.FormatKey("S721C2EE8B2BBC479", plugin.NotifyWhenFriendIsNear.PatternCount));
            ImGui.TextDisabled(UiLocale.FormatKey("S81C47A769645CEB3", plugin.NotifyWhenFriendIsNear.LocalizedLastMatchedPlayer));
            ImGui.TextWrapped(plugin.NotifyWhenFriendIsNear.LocalizedLastActionText);
            if (plugin.NotifyWhenFriendIsNear.SpeechStatus.Length != 0) ImGui.TextWrapped(plugin.NotifyWhenFriendIsNear.LocalizedSpeechStatus);
            DrawNearbyPlayerAdvancedRules();

            if (configuration.NotifyWhenFriendIsNearPatterns.Count == 0)
            {
                ImGui.TextDisabled(UiLocale.Get("S8C5B969ABF0DEDC1"));
                return;
            }

            foreach (var pattern in configuration.NotifyWhenFriendIsNearPatterns.ToList())
            {
                if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S2F4A5CB86940B45B"), pattern)))
                {
                    configuration.NotifyWhenFriendIsNearPatterns.Remove(pattern);
                    ApplyNotifyWhenFriendIsNearConfiguration();
                    SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S2D83920F9433B4BB", pattern));
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(pattern);
            }
        }

        void DrawAlertWhenTypingInCombatOptions()
        {
            var cooldown = configuration.AlertWhenTypingInCombatCooldownSeconds;
            if (UiNative.InputInt(UiLocale.Native("S61A6B2F0675DD241"), ref cooldown, 30, 300))
                configuration.AlertWhenTypingInCombatCooldownSeconds = cooldown;

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ApplyAlertWhenTypingInCombatConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SFD62B7FCC92B1A72", configuration.AlertWhenTypingInCombatCooldownSeconds));
            }

            ImGui.TextDisabled(UiLocale.FormatKey("S97325F31F743F94C", AlertWhenTypingInCombatService.MinimumCooldownSeconds, AlertWhenTypingInCombatService.MaximumCooldownSeconds));

            var toneId = AlertWhenTypingInCombatService.NormalizeToneId(configuration.AlertWhenTypingInCombatToneId);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SCFAC9603D638AFAA"), XAPeepSoundPlayer.GetToneLabelMessage(toneId).Display))
            {
                if (combo)
                {
                    for (var option = 1; option <= XAPeepData.MaxSoundEffectId; option++)
                    {
                        var selected = toneId == option;
                        if (UiNative.Selectable(XAPeepSoundPlayer.GetToneLabel(option), selected, display: XAPeepSoundPlayer.GetToneLabelMessage(option).Display))
                        {
                            configuration.AlertWhenTypingInCombatToneId = option;
                            ApplyAlertWhenTypingInCombatConfiguration();
                            plugin.AlertWhenTypingInCombat.PreviewSound();
                            toneId = option;
                        }

                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
            }

            var beepCount = configuration.AlertWhenTypingInCombatBeepCount;
            if (UiNative.InputInt(UiLocale.Native("SF7EEC7D0F163FDFB"), ref beepCount, 1, 5))
                configuration.AlertWhenTypingInCombatBeepCount = beepCount;

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ApplyAlertWhenTypingInCombatConfiguration();
                plugin.AlertWhenTypingInCombat.PreviewSound();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SB2B17A66083E7EE8", configuration.AlertWhenTypingInCombatBeepCount));
            }

            var volumePercent = Math.Clamp(configuration.AlertWhenTypingInCombatSoundVolume, 0f, 1f) * 100f;
            if (UiNative.SliderFloat(UiLocale.Native("S9658509679EBAA1E"), ref volumePercent, 0f, 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
                configuration.AlertWhenTypingInCombatSoundVolume = Math.Clamp(volumePercent / 100f, 0f, 1f);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ApplyAlertWhenTypingInCombatConfiguration();
                plugin.AlertWhenTypingInCombat.PreviewSound();
            }

            ImGui.TextDisabled(UiLocale.Get("S6D7CCE95D977CF9A"));

            if (UiNative.Button(UiLocale.Native("S7230C3D4E377ED8C")))
            {
                ApplyAlertWhenTypingInCombatConfiguration();
                plugin.AlertWhenTypingInCombat.PreviewAlert();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SE657951623A4308C"));
            }

            ImGui.TextDisabled(UiLocale.Get("SA290F03FE0AF33B8"));
            ImGui.TextDisabled(configuration.AlertWhenTypingInCombatEnabled
                ? UiLocale.FormatKey("S4B28B61E1D12A453", (plugin.AlertWhenTypingInCombat.IsInCombat ? UiLocale.Get("S85A39AB345D672FF") : UiLocale.Get("S1EA442A134B2A184")), (plugin.AlertWhenTypingInCombat.IsChatLogFocused ? UiLocale.Get("S85A39AB345D672FF") : UiLocale.Get("S1EA442A134B2A184")))
                : UiLocale.Get("SF31A4202F7AFB21D"));
            ImGui.TextDisabled(plugin.AlertWhenTypingInCombat.CooldownRemainingSeconds > 0
                ? UiLocale.FormatKey("SF44845A5E1716BE6", plugin.AlertWhenTypingInCombat.CooldownRemainingSeconds)
                : UiLocale.Get("SEF77E17B5C9E9688"));
            ImGui.TextDisabled(plugin.AlertWhenTypingInCombat.LocalizedLastActionText);
        }

        void DrawBetterCastBarOptions()
        {
            var slidecastMode = BetterCastBarService.NormalizeSlidecastMode(configuration.BetterCastBarSlidecastMode);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("S2D8622331B9B86EB"), UiLocale.T(GetBetterCastBarModeLabel(slidecastMode))))
            {
                if (combo)
                {
                    DrawBetterCastBarModeOption(UiLocale.Native("SDC937B59892604F5"), BetterCastBarService.SlidecastModeNone, ref slidecastMode);
                    DrawBetterCastBarModeOption(UiLocale.Native("SA8A06E4A5695553C"), BetterCastBarService.SlidecastModeZone, ref slidecastMode);
                    DrawBetterCastBarModeOption(UiLocale.Native("SD7852CD0D2453E8C"), BetterCastBarService.SlidecastModeLine, ref slidecastMode);
                }
            }

            var thresholdMs = configuration.BetterCastBarSlidecastThresholdMs;
            if (UiNative.InputInt(UiLocale.Native("SE6E7A7FFE9C9D7A1"), ref thresholdMs))
            {
                configuration.BetterCastBarSlidecastThresholdMs = thresholdMs;
                ApplyBetterCastBarConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S10C5C1578AE7F224", configuration.BetterCastBarSlidecastThresholdMs));
            }

            var lineWidth = configuration.BetterCastBarSlidecastLineWidth;
            if (UiNative.InputInt(UiLocale.Native("S55E731D8E1D79C99"), ref lineWidth))
            {
                configuration.BetterCastBarSlidecastLineWidth = lineWidth;
                ApplyBetterCastBarConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SDD8F1E8DDBEB783E", configuration.BetterCastBarSlidecastLineWidth));
            }

            if (configuration.BetterCastBarSlidecastMode == BetterCastBarService.SlidecastModeLine)
            {
                var lineHeight = configuration.BetterCastBarSlidecastLineHeight;
                if (UiNative.InputInt(UiLocale.Native("S5029EEC9FAED5E05"), ref lineHeight))
                {
                    configuration.BetterCastBarSlidecastLineHeight = lineHeight;
                    ApplyBetterCastBarConfiguration();
                    SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S21F5F62DDCB1CB2E", configuration.BetterCastBarSlidecastLineHeight));
                }
            }

            var readyColor = configuration.BetterCastBarSlidecastReadyColor;
            if (UiNative.ColorEdit4(UiLocale.Native("S343182FD877493D8"), ref readyColor))
            {
                configuration.BetterCastBarSlidecastReadyColor = readyColor;
                ApplyBetterCastBarConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SD1B2087DA8587D6D"));
            }

            var standbyColor = configuration.BetterCastBarSlidecastNotReadyColor;
            if (UiNative.ColorEdit4(UiLocale.Native("S56040CC49AA358F1"), ref standbyColor))
            {
                configuration.BetterCastBarSlidecastNotReadyColor = standbyColor;
                ApplyBetterCastBarConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SA4F76D8D7A9928D1"));
            }

            var iconAlpha = configuration.BetterCastBarIconAlpha;
            if (UiNative.InputInt(UiLocale.Native("S308C03134628953D"), ref iconAlpha))
            {
                configuration.BetterCastBarIconAlpha = iconAlpha;
                ApplyBetterCastBarConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SA417DAC2DE11046B", configuration.BetterCastBarIconAlpha));
            }

            ImGui.TextDisabled(UiLocale.FormatKey("SBF667A47DBABF45F", plugin.BetterCastBar.LocalizedCurrentCastName));
            ImGui.TextDisabled(UiLocale.FormatKey("SC696C20426C2F984", plugin.BetterCastBar.CurrentCastRemainingMs, plugin.BetterCastBar.CurrentCastTimeMs));
            ImGui.TextDisabled(UiLocale.FormatKey("SFD825878866FB9FE", (plugin.BetterCastBar.IsSlidecastReady ? UiLocale.Get("S85A39AB345D672FF") : UiLocale.Get("S1EA442A134B2A184"))));
        }

        void DrawBetterCastBarModeOption(string label, int value, ref int currentValue)
        {
            var isSelected = currentValue == value;
            if (!UiNative.Selectable($"{label}##BetterCastBarMode_{value}", isSelected))
                return;

            configuration.BetterCastBarSlidecastMode = value;
            currentValue = value;
            ApplyBetterCastBarConfiguration();
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SC8D0D3EEEC27C7FB", global::XASlave.Localization.UiMessage.Authored(label)));
        }

        static string GetBetterCastBarModeLabel(int value)
        {
            return value switch
            {
                BetterCastBarService.SlidecastModeNone => UiLocale.Native("SDC937B59892604F5"),
                BetterCastBarService.SlidecastModeLine => UiLocale.Native("SD7852CD0D2453E8C"),
                _ => UiLocale.Native("SA8A06E4A5695553C"),
            };
        }

        void DrawBetterInventoryMoverOptions()
        {
            var quickMoveModifier = BetterInventoryMoverService.NormalizeModifier(configuration.BetterInventoryMoverQuickMoveModifier);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SC240F3E1CBE43BDF"), UiLocale.T(BetterInventoryMoverService.GetModifierLabel(quickMoveModifier))))
            {
                if (combo)
                {
                    DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey.LeftShift, ref quickMoveModifier);
                    DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey.LeftControl, ref quickMoveModifier);
                    DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey.LeftAlt, ref quickMoveModifier);
                    DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey.RightShift, ref quickMoveModifier);
                    DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey.RightControl, ref quickMoveModifier);
                    DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey.RightAlt, ref quickMoveModifier);
                }
            }

            ImGui.TextDisabled(UiLocale.FormatKey("SD91B30EC7E73CA50", plugin.BetterInventoryMover.LocalizedQuickMoveModifierLabel));
            ImGui.TextDisabled(UiLocale.Get("S6F446675F86D30D6"));
            ImGui.TextDisabled(UiLocale.Get("SF49F86CFB213B20B"));
            ImGui.TextWrapped(plugin.BetterInventoryMover.LocalizedOccupiedStackStatus);
            ImGui.TextWrapped(plugin.BetterInventoryMover.LocalizedLastActionText);
            ImGui.TextDisabled(UiLocale.Get("S70063A9010887FA7"));
            ImGui.TextDisabled(UiLocale.Get("S571E8D44BA125D5F"));
            ImGui.TextDisabled(UiLocale.FormatKey("SD18AABD18244164C", plugin.BetterInventoryMover.LocalizedLastSourceAddon));
            ImGui.TextDisabled(UiLocale.FormatKey("S71A7AE0F5A0FB051", plugin.BetterInventoryMover.LocalizedLastDestinationLabel));
            ImGui.TextDisabled(UiLocale.FormatKey("S244F182A90EFA5CE", plugin.BetterInventoryMover.LastItemId));
            ImGui.TextDisabled(UiLocale.FormatKey("S5F6B262C0AD70FE2", plugin.BetterInventoryMover.AvailableDestinationCount));
        }

        void DrawBetterInventoryMoverModifierOption(BetterInventoryMoverModifierKey modifier, ref BetterInventoryMoverModifierKey currentValue)
        {
            var isSelected = currentValue == modifier;
            var label = BetterInventoryMoverService.GetModifierLabel(modifier);
            if (!UiNative.Selectable($"{label}##BetterInventoryMoverModifier_{modifier}", isSelected))
                return;

            configuration.BetterInventoryMoverQuickMoveModifier = modifier;
            currentValue = modifier;
            plugin.ApplyBetterInventoryMoverConfiguration();
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S6296217AFB4B4212", global::XASlave.Localization.UiMessage.Authored(label)));
        }

        void DrawBetterCompanyChestOptions()
        {
            var defaultPage = Math.Clamp(configuration.BetterCompanyChestDefaultPage, 0, 6);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("S135CEF78E06EC83E"), UiLocale.T(GetBetterCompanyChestPageLabel(defaultPage))))
            {
                if (combo)
                {
                    DrawBetterCompanyChestPageOption(UiLocale.Native("S75081B593D15CF6E"), 0, ref defaultPage);
                    DrawBetterCompanyChestPageOption(UiLocale.Native("SE8B8355262D0AF49"), 1, ref defaultPage);
                    DrawBetterCompanyChestPageOption(UiLocale.Native("S794F7062CF3F56F2"), 2, ref defaultPage);
                    DrawBetterCompanyChestPageOption(UiLocale.Native("SB2172E20DF3711D8"), 3, ref defaultPage);
                    DrawBetterCompanyChestPageOption(UiLocale.Native("SBE315C294E89DA2F"), 4, ref defaultPage);
                    DrawBetterCompanyChestPageOption(UiLocale.Native("S438FF2AFCF15E4B4"), 5, ref defaultPage);
                    DrawBetterCompanyChestPageOption(UiLocale.Native("SB502B407719D0563"), 6, ref defaultPage);
                }
            }

            var quickMoveEnabled = configuration.BetterCompanyChestQuickMoveEnabled;
            if (UiNative.Checkbox(UiLocale.Native("S59E291721594C135"), ref quickMoveEnabled))
            {
                configuration.BetterCompanyChestQuickMoveEnabled = quickMoveEnabled;
                plugin.ApplyBetterCompanyChestConfiguration();
                SetToonModsStatus((quickMoveEnabled ? global::XASlave.Localization.UiMessage.Key("S103E930ED59CFD26") : global::XASlave.Localization.UiMessage.Key("S660BD6545E2423EE")));
            }

            var autoConfirmNumeric = configuration.BetterCompanyChestAutoConfirmNumericInput;
            if (UiNative.Checkbox(UiLocale.Native("SC49435094335D0E0"), ref autoConfirmNumeric))
            {
                configuration.BetterCompanyChestAutoConfirmNumericInput = autoConfirmNumeric;
                plugin.ApplyBetterCompanyChestConfiguration();
                SetToonModsStatus((autoConfirmNumeric ? global::XASlave.Localization.UiMessage.Key("S16EFF432B5BFF4E2") : global::XASlave.Localization.UiMessage.Key("S5B6A7EE262B032B7")));
            }

            var showExchangeableValue = configuration.BetterCompanyChestShowExchangeableValue;
            if (UiNative.Checkbox(UiLocale.Native("S457969F35B2105FC"), ref showExchangeableValue))
            {
                configuration.BetterCompanyChestShowExchangeableValue = showExchangeableValue;
                plugin.ApplyBetterCompanyChestConfiguration();
                SetToonModsStatus((showExchangeableValue ? global::XASlave.Localization.UiMessage.Key("S74FB5D0946124F66") : global::XASlave.Localization.UiMessage.Key("S64242E6E9B946348")));
            }

            ImGui.TextDisabled(UiLocale.FormatKey("S96A7CC30090AC1FF", plugin.BetterCompanyChest.LocalizedCurrentPageLabel));
            ImGui.TextDisabled(UiLocale.FormatKey("S2C61EEAFBE675473", plugin.BetterCompanyChest.CurrentExchangeableValue));
        }

        void DrawBetterCompanyChestPageOption(string label, int value, ref int currentValue)
        {
            var isSelected = currentValue == value;
            if (!UiNative.Selectable($"{label}##BetterCompanyChestPage_{value}", isSelected))
                return;

            configuration.BetterCompanyChestDefaultPage = value;
            currentValue = value;
            plugin.ApplyBetterCompanyChestConfiguration();
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S8371A961119308F7", global::XASlave.Localization.UiMessage.Authored(label)));
        }

        string GetBetterCompanyChestPageLabel(int value)
        {
            return value switch
            {
                1 => UiLocale.Native("SE8B8355262D0AF49"),
                2 => UiLocale.Native("S794F7062CF3F56F2"),
                3 => UiLocale.Native("SB2172E20DF3711D8"),
                4 => UiLocale.Native("SBE315C294E89DA2F"),
                5 => UiLocale.Native("S438FF2AFCF15E4B4"),
                6 => UiLocale.Native("SB502B407719D0563"),
                _ => UiLocale.Native("S75081B593D15CF6E"),
            };
        }

        void DrawAutoOpenMoogleMailOptions()
        {
            var enabledForActions = configuration.AutoOpenMoogleMailEnabled;
            using (ImRaii.Disabled(!enabledForActions))
            {
                if (UiNative.Button(UiLocale.Native("S3502BD5DBAD6B484")))
                {
                    var success = plugin.AutoOpenMoogleMail.QueueClaimAttachments(configuration.AutoOpenMoogleMailDeleteAllWhenFinished);
                    SetToonModsStatus(plugin.AutoOpenMoogleMail.LastActionTextMessage, !success);
                }

                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("SB3D9D09ED81F1478")))
                {
                    var success = plugin.AutoOpenMoogleMail.QueueDeleteNonPlayerLetters();
                    SetToonModsStatus(plugin.AutoOpenMoogleMail.LastActionTextMessage, !success);
                }

                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("SA23982C3715C6593")))
                {
                    var success = plugin.AutoOpenMoogleMail.QueueDeleteAllLetters();
                    SetToonModsStatus(plugin.AutoOpenMoogleMail.LastActionTextMessage, !success);
                }

                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("SEC43E10BC723752C")))
                {
                    var success = plugin.AutoOpenMoogleMail.QueueRequestDelivery();
                    SetToonModsStatus(plugin.AutoOpenMoogleMail.LastActionTextMessage, !success);
                }
            }

            var deleteAllWhenFinished = configuration.AutoOpenMoogleMailDeleteAllWhenFinished;
            if (UiNative.Checkbox(UiLocale.Native("SBC5ADE2602462DC7"), ref deleteAllWhenFinished))
            {
                configuration.AutoOpenMoogleMailDeleteAllWhenFinished = deleteAllWhenFinished;
                SaveConfiguration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(UiLocale.Get("S75F6C51A779B13CC"));

            ImGui.TextDisabled(UiLocale.FormatKey("S45743E343E237056", plugin.AutoOpenMoogleMail.LocalizedPendingOperationText));
            ImGui.TextDisabled(UiLocale.FormatKey("S8846D86049DEE82D", plugin.AutoOpenMoogleMail.KnownLetterCount));
            ImGui.TextDisabled(UiLocale.FormatKey("S7657DBEAAEE1291D", plugin.AutoOpenMoogleMail.KnownNewLetterCount));
            ImGui.TextDisabled(UiLocale.FormatKey("S9E06A286C49698D1", plugin.AutoOpenMoogleMail.KnownAttachmentCount));
            ImGui.TextDisabled(UiLocale.FormatKey("SE93B023E2A051E81", plugin.AutoOpenMoogleMail.CompletedOperationCount));
            ImGui.TextDisabled(UiLocale.FormatKey("S4AB6682DCF19C546", plugin.AutoOpenMoogleMail.LastProcessedLetterCount));
        }

        void DrawEnableItemIconInShopsOptions()
        {
            ImGui.TextDisabled(UiLocale.Get("SD61E6637D2483BF7"));
            ImGui.TextDisabled(UiLocale.FormatKey("S36378FBD48C9B3A1", plugin.EnableItemIconInShops.LocalizedLastAddonName));
            ImGui.TextDisabled(UiLocale.FormatKey("SC069A39528720F92", plugin.EnableItemIconInShops.LastReplacementCount));
        }

        void DrawFieldEntryCommandOptions()
        {
            ImGui.TextDisabled(plugin.FieldEntryCommand.BuildLocalizedUsageText().Replace("[XASlave] ", string.Empty, StringComparison.Ordinal));

            using (ImRaii.Disabled(!configuration.FieldEntryCommandEnabled))
            {
                ImGui.SetNextItemWidth(Scale(260f));
                UiNative.InputTextWithHint(
                    UiLocale.Native("S2CCA9DE28175849B"),
                    UiLocale.Get("S7E220B8E154526E4"),
                    ref xaModsFieldEntryQuery,
                    128);

                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("SE9B1FBC21FBAE848")))
                    TryStartFieldEntryCommand(xaModsFieldEntryQuery);

                foreach (var key in plugin.FieldEntryCommand.SupportedKeys)
                {
                    if (ImGui.SmallButton($"{key}##FieldEntryCommand_{key}"))
                        TryStartFieldEntryCommand(key);

                    ImGui.SameLine();
                }

                ImGui.NewLine();
            }

            ImGui.TextDisabled(UiLocale.FormatKey("SDF4B915D01C4D176", plugin.FieldEntryCommand.LocalizedPendingEntryText));
            ImGui.TextDisabled(UiLocale.FormatKey("SEBFC403502D9DACC", plugin.FieldEntryCommand.LocalizedLastSuggestedZone));
            ImGui.TextWrapped(UiLocale.FormatKey("SA1F153C4BE3AD4DC", plugin.FieldEntryCommand.LocalizedLastTeleportCommand));
            ImGui.TextDisabled(UiLocale.FormatKey("S825BAB9AC17BA57A", plugin.FieldEntryCommand.SupportedEntryCount));
        }

        void TryStartFieldEntryCommand(string query)
        {
            var success = plugin.FieldEntryCommand.TryStart(query);
            var message = !success && plugin.FieldEntryCommand.StatusText.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? plugin.FieldEntryCommand.StatusTextMessage
                : plugin.FieldEntryCommand.LastActionTextMessage;
            SetToonModsStatus(message, !success);
        }

        void DrawEurekaInstanceIdOptions()
        {
            DrawEurekaInstanceIdSharedDisplayOptions("EurekaInstanceId");

            if (UiNative.Button(UiLocale.Native("S3C5C0FCC0ABA4280")))
                OpenEurekaInstanceHunterTask();

            ImGui.TextDisabled(UiLocale.Get("SDAC2988A14A5F86D"));
            ImGui.TextDisabled(UiLocale.Get("SD2ACFB6928ADA64A"));
            ImGui.TextDisabled(plugin.EurekaInstanceId.LocalizedStatusText);
        }

        void DrawExpertDeliveryOptions()
        {
            var autoSwitchWhenOpen = configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen;
            if (UiNative.Checkbox(UiLocale.Native("S0C62C30AE8316102"), ref autoSwitchWhenOpen))
            {
                configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen = autoSwitchWhenOpen;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            using (ImRaii.Disabled(!configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen))
            {
                var selectedPage = Math.Clamp(configuration.AutoUnlockExpertDeliveryDefaultPage, 0, AutoExpertDeliveryPageLabels.Length - 1);
                using var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SE173D9C34049C6A7"), UiLocale.T(AutoExpertDeliveryPageLabels[selectedPage]));
                if (combo)
                {
                    for (var i = 0; i < AutoExpertDeliveryPageLabels.Length; i++)
                    {
                        var isSelected = selectedPage == i;
                        if (UiNative.Selectable(AutoExpertDeliveryPageLabels[i], isSelected))
                        {
                            configuration.AutoUnlockExpertDeliveryDefaultPage = i;
                            ApplyExpertDeliveryConfiguration();
                            SaveConfiguration();
                            selectedPage = i;
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
            }

            var skipHq = configuration.AutoUnlockExpertDeliverySkipHq;
            if (UiNative.Checkbox(UiLocale.Native("S1082433B85B52B4A"), ref skipHq))
            {
                configuration.AutoUnlockExpertDeliverySkipHq = skipHq;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            var skipMateria = configuration.AutoUnlockExpertDeliverySkipMateria;
            if (UiNative.Checkbox(UiLocale.Native("S3750B48BFDD8BA37"), ref skipMateria))
            {
                configuration.AutoUnlockExpertDeliverySkipMateria = skipMateria;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            var ignoreSealCap = configuration.AutoUnlockExpertDeliveryIgnoreSealCap;
            if (UiNative.Checkbox(UiLocale.Native("SC84445187F4D2229"), ref ignoreSealCap))
            {
                configuration.AutoUnlockExpertDeliveryIgnoreSealCap = ignoreSealCap;
                ApplyExpertDeliveryConfiguration();
                SaveConfiguration();
            }

            var speed = Math.Clamp(configuration.AutoUnlockExpertDeliverySpeedProfile, 0, 2);
            if (global::XASlave.Localization.UiNative.Combo(UiLocale.Native("SEF674A7DB16CEC4F"), ref speed, UiLocale.Native("SA26027F81BE47F02") + "\0" + UiLocale.Native("SF9AE8B1F58E5A4CF") + "\0" + UiLocale.Native("SC723A45B30B6E2D2") + "\0"))
            {
                configuration.AutoUnlockExpertDeliverySpeedProfile = speed;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            var scope = Math.Clamp(configuration.AutoUnlockExpertDeliveryItemScope, 0, 2);
            if (global::XASlave.Localization.UiNative.Combo(UiLocale.Native("S1904143F152EC6C3"), ref scope, UiLocale.Native("S30A7C86A8F8F8269") + "\0" + UiLocale.Native("S33A166531A4A9B79") + "\0" + UiLocale.Native("S52135CC8EEF841AE") + "\0"))
            {
                configuration.AutoUnlockExpertDeliveryItemScope = scope;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            ImGui.TextDisabled(UiLocale.Get("SBAF65BDBFA35BCA5"));
            var useAr = configuration.AutoUnlockExpertDeliveryUseArProtection;
            if (UiNative.Checkbox(UiLocale.Native("S1BB1D285521CA2FD"), ref useAr))
            {
                configuration.AutoUnlockExpertDeliveryUseArProtection = useAr;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            var protectedIds = configuration.AutoUnlockExpertDeliveryProtectedItemIds ?? string.Empty;
            if (UiNative.InputText(UiLocale.Native("S8B2DC5220FE46C6B"), ref protectedIds, 4096))
            {
                configuration.AutoUnlockExpertDeliveryProtectedItemIds = protectedIds;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            if (!ExpertDeliveryPolicy.TryParseProtectedIds(protectedIds, out _))
                ImGui.TextWrapped(UiLocale.Get("S9D222C187E142D38"));
            var close = configuration.AutoUnlockExpertDeliveryCloseOnCompletion;
            if (UiNative.Checkbox(UiLocale.Native("SB3455A176F9056E1"), ref close))
            {
                configuration.AutoUnlockExpertDeliveryCloseOnCompletion = close;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            var notify = configuration.AutoUnlockExpertDeliveryNotifyOutcome;
            if (UiNative.Checkbox(UiLocale.Native("SFD5EF31A9361BA4D"), ref notify))
            {
                configuration.AutoUnlockExpertDeliveryNotifyOutcome = notify;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            var runCommand = configuration.AutoUnlockExpertDeliveryRunCompletionCommand;
            if (UiNative.Checkbox(UiLocale.Native("S5A23032A0C24AF54"), ref runCommand))
            {
                configuration.AutoUnlockExpertDeliveryRunCompletionCommand = runCommand;
                ApplyExpertDeliveryConfiguration(); SaveConfiguration();
            }
            if (runCommand)
            {
                var command = configuration.AutoUnlockExpertDeliveryCompletionCommand ?? string.Empty;
                if (UiNative.InputText(UiLocale.Native("S2FDB128BFA1B638C"), ref command, 500))
                {
                    configuration.AutoUnlockExpertDeliveryCompletionCommand = command;
                    ApplyExpertDeliveryConfiguration(); SaveConfiguration();
                }
                if (!ExpertDeliveryPolicy.IsValidCommand(command)) ImGui.TextWrapped(UiLocale.Get("S6667D6A513B26E3B"));
            }
            ImGui.TextDisabled(UiLocale.Get("S3DB1CC399557C19B"));
            ImGui.TextWrapped(plugin.AutoUnlockExpertDelivery.LocalizedListSummary);
            ImGui.TextDisabled(UiLocale.FormatKey("SCFA1EA5E0C20D72D", plugin.AutoUnlockExpertDelivery.RunId, plugin.AutoUnlockExpertDelivery.DeliveredCount));
            if (plugin.AutoUnlockExpertDelivery.LastResult is { } result)
                ImGui.TextWrapped(UiLocale.FormatKey("S88EFF471F1A4CE79", result.RunId, AutoUnlockExpertDeliveryService.OutcomeMessage(result.Outcome).Display, result.Delivered, result.ElapsedMilliseconds / 1000.0));
            if (UiNative.Button(UiLocale.Native("SC698C5B9B0F77886"))) plugin.AutoUnlockExpertDelivery.RequestNewRun();

            ImGui.TextDisabled(ignoreSealCap
                ? UiLocale.Get("S767EBF31B1C3205F")
                : UiLocale.Get("S9DF5B73399387C64"));
        }

        void DrawUnlockExpertDeliveryOptions()
        {
            var selectedRank = ExpertDeliveryUnlockService.NormalizeForcedRankFloor(configuration.UnlockExpertDeliveryForcedRankFloor);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SF8987ADCF6109012"), UiLocale.T(GetUnlockExpertDeliveryRankLabel(selectedRank))))
            {
                if (combo)
                {
                    for (var rank = ExpertDeliveryUnlockService.MinForcedRankFloor; rank <= ExpertDeliveryUnlockService.MaxForcedRankFloor; rank++)
                    {
                        var isSelected = selectedRank == rank;
                        if (UiNative.Selectable(GetUnlockExpertDeliveryRankLabel(rank), isSelected))
                        {
                            configuration.UnlockExpertDeliveryForcedRankFloor = rank;
                            ApplyUnlockExpertDeliveryConfiguration();
                            SaveConfiguration();
                            selectedRank = rank;
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
            }

            ImGui.TextDisabled(UiLocale.Get("S87154261ECBC6C1B"));
        }

        void DrawTradeRefusalOptions()
        {
            var showNotification = configuration.AutoRefuseTradeShowNotification;
            if (UiNative.Checkbox(UiLocale.Native("SE994E4B843EBC7B8"), ref showNotification))
            {
                configuration.AutoRefuseTradeShowNotification = showNotification;
                ApplyTradeRefusalConfiguration();
                SaveConfiguration();
            }

            var sendEcho = configuration.AutoRefuseTradeSendEcho;
            if (UiNative.Checkbox(UiLocale.Native("S92E67A8E5EA57B3B"), ref sendEcho))
            {
                configuration.AutoRefuseTradeSendEcho = sendEcho;
                ApplyTradeRefusalConfiguration();
                SaveConfiguration();
            }

            var extraCommands = configuration.AutoRefuseTradeExtraCommands;
            if (global::XASlave.Localization.UiNative.InputTextMultiline(
                    UiLocale.Native("S8A1CF4BCACCB5AAC"),
                    ref extraCommands,
                    1024,
                    new Vector2(Math.Max(320f, ImGui.GetContentRegionAvail().X), 72f)))
            {
                configuration.AutoRefuseTradeExtraCommands = extraCommands;
                ApplyTradeRefusalConfiguration();
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfiguration();

            ImGui.TextDisabled(UiLocale.Get("SF5244FD6668E09E0"));
            ImGui.TextDisabled(UiLocale.Get("SA2C7F76743E58C03"));
            ImGui.TextDisabled(UiLocale.Get("S40835B29AD71A442"));
        }

        void DrawTeleportHelperOptions()
        {
            var selectYes = configuration.TeleportHelperSelectYes;
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SD97077BE8A7A19BE"), UiLocale.T(GetTeleportHelperResponseLabel(selectYes))))
            {
                if (combo)
                {
                    DrawTeleportHelperResponseOption(UiLocale.Native("S74A4E1C14DD34C87"), false, ref selectYes);
                    DrawTeleportHelperResponseOption(UiLocale.Native("S9B6DDA8BC55F5BAC"), true, ref selectYes);
                }
            }

            ImGui.TextDisabled(selectYes
                ? UiLocale.Get("SBD8656979DCB88F6")
                : UiLocale.Get("SE7C47D35DE3EAD41"));
            ImGui.TextDisabled(plugin.TeleportHelper.LocalizedLastActionText);
        }

        void DrawTeleportHelperResponseOption(string label, bool selectYesValue, ref bool currentValue)
        {
            var isSelected = currentValue == selectYesValue;
            if (!UiNative.Selectable($"{label}##TeleportHelper_{selectYesValue}", isSelected))
                return;

            configuration.TeleportHelperSelectYes = selectYesValue;
            currentValue = selectYesValue;
            ApplyTeleportHelperConfiguration();
            SaveConfiguration();
            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("SAF3C6E96BFC6E536", ((selectYesValue ? global::XASlave.Localization.UiMessage.Key("S85A39AB345D672FF") : global::XASlave.Localization.UiMessage.Key("S1EA442A134B2A184")))));
        }

        string GetTeleportHelperResponseLabel(bool selectYes)
            => selectYes ? UiLocale.Native("S9B6DDA8BC55F5BAC") : UiLocale.Native("S74A4E1C14DD34C87");

        void DrawXAPeepOptions()
        {
            if (UiNative.Button(plugin.XAPeepWindow.IsOpen ? UiLocale.Native("S278F409FDCA897AF") : UiLocale.Native("SDEB63E246E0139EE")))
                plugin.ToggleXAPeepUi();

            ImGui.SameLine();
            if (UiNative.Button(plugin.XAPeepHistoryWindow.IsOpen ? UiLocale.Native("SD3A119053237A16A") : UiLocale.Native("SDD001F7419477E07")))
                plugin.ToggleXAPeepHistoryUi();

            ImGui.SameLine();
            var clearXaPeepHistoryModifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            using (ImRaii.Disabled(!clearXaPeepHistoryModifierHeld))
            {
                if (UiNative.Button(UiLocale.Native("S6EDFDA7AF595743D")))
                    plugin.XAPeep.ClearHistory();
            }
            if (!clearXaPeepHistoryModifierHeld)
            {
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(UiLocale.Get("SB5788021233F3E54"));
            }

            var autoOpenWindow = configuration.XAPeepAutoOpenWindowOnPluginLoad;
            if (UiNative.Checkbox(UiLocale.Native("SC01BF0F1A1D9EC4F"), ref autoOpenWindow))
            {
                configuration.XAPeepAutoOpenWindowOnPluginLoad = autoOpenWindow;
                SaveConfiguration();
            }

            var logParty = configuration.XAPeepLogParty;
            if (UiNative.Checkbox(UiLocale.Native("S06B0760DEB1A26FF"), ref logParty))
            {
                configuration.XAPeepLogParty = logParty;
                SaveConfiguration();
            }

            var logAlliance = configuration.XAPeepLogAlliance;
            if (UiNative.Checkbox(UiLocale.Native("SB7D43723F4A37E34"), ref logAlliance))
            {
                configuration.XAPeepLogAlliance = logAlliance;
                SaveConfiguration();
            }

            var logPlayersInCombat = configuration.XAPeepLogInCombat;
            if (UiNative.Checkbox(UiLocale.Native("SDEF955B4FEA6CB9E"), ref logPlayersInCombat))
            {
                configuration.XAPeepLogInCombat = logPlayersInCombat;
                SaveConfiguration();
            }

            var logWhileInDuty = configuration.XAPeepLogInDuty;
            if (UiNative.Checkbox(UiLocale.Native("SF102C07E014C1C7B"), ref logWhileInDuty))
            {
                configuration.XAPeepLogInDuty = logWhileInDuty;
                SaveConfiguration();
            }

            var showCardWhenTargeted = configuration.XAPeepDisplayLineWhenTargetingMe;
            if (UiNative.Checkbox(UiLocale.Native("S8AAA40CBD5953D6D"), ref showCardWhenTargeted))
            {
                configuration.XAPeepDisplayLineWhenTargetingMe = showCardWhenTargeted;
                SaveConfiguration();
            }

            var showTargeterLine = configuration.XAPeepShowTargeterLine;
            if (UiNative.Checkbox(UiLocale.Native("SC755EFF27C895D48"), ref showTargeterLine))
            {
                configuration.XAPeepShowTargeterLine = showTargeterLine;
                SaveConfiguration();
            }

            var targeterLineColor = configuration.XAPeepTargeterLineColor;
            if (UiNative.ColorEdit4(UiLocale.Native("SD2383F4F8BE0BE02"), ref targeterLineColor))
            {
                configuration.XAPeepTargeterLineColor = targeterLineColor;
                SaveConfiguration();
            }

            var showTargeterDot = configuration.XAPeepShowTargeterDot;
            if (UiNative.Checkbox(UiLocale.Native("SA64872D47E18F622"), ref showTargeterDot))
            {
                configuration.XAPeepShowTargeterDot = showTargeterDot;
                SaveConfiguration();
            }

            var targeterDotColor = configuration.XAPeepTargeterDotColor;
            if (UiNative.ColorEdit4(UiLocale.Native("SFCAEF5068E93B634"), ref targeterDotColor))
            {
                configuration.XAPeepTargeterDotColor = targeterDotColor;
                SaveConfiguration();
            }

            var targeterDotSize = Math.Clamp(configuration.XAPeepTargeterDotSize, 1f, 15f);
            if (UiNative.SliderFloat(UiLocale.Native("S54AE36512F97AFC3"), ref targeterDotSize, 1f, 15f, "%.1f", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.XAPeepTargeterDotSize = Math.Clamp(targeterDotSize, 1f, 15f);
                SaveConfiguration();
            }

            var showTargetersCard = configuration.XAPeepShowTargetersCard;
            if (UiNative.Checkbox(UiLocale.Native("S22D397C66676FF18"), ref showTargetersCard))
            {
                configuration.XAPeepShowTargetersCard = showTargetersCard;
                SaveConfiguration();
            }

            var showCenterNotification = configuration.XAPeepShowCenterNotification;
            if (UiNative.Checkbox(UiLocale.Native("S6B72D7D088B49B33"), ref showCenterNotification))
            {
                configuration.XAPeepShowCenterNotification = showCenterNotification;
                SaveConfiguration();
            }

            var showChatNotification = configuration.XAPeepShowChatNotification;
            if (UiNative.Checkbox(UiLocale.Native("SC297C0134C3A3F18"), ref showChatNotification))
            {
                configuration.XAPeepShowChatNotification = showChatNotification;
                SaveConfiguration();
            }

            var soundEffectId = XAPeepData.ClampSoundEffectId(configuration.XAPeepSoundEffectId);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SD7C91D4395E196FF"), XAPeepData.GetSoundEffectLabelMessage(soundEffectId).Display))
            {
                if (combo)
                {
                    for (var i = 0; i <= XAPeepData.MaxSoundEffectId; i++)
                    {
                        var selected = soundEffectId == i;
                        if (UiNative.Selectable(XAPeepData.GetSoundEffectLabel(i), selected, display: XAPeepData.GetSoundEffectLabelMessage(i).Display))
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
                }
            }

            var soundVolumePercent = Math.Clamp(configuration.XAPeepSoundVolume, 0f, 1f) * 100f;
            if (UiNative.SliderFloat(UiLocale.Native("SE62FA89A3873D158"), ref soundVolumePercent, 0f, 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.XAPeepSoundVolume = Math.Clamp(soundVolumePercent / 100f, 0f, 1f);
                SaveConfiguration();
            }

            if (ImGui.IsItemDeactivatedAfterEdit() && XAPeepData.ClampSoundEffectId(configuration.XAPeepSoundEffectId) > 0)
                plugin.XAPeep.PlayConfiguredSoundPreview();
        }

        void DrawDalamudNotificationsSuckOptions()
        {
            ImGui.TextDisabled(UiLocale.Get("SD9DECC19F719AFA6"));
            ImGui.TextDisabled(plugin.DalamudNotificationsSuck.LocalizedLastActionText);

            DrawDalamudNotificationOption(
                UiLocale.Native("SD4EFA4DB5CAD6E05"),
                () => configuration.DalamudNotificationsSuckHideAll,
                value => configuration.DalamudNotificationsSuckHideAll = value,
                UiLocale.Native("S2003AD8572528F15"));

            using (ImRaii.Disabled(configuration.DalamudNotificationsSuckHideAll))
            {
                DrawDalamudNotificationOption(
                    UiLocale.Native("S19DAE7CB48A662B6"),
                    () => configuration.DalamudNotificationsSuckHideDalamudUpdates,
                    value => configuration.DalamudNotificationsSuckHideDalamudUpdates = value,
                    UiLocale.Native("S6EB74A66EDFD7446"));

                DrawDalamudNotificationOption(
                    UiLocale.Native("SF76ACF127351D5DE"),
                    () => configuration.DalamudNotificationsSuckHidePluginLifecycle,
                    value => configuration.DalamudNotificationsSuckHidePluginLifecycle = value,
                    UiLocale.Native("SA2C688BE7AE38E08"));

                DrawDalamudNotificationOption(
                    UiLocale.Native("S5543B42429968362"),
                    () => configuration.DalamudNotificationsSuckHidePluginErrors,
                    value => configuration.DalamudNotificationsSuckHidePluginErrors = value,
                    UiLocale.Native("S0E6A474E4DB9A517"));

                DrawDalamudNotificationOption(
                    UiLocale.Native("S44ACFE896E43ECB1"),
                    () => configuration.DalamudNotificationsSuckHideModManagerAlerts,
                    value => configuration.DalamudNotificationsSuckHideModManagerAlerts = value,
                    UiLocale.Native("S01BB3555E253F35B"));

                DrawDalamudNotificationOption(
                    UiLocale.Native("S900F32600DB2F59C"),
                    () => configuration.DalamudNotificationsSuckHideSuccessInfo,
                    value => configuration.DalamudNotificationsSuckHideSuccessInfo = value,
                    UiLocale.Native("SC693E5BBB550BB4F"));

                DrawDalamudNotificationOption(
                    UiLocale.Native("S236DD56D22204171"),
                    () => configuration.DalamudNotificationsSuckHideWarningsErrors,
                    value => configuration.DalamudNotificationsSuckHideWarningsErrors = value,
                    UiLocale.Native("SAA1F1F822E4659E9"));
            }

            DrawWarningText(UiLocale.Native("S27A1E0513E0A9DA9"));
        }

        void DrawDalamudNotificationOption(string label, Func<bool> getValue, Action<bool> setValue, string helpText)
        {
            var value = getValue();
            if (UiNative.Checkbox(label, ref value))
            {
                setValue(value);
                ApplyDalamudNotificationsSuckConfiguration();
                SaveConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SC18916A56EB9B9F8"));
            }

            ImGui.SameLine(0f, 6f);
            DrawHelpMarker(helpText);
        }

        void DrawBetterHighlightPotentialTargetsOptions()
        {
            ImGui.TextDisabled(UiLocale.Get("S2F5B82FDD995EB00"));

            var selectedColor = BetterHighlightPotentialTargetsService.NormalizeHighlightColor(configuration.BetterHighlightPotentialTargetsColor);
            using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SC31EB599032A8BEC"), UiLocale.T(BetterHighlightPotentialTargetsService.GetColorLabel(selectedColor))))
            {
                if (combo)
                {
                    foreach (var color in BetterHighlightPotentialTargetsService.SelectableHighlightColors)
                    {
                        var isSelected = selectedColor == color;
                        if (UiNative.Selectable($"{BetterHighlightPotentialTargetsService.GetColorLabel(color)}##BetterHighlightPotentialTargets_{color}", isSelected))
                        {
                            configuration.BetterHighlightPotentialTargetsColor = color;
                            ApplyBetterHighlightPotentialTargetsConfiguration();
                            SaveConfiguration();
                            SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S9CE5F56A180444A0", global::XASlave.Localization.UiMessage.Authored(BetterHighlightPotentialTargetsService.GetColorLabel(color))));
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
            }

            ImGui.SameLine(0f, 6f);
            DrawHelpMarker(UiLocale.Native("S2817BCF7D81BF403"));
        }

        void DrawNameplateStatusIconOptions()
        {
            ImGui.TextWrapped(UiLocale.Get("S3C642DF58B4AB02C"));
            if (UiNative.Button(UiLocale.Native("SEE9D86696F8CA6D5")))
                plugin.NameplatePrivacy.RefreshStatusOptions();

            configuration.HiddenNameplateStatusIds ??= new HashSet<uint>();
            var options = plugin.NameplatePrivacy.StatusOptions;
            if (options.Count == 0)
            {
                ImGui.TextDisabled(UiLocale.Get("S44884BF95E78C29E"));
                return;
            }

            var changed = false;
            if (UiNative.Button(UiLocale.Native("S55E2DF13B05048E4")))
            {
                foreach (var option in options)
                    configuration.HiddenNameplateStatusIds.Add(option.StatusId);
                changed = true;
            }
            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("S6FE137F4838014B7")))
            {
                configuration.HiddenNameplateStatusIds.Clear();
                changed = true;
            }
            foreach (var option in options)
            {
                var hide = configuration.HiddenNameplateStatusIds.Contains(option.StatusId);
                if (UiNative.Checkbox($"{option.Label}##HiddenNameplateStatus{option.StatusId}", ref hide))
                {
                    if (hide)
                        configuration.HiddenNameplateStatusIds.Add(option.StatusId);
                    else
                        configuration.HiddenNameplateStatusIds.Remove(option.StatusId);
                    changed = true;
                }
            }
            if (changed)
            {
                plugin.NameplatePrivacy.ApplyStatusIconConfiguration(configuration.HiddenNameplateStatusIds);
                SaveConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S55DD7F2BF45BC67A"));
            }
        }

        void DrawShowTravelerWorldNamesOptions()
        {
            var disableInDuties = configuration.ShowTravelerWorldNamesDisableInDuties;
            if (UiNative.Checkbox(UiLocale.Native("SB271F8E738E11B42"), ref disableInDuties))
            {
                configuration.ShowTravelerWorldNamesDisableInDuties = disableInDuties;
                ApplyShowTravelerWorldNamesConfiguration();
                SaveConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("SDAF204DD53F6B420"));
            }

            var addSpacer = configuration.ShowTravelerWorldNamesAddSpacer;
            if (UiNative.Checkbox(UiLocale.Native("SDBBD76C3BB98FD42"), ref addSpacer))
            {
                configuration.ShowTravelerWorldNamesAddSpacer = addSpacer;
                ApplyShowTravelerWorldNamesConfiguration();
                SaveConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S713BCDA62C625479"));
            }

            var nameFormat = addSpacer ? "Name @ HomeWorld" : "Name@HomeWorld";
            ImGui.TextDisabled(disableInDuties
                ? UiLocale.FormatKey("SB816F61DF0BBECC6", nameFormat)
                : UiLocale.FormatKey("S1A215A4085FFA98D", nameFormat));
        }

        void DrawShowTitlesAsPlayernamesOptions()
        {
            var honorificSupport = configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled;
            if (UiNative.Checkbox(UiLocale.Native("S86EDB787541A938D"), ref honorificSupport))
            {
                configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled = honorificSupport;
                ApplyShowTitlesAsPlayernamesConfiguration();
                SaveConfiguration();
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Key("S368B592877DE8464"));
            }
        }

        void SaveAutoSkipCutsceneOptions(global::XASlave.Localization.UiMessage message)
        {
            configuration.AutoSkipCutscenesWhitelistTerritories = NormalizeTerritoryList(configuration.AutoSkipCutscenesWhitelistTerritories);
            configuration.AutoSkipCutscenesBlacklistTerritories = NormalizeTerritoryList(configuration.AutoSkipCutscenesBlacklistTerritories);
            ApplyAutoSkipCutscenesConfiguration();
            SaveConfiguration();
            SetToonModsStatus(message);
        }

        List<uint> NormalizeTerritoryList(IEnumerable<uint>? territories)
        {
            return territories?
                .Where(territory => territory > 0)
                .Distinct()
                .OrderBy(territory => territory)
                .ToList()
                ?? new List<uint>();
        }

        void AddCurrentTerritoryToList(List<uint> territories, string listName)
        {
            var territoryId = plugin.AutoSkipCutscenes.CurrentTerritoryId;
            if (territoryId == 0)
            {
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S9A1909494E98EC66", global::XASlave.Localization.UiMessage.Authored(listName)), true);
                return;
            }

            if (!territories.Contains(territoryId))
                territories.Add(territoryId);

            SaveAutoSkipCutsceneOptions(global::XASlave.Localization.UiMessage.Format("S0FE369F0AD9FEBB7", territoryId, global::XASlave.Localization.UiMessage.Authored(listName)));
        }

        void RemoveCurrentTerritoryFromList(List<uint> territories, string listName)
        {
            var territoryId = plugin.AutoSkipCutscenes.CurrentTerritoryId;
            if (territoryId == 0 || !territories.Remove(territoryId))
            {
                SetToonModsStatus(global::XASlave.Localization.UiMessage.Format("S5392DA5728C2230D", global::XASlave.Localization.UiMessage.Authored(listName)), true);
                return;
            }

            SaveAutoSkipCutsceneOptions(global::XASlave.Localization.UiMessage.Format("S43E9C0A098F4C185", territoryId, global::XASlave.Localization.UiMessage.Authored(listName)));
        }

        void DrawTerritoryList(string title, List<uint> territories, string id)
        {
            ImGui.TextDisabled(UiLocale.FormatKey("SFA1E7180CF6C8EA6", UiLocale.T(title), territories.Count));
            if (territories.Count == 0)
            {
                ImGui.TextDisabled(UiLocale.Get("S903EA500E7CCF842"));
                return;
            }

            foreach (var territoryId in territories.OrderBy(territory => territory).ToList())
            {
                if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("SD43B2FEFC669F80F"), id, territoryId)))
                {
                    territories.RemoveAll(entry => entry == territoryId);
                    SaveAutoSkipCutsceneOptions(global::XASlave.Localization.UiMessage.Format("SD69E62FB23C9B211", territoryId, global::XASlave.Localization.UiMessage.Authored(title)));
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{territoryId} - {plugin.AutoSkipCutscenes.GetTerritoryNameMessage(territoryId).Display}");
            }
        }

        void DrawAutoSkipCutsceneOption(string label, Func<bool> getValue, Action<bool> setValue, string helpText, string? warningText = null)
        {
            var value = getValue();
            if (UiNative.Checkbox(label, ref value))
            {
                setValue(value);
                SaveAutoSkipCutsceneOptions(global::XASlave.Localization.UiMessage.Key("S993FF3A40CFD72D4"));
            }

            ImGui.SameLine(0f, 6f);
            DrawHelpMarker(helpText);
            DrawWarningText(warningText ?? string.Empty);
        }

        void DrawAutoSkipCutsceneOptionGroup(string title, string id, Action drawContent)
        {
            ImGui.Spacing();
            bool isOpen;
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.6f, 0.9f, 1.0f, 1.0f)))
                isOpen = UiNative.CollapsingHeader($"{title}##{id}");

            if (!isOpen)
                return;

            ImGui.Indent();
            drawContent();
            ImGui.Unindent();
        }

        void DrawAutoSkipCutsceneOptions()
        {
            configuration.AutoSkipCutscenesWhitelistTerritories ??= new List<uint>();
            configuration.AutoSkipCutscenesBlacklistTerritories ??= new List<uint>();

            var currentTerritory = plugin.AutoSkipCutscenes.CurrentTerritoryId;
            var currentZone = plugin.AutoSkipCutscenes.LocalizedCurrentTerritoryName;
            var currentCategory = plugin.AutoSkipCutscenes.LocalizedCurrentCategoryLabel;
            var areaMode = configuration.AutoSkipCutscenesUseZoneWhitelist ? UiLocale.Native("S8559AFA62D4671CA") : UiLocale.Native("S3094851745D4BE7E");
            var areaAllowed = plugin.AutoSkipCutscenes.CurrentTerritoryAllowed;

            ImGui.TextDisabled(UiLocale.FormatKey("S441C8B5121F4C043", currentTerritory, currentZone));
            ImGui.TextDisabled(UiLocale.FormatKey("S993051B0010A1D62", currentCategory));
            ImGui.TextColored(
                areaAllowed ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.45f, 0.35f, 1.0f),
                UiLocale.FormatKey("S4E462B4E1F804194", UiLocale.T(areaMode), (areaAllowed ? UiLocale.Get("SEABC01F12EC3E7CB") : UiLocale.Get("S6973DDDD3EF9CB6A"))));

            DrawAutoSkipCutsceneOptionGroup(UiLocale.Native("SB00AC0B7ACD72076"), "AutoSkipCutscenesTerritoryGates", () =>
            {
                var useWhitelist = configuration.AutoSkipCutscenesUseZoneWhitelist;
                if (UiNative.Checkbox(UiLocale.Native("SC8C2AB630C2769ED"), ref useWhitelist))
                {
                    configuration.AutoSkipCutscenesUseZoneWhitelist = useWhitelist;
                    SaveAutoSkipCutsceneOptions(global::XASlave.Localization.UiMessage.Key("SB14536AEDC439B20"));
                }

                ImGui.SameLine(0f, 6f);
                DrawHelpMarker(UiLocale.Native("S1985485B078B2702"));

                if (UiNative.Button(UiLocale.Native("S01DFBC02C7925F01")))
                    AddCurrentTerritoryToList(configuration.AutoSkipCutscenesWhitelistTerritories, "whitelist");
                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("S2B330AA50D076E11")))
                    RemoveCurrentTerritoryFromList(configuration.AutoSkipCutscenesWhitelistTerritories, "whitelist");

                if (UiNative.Button(UiLocale.Native("S0FBE46E70CC9D5C5")))
                    AddCurrentTerritoryToList(configuration.AutoSkipCutscenesBlacklistTerritories, "blacklist");
                ImGui.SameLine();
                if (UiNative.Button(UiLocale.Native("S9EF9257357B56D2C")))
                    RemoveCurrentTerritoryFromList(configuration.AutoSkipCutscenesBlacklistTerritories, "blacklist");

                ImGui.Indent();
                DrawTerritoryList(UiLocale.Native("S8559AFA62D4671CA"), configuration.AutoSkipCutscenesWhitelistTerritories, "Whitelist");
                DrawTerritoryList(UiLocale.Native("S3094851745D4BE7E"), configuration.AutoSkipCutscenesBlacklistTerritories, "Blacklist");
                ImGui.Unindent();
            });

            DrawAutoSkipCutsceneOptionGroup(UiLocale.Native("S4504007C188407EC"), "AutoSkipCutscenesCategories", () =>
            {
                DrawAutoSkipCutsceneOption(UiLocale.Native("S80DADD84FF3665C1"), () => configuration.AutoSkipCutscenesSkipNormalCutscenes, value => configuration.AutoSkipCutscenesSkipNormalCutscenes = value, UiLocale.Native("S92E10FAC87088B94"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S5DA2DAAE3A808E89"), () => configuration.AutoSkipCutscenesSkipMsqRoulette, value => configuration.AutoSkipCutscenesSkipMsqRoulette = value, UiLocale.Native("S311C58E0AFD9EC33"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S3FE85499D920E456"), () => configuration.AutoSkipCutscenesAutoEnableMsqFourPlayer, value => configuration.AutoSkipCutscenesAutoEnableMsqFourPlayer = value, UiLocale.Native("S540072850A0F3029"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S039DE012FA523749"), () => configuration.AutoSkipCutscenesExemptPraetorium, value => configuration.AutoSkipCutscenesExemptPraetorium = value, UiLocale.Native("SEFCED9E65505997F"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S0192313385A02F56"), () => configuration.AutoSkipCutscenesExemptCastrum, value => configuration.AutoSkipCutscenesExemptCastrum = value, UiLocale.Native("S0215C620A3F778A9"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("SAF656552835B48B1"), () => configuration.AutoSkipCutscenesExemptPortaDecumana, value => configuration.AutoSkipCutscenesExemptPortaDecumana = value, UiLocale.Native("S83344C09F3B7AE98"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("SD57FEA6FD8AB27CA"), () => configuration.AutoSkipCutscenesSkipMassivePc, value => configuration.AutoSkipCutscenesSkipMassivePc = value, UiLocale.Native("SB27EA4D5C3853164"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S4143A9F1D7317F71"), () => configuration.AutoSkipCutscenesSkipCustomTalk, value => configuration.AutoSkipCutscenesSkipCustomTalk = value, UiLocale.Native("SCD647E7E8114D364"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S877A038BE0B19BB6"), () => configuration.AutoSkipCutscenesFeedingChocoboEnabled, value => configuration.AutoSkipCutscenesFeedingChocoboEnabled = value, UiLocale.Native("S01406B12A875FA45"));
            });

            DrawAutoSkipCutsceneOptionGroup(UiLocale.Native("S264127BB1C4A3A70"), "AutoSkipCutscenesGoldSaucerGroup", () =>
            {
                DrawAutoSkipCutsceneOption(UiLocale.Native("S97055E39B6F809B4"), () => configuration.AutoSkipCutscenesSkipGoldSaucer, value => configuration.AutoSkipCutscenesSkipGoldSaucer = value, UiLocale.Native("S91CC3F75FB08492F"));

                var goldSaucerModePrefix = configuration.AutoSkipCutscenesSkipGoldSaucer ? "Exempt" : "Skip";
                DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("SBC4B08694E0A54E0"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerMahjong, value => configuration.AutoSkipCutscenesGoldSaucerMahjong = value, UiLocale.Native("S0243A9B09CBDFCC0"));
                DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("SA46994DF207D79D6"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerAirForceOne, value => configuration.AutoSkipCutscenesGoldSaucerAirForceOne = value, UiLocale.Native("SF38270226845F379"));
                DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S990D7674803F4AAC"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerChocoboRacing, value => configuration.AutoSkipCutscenesGoldSaucerChocoboRacing = value, UiLocale.Native("SB3D7AE806BD176E8"));
                DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S66A04852E02DA73D"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerLordOfVerminion, value => configuration.AutoSkipCutscenesGoldSaucerLordOfVerminion = value, UiLocale.Native("S2B1BDBF2907460A3"));
                DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S78577827CB90FB88"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerTripleTriad, value => configuration.AutoSkipCutscenesGoldSaucerTripleTriad = value, UiLocale.Native("S505BDF8ACADDFF6B"));
                DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S02DC464C59647D34"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerBlunderville, value => configuration.AutoSkipCutscenesGoldSaucerBlunderville = value, UiLocale.Native("SABA910C06DEA4C83"));
            });

            DrawAutoSkipCutsceneOptionGroup(UiLocale.Native("S9601BCDC396DD3AC"), "AutoSkipCutscenesRiskyAreas", () =>
            {
                DrawWarningText(UiLocale.Native("S049E6558947522FB"));
                if (plugin.Configuration.DebugMenuVisible)
                {
                    var goldSaucerModePrefix = configuration.AutoSkipCutscenesSkipGoldSaucer ? "Exempt" : "Skip";
                    DrawAutoSkipCutsceneOption(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S8D3D2885DDACAA65"), goldSaucerModePrefix), () => configuration.AutoSkipCutscenesGoldSaucerFashionReport, value => configuration.AutoSkipCutscenesGoldSaucerFashionReport = value, UiLocale.Native("S80AC7D9A43B48DF9"), UiLocale.Native("S98A7BE4A43D289A8"));
                }

                DrawAutoSkipCutsceneOption(UiLocale.Native("S64FCD408AA2F4F43"), () => configuration.AutoSkipCutscenesSkipOceanFishing, value => configuration.AutoSkipCutscenesSkipOceanFishing = value, UiLocale.Native("SDA677CF7BAFCBC3A"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S15444D594B76D4E7"), () => configuration.AutoSkipCutscenesSkipCrystallineConflict, value => configuration.AutoSkipCutscenesSkipCrystallineConflict = value, UiLocale.Native("S0176D8817E7E5875"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("S4F4856D505617DAF"), () => configuration.AutoSkipCutscenesSkipFrontlineRivalWings, value => configuration.AutoSkipCutscenesSkipFrontlineRivalWings = value, UiLocale.Native("S3318CE43FA272920"));
                DrawAutoSkipCutsceneOption(UiLocale.Native("SF261212F5FDB5E1C"), () => configuration.AutoSkipCutscenesSkipInn, value => configuration.AutoSkipCutscenesSkipInn = value, UiLocale.Native("S4737E1226DFCC675"));
            });
        }

        if (buildCatalogue)
        {
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "custom-mouse",
            UiLocale.Native("SD216F5BE3C2D80DD"),
            () => configuration.CustomMouseEnabled,
            plugin.CustomMouse.SetEnabled,
            applied => configuration.CustomMouseEnabled = applied,
            UiLocale.Native("S5B4F466C128023F1"),
            UiLocale.Native("S4695C65FFB2C1996"),
            () => plugin.CustomMouse.LocalizedStatusText,
            searchTerms: ["cursor", "mouse", "size", "scale", "outline", "color", "custom", "artwork", "animation", "trail", "starcraft", "brood", "ani", "cur"],
            drawOptions: DrawCustomMouseOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-allow-multiple-game-instances",
            UiLocale.Native("S70C9D007A7F6FE3E"),
            () => configuration.AutoAllowMultipleGameInstancesEnabled,
            plugin.SystemWindowMods.SetAllowMultipleGameInstancesEnabled,
            applied => configuration.AutoAllowMultipleGameInstancesEnabled = applied,
            UiLocale.Native("S133B3936ABC1919D"),
            UiLocale.Native("S9CDBA67AB6170026"),
            () => plugin.SystemWindowMods.LocalizedAllowMultipleGameInstancesStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-cancel-login-cooldown",
            UiLocale.Native("SBAA9A862D2C2AC8C"),
            () => configuration.AutoCancelLoginCooldownEnabled,
            plugin.SystemWindowMods.SetCancelLoginCooldownEnabled,
            applied => configuration.AutoCancelLoginCooldownEnabled = applied,
            UiLocale.Native("S35F70100ED882725"),
            UiLocale.Native("S9D550D342B957ED2"),
            () => plugin.SystemWindowMods.LocalizedCancelLoginCooldownStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "auto-display-msq-progress",
            UiLocale.Native("S10F930E55675BD2E"),
            () => configuration.AutoDisplayMsqProgressEnabled,
            plugin.MsqProgressDisplay.SetEnabled,
            applied => configuration.AutoDisplayMsqProgressEnabled = applied,
            UiLocale.Native("S7E57C4AE876A4A5A"),
            UiLocale.Native("S1719A2E3AF8D2466"),
            () => plugin.MsqProgressDisplay.LocalizedStatusText,
            searchTerms: ["Scenario Tree", "main scenario", "remaining", "completion percentage"]);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "disable-title-screen-movie",
            UiLocale.Native("S2A42EC4294B8E320"),
            () => configuration.DisableTitleScreenMovieEnabled,
            plugin.SystemWindowMods.SetDisableTitleScreenMovieEnabled,
            applied => configuration.DisableTitleScreenMovieEnabled = applied,
            UiLocale.Native("S7BF4F96B6822BEF8"),
            UiLocale.Native("SA283EC9C7148B9B3"),
            () => plugin.SystemWindowMods.LocalizedDisableTitleScreenMovieStatusText,
            searchTerms: ["title screen", "movie", "intro", "AgentLobby", "IdleTime"]);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "auto-display-ids",
            UiLocale.Native("S123052455FC6ED8D"),
            () => configuration.AutoDisplayIdsEnabled,
            value =>
            {
                configuration.AutoDisplayIdsEnabled = value;
                plugin.ApplyAutoDisplayIdsConfiguration(save: false);

                if (!value)
                    return plugin.AutoDisplayIds.SetEnabled(false);

                return plugin.AutoDisplayIds.SetEnabled(true);
            },
            applied => configuration.AutoDisplayIdsEnabled = applied,
            UiLocale.Native("S6D421ECB2380123F"),
            UiLocale.Native("SB5B42835EEF40667"),
            () => plugin.AutoDisplayIds.LocalizedStatusText,
            searchTerms: ["tooltip", "skill id", "action id", "target data id", "weather id", "zone id", "map id"],
            drawOptions: DrawAutoDisplayIdsOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "display-network-latency",
            UiLocale.Native("SEE47AC5C8172FBBB"),
            () => configuration.AutoDisplayNetworkLatencyEnabled,
            value =>
            {
                ApplyAutoDisplayNetworkLatencyConfiguration();
                return plugin.AutoDisplayNetworkLatency.SetEnabled(value);
            },
            applied => configuration.AutoDisplayNetworkLatencyEnabled = applied,
            UiLocale.Native("S8EDD237D0AC7FC98"),
            UiLocale.Native("S947AAE76E976616F"),
            () => plugin.AutoDisplayNetworkLatency.LocalizedStatusText,
            searchTerms: ["ping", "latency", "DTR", "server", "network"],
            drawOptions: DrawAutoDisplayNetworkLatencyOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "custom-timestamp-format",
            UiLocale.Native("S7F54A2B6E5B74BC5"),
            () => configuration.CustomTimestampFormatEnabled,
            plugin.ChatTimestampFormat.SetEnabled,
            applied => configuration.CustomTimestampFormatEnabled = applied,
            UiLocale.Native("S5AC96531F36648C5"),
            UiLocale.Native("S0682DC07BE79BABD"),
            () => plugin.ChatTimestampFormat.LocalizedStatusText,
            warningText: UiLocale.Native("S28AD1421BED6B61B"),
            searchTerms: ["chat", "timestamp", "seconds", "time", "7840", "7841"],
            drawOptions: DrawCustomTimestampFormatOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "no-ui-fade",
            UiLocale.Native("SEAB3878411C39C89"),
            () => configuration.NoUiFadeEnabled,
            plugin.NoUiFade.SetEnabled,
            applied => configuration.NoUiFadeEnabled = applied,
            UiLocale.Native("S2F73BCA69F002A5E"),
            UiLocale.Native("S2EA401BB14A4AE44"),
            () => plugin.NoUiFade.LocalizedStatusText,
            warningText: UiLocale.Native("S0C16D2D79787E517"),
            searchTerms: ["fade", "black fade", "white fade", "event fade", "screen fade", "transition", "UIOptimization"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "target-command-fix",
            UiLocale.Native("S762FC3E87F23DC97"),
            () => configuration.TargetCommandFixEnabled,
            plugin.TargetCommandFix.SetEnabled,
            applied => configuration.TargetCommandFixEnabled = applied,
            UiLocale.Native("SF448DC2B25F04CFF"),
            UiLocale.Native("S9ECCBA8775C04D82"),
            () => plugin.TargetCommandFix.LocalizedStatusText,
            searchTerms: ["/target", "target fix", "Rodney", "NPC", "player"]);
        var autoSkipCutsceneHelpText = plugin.Configuration.DebugMenuVisible
            ? UiLocale.Native("S83096431620DDD1A")
            : UiLocale.Native("SCCD20E323DEC52DC");
        var autoSkipCutsceneSearchTerms = plugin.Configuration.DebugMenuVisible
            ? new[] { "Whitelist", "Blacklist", "MSQ", "Praetorium", "Castrum", "Porta", "Massive PC", "Gold Saucer", "Mahjong", "Air Force One", "Chocobo Racing", "Lord of Verminion", "Triple Triad", "Blunderville", "Fashion Report", "Custom Talk", "Feed Buddy", "Ocean Fishing", "Crystalline Conflict", "Frontline", "Rival Wings", "Inn" }
            : new[] { "Whitelist", "Blacklist", "MSQ", "Praetorium", "Castrum", "Porta", "Massive PC", "Gold Saucer", "Mahjong", "Air Force One", "Chocobo Racing", "Lord of Verminion", "Triple Triad", "Blunderville", "Custom Talk", "Feed Buddy", "Ocean Fishing", "Crystalline Conflict", "Frontline", "Rival Wings", "Inn" };
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-skip-cutscenes",
            UiLocale.Native("S87AEB02125D0132D"),
            () => configuration.AutoSkipCutscenesEnabled,
            value =>
            {
                ApplyAutoSkipCutscenesConfiguration();
                return plugin.AutoSkipCutscenes.SetEnabled(value);
            },
            applied => configuration.AutoSkipCutscenesEnabled = applied,
            UiLocale.Native("S79AA86C4BF81E331"),
            autoSkipCutsceneHelpText,
            () => plugin.AutoSkipCutscenes.LocalizedStatusText,
            searchTerms: autoSkipCutsceneSearchTerms,
            drawOptions: DrawAutoSkipCutsceneOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "auto-ignore-minimum-window-size",
            UiLocale.Native("SD9CCEE8B9E9A452B"),
            () => configuration.AutoIgnoreMinimumWindowSizeEnabled,
            plugin.SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled,
            applied => configuration.AutoIgnoreMinimumWindowSizeEnabled = applied,
            UiLocale.Native("S933F814D96A6656C"),
            UiLocale.Native("S2CF4D9C3ACDA661E"),
            () => plugin.SystemWindowMods.LocalizedIgnoreMinimumWindowSizeStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "auto-hide-unnecessary-popups",
            UiLocale.Native("S5644B7CB830E4670"),
            () => configuration.AutoHideUnnecessaryPopupsEnabled,
            plugin.PopupCleaner.SetEnabled,
            applied => configuration.AutoHideUnnecessaryPopupsEnabled = applied,
            UiLocale.Native("SB9AB76E639D0AF55"),
            UiLocale.Native("S2D0798E2F7A50FC9"),
            () => plugin.PopupCleaner.LocalizedStatusText,
            searchTerms: ["HowToNotice", "HowTo", "PlayGuide", "RecommendList", "AchievementInfo"],
            drawOptions: DrawHideUnnecessaryPopupsOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "dalamud-notifications-suck",
            UiLocale.Native("SE5A6781ECCBB2E55"),
            () => configuration.DalamudNotificationsSuckEnabled,
            value =>
            {
                ApplyDalamudNotificationsSuckConfiguration();
                return plugin.DalamudNotificationsSuck.SetEnabled(value);
            },
            applied => configuration.DalamudNotificationsSuckEnabled = applied,
            UiLocale.Native("S83657192E4C52039"),
            UiLocale.Native("SE06443E1DE8CC982"),
            () => plugin.DalamudNotificationsSuck.LocalizedStatusText,
            warningText: UiLocale.Native("SDA1401774C847E4B"),
            searchTerms: ["Dalamud", "notification", "toast", "plugin error", "plugin load", "Penumbra", "Glamourer", "mod failed", "update alert"],
            drawOptions: DrawDalamudNotificationsSuckOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "better-highlight-potential-targets",
            UiLocale.Native("S01C98DEAB6E2632E"),
            () => configuration.BetterHighlightPotentialTargetsEnabled,
            value =>
            {
                ApplyBetterHighlightPotentialTargetsConfiguration();
                return plugin.BetterHighlightPotentialTargets.SetEnabled(value);
            },
            applied => configuration.BetterHighlightPotentialTargetsEnabled = applied,
            UiLocale.Native("S60063B7224579D87"),
            UiLocale.Native("SD54576363D18B97A"),
            () => plugin.BetterHighlightPotentialTargets.LocalizedStatusText,
            warningText: UiLocale.Native("S146A3DE1F815CF97"),
            searchTerms: ["highlight", "target highlight", "mouseover", "mouse over", "hover", "yellow outline", "potential target", "ObjectHighlightColor"],
            drawOptions: DrawBetterHighlightPotentialTargetsOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-prevent-game-exiting-from-lobby-errors",
            UiLocale.Native("S0BB03716893147BA"),
            () => configuration.AutoPreventGameExitingFromLobbyErrorsEnabled,
            plugin.SystemWindowMods.SetPreventLobbyExitEnabled,
            applied => configuration.AutoPreventGameExitingFromLobbyErrorsEnabled = applied,
            UiLocale.Native("SF9A0DCABBF95DE4D"),
            UiLocale.Native("S1F8E1DF287F3C738"),
            () => plugin.SystemWindowMods.LocalizedPreventLobbyExitStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-close-lobby-errors",
            UiLocale.Native("S272725A7DCEDD630"),
            () => configuration.AutoCloseLobbyErrorsEnabled,
            plugin.LobbyErrorAutoClose.SetEnabled,
            applied => configuration.AutoCloseLobbyErrorsEnabled = applied,
            UiLocale.Native("SEA1E050C03043098"),
            UiLocale.Native("S3EF9B938695EF420"),
            () => plugin.LobbyErrorAutoClose.LocalizedStatusText,
            searchTerms: ["505", "Data Center Travel", "SelectOk", "Xagman", "3088", "5006", "90002", "3102", "Connection with the server was lost.", "You are still logged into the game.", "Dialogue", "OK", "NoKillPlugin", "nokill", "No Kill Plugin Panel", "auth error"]);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "bailout-esc-menu",
            UiLocale.Native("SA41C83913F62F6ED"),
            () => configuration.BailoutEscMenuEnabled,
            plugin.EscMenuBailout.SetEnabled,
            applied => configuration.BailoutEscMenuEnabled = applied,
            UiLocale.Native("S210EF3798A512089"),
            UiLocale.Native("SF3F2A30E5065EB96"),
            () => plugin.EscMenuBailout.LocalizedStatusText,
            searchTerms: ["SystemMenu", "ESC menu", "escape menu", "close system menu", "timeout", "bailout"],
            drawOptions: DrawBailoutEscMenuOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "auto-hide-game-objects",
            UiLocale.Native("SFC7DFC9B7B8D3E3D"),
            () => configuration.AutoHideGameObjectsEnabled,
            plugin.SetAutoHideGameObjectsEnabled,
            applied => configuration.AutoHideGameObjectsEnabled = applied,
            UiLocale.Native("S6A32C73CD31853D7"),
            UiLocale.Native("S194CB4AE3436C741"),
            () => plugin.AutoHideGameObjects.LocalizedStatusText,
            searchTerms: ["Hide players", "Hide Friends", "Hide Party & Alliance Members", "Hide Non-Friends", "Only Hide Players Beyond Distance", "Sanctuary Distance", "Max Distance", "Honor Sanctuary Minimums", "yalms", "Hide unimportant NPCs", "Hide pets", "minions", "Hide Fashion Accessories", "ornaments", "parasols", "umbrellas", "Hide Beasts", "Hide own Beast", "Beastmaster", "BST", "Hide chocobos", "Occult Crescent", "Island Sanctuary", "duty"],
            drawOptions: DrawAutoHideGameObjectsOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "custom-resolutions",
            UiLocale.Native("S5FE7C6A49AB02414"),
            () => configuration.CustomResolutionsEnabled,
            plugin.SystemWindowMods.SetCustomResolutionsEnabled,
            applied => configuration.CustomResolutionsEnabled = applied,
            UiLocale.Native("S0C6345BBBE3444C3"),
            UiLocale.Native("SB021CA59C4D00A6B"),
            () => plugin.SystemWindowMods.LocalizedCustomResolutionsStatusText,
            searchTerms: ["/xa res", "/xa res reset", "500x345", "1280 720", "Width", "Height", "Add button", "Delete"],
            drawOptions: DrawCustomResolutionTools);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "auto-skip-dialogue",
            UiLocale.Native("SACBF3F6A5B6FFEB5"),
            () => configuration.AutoSkipDialogueEnabled,
            plugin.DialogueSkip.SetEnabled,
            applied => configuration.AutoSkipDialogueEnabled = applied,
            UiLocale.Native("SA3B79C2F9F81D1BD"),
            UiLocale.Native("S908C336176999BED"),
            () => plugin.DialogueSkip.LocalizedStatusText,
            searchTerms: ["Talk", "SystemTalk", "ShortTalk", "npc", "dialogue", "conversation", "leve", "guildleve"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "lock-game-window-in-combat",
            UiLocale.Native("S29F583707A18B82E"),
            () => configuration.LockGameWindowInCombatEnabled,
            plugin.AutoLockGameWindow.SetEnabled,
            applied => configuration.LockGameWindowInCombatEnabled = applied,
            UiLocale.Native("S76FF345B02ABBFDF"),
            UiLocale.Native("S526D8DB5CBA40508"),
            () => plugin.AutoLockGameWindow.LocalizedStatusText,
            searchTerms: ["window", "combat", "lock", "move", "position"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "notify-when-friend-is-near",
            UiLocale.Native("S565B74269109BD23"),
            () => configuration.NotifyWhenFriendIsNearEnabled,
            value =>
            {
                ApplyNotifyWhenFriendIsNearConfiguration();
                return plugin.NotifyWhenFriendIsNear.SetEnabled(value);
            },
            applied => configuration.NotifyWhenFriendIsNearEnabled = applied,
            UiLocale.Native("SF589A11112AA3486"),
            UiLocale.Native("S00E3563479B6EE5C"),
            () => plugin.NotifyWhenFriendIsNear.LocalizedStatusText,
            searchTerms: ["friend", "near", "player", "notify", "regex", "system message"],
            drawOptions: DrawNotifyWhenFriendIsNearOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "alert-when-typing-in-combat",
            UiLocale.Native("S3CBE7B499836F879"),
            () => configuration.AlertWhenTypingInCombatEnabled,
            value =>
            {
                ApplyAlertWhenTypingInCombatConfiguration();
                return plugin.AlertWhenTypingInCombat.SetEnabled(value);
            },
            applied => configuration.AlertWhenTypingInCombatEnabled = applied,
            UiLocale.Native("S2CD3F48D2F7C0F6F"),
            UiLocale.Native("S7F28964792175E3E"),
            () => plugin.AlertWhenTypingInCombat.LocalizedStatusText,
            searchTerms: ["combat", "typing", "ChatLog", "chat box", "toast", "sound", "tone", "pitch", "beeps", "cooldown", "/xa typingcombat"],
            drawOptions: DrawAlertWhenTypingInCombatOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "better-cast-bar",
            UiLocale.Native("S49DE54B84FC8AA53"),
            () => configuration.BetterCastBarEnabled,
            value =>
            {
                ApplyBetterCastBarConfiguration();
                return plugin.BetterCastBar.SetEnabled(value);
            },
            applied => configuration.BetterCastBarEnabled = applied,
            UiLocale.Native("SE3C32DC912C6E73F"),
            UiLocale.Native("S29D31902115EE920"),
            () => plugin.BetterCastBar.LocalizedStatusText,
            searchTerms: ["_CastBar", "slidecast", "cast", "casting", "marker", "spell"],
            drawOptions: DrawBetterCastBarOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "better-duty-finder",
            UiLocale.Native("S023C4C5983AAE7D3"),
            () => configuration.BetterDutyFinderEnabled,
            plugin.BetterDutyFinder.SetEnabled,
            applied => configuration.BetterDutyFinderEnabled = applied,
            UiLocale.Native("S7621348B89885E98"),
            UiLocale.Native("S6F9E1820636CB793"),
            () => plugin.BetterDutyFinder.LocalizedStatusText,
            searchTerms: ["duty finder", "ContentsFinder", "RaidFinder", "unsync", "explorer", "loot", "language", "Simple Tweaks", "conflict"],
            drawOptions: SimpleTweaksDutyFinderIntegration.DrawSettings);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "display-actual-queue-position",
            UiLocale.Native("SA67487F2DBAEAB93"),
            () => configuration.DisplayActualQueuePositionEnabled,
            plugin.QueuePositionDisplay.SetEnabled,
            applied => configuration.DisplayActualQueuePositionEnabled = applied,
            UiLocale.Native("SDADC2844CE20395E"),
            UiLocale.Native("S04475D8DC1B8F0A7"),
            () => plugin.QueuePositionDisplay.LocalizedStatusText,
            searchTerms: ["ETA", "elapsed", "queue position", "wait time"]);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "replace-unowned-mount-hotbars",
            UiLocale.Native("S6D3BD3F66D7757D7"),
            () => configuration.ReplaceUnownedMountHotbarsEnabled,
            plugin.ReplaceUnownedMountHotbars.SetEnabled,
            applied => configuration.ReplaceUnownedMountHotbarsEnabled = applied,
            UiLocale.Native("S835714CCC4496649"),
            UiLocale.Native("S83361C10B14C62E8"),
            () => plugin.ReplaceUnownedMountHotbars.LocalizedStatusText,
            searchTerms: ["mount roulette", "unowned mount", "hotbar", "cross hotbar", "missing mount"]);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "disable-background-game-rendering",
            UiLocale.Native("SA0D7106CA86D90A6"),
            () => configuration.DisableBackgroundGameRenderingEnabled,
            plugin.SystemWindowMods.SetDisableBackgroundRenderingEnabled,
            applied => configuration.DisableBackgroundGameRenderingEnabled = applied,
            UiLocale.Native("S74063A6FB8C3DED9"),
            UiLocale.Native("SB142B1321DD59F75"),
            () => plugin.SystemWindowMods.LocalizedDisableBackgroundRenderingStatusText,
            searchTerms: ["Only while minimized", "Disable when AR Multi is on", "AutoRetainer multi-mode", "DX11", "nameplate"],
            drawOptions: DrawBackgroundRenderingOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "low-resolution",
            UiLocale.Native("S87DA16CCE36926CD"),
            () => configuration.LowResolutionEnabled,
            plugin.SystemWindowMods.SetLowResolutionEnabled,
            applied => configuration.LowResolutionEnabled = applied,
            UiLocale.Native("S2EC931FAE279B670"),
            UiLocale.Native("SC65D8773EFADFD3B"),
            () => plugin.SystemWindowMods.LocalizedLowResolutionStatusText,
            searchTerms: ["3D resolution scale", "resolution scale", "upscale type", "0.01", "1.00", "DLSS", "FSR"],
            drawOptions: DrawLowResolutionOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "copy-item-name-for-all",
            UiLocale.Native("SE47CEA80BEEED0B2"),
            () => configuration.CopyItemNameForAllEnabled,
            plugin.CopyItemNameContextMenu.SetEnabled,
            applied => configuration.CopyItemNameForAllEnabled = applied,
            UiLocale.Native("SBB77884D01440A1C"),
            UiLocale.Native("SF1239BB603DA5D61"),
            () => plugin.CopyItemNameContextMenu.LocalizedStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.GraphicMods,
            "special-rendering-modes",
            UiLocale.Native("SB26660220C4D59AB"),
            () => configuration.SpecialRenderModesEnabled,
            SetSpecialRenderModesEnabled,
            applied => configuration.SpecialRenderModesEnabled = applied,
            UiLocale.Native("S3BB0E78027555330"),
            UiLocale.Native("S09E9B77C5FE0DF9A"),
            () => configuration.SpecialRenderModesEnabled
                ? plugin.SystemWindowMods.LocalizedSpecialRenderModesStatusText
                : global::XASlave.Localization.UiLocale.Get("S75081B593D15CF6E"),
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
            ToonModsSection.UiMods,
            "expanded-player-right-click-menu-search",
            UiLocale.Native("SF6F93682DCAB79ED"),
            () => configuration.ExpandedPlayerRightClickMenuSearchEnabled,
            plugin.PlayerSearchContextMenu.SetEnabled,
            applied => configuration.ExpandedPlayerRightClickMenuSearchEnabled = applied,
            UiLocale.Native("S9C8E6B56A40238CC"),
            UiLocale.Native("S354527B654EB7791"),
            () => plugin.PlayerSearchContextMenu.LocalizedStatusText,
            searchTerms: ["FFLogs", "Lodestone", "Lalachievements", "Open All"],
            drawOptions: DrawPlayerSearchOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "live-anonymous-mode",
            UiLocale.Native("S82ED5909A4A95EC2"),
            () => configuration.LiveAnonymousModeEnabled,
            plugin.NameplatePrivacy.SetAnonymousModeEnabled,
            applied => configuration.LiveAnonymousModeEnabled = applied,
            UiLocale.Native("S36D42E20ABF8C875"),
            UiLocale.Native("SC0F61F81D8770647"),
            () => plugin.NameplatePrivacy.LocalizedAnonymousModeStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "estate-teleportation-context-menu",
            UiLocale.Native("S9DFC8C2F4F794E5C"),
            () => configuration.EstateTeleportationContextMenuEnabled,
            plugin.EstateTeleportationContextMenu.SetEnabled,
            applied => configuration.EstateTeleportationContextMenuEnabled = applied,
            UiLocale.Native("S1CE4B909FE335364"),
            UiLocale.Native("SB212DF84432E35E3"),
            () => plugin.EstateTeleportationContextMenu.LocalizedStatusText,
            searchTerms: ["housing", "estate", "teleport", "friend", "party", "right click"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "better-inventory-mover",
            UiLocale.Native("SD322E1206FB77885"),
            () => configuration.BetterInventoryMoverEnabled,
            plugin.BetterInventoryMover.SetEnabled,
            applied => configuration.BetterInventoryMoverEnabled = applied,
            UiLocale.Native("S0FF9CCFB6A24EE55"),
            UiLocale.Native("SDCF667D6B0964E88"),
            () => plugin.BetterInventoryMover.LocalizedStatusText,
            searchTerms: ["inventory", "retainer", "saddlebag", "premium saddlebag", "move item", "context menu"],
            drawOptions: DrawBetterInventoryMoverOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "better-company-chest",
            UiLocale.Native("S60090C51AF9AAB79"),
            () => configuration.BetterCompanyChestEnabled,
            plugin.BetterCompanyChest.SetEnabled,
            applied => configuration.BetterCompanyChestEnabled = applied,
            UiLocale.Native("S3B8D7D5C2EA88D38"),
            UiLocale.Native("S4A43A26BF77CA2A6"),
            () => plugin.BetterCompanyChest.LocalizedStatusText,
            searchTerms: ["Company Chest", "FC chest", "free company chest", "quick move", "quantity", "exchangeable", "crystals"],
            drawOptions: DrawBetterCompanyChestOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-open-moogle-mail",
            UiLocale.Native("SFF7567E271F23362"),
            () => configuration.AutoOpenMoogleMailEnabled,
            plugin.AutoOpenMoogleMail.SetEnabled,
            applied => configuration.AutoOpenMoogleMailEnabled = applied,
            UiLocale.Native("SD71CE33ECD43F931"),
            UiLocale.Native("S1869F86943A96E2F"),
            () => plugin.AutoOpenMoogleMail.LocalizedStatusText,
            searchTerms: ["Moogle Mail", "Delivery Moogle", "letters", "attachments", "request delivery", "delete letters"],
            drawOptions: DrawAutoOpenMoogleMailOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "enable-item-icon-in-shops",
            UiLocale.Native("S3837B5DE6FD2CC40"),
            () => configuration.EnableItemIconInShopsEnabled,
            plugin.EnableItemIconInShops.SetEnabled,
            applied => configuration.EnableItemIconInShopsEnabled = applied,
            UiLocale.Native("SC5D755460EA10F7C"),
            UiLocale.Native("S8CE051748612DA57"),
            () => plugin.EnableItemIconInShops.LocalizedStatusText,
            searchTerms: ["shop", "item icon", "GrandCompanyExchange", "CollectablesShop", "FreeShop", "exchange"],
            drawOptions: DrawEnableItemIconInShopsOptions);
        AddSavedFeatureEntry(
            ToonModsSection.EurekaMods,
            "field-operations-entry-command",
            UiLocale.Native("SF44939EAF2840308"),
            () => configuration.FieldEntryCommandEnabled,
            plugin.FieldEntryCommand.SetEnabled,
            applied => configuration.FieldEntryCommandEnabled = applied,
            UiLocale.Native("SBD665CEF3C81AA04"),
            UiLocale.Native("SD3EDB7525D312DA3"),
            () => plugin.FieldEntryCommand.LocalizedStatusText,
            searchTerms: ["/xa fe", "field entry", "field operations", "Eureka", "Anemos", "Pagos", "Pyros", "Hydatos", "Rodney"],
            drawOptions: DrawFieldEntryCommandOptions);

        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "anti-afk",
            UiLocale.Native("SEE53F9C504B94DCF"),
            () => configuration.AntiAfkEnabled,
            plugin.AntiAfk.SetEnabled,
            applied => configuration.AntiAfkEnabled = applied,
            UiLocale.Native("SF4DF3F21790184EB"),
            UiLocale.Native("S52B5571D6D161E11"),
            () => plugin.AntiAfk.LocalizedStatusText,
            searchTerms: ["afk", "idle", "kick", "timer"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-duty-commence",
            UiLocale.Native("SBCF1820DD8640D0F"),
            () => configuration.AutoDutyCommenceEnabled,
            plugin.AutoDutyCommence.SetEnabled,
            applied => configuration.AutoDutyCommenceEnabled = applied,
            UiLocale.Native("S94923C883478032C"),
            UiLocale.Native("SC6E917F69A6337CA"),
            () => plugin.AutoDutyCommence.LocalizedStatusText,
            searchTerms: ["duty", "commence", "ContentsFinderConfirm", "queue pop", "ready check"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-expert-delivery",
            UiLocale.Native("SDDD0990DE8ED14DD"),
            () => configuration.AutoUnlockExpertDeliveryEnabled,
            plugin.AutoUnlockExpertDelivery.SetEnabled,
            applied => configuration.AutoUnlockExpertDeliveryEnabled = applied,
            UiLocale.Native("S36381836B6E41691"),
            UiLocale.Native("S549493006A644248"),
            () => plugin.AutoUnlockExpertDelivery.LocalizedStatusText,
            searchTerms: ["Auto switch on window open", "Landing page", "Skip HQ items", "Skip items with materia", "Ignore seal max and keep selling"],
            drawOptions: DrawExpertDeliveryOptions);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "auto-unlock-expert-delivery",
            UiLocale.Native("SBF1FF873224BC93F"),
            () => configuration.UnlockExpertDeliveryEnabled,
            plugin.ExpertDeliveryUnlock.SetEnabled,
            applied => configuration.UnlockExpertDeliveryEnabled = applied,
            UiLocale.Native("SE73337036CC3F557"),
            UiLocale.Native("S2C4D6232A26A4B27"),
            () => plugin.ExpertDeliveryUnlock.LocalizedStatusText,
            warningText: UiLocale.Native("S261711DAB2DD847B"),
            requireCtrlShiftToEnable: true,
            searchTerms: ["GC rank floor", "Grand Company rank", "rank 0", "rank 19", "Storm Captain", "Storm Champion"],
            drawOptions: DrawUnlockExpertDeliveryOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-refuse-trade-request",
            UiLocale.Native("SD4C5C475E71938D8"),
            () => configuration.AutoRefuseTradeRequestEnabled,
            plugin.AutoRefuseTrade.SetEnabled,
            applied => configuration.AutoRefuseTradeRequestEnabled = applied,
            UiLocale.Native("S33AA136B4CB3AC85"),
            UiLocale.Native("SD831F917366D7B1E"),
            () => plugin.AutoRefuseTrade.LocalizedStatusText,
            searchTerms: ["Show notification", "Send /e message", "Echo lines", "Commands after refusal", "<trader>", "<target>"],
            drawOptions: DrawTradeRefusalOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "show-titles-as-playernames",
            UiLocale.Native("S3CAF43B3413D96EB"),
            () => configuration.ShowTitlesAsPlayernamesEnabled,
            SetShowTitlesAsPlayernamesEnabled,
            applied => configuration.ShowTitlesAsPlayernamesEnabled = applied,
            UiLocale.Native("S5CEC1B51506C49CD"),
            UiLocale.Native("SF52DE5303A0E9DF7"),
            () => plugin.NameplatePrivacy.LocalizedShowTitlesAsPlayernamesStatusText,
            searchTerms: ["title", "titles", "player title", "prefix title", "suffix title", "nameplate", "playernames", "Honorific", "custom title", "traveler"],
            drawOptions: DrawShowTitlesAsPlayernamesOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "hide-nameplate-status-icons",
            UiLocale.Native("S6FC3FB611DBCAA8B"),
            () => configuration.HideNameplateStatusIconsEnabled,
            SetHideNameplateStatusIconsEnabled,
            applied => configuration.HideNameplateStatusIconsEnabled = applied,
            UiLocale.Native("SD6652313B063BB7A"),
            UiLocale.Native("S05EEDB76D467565B"),
            () => plugin.NameplatePrivacy.LocalizedHideStatusIconsStatusText,
            searchTerms: ["nameplate", "status", "mentor", "sprout", "new adventurer", "returner", "disconnect", "busy", "away", "online"],
            drawOptions: DrawNameplateStatusIconOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "remove-nameplate-fc-tag",
            UiLocale.Native("S2455041D1042D3DD"),
            () => configuration.RemoveNameplateFcTagEnabled,
            plugin.NameplatePrivacy.SetRemoveFcTagEnabled,
            applied => configuration.RemoveNameplateFcTagEnabled = applied,
            UiLocale.Native("SA7757F802E6794F9"),
            UiLocale.Native("SB4AFF5552C141670"),
            () => plugin.NameplatePrivacy.LocalizedRemoveFcTagStatusText,
            searchTerms: ["nameplate", "free company", "FC", "tag", "wanderer", "traveler", "voyager"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "show-blacklisted-playername-in-party",
            UiLocale.Native("S04E4A04253B995BA"),
            () => configuration.ShowBlacklistedPlayernameInPartyEnabled,
            plugin.BlacklistedPartyName.SetEnabled,
            applied => configuration.ShowBlacklistedPlayernameInPartyEnabled = applied,
            UiLocale.Native("S44A78249C3CF5318"),
            UiLocale.Native("S681BDCED249A36F0"),
            () => plugin.BlacklistedPartyName.LocalizedStatusText,
            searchTerms: ["blacklist", "blacklisted", "block list", "blocked", "party list", "_PartyList", "Unknown 01", "Unknown", "account id", "red text"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "show-traveler-world-names",
            UiLocale.Native("S1C8BB37AFDACB597"),
            () => configuration.ShowTravelerWorldNamesEnabled,
            SetShowTravelerWorldNamesEnabled,
            applied => configuration.ShowTravelerWorldNamesEnabled = applied,
            UiLocale.Native("S7589D8D7A0BEE5CB"),
            UiLocale.Native("SF4F0754AAAB8997E"),
            () => plugin.NameplatePrivacy.LocalizedShowTravelerWorldNamesStatusText,
            searchTerms: ["traveler", "traveller", "wanderer", "voyager", "world visit", "data center travel", "cross data center", "physical data center", "FC tag", "free company tag", "home world", "disable in duties", "duty", "spacer", "space", "Name @ HomeWorld"],
            drawOptions: DrawShowTravelerWorldNamesOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-reveal-undiscovered-areas",
            UiLocale.Native("S58BFF7A44F39AA88"),
            () => configuration.AutoRevealUndiscoveredAreasEnabled,
            plugin.SystemWindowMods.SetRevealUndiscoveredAreasEnabled,
            applied => configuration.AutoRevealUndiscoveredAreasEnabled = applied,
            UiLocale.Native("S5A2CCA43127FEC0A"),
            UiLocale.Native("S6B7471DB31495F30"),
            () => plugin.SystemWindowMods.LocalizedRevealUndiscoveredAreasStatusText);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-clear-teleportation-lock",
            UiLocale.Native("S1123E9E883367E74"),
            () => configuration.AutoClearTeleportationLockEnabled,
            plugin.TeleportLockClear.SetEnabled,
            applied => configuration.AutoClearTeleportationLockEnabled = applied,
            UiLocale.Native("S3D3D49619C4919F0"),
            UiLocale.Native("S3FC4BE10EF581AAB"),
            () => plugin.TeleportLockClear.LocalizedStatusText,
            searchTerms: ["1665", "Teleport", "stuck", "log message"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-leave-duty",
            UiLocale.Native("S6CDBCC0A69DE6B17"),
            () => configuration.AutoLeaveDutyEnabled,
            plugin.AutoLeaveDuty.SetEnabled,
            applied => configuration.AutoLeaveDutyEnabled = applied,
            UiLocale.Native("S4CAA3F0D777AC415"),
            UiLocale.Native("SF18265A92A59730D"),
            () => plugin.AutoLeaveDuty.LocalizedStatusText,
            searchTerms: ["completed duty", "leave duty", "instance", "dungeon", "raid", "delay", "1-10 sec", "duty menu", "controller", "no U"],
            drawOptions: DrawAutoLeaveDutyOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-sort-items",
            UiLocale.Native("SA318747875972AD1"),
            () => configuration.AutoSortItemsEnabled,
            plugin.AutoSortItems.SetEnabled,
            applied => configuration.AutoSortItemsEnabled = applied,
            UiLocale.Native("S175E4A4308EA83D1"),
            UiLocale.Native("S0B8F5356AEB30308"),
            () => plugin.AutoSortItems.LocalizedStatusText,
            searchTerms: ["inventory", "armoury", "sort", "itemsort", "HQ", "tabs", "category", "item level"],
            drawOptions: plugin.AutoSortItems.DrawOptions);
        AddSavedFeatureEntry(
            ToonModsSection.UiMods,
            "inspect-outfit-try-on",
            UiLocale.Native("S48A68C87A51F1E37"),
            () => configuration.InspectOutfitTryOnEnabled,
            plugin.InspectOutfitTryOn.SetEnabled,
            applied => configuration.InspectOutfitTryOnEnabled = applied,
            UiLocale.Native("S7DFC9061C2CA8AFC"),
            UiLocale.Native("S4DA0F109FC7BCA79"),
            () => plugin.InspectOutfitTryOn.LocalizedStatusText,
            searchTerms: ["inspect", "outfit", "try on", "fitting room", "glamour", "dyes", "history", "saved", "/xa outfits"],
            drawOptions: plugin.InspectOutfitTryOn.DrawOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "view-single-item",
            UiLocale.Native("S26AFC61257A5D865"),
            () => configuration.ViewSingleItemEnabled,
            plugin.ViewSingleItem.SetEnabled,
            applied => configuration.ViewSingleItemEnabled = applied,
            UiLocale.Native("S7BD8FE53A561A448"),
            UiLocale.Native("S98C19A79C453233B"),
            () => plugin.ViewSingleItem.LocalizedStatusText,
            searchTerms: ["inspect", "view item", "single item", "weapon", "equipment", "hair", "screenshot", "glamour", "dyes", "history", "facewear", "shield", "shop", "inventory", "chat", "minion", "mount", "framer", "adventurer plate", "find item", "search", "animation", "play", "pause", "timeline", "glow"],
            drawOptions: () =>
            {
                using (ImRaii.Disabled(!configuration.ViewSingleItemEnabled))
                    if (UiNative.Button(UiLocale.Native("S5453398889A6CED5"))) plugin.ViewSingleItem.ShowWindow();
            });
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-restore-furniture",
            UiLocale.Native("S62F6C455F98922D0"),
            () => configuration.AutoRestoreFurnitureEnabled,
            plugin.AutoRestoreFurniture.SetEnabled,
            applied => configuration.AutoRestoreFurnitureEnabled = applied,
            UiLocale.Native("SB54B968C50EB4C2B"),
            UiLocale.Native("S9E379327D6141648"),
            () => plugin.AutoRestoreFurniture.LocalizedStatusText,
            searchTerms: ["housing", "furniture", "storeroom", "inventory", "restore", "batch"],
            drawOptions: plugin.AutoRestoreFurniture.DrawOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "auto-merge",
            UiLocale.Native("S90D9264B8B7B93D3"),
            () => configuration.AutoMergeEnabled,
            plugin.AutoMerge.SetEnabled,
            applied => configuration.AutoMergeEnabled = applied,
            UiLocale.Native("S9089B455985130D2"),
            UiLocale.Native("S92477A412A357937"),
            () => plugin.AutoMerge.LocalizedStatusText,
            searchTerms: ["inventory", "stacks", "merge", "bags"]);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "quick-return",
            UiLocale.Native("S6CF86715A47CB6D6"),
            () => configuration.QuickReturnEnabled,
            plugin.QuickReturn.SetEnabled,
            applied => configuration.QuickReturnEnabled = applied,
            UiLocale.Native("SAE31562FAC684288"),
            UiLocale.Native("S3B29864CA94BCC07"),
            () => plugin.QuickReturn.LocalizedStatusText,
            searchTerms: ["Return", "instant return", "instance return", "cast", "cooldown", "instant", "PvP", "party", "disband", "leave party", "manual confirm"],
            requireCtrlShiftToEnable: true);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "custom-sight-distance",
            UiLocale.Native("SA911299386D7574E"),
            () => configuration.CustomSightDistanceEnabled,
            plugin.SightDistance.SetEnabled,
            applied => configuration.CustomSightDistanceEnabled = applied,
            UiLocale.Native("S38BBFD2B5FEEFE37"),
            UiLocale.Native("SF13EF6E064DBC98E"),
            () => plugin.SightDistance.LocalizedStatusText,
            searchTerms: ["Max distance", "Min distance", "Max rotation", "Min rotation", "Max FoV", "Min FoV", "Current FoV", "Ignore camera collision"],
            drawOptions: DrawSightDistanceOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "doze-sit-anywhere",
            UiLocale.Native("S024F9EF0DF86E54F"),
            () => configuration.DozeSitAnywhereEnabled,
            plugin.DozeSitAnywhere.SetEnabled,
            applied => configuration.DozeSitAnywhereEnabled = applied,
            UiLocale.Native("SFF9867F79EAED273"),
            UiLocale.Native("S576F21A11E5A06C9"),
            () => plugin.DozeSitAnywhere.LocalizedStatusText,
            searchTerms: ["/xa sit", "/xa doze", "sit now", "doze now", "titlebar favourite", "emote", "bed", "chair"],
            drawOptions: DrawDozeSitAnywhereTools);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "infinite-sprint",
            UiLocale.Native("S3187BAA85C51F55B"),
            () => configuration.InfiniteSprintEnabled,
            plugin.PlayerMods.SetInfiniteSprintEnabled,
            applied => configuration.InfiniteSprintEnabled = applied,
            UiLocale.Native("S520A72D15F4C1786"),
            UiLocale.Native("S13EB614293A25632"),
            () => plugin.PlayerMods.LocalizedInfiniteSprintStatusText,
            searchTerms: ["Sprint delay", "movement-start delay", "recast delay"],
            drawOptions: DrawInfiniteSprintOptions);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "instant-logout",
            UiLocale.Native("SB358629B2E68D434"),
            () => configuration.InstantLogoutEnabled,
            plugin.InstantLogout.SetEnabled,
            applied => configuration.InstantLogoutEnabled = applied,
            UiLocale.Native("SE6A63112A9C615D2"),
            UiLocale.Native("S37E52B4F5CE84EDC"),
            () => plugin.InstantLogout.LocalizedStatusText,
            searchTerms: ["logout", "/xa logout", "/xa killgame", "Log out now", "Kill game now"],
            requireCtrlShiftToEnable: true,
            drawOptions: DrawInstantLogoutTool);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "item-commands",
            UiLocale.Native("S943452438F6F70FE"),
            () => configuration.ItemCommandsEnabled,
            plugin.ItemCommands.SetEnabled,
            applied => configuration.ItemCommandsEnabled = applied,
            UiLocale.Native("SC30BF5DB81148BEB"),
            UiLocale.Native("SAF482BCB201972E0"),
            () => plugin.ItemCommands.LocalizedStatusText,
            searchTerms: ["/xa equip", "equip item id", "armory"]);
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "nearby-players",
            UiLocale.Native("S8B51AF9B04952D38"),
            () => configuration.NearbyPlayers.Enabled,
            plugin.NearbyPlayers.SetEnabled,
            applied => configuration.NearbyPlayers.Enabled = applied,
            UiLocale.Native("S13EA6A958ACFC301"),
            UiLocale.Native("SF0B8B71659A32D1E"),
            () => plugin.NearbyPlayers.LocalizedStatusText,
            searchTerms: ["nearby", "radar", "/xa nearby", "distance", "columns", "zone"],
            drawOptions: () =>
            {
                if (UiNative.Button(UiLocale.Native("S2FF803176B857596"))) plugin.NearbyPlayers.WindowOpen = true;
                var settings = configuration.NearbyPlayers;
                var friendsFirst = settings.ShowFriendsOnTop;
                if (UiNative.Checkbox(UiLocale.Native("S7899265D8FF860AF"), ref friendsFirst)) plugin.NearbyPlayers.ChangeSettings(s => s.ShowFriendsOnTop = friendsFirst);
                var peepIntegration = settings.UseXAPeepIntegration;
                if (UiNative.Checkbox(UiLocale.Native("S2B1FB3222EDF50BD"), ref peepIntegration)) plugin.NearbyPlayers.ChangeSettings(s => s.UseXAPeepIntegration = peepIntegration);
                ImGui.TextWrapped(UiLocale.Get("S458993E89A734314"));
                var friendColor = settings.FriendColor;
                if (UiNative.ColorEdit4(UiLocale.Native("S4CC2E85A799C0C3A"), ref friendColor)) plugin.NearbyPlayers.ChangeSettings(s => s.FriendColor = friendColor);
                var targetingMeColor = settings.TargetingMeColor;
                if (UiNative.ColorEdit4(UiLocale.Native("S26A07FCFC6ECED64"), ref targetingMeColor)) plugin.NearbyPlayers.ChangeSettings(s => s.TargetingMeColor = targetingMeColor);
                var dtr = settings.ShowDtr;
                if (UiNative.Checkbox(UiLocale.Native("S8C983951CB2404CC"), ref dtr)) plugin.NearbyPlayers.ChangeSettings(s => s.ShowDtr = dtr);
                var lines = settings.ShowNearbyLines;
                if (UiNative.Checkbox(UiLocale.Native("S52FFAD197A092905"), ref lines)) plugin.NearbyPlayers.ChangeSettings(s => s.ShowNearbyLines = lines);
                var scale = settings.Scale;
                if (UiNative.DragFloat(UiLocale.Native("SA9621E91FDFC32F4"), ref scale, 0.05f, 0.1f, 5f)) plugin.NearbyPlayers.ChangeSettings(s => s.Scale = scale);
            });
        AddSavedFeatureEntry(
            ToonModsSection.PlayerMods,
            "xa-peep",
            UiLocale.Native("S1079C85AFAB8AEDC"),
            () => configuration.XAPeepEnabled,
            plugin.SetXAPeepEnabled,
            applied => configuration.XAPeepEnabled = applied,
            UiLocale.Native("S51FE4928A4A15C76"),
            UiLocale.Native("SA7B02344D014943A"),
            () => plugin.XAPeep.LocalizedStatusText,
            searchTerms: ["Show card when targeted", "Show Targeter's Line", "Show targeter's dot", "targeter line color", "targeter dot color", "targeter dot size", "Show targeters card", "Show center-screen notification", "Print chat notification", "is targeting you", "Log party members", "Log alliance members", "Log players in combat", "Log while in duty", "Auto open XA Peep on plugin load", "lock", "resize", "/xa peep", "history", "PvP", "sound", "window"],
            drawOptions: DrawXAPeepOptions);
    void DrawInstantTeleportOptions()
    {
        if (plugin.Configuration.InstantTeleportEnabled && !plugin.InstantTeleport.IsEnabled && UiNative.Button(UiLocale.Native("S319CF6241D0C2371")))
            plugin.InstantTeleport.SetEnabled(true);
        var soloOnly = plugin.Configuration.InstantTeleportSoloOnly;
        if (UiNative.Checkbox(UiLocale.Native("SE3DC9C47565753F2"), ref soloOnly))
        {
            plugin.Configuration.InstantTeleportSoloOnly = soloOnly;
            plugin.Configuration.Save();
        }
        var safeDistance = TeleportProximity.ClampDistance(plugin.Configuration.InstantTeleportSafeDistanceYalms);
        ImGui.BeginDisabled(!soloOnly);
        if (UiNative.SliderFloat(UiLocale.Native("S4901CA2434A98A04"), ref safeDistance, 1f, 300f, "%.0f"))
        {
            plugin.Configuration.InstantTeleportSafeDistanceYalms = TeleportProximity.ClampDistance(safeDistance);
            plugin.Configuration.Save();
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(UiLocale.Get("S60CA20C08CDB8CCE"));
        ImGui.TextWrapped(plugin.InstantTeleport.ProximityTextDisplay);
        ImGui.TextWrapped(plugin.InstantTeleport.ReadinessTextDisplay);
        ImGui.TextWrapped(UiLocale.Get("S16D24C98E54BC83A"));
        ImGui.TextWrapped(UiLocale.Get("SB2EA6AB13C00529B"));
        ImGui.TextWrapped(UiLocale.Get("S24DF158824CE8521"));
        if (plugin.InstantTeleport.IsTeleporting && UiNative.Button(UiLocale.Native("SD1B2318BC48A63B1")))
            plugin.InstantTeleport.RequestCancel();
        if (UiNative.TreeNode((UiLocale.Native("S268F14BBFE119C1E") + "##instantTeleportDiagnostics")))
        {
            ImGui.TextWrapped(plugin.InstantTeleport.DiagnosticsTextDisplay);
            ImGui.TreePop();
        }
    }

        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "instant-teleport",
            UiLocale.Native("S645072C9892CD773"),
            () => configuration.InstantTeleportEnabled,
            plugin.InstantTeleport.SetEnabled,
            applied => configuration.InstantTeleportEnabled = applied,
            UiLocale.Native("S3EA04B9D77CEE6F6"),
            UiLocale.Native("S7740D5093A8210E0"),
            () => plugin.InstantTeleport.StatusTextDisplay,
            searchTerms: ["instant", "teleport"],
            drawOptions: DrawInstantTeleportOptions,
            requireCtrlShiftToEnable: true);
        AddSavedFeatureEntry(
            ToonModsSection.IllegalMods,
            "moveable-after-death",
            UiLocale.Native("SCD920ED5B9679DFF"),
            () => configuration.MoveableAfterDeathEnabled,
            plugin.PlayerMods.SetMoveableAfterDeathEnabled,
            applied => configuration.MoveableAfterDeathEnabled = applied,
            UiLocale.Native("SD3C8970B8A69F85D"),
            UiLocale.Native("S9D179CE48D3A6D0C"),
            () => plugin.PlayerMods.LocalizedMoveableAfterDeathStatusText,
            warningText: UiLocale.Native("S4BEED1BF0FDBADA1"),
            requireCtrlShiftToEnable: true);

        AddSavedFeatureEntry(
            ToonModsSection.PluginMods,
            "anonymize-character-lists",
            UiLocale.Native("S94ACE94457981245"),
            () => configuration.GlobalCharacterListAnonymizeEnabled,
            enabled =>
            {
                configuration.GlobalCharacterListAnonymizeEnabled = enabled;
                return enabled;
            },
            applied => configuration.GlobalCharacterListAnonymizeEnabled = applied,
            UiLocale.Native("S4653BC2E487F1F8B"),
            UiLocale.Native("S2C169F6BF8E223C0"),
            () => configuration.GlobalCharacterListAnonymizeEnabled
                ? global::XASlave.Localization.UiLocale.Get("S75D0374F3B1E4F74")
                : global::XASlave.Localization.UiLocale.Get("S75081B593D15CF6E"),
            searchTerms: ["screenshot", "character list", "character lists", "anonymize", "privacy", "titlebar favourite"]);
        AddSavedFeatureEntry(
            ToonModsSection.PluginMods,
            "teleport-helper",
            UiLocale.Native("SECA9B296F48C520C"),
            () => configuration.TeleportHelperEnabled,
            value =>
            {
                ApplyTeleportHelperConfiguration();
                return plugin.TeleportHelper.SetEnabled(value);
            },
            applied => configuration.TeleportHelperEnabled = applied,
            UiLocale.Native("SA01510FD41113854"),
            UiLocale.Native("SC1232F1D0588EC6A"),
            () => plugin.TeleportHelper.LocalizedStatusText,
            warningText: configuration.TeleportHelperSelectYes
                ? UiLocale.Native("S2CDC29C4D1DE8FCD")
                : UiLocale.Native("SE7C47D35DE3EAD41"),
            searchTerms: ["SelectYesNo", "SelectYesno", "aetheryte ticket", "teleport", "ticket", "Yes", "No"],
            drawOptions: DrawTeleportHelperOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PluginMods,
            "disable-this",
            UiLocale.Native("S0678DE437AABA714"),
            () => configuration.DisableThisEnabled,
            plugin.PluginGuard.SetEnabled,
            applied => configuration.DisableThisEnabled = applied,
            UiLocale.Native("S3C5D2FC93DFB2B35"),
            UiLocale.Native("S3D245F66E02DD87B"),
            () => plugin.PluginGuard.LocalizedStatusText,
            searchTerms: ["Aethertek", "Reborn", "XIVCombo", "XIV Combo", "disable", "unload", "IPC", "plugin", "guard", "command", "protect", "list"],
            drawOptions: DrawPluginGuardOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PluginMods,
            "force-peepingtom",
            UiLocale.Native("SEFE91AA503DA1BAF"),
            () => configuration.ForcePeepingTomEnabled,
            plugin.PeepingTomIntegration.SetForceEnabled,
            applied => configuration.ForcePeepingTomEnabled = applied,
            UiLocale.Native("S13B9A1ADA8B1560C"),
            UiLocale.Native("SDB8BBC63898726C0"),
            () => plugin.PeepingTomIntegration.LocalizedStatusText,
            searchTerms: ["PvP", "Peeping Tom"]);
        void ApplyARealmRecordedAllZonesConfiguration()
        {
            plugin.ARealmRecordedIntegration.ApplyConfiguration(
                configuration.ARealmRecordedAllZonesAllContentTypes,
                configuration.ARealmRecordedAllZonesSelectedContentTypes);
        }

        void ApplyDalamudLogDisablerConfiguration()
        {
            plugin.DalamudLogDisabler.ApplyConfiguration(
                configuration.DalamudLogDisablerBlockedPlugins,
                configuration.DalamudLogDisablerMinimumKeptLevel);
        }

        bool IsPluginLogBlocked(string internalName, string name)
        {
            return configuration.DalamudLogDisablerBlockedPlugins.Any(entry =>
                entry.Equals(internalName, StringComparison.OrdinalIgnoreCase)
                || entry.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        void SetPluginLogBlocked(string internalName, string name, bool blocked)
        {
            if (blocked)
            {
                var key = string.IsNullOrWhiteSpace(internalName) ? name : internalName;
                if (!string.IsNullOrWhiteSpace(key) && !configuration.DalamudLogDisablerBlockedPlugins.Contains(key, StringComparer.OrdinalIgnoreCase))
                    configuration.DalamudLogDisablerBlockedPlugins.Add(key);
            }
            else
            {
                configuration.DalamudLogDisablerBlockedPlugins.RemoveAll(entry =>
                    entry.Equals(internalName, StringComparison.OrdinalIgnoreCase)
                    || entry.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            ApplyDalamudLogDisablerConfiguration();
            SaveConfiguration();
        }

        void DrawDalamudLogCleaner()
        {
            ImGui.TextUnformatted(UiLocale.Get("SDCB2003435386091"));
            using (ImRaii.TextWrapPos(0f))
                ImGui.TextDisabled(UiLocale.Get("S3477BD36A75EC305"));
            var automatic = configuration.DalamudLogCleanerAutomaticEnabled;
            if (UiNative.Checkbox(UiLocale.Native("S8EC6DD17F9090ADE"), ref automatic))
            {
                configuration.DalamudLogCleanerAutomaticEnabled = plugin.SetDalamudLogCleanerAutomatic(automatic);
                SaveConfiguration();
            }
            var interval = configuration.DalamudLogCleanerCheckIntervalMinutes;
            ImGui.SetNextItemWidth(Scale(120f));
            if (UiNative.InputInt(UiLocale.Native("S7AA7A676F53621D4"), ref interval))
            {
                configuration.DalamudLogCleanerCheckIntervalMinutes = DalamudLogCleanerService.NormalizeCheckInterval(interval);
                plugin.SetDalamudLogCleanerAutomatic(configuration.DalamudLogCleanerAutomaticEnabled);
                SaveConfiguration();
            }
            using (ImRaii.TextWrapPos(0f))
            {
                ImGui.TextDisabled(UiLocale.Get("S225F75C48186A3DA"));
                ImGui.TextDisabled(plugin.DalamudLogCleaner.LocalizedAutomaticStatusText);
            }
            if (UiNative.Button(UiLocale.Native("S2862E116C015AD59")))
            {
                var success = plugin.DalamudLogCleaner.TryCheckSizeMessage(out var message);
                SetToonModsStatus(message, !success);
            }
            using (ImRaii.TextWrapPos(0f))
                ImGui.TextUnformatted(plugin.DalamudLogCleaner.LocalizedSizeStatusText);
            using (ImRaii.Disabled(plugin.DalamudLogCleaner.IsCleaning))
            {
                if (UiNative.Button(UiLocale.Native("S56957F5EF36A5B89")))
                {
                    var success = plugin.DalamudLogCleaner.TryCleanMessage(string.Empty, out var message);
                    SetToonModsStatus(message, !success);
                }
            }
            using (ImRaii.TextWrapPos(0f))
                ImGui.TextDisabled(plugin.DalamudLogCleaner.LocalizedStatusText);
            ImGui.TextDisabled(UiLocale.Get("SB893943A80B8632E"));
            ImGui.Spacing();
        }

        void DrawDalamudLogDisablerOptions()
        {
            ImGui.TextDisabled(UiLocale.Get("SA3A584960ED1358D"));

            var levelOptions = plugin.DalamudLogDisabler.GetLevelOptionMessages();
            if (levelOptions.Count > 0)
            {
                var currentLevel = configuration.DalamudLogDisablerMinimumKeptLevel;
                var currentOption = levelOptions.FirstOrDefault(option => option.Value == currentLevel);
                if (currentOption.Label == null)
                    currentOption = levelOptions[levelOptions.Count - 1];

                ImGui.SetNextItemWidth(Scale(320f));
                using (var combo = global::XASlave.Localization.UiNative.ComboScope(UiLocale.Native("SFE8D08C2AC1787BB"), currentOption.Label.Display))
                {
                    if (combo)
                    {
                        foreach (var option in levelOptions)
                        {
                            var selected = option.Value == currentLevel;
                            if (UiNative.Selectable($"{option.Label}##DalamudLogDisablerLevel{option.Value}", selected, display: option.Label.Display)
                                && option.Value != currentLevel)
                            {
                                configuration.DalamudLogDisablerMinimumKeptLevel = option.Value;
                                ApplyDalamudLogDisablerConfiguration();
                                SaveConfiguration();
                            }

                            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(option.Blocked.Original))
                                ImGui.SetTooltip(UiLocale.FormatKey("S9B60FE30A9C7C004", option.Blocked.Display));

                            if (selected)
                                ImGui.SetItemDefaultFocus();
                        }
                    }
                }

                ImGui.SameLine(0f, 6f);
                DrawHelpMarker(UiLocale.Native("S924D42615FB1872D"));

                if (!string.IsNullOrEmpty(currentOption.Blocked.Original))
                    ImGui.TextDisabled(UiLocale.FormatKey("S32BF861F963C72E3", currentOption.Blocked.Display));
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(Scale(220f));
            UiNative.InputTextWithHint("##DalamudLogDisablerAdd", UiLocale.Get("S5B46ED9D1A47180B"), ref dalamudLogDisablerAddInput, 128);
            ImGui.SameLine();
            if (UiNative.Button(UiLocale.Native("S2C42B9A3CA347E52")))
            {
                var trimmed = dalamudLogDisablerAddInput.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && !configuration.DalamudLogDisablerBlockedPlugins.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    configuration.DalamudLogDisablerBlockedPlugins.Add(trimmed);
                    ApplyDalamudLogDisablerConfiguration();
                    SaveConfiguration();
                }

                dalamudLogDisablerAddInput = string.Empty;
            }

            ImGui.Spacing();
            if (!UiNative.CollapsingHeader(UiLocale.Native("S0C503C0FEA903246")))
                return;

            var loadedPlugins = plugin.DalamudLogDisabler.GetLoadedPlugins();
            var loadedKeys = new HashSet<string>(
                loadedPlugins.SelectMany(entry => new[] { entry.InternalName, entry.Name }),
                StringComparer.OrdinalIgnoreCase);

            var blockedNotLoaded = configuration.DalamudLogDisablerBlockedPlugins
                .Where(entry => !loadedKeys.Contains(entry))
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blockedNotLoaded.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled(UiLocale.Get("S3759600CF3F8B33A"));
                foreach (var entry in blockedNotLoaded)
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.5f, 0.5f, 1.0f)))
                    {
                        if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S8255C172AFB632C6"), entry)))
                        {
                            configuration.DalamudLogDisablerBlockedPlugins.RemoveAll(e => e.Equals(entry, StringComparison.OrdinalIgnoreCase));
                            ApplyDalamudLogDisablerConfiguration();
                            SaveConfiguration();
                            break;
                        }
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(entry);
                }
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(Scale(220f));
            UiNative.InputTextWithHint("##DalamudLogDisablerFilter", UiLocale.Get("SD7E0D99642DBA4D3"), ref dalamudLogDisablerFilter, 128);

            if (loadedPlugins.Count == 0)
            {
                ImGui.TextDisabled(UiLocale.Get("SAC3DF46CDBB2156C"));
                return;
            }

            var visiblePlugins = loadedPlugins
                .Where(entry => string.IsNullOrWhiteSpace(dalamudLogDisablerFilter)
                    || entry.Name.Contains(dalamudLogDisablerFilter, StringComparison.OrdinalIgnoreCase)
                    || entry.InternalName.Contains(dalamudLogDisablerFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var filterActive = !string.IsNullOrWhiteSpace(dalamudLogDisablerFilter);
            if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("S228DD3C29D6B09F6"), (filterActive ? " (filtered)" : string.Empty)), display: filterActive ? UiLocale.Get("SC1B3C8C8D6AB266F") : UiLocale.Get("SB9E81709A10D66C8")) && visiblePlugins.Count > 0)
            {
                var changed = false;
                foreach (var (internalName, name) in visiblePlugins)
                {
                    if (IsPluginLogBlocked(internalName, name))
                        continue;

                    var key = string.IsNullOrWhiteSpace(internalName) ? name : internalName;
                    if (!string.IsNullOrWhiteSpace(key) && !configuration.DalamudLogDisablerBlockedPlugins.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        configuration.DalamudLogDisablerBlockedPlugins.Add(key);
                        changed = true;
                    }
                }

                if (changed)
                {
                    ApplyDalamudLogDisablerConfiguration();
                    SaveConfiguration();
                }
            }

            ImGui.SameLine();
            if (UiNative.SmallButton(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("SA043F52D46E9C579"), (filterActive ? " (filtered)" : string.Empty)), display: filterActive ? UiLocale.Get("S0163664DDED49443") : UiLocale.Get("S5383C8C3A606E9D0")) && visiblePlugins.Count > 0)
            {
                var changed = false;
                foreach (var (internalName, name) in visiblePlugins)
                {
                    changed |= configuration.DalamudLogDisablerBlockedPlugins.RemoveAll(entry =>
                        entry.Equals(internalName, StringComparison.OrdinalIgnoreCase)
                        || entry.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
                }

                if (changed)
                {
                    ApplyDalamudLogDisablerConfiguration();
                    SaveConfiguration();
                }
            }

            ImGui.Indent();
            foreach (var (internalName, name) in visiblePlugins)
            {
                var blocked = IsPluginLogBlocked(internalName, name);
                var label = string.Equals(internalName, name, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(internalName)
                    ? name
                    : $"{name} ({internalName})";
                if (ImGui.Checkbox($"{label}##DalamudLogDisabler{internalName}", ref blocked))
                    SetPluginLogBlocked(internalName, name, blocked);
            }

            ImGui.Unindent();
        }

        void DrawARealmRecordedStateOptions()
        {
            if (configuration.ARealmRecordedAllZonesEnabled)
            {
                var allContentTypes = configuration.ARealmRecordedAllZonesAllContentTypes;
                if (UiNative.Checkbox(UiLocale.Native("SF71ADEBBDA9FE765"), ref allContentTypes))
                {
                    configuration.ARealmRecordedAllZonesAllContentTypes = allContentTypes;
                    if (!allContentTypes && configuration.ARealmRecordedAllZonesSelectedContentTypes.Count == 0)
                    {
                        configuration.ARealmRecordedAllZonesSelectedContentTypes = plugin.ARealmRecordedIntegration
                            .GetSelectableContentTypes()
                            .Where(entry => !entry.IsStock)
                            .Select(entry => entry.Id)
                            .ToList();
                    }

                    ApplyARealmRecordedAllZonesConfiguration();
                    SaveConfiguration();
                }

                if (!allContentTypes)
                {
                    ImGui.TextDisabled(UiLocale.Get("S19DC0F990A1B51CC"));
                    ImGui.Indent();
                    foreach (var entry in plugin.ARealmRecordedIntegration.GetSelectableContentTypes())
                    {
                        if (entry.IsStock)
                        {
                            var alwaysOn = true;
                            using (ImRaii.Disabled())
                                UiNative.Checkbox(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiLocale.Native("SC0036CAF11353AC7"), entry.Name, entry.Id), ref alwaysOn, display: UiLocale.FormatKey("S860E84EB02C0B8F2", entry.LocalizedName));
                            continue;
                        }

                        var selected = configuration.ARealmRecordedAllZonesSelectedContentTypes.Contains(entry.Id);
                        if (!UiNative.Checkbox($"{entry.Name}##ARealmRecordedContentType{entry.Id}", ref selected, display: entry.LocalizedName))
                            continue;

                        if (selected)
                        {
                            if (!configuration.ARealmRecordedAllZonesSelectedContentTypes.Contains(entry.Id))
                                configuration.ARealmRecordedAllZonesSelectedContentTypes.Add(entry.Id);
                        }
                        else
                        {
                            configuration.ARealmRecordedAllZonesSelectedContentTypes.RemoveAll(id => id == entry.Id);
                        }

                        ApplyARealmRecordedAllZonesConfiguration();
                        SaveConfiguration();
                    }

                    ImGui.Unindent();
                }

                ImGui.Spacing();
            }

            var state = plugin.ARealmRecordedIntegration.GetLiveState();
            if (!state.PluginLoaded)
            {
                ImGui.TextDisabled(UiLocale.FormatKey("SA907266C6159AD9B", state.LocalizedDescription));
                return;
            }

            if (!state.StateAvailable)
            {
                ImGui.TextDisabled(UiLocale.FormatKey("SA907266C6159AD9B", state.LocalizedDescription));
                return;
            }

            var color = state.IsRecording
                ? new Vector4(1.0f, 0.35f, 0.35f, 1.0f)
                : state.IsArmed
                    ? new Vector4(1.0f, 0.7f, 0.25f, 1.0f)
                    : state.InPlayback
                        ? new Vector4(0.6f, 0.9f, 1.0f, 1.0f)
                        : new Vector4(0.55f, 0.9f, 0.55f, 1.0f);

            ImGui.TextUnformatted(UiLocale.Get("S041567E3D8BA329A"));
            ImGui.SameLine();
            ImGui.TextColored(color, state.LocalizedDescription);
            ImGui.TextDisabled(UiLocale.FormatKey("SF30C5E73ECF74052", state.StatusByte, state.LocalizedStatusBitsText, state.PlaybackControls));
            ImGui.TextDisabled(UiLocale.Get("SDE35841DC89801A3"));

            var canForceStart = state.StateAvailable && !state.IsRecording && !state.InPlayback;
            using (ImRaii.Disabled(!canForceStart))
            {
                if (UiNative.Button(UiLocale.Native("S8A2FB5087A52E623")))
                    plugin.ARealmRecordedIntegration.RequestForceStartRecording();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                using (ImRaii.Tooltip())
                using (ImRaii.TextWrapPos(Scale(360f)))
                    ImGui.TextUnformatted(canForceStart
                        ? UiLocale.Get("SAC1BED8770318DD2")
                        : UiLocale.Get("SF97D000678539103"));
            }

            ImGui.SameLine();

            var canForceStop = state.IsRecording || state.IsArmed;
            using (ImRaii.Disabled(!canForceStop))
            {
                if (UiNative.Button(UiLocale.Native("S09B932A1EB1AE854")))
                    plugin.ARealmRecordedIntegration.RequestForceStopRecording();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                using (ImRaii.Tooltip())
                using (ImRaii.TextWrapPos(Scale(360f)))
                    ImGui.TextUnformatted(canForceStop
                        ? UiLocale.Get("SA8F9CAA99A160865")
                        : UiLocale.Get("S1B9BABFFBC1562F2"));
            }

            var lastForceAction = plugin.ARealmRecordedIntegration.LocalizedLastForceActionMessage;
            if (!string.IsNullOrEmpty(lastForceAction))
                ImGui.TextDisabled(UiLocale.FormatKey("SF5A9578797C77B7D", lastForceAction));
        }

        toonModDefinitions.Add(("dalamud-log-cleaner",
            () => configuration.DalamudLogCleanerAutomaticEnabled,
            plugin.SetDalamudLogCleanerAutomatic,
            applied => configuration.DalamudLogCleanerAutomaticEnabled = applied));
        featureEntries.Add(new XAModCatalogueEntry(
            ToonModsSection.GameMods,
            global::XASlave.Localization.UiLocale.Native("SDCB2003435386091"),
            BuildToonModsSearchHaystack("Dalamud Log Cleaner", "Archive and clear the active log file", "Manual action: /xa clearlog. Optional automatic cleanup at 90 MiB, checked every 15 minutes by default.", ["logging", "dalamud.log", "cache", "reset", "clearlog", "automatic", "interval"]),
            () => configuration.DalamudLogCleanerAutomaticEnabled,
            DrawDalamudLogCleaner));
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "clean-chat",
            UiLocale.Native("S619A69E12D201498"),
            () => configuration.CleanChatEnabled,
            plugin.SetCleanChatEnabled,
            applied => configuration.CleanChatEnabled = applied,
            UiLocale.Native("S1FBFE258ACEAC17A"),
            UiLocale.Native("S8A5CBE4859A6F1DD"),
            () => plugin.CleanChat.LocalizedStatusText,
            searchTerms: ["chat", "spam", "RMT", "regex", "welcome", "event", "phishing", "filter"],
            drawOptions: DrawCleanChatOptions);
        AddSavedFeatureEntry(
            ToonModsSection.GameMods,
            "dalamud-log-disabler",
            UiLocale.Native("S6249A72639DF6380"),
            () => configuration.DalamudLogDisablerEnabled,
            value =>
            {
                ApplyDalamudLogDisablerConfiguration();
                return plugin.DalamudLogDisabler.SetEnabled(value);
            },
            applied => configuration.DalamudLogDisablerEnabled = applied,
            UiLocale.Native("SB7DE0FE8813C10ED"),
            UiLocale.Native("SC5B17E5BF59272EE"),
            () => plugin.DalamudLogDisabler.LocalizedStatusText,
            searchTerms: ["Dalamud", "log", "logging", "xllog", "logger", "serilog", "mute", "silence", "spam", "verbose", "debug", "warning", "error", "fatal", "level", "disable logs", "blacklist"],
            drawOptions: DrawDalamudLogDisablerOptions);
        AddSavedFeatureEntry(
            ToonModsSection.PluginMods,
            "arealmrecorded-all-zones",
            UiLocale.Native("S7BA474F5BFACF460"),
            () => configuration.ARealmRecordedAllZonesEnabled,
            value =>
            {
                ApplyARealmRecordedAllZonesConfiguration();
                return plugin.ARealmRecordedIntegration.SetForceEnabled(value);
            },
            applied => configuration.ARealmRecordedAllZonesEnabled = applied,
            UiLocale.Native("SDA1D9CE6F8F8FAC6"),
            UiLocale.Native("SC3A4EA94C744107F"),
            () => plugin.ARealmRecordedIntegration.LocalizedStatusText,
            warningText: UiLocale.Native("SC91A50A5A6493F73"),
            searchTerms: ["ARealmRecorded", "A Realm Recorded", "duty recorder", "replay", "recording", "Eureka", "Carnivale", "Bozja", "whitelist", "bounce", "zone-in", "plugin state"],
            drawOptions: DrawARealmRecordedStateOptions);
        AddSavedFeatureEntry(
            ToonModsSection.EurekaMods,
            "eureka-instance-id",
            UiLocale.Native("S0DC49FB52EF24934"),
            () => configuration.EurekaInstanceIdEnabled,
            plugin.EurekaInstanceId.SetEnabled,
            applied => configuration.EurekaInstanceIdEnabled = applied,
            UiLocale.Native("S44900F691A71BED0"),
            UiLocale.Native("SF1BCB60873D16ED4"),
            () => plugin.EurekaInstanceId.LocalizedStatusText,
            searchTerms: ["Eureka", "instance", "DTR", "server bar", "Field Operations", "Eureka Instance Hunter", "Rodney", "Anemos", "Pagos", "Pyros", "Hydatos", "farming"],
            drawOptions: DrawEurekaInstanceIdOptions);

        xaModCatalogueBySection = featureEntries
            .GroupBy(entry => entry.Section)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToArray());
        xaModCatalogueBuilt = true;
        }

        xaModCurrentStateFrame.Clear();
        xaModVisibilityFrame.Clear();
        var enabledXAModsCount = 0;
        foreach (var entry in featureEntries)
        {
            if (GetToonModEntryCurrent(entry))
                enabledXAModsCount++;
        }
        ImGui.TextColored(
            new Vector4(0.4f, 0.8f, 1.0f, 1.0f),
            UiLocale.FormatKey("S15C2E2C35318F01D", toonModDefinitions.Count, enabledXAModsCount));
        ImGui.TextDisabled(UiLocale.Get("SBE0A90A2589C2530"));
        ImGui.Spacing();

        var showOnlyEnabledLabel = UiLocale.Native("S8799A44C1FCB6F19");
        var showAllPluginsLabel = UiLocale.Native("S5EB3232330EEEF71");
        var showOnlyEnabledButtonWidth = MathF.Max(
            ImGui.CalcTextSize(UiLocale.T(showOnlyEnabledLabel)).X,
            ImGui.CalcTextSize(UiLocale.T(showAllPluginsLabel)).X)
            + (ImGui.GetStyle().FramePadding.X * 2f);

        if (UiNative.Button(
                $"{(toonModsShowOnlyEnabled ? showAllPluginsLabel : showOnlyEnabledLabel)}##ToonModsShowOnlyEnabled",
                new Vector2(showOnlyEnabledButtonWidth, 0f)))
        {
            toonModsShowOnlyEnabled = !toonModsShowOnlyEnabled;
        }
        ImGui.SameLine();
        var disableAllModsModifierHeld = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!disableAllModsModifierHeld))
        {
            if (UiNative.Button(UiLocale.Native("S249BF31B58EA88A8")))
                DisableAllMods();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(UiLocale.Get("SF8CED8308AFBD83B"));
        ImGui.SameLine();
        if (UiNative.Button(UiLocale.Native("S002C31CACA8F478D")))
            ImGui.OpenPopup("ToonModsSaveListPopup");
        ImGui.SameLine();
        if (UiNative.Button(UiLocale.Native("S141C1792CCB32E5F")))
            ImGui.OpenPopup("ToonModsLoadListPopup");
        ImGui.SameLine();
        if (UiNative.Button(UiLocale.Native("S010EBDF4E4119E83")))
            ExportCurrentToonModsList();
        ImGui.SameLine();
        if (UiNative.Button(UiLocale.Native("S40DC5F9183708FC2")))
            ImportCurrentToonModsList();

        ImGui.SetNextItemWidth(Math.Max(Scale(220f), ImGui.GetContentRegionAvail().X));
        UiNative.InputTextWithHint("##ToonModsSearch", UiLocale.Get("S3C1098DF85F9B127"), ref toonModsSearchText, 256);

        DrawToonModsSaveListPopup();
        DrawToonModsLoadListPopup();

        if (!string.IsNullOrEmpty(toonModsStatus) && DateTime.UtcNow < toonModsStatusExpiryUtc)
            ImGui.TextColored(toonModsStatusIsError ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f) : new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiMessage.Resolve(ref toonModsStatusUiMessage, toonModsStatus));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawStickyToonModsSectionHeader();

        using (var child = ImRaii.Child("##XAModsSectionsScrollRegion", new Vector2(0f, 0f), false))
        {
            if (child)
            {
                toonModsSectionsScrollY = ImGui.GetScrollY();
                toonModsSectionScrollRanges.Clear();

                foreach (var (section, title) in ToonModsSectionOrder)
                    DrawModSection(section, title);

                var hasVisibleEntries = false;
                foreach (var entry in featureEntries)
                {
                    if (!IsToonModEntryVisible(entry))
                        continue;
                    hasVisibleEntries = true;
                    break;
                }

                if (!hasVisibleEntries)
                {
                    ImGui.TextDisabled(UiLocale.Get("SAA42FF56221C5715"));
                    ImGui.TextDisabled(UiLocale.Get("SD00AD2CDB2B73767"));
                }
            }
        }
    }

}
