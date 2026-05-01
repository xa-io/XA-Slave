using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Numerics;
using XASlave.Data;
using XASlave.Services;

namespace XASlave;

/// <summary>
/// Persistent per-character data for Monthly Relogger table columns.
/// Keyed by "Name@World" in Configuration.ReloggerCharacterInfo.
/// Populated from AutoRetainer config, XA Database, or relogger runs.
/// </summary>
[Serializable]
public class ReloggerCharacterData
{
    public long CID { get; set; }
    public int HighestLevel { get; set; }
    public int Gil { get; set; }
    public string FcName { get; set; } = "";
    public long FCID { get; set; }
    public DateTime LastLoggedIn { get; set; } = DateTime.MinValue;
    public string PersonalEstate { get; set; } = "";
    public string Apartment { get; set; } = "";
    public string FcEstate { get; set; } = "";
    public string CurrentWorld { get; set; } = "";
    public int RetainerCount { get; set; }
    public int SubmarineCount { get; set; }
    public string FcMemberRankName { get; set; } = "";
    public int FcMemberRankSort { get; set; } = int.MaxValue;
    public int FreeCompanyRank { get; set; }
    public int MainInventoryUsedSlots { get; set; }
    public int MainInventoryTotalSlots { get; set; }
    public int MainInventoryFreeSlots { get; set; }
}

[Serializable]
public class TitleBarFavCustomItem
{
    public bool Enabled { get; set; } = true;
    public string SelectionKey { get; set; } = string.Empty;
    public string MenuTarget { get; set; } = string.Empty;
}

[Serializable]
public class TitleBarFavResolutionItem
{
    public bool Enabled { get; set; } = true;
    public int Width { get; set; } = 500;
    public int Height { get; set; } = 345;
}

public static class TitleBarFavSelectionKeys
{
    public const string MenuPrefix = "menu:";
    public const string XAModPrefix = "xamod:";
    public const string DisableAllXAModsKey = "action:disable-all-xa-mods";
    public const string StopAllKey = "action:stop-all";
    public const string SpecialRenderHideAddonsKeepNameplatesKey = "action:special-render:hide-addons-keep-nameplates";
    public const string SpecialRenderHideAddonsKeepChatKey = "action:special-render:hide-addons-keep-chat";
    public const string SpecialRenderHideChatKey = "action:special-render:hide-chat";
    public const string SpecialRenderHideActionBarsKey = "action:special-render:hide-action-bars";
    public const string SpecialRenderHideTargetInfoKey = "action:special-render:hide-target-info";
    public const string SpecialRenderHideNameplatesKey = "action:special-render:hide-nameplates";
    public const string SpecialRenderRestoreAllKey = "action:special-render:restore-all";
    public const string SitNowKey = "action:doze-sit:sit";
    public const string DozeNowKey = "action:doze-sit:doze";

    public static string FromMenuTarget(string? menuTarget)
    {
        return string.IsNullOrWhiteSpace(menuTarget)
            ? string.Empty
            : $"{MenuPrefix}{menuTarget.Trim()}";
    }

    public static string FromXAModKey(string? xamodKey)
    {
        return string.IsNullOrWhiteSpace(xamodKey)
            ? string.Empty
            : $"{XAModPrefix}{xamodKey.Trim()}";
    }

    public static string Normalize(string? selectionKey, string? legacyMenuTarget = null)
    {
        var trimmed = selectionKey?.Trim() ?? string.Empty;
        return trimmed.Length > 0 ? trimmed : FromMenuTarget(legacyMenuTarget);
    }

    public static bool TryGetMenuTarget(string? selectionKey, out string menuTarget)
    {
        var trimmed = selectionKey?.Trim() ?? string.Empty;
        if (trimmed.StartsWith(MenuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            menuTarget = trimmed[MenuPrefix.Length..].Trim();
            return menuTarget.Length > 0;
        }

        menuTarget = string.Empty;
        return false;
    }

    public static bool TryGetXAModKey(string? selectionKey, out string xamodKey)
    {
        var trimmed = selectionKey?.Trim() ?? string.Empty;
        if (trimmed.StartsWith(XAModPrefix, StringComparison.OrdinalIgnoreCase))
        {
            xamodKey = trimmed[XAModPrefix.Length..].Trim();
            return xamodKey.Length > 0;
        }

        xamodKey = string.Empty;
        return false;
    }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;
    public bool OpenPluginOnLoad { get; set; } = false;
    public bool VerboseTaskLogging { get; set; } = false;
    public bool ShowVersionInUpdatesTitle { get; set; } = true;
    public bool ShowVersionInWindowTitleDefaultApplied { get; set; } = false;
    public bool GlobalCharacterListAnonymizeEnabled { get; set; } = false;

    // Auto-collection on login opens saddlebag/FC windows to collect data
    public bool AutoCollectOnLogin { get; set; } = false;
    public bool AutoCollectArmouryChest { get; set; } = true;
    public bool AutoCollectSaddlebag { get; set; } = true;
    public bool AutoCollectJournal { get; set; } = true;
    public bool AutoCollectPersonalPlotInfo { get; set; } = true;
    public bool AutoCollectFc { get; set; } = true;
    public int AutoCollectCheckEveryHours { get; set; } = 0;
    public bool AutoCollectDisableWhenArMultiEnabled { get; set; } = false;

    // Delay before auto-collection starts after login (seconds)
    public float AutoCollectDelaySeconds { get; set; } = 8f;

    // IPC panel live value polling
    public bool IpcLivePullsEnabled { get; set; } = false;
    public int IpcLivePullIntervalSeconds { get; set; } = 10;

    // -- Monthly Relogger --
    // Character list in "Name@World" format persisted across sessions
    public List<string> ReloggerCharacters { get; set; } = new();

    // -- Auto-Glam Weather --
    public int AutoGlamWeatherClassJob { get; set; } = 1;
    public int AutoGlamWeatherSunnyPlate { get; set; } = 2;
    public int AutoGlamWeatherRainPlate { get; set; } = 3;
    public int AutoGlamWeatherFreezePlate { get; set; } = 1;
    public string AutoGlamWeatherClassJobOptions { get; set; } = "1";
    public string AutoGlamWeatherSunnyPlateOptions { get; set; } = "2";
    public string AutoGlamWeatherRainPlateOptions { get; set; } = "3";
    public string AutoGlamWeatherFreezePlateOptions { get; set; } = "1";
    public Dictionary<int, string> AutoGlamWeatherPlateOptionsByWeatherId { get; set; } = new();
    public bool AutoGlamWeatherOptionsInitialized { get; set; } = false;
    public float AutoGlamWeatherCheckIntervalSeconds { get; set; } = 3.0f;

    // Per-character action toggles
    public bool ReloggerDoTextAdvance { get; set; } = true;
    public bool ReloggerDoRemoveSprout { get; set; } = true;
    public bool ReloggerDoOpenInventory { get; set; } = true;
    public bool ReloggerDoOpenArmouryChest { get; set; } = true;
    public bool ReloggerDoOpenSaddlebags { get; set; } = true;
    public bool ReloggerDoOpenJournal { get; set; } = true;
    public bool ReloggerDoReturnToHome { get; set; } = true;
    public bool ReloggerDoCollectPersonalPlotInfo { get; set; } = true;
    public bool ReloggerDoReturnToFc { get; set; } = true;
    public bool ReloggerDoParseForXaDatabase { get; set; } = true;
    public bool ReloggerDoLogoutOnComplete { get; set; } = false;
    public bool ReloggerDoKillGameOnComplete { get; set; } = false;
    public bool ReloggerDoEnableArMultiOnComplete { get; set; } = false;

    // Region filter for character list display
    public string ReloggerRegionFilter { get; set; } = "All";
    public int ReloggerStaleSelectDays { get; set; } = 20;
    public bool ReloggerAnonymizeCharacters { get; set; } = false;

    // Per-character persistent data for table columns (Lv, Gil, FC, In FC, Last Logged In).
    // Keyed by "Name@World". Updated from AutoRetainer imports and relogger runs.
    public Dictionary<string, ReloggerCharacterData> ReloggerCharacterInfo { get; set; } = new();

    // Legacy CID - last login timestamp. Migrated to ReloggerCharacterInfo on load.
    public Dictionary<long, DateTime> ReloggerLastSeen { get; set; } = new();

    // -- Refresh AR Subs/Bell --
    public List<string> RefreshSubsCharacters { get; set; } = new();
    public string RefreshSubsRegionFilter { get; set; } = "All";
    public bool RefreshSubsAnonymizeCharacters { get; set; } = false;

    // -- Prep Logistics --
    public List<string> PrepLogisticsCharacters { get; set; } = new();
    public string PrepLogisticsTargetWorld { get; set; } = string.Empty;
    public string PrepLogisticsTargetAetheryte { get; set; } = string.Empty;
    public bool PrepLogisticsEnableArMultiOnComplete { get; set; } = true;
    public bool PrepLogisticsLogoutOnComplete { get; set; } = false;
    public bool PrepLogisticsKillGameOnComplete { get; set; } = false;
    public string PrepLogisticsRegionFilter { get; set; } = "All";
    public bool PrepLogisticsAnonymizeCharacters { get; set; } = false;

    public XagmanRole XagmanRole { get; set; } = XagmanRole.Tony;
    public List<XagmanTonyCharacterEntry> XagmanTonyCharacters { get; set; } = new();
    public List<string> XagmanFranchiseCharacters { get; set; } = new();
    public List<XagmanItemEntry> XagmanItems { get; set; } = new();
    public List<XagmanNamedItemList> XagmanSavedItemLists { get; set; } = new();
    public List<XagmanItemEntry> XagmanTonyItems { get; set; } = new();
    public List<XagmanItemEntry> XagmanFranchiseItems { get; set; } = new();
    public bool XagmanSharedItemsMigrationComplete { get; set; }
    public string XagmanTargetWorld { get; set; } = string.Empty;
    public string XagmanTargetAetheryte { get; set; } = string.Empty;
    public int XagmanTonyGilMinimum { get; set; } = 10000;
    public bool XagmanEnableArMultiOnComplete { get; set; } = true;
    public bool XagmanLogoutOnComplete { get; set; } = false;
    public bool XagmanKillGameOnComplete { get; set; } = false;
    public bool XagmanUsePreflightOnFirstCharacter { get; set; } = true;
    public bool XagmanAutoReturnToFc { get; set; } = true;
    public bool XagmanIgnoreGilInMatchingSelection { get; set; } = true;
    public bool XagmanWarningDetailsExpanded { get; set; } = true;
    public bool XagmanRoleInstructionsExpanded { get; set; } = true;
    public string XagmanRegionFilter { get; set; } = "All";
    public bool XagmanTonyAnonymizeCharacters { get; set; } = false;
    public bool XagmanFranchiseAnonymizeCharacters { get; set; } = false;
    public string XagmanHubAddress { get; set; } = XagmanPeerService.DefaultHubAddress;
    public int XagmanHubPort { get; set; } = 45215;
    public bool XagmanPeerConnectionsEnabled { get; set; } = false;

    // -- FC Permissions Updater --
    public List<string> FcPermsCharacters { get; set; } = new();
    public string FcPermsRegionFilter { get; set; } = "All";
    public bool FcPermsLogoutOnComplete { get; set; } = false;
    public bool FcPermsKillGameOnComplete { get; set; } = false;
    public bool FcPermsEnableArMultiOnComplete { get; set; } = true;
    public bool FcPermsAnonymizeCharacters { get; set; } = false;
    public bool DupPlotsAnonymizeCharacters { get; set; } = false;
    public bool ReturnAltsAnonymizeCharacters { get; set; } = false;

    // -- AR Pre-Processing --
    // Master toggle when enabled, runs collection steps on login BEFORE AR starts retainer processing
    // Uses AR Suppressed pattern: suppress AR - run steps - un-suppress AR
    public bool ArPreProcessEnabled { get; set; } = false;
    public float ArPreProcessLoginDelay { get; set; } = 5f;
    public int ArPrePostCheckEveryHours { get; set; } = 0;
    public bool ArShipExplorationBailoutEnabled { get; set; } = false;
    public int ArShipExplorationBailoutSeconds { get; set; } = 30;
    public bool BailoutEscMenuEnabled { get; set; } = false;
    public int BailoutEscMenuSeconds { get; set; } = 5;
    // Per-step toggles what to do before AR processes retainers
    public bool ArPreProcessOpenInventory { get; set; } = true;
    public bool ArPreProcessOpenArmouryChest { get; set; } = true;
    public bool ArPreProcessOpenSaddlebags { get; set; } = true;
    public bool ArPreProcessOpenJournal { get; set; } = true;
    public bool ArPreProcessCollectPersonalPlotInfo { get; set; } = true;
    public bool ArPreProcessFcWindow { get; set; } = true;
    public bool ArPreProcessSaveToXaDatabase { get; set; } = true;

    // -- AR Post-Processing --
    // Master toggle when enabled, registers with AR for character post-processing in multi-mode
    public bool ArPostProcessEnabled { get; set; } = false;
    public bool ArStartupRecoveryEnabled { get; set; } = true;
    // Per-step toggles what to do after AR finishes each character
    public bool ArPostProcessOpenInventory { get; set; } = true;
    public bool ArPostProcessOpenArmouryChest { get; set; } = true;
    public bool ArPostProcessOpenSaddlebags { get; set; } = true;
    public bool ArPostProcessOpenJournal { get; set; } = true;
    public bool ArPostProcessCollectPersonalPlotInfo { get; set; } = true;
    public bool ArPostProcessFcWindow { get; set; } = true;
    public bool ArPostProcessCheckFcChestForGil { get; set; } = false;
    public bool ArPostProcessSaveToXaDatabase { get; set; } = true;
    public bool ArProcessLogEnabled { get; set; } = false;

    // -- Window Renamer --
    public bool WindowRenamerEnabled { get; set; } = false;
    public string WindowRenamerTitle { get; set; } = "";
    public bool WindowRenamerUseProcessId { get; set; } = false;
    public bool WindowRenamerShowCurrentCharacter { get; set; } = false;

    // Toon Mods
    public bool AutoSkipCutscenesEnabled { get; set; } = false;
    public bool AutoSkipCutscenesUseZoneWhitelist { get; set; } = false;
    public List<uint> AutoSkipCutscenesWhitelistTerritories { get; set; } = new();
    public List<uint> AutoSkipCutscenesBlacklistTerritories { get; set; } = new();
    public bool AutoAllowMultipleGameInstancesEnabled { get; set; } = false;
    public bool AutoCancelLoginCooldownEnabled { get; set; } = false;
    public bool AutoDisplayMsqProgressEnabled { get; set; } = false;
    public bool CopyItemNameForAllEnabled { get; set; } = false;
    public bool AutoSkipCutscenesFeedingChocoboEnabled { get; set; } = false;
    public bool AutoIgnoreMinimumWindowSizeEnabled { get; set; } = false;
    public bool AutoHideUnnecessaryPopupsEnabled { get; set; } = false;
    public bool AutoHideUnnecessaryPopupsHideHowToNoticeEnabled { get; set; } = false;
    public bool AutoPreventGameExitingFromLobbyErrorsEnabled { get; set; } = false;
    public bool AutoCloseLobbyErrorsEnabled { get; set; } = false;
    public bool DisplayActualQueuePositionEnabled { get; set; } = false;
    public bool DisableBackgroundGameRenderingEnabled { get; set; } = false;
    public bool DisableBackgroundGameRenderingOnlyWhenMinimized { get; set; } = false;
    public bool DisableBackgroundGameRenderingDisableWhenArMultiIsOn { get; set; } = false;
    public bool AutoHideGameObjectsEnabled { get; set; } = false;
    public bool AutoHideGameObjectsHidePlayer { get; set; } = true;
    public bool AutoHideGameObjectsHideUnimportantEnpc { get; set; } = true;
    public bool AutoHideGameObjectsHidePet { get; set; } = true;
    public bool AutoHideGameObjectsHideChocobo { get; set; } = true;
    public bool AutoHideGameObjectsDisableInDuties { get; set; } = true;
    public bool AutoHideGameObjectsDisableInIslandSanctuary { get; set; } = true;
    public bool AutoHideGameObjectsUseOccultCrescentRules { get; set; } = true;
    public bool AutoSkipDialogueEnabled { get; set; } = false;
    public bool CustomResolutionsEnabled { get; set; } = false;
    public List<XAModResolutionPreset> CustomResolutionPresets { get; set; } = new();
    public bool SpecialRenderModesEnabled { get; set; } = false;
    public bool LowResolutionEnabled { get; set; } = false;
    public float LowResolutionScale { get; set; } = 0.25f;
    public bool CustomSightDistanceEnabled { get; set; } = false;
    public float CustomSightDistanceMaxDistance { get; set; } = 80f;
    public float CustomSightDistanceMinDistance { get; set; } = 1.5f;
    public float CustomSightDistanceMaxRotation { get; set; } = 1.569f;
    public float CustomSightDistanceMinRotation { get; set; } = -1.569f;
    public float CustomSightDistanceMaxFoV { get; set; } = 0.78f;
    public float CustomSightDistanceMinFoV { get; set; } = 0.69f;
    public float CustomSightDistanceFoV { get; set; } = 0.78f;
    public bool CustomSightDistanceIgnoreCollision { get; set; } = true;
    public float SpecialRenderModeBackgroundColorR { get; set; } = 0.53f;
    public float SpecialRenderModeBackgroundColorG { get; set; } = 0.81f;
    public float SpecialRenderModeBackgroundColorB { get; set; } = 0.98f;
    public float SpecialRenderModeBackgroundColorA { get; set; } = 1.0f;
    public bool SpecialRenderHideAddonsKeepNameplatesEnabled { get; set; } = false;
    public bool SpecialRenderHideAddonsKeepChatEnabled { get; set; } = false;
    public bool SpecialRenderHideChatEnabled { get; set; } = false;
    public bool SpecialRenderHideActionBarsEnabled { get; set; } = false;
    public bool SpecialRenderHideTargetInfoEnabled { get; set; } = false;
    public bool SpecialRenderHideNameplatesEnabled { get; set; } = false;
    public bool ExpandedPlayerRightClickMenuSearchEnabled { get; set; } = false;
    public bool ExpandedPlayerRightClickMenuSearchFflogsEnabled { get; set; } = true;
    public bool ExpandedPlayerRightClickMenuSearchLodestoneEnabled { get; set; } = true;
    public bool ExpandedPlayerRightClickMenuSearchLalachievementsEnabled { get; set; } = true;
    public bool ExpandedPlayerRightClickMenuSearchOpenAllEnabled { get; set; } = true;
    public bool LiveAnonymousModeEnabled { get; set; } = false;
    public bool AutoUnlockExpertDeliveryEnabled { get; set; } = false;
    public bool AutoUnlockExpertDeliveryAutoSwitchWhenOpen { get; set; } = true;
    public int AutoUnlockExpertDeliveryDefaultPage { get; set; } = 2;
    public bool AutoUnlockExpertDeliverySkipHq { get; set; } = true;
    public bool AutoUnlockExpertDeliverySkipMateria { get; set; } = true;
    public bool AutoUnlockExpertDeliveryIgnoreSealCap { get; set; } = false;
    public bool AntiAfkEnabled { get; set; } = false;
    public bool AutoLeaveDutyEnabled { get; set; } = false;
    public int AutoLeaveDutyDelaySeconds { get; set; } = AutoLeaveDutyService.DelaySecondsDefault;
    public bool EurekaInstanceIdEnabled { get; set; } = false;
    public int EurekaInstanceIdZone { get; set; } = (int)EurekaInstanceIdService.DefaultZone;
    public int EurekaInstanceIdBaselineInstanceId { get; set; } = 0;
    public bool EurekaInstanceIdShowInDtr { get; set; } = false;
    public int EurekaInstanceIdLeaveDutyDelaySeconds { get; set; } = EurekaInstanceIdService.LeaveDutyDelaySecondsDefault;
    public bool EurekaInstanceIdAnemosEnabled { get; set; } = false;
    public int EurekaInstanceIdAnemosBaselineInstanceId { get; set; } = 0;
    public bool EurekaInstanceIdPagosEnabled { get; set; } = false;
    public int EurekaInstanceIdPagosBaselineInstanceId { get; set; } = 0;
    public bool EurekaInstanceIdPyrosEnabled { get; set; } = false;
    public int EurekaInstanceIdPyrosBaselineInstanceId { get; set; } = 0;
    public bool EurekaInstanceIdHydatosEnabled { get; set; } = false;
    public int EurekaInstanceIdHydatosBaselineInstanceId { get; set; } = 0;
    public bool EurekaInstanceIdPlaySound { get; set; } = false;
    public int EurekaInstanceIdSoundEffectId { get; set; } = 0;
    public float EurekaInstanceIdSoundVolume { get; set; } = EurekaInstanceIdService.DefaultSoundVolume;

    // Eureka Logogram Creator
    public bool AutoRefreshAllPagesOnOpen { get; set; } = true;
    public bool AutoDestroyWhenMagiaBoardFull { get; set; } = false;
    public bool AutoRetryFailedExtraction { get; set; } = false;
    public Dictionary<uint, int> PreferredRecipeIndexes { get; set; } = new();
    public Dictionary<ulong, int> LogogramSourceGilCosts { get; set; } = new();
    public List<FavoritePlate> FavoritePlates { get; set; } = new();
    public bool ShowFavoritesOverlay { get; set; } = true;
    public int QueueStepFrameDelay { get; set; } = 20;
    public bool EurekaLogogramCreatorDefaultSettingsMigrationApplied { get; set; } = false;
    public bool AutoMergeEnabled { get; set; } = false;
    public bool QuickReturnEnabled { get; set; } = false;
    public bool UnlockExpertDeliveryEnabled { get; set; } = false;
    public int UnlockExpertDeliveryForcedRankFloor { get; set; } = ExpertDeliveryUnlockService.DefaultForcedRankFloor;
    public bool AutoRefuseTradeRequestEnabled { get; set; } = false;
    public bool AutoRefuseTradeShowNotification { get; set; } = true;
    public bool AutoRefuseTradeSendEcho { get; set; } = false;
    public string AutoRefuseTradeExtraCommands { get; set; } = string.Empty;
    public bool TargetCommandFixEnabled { get; set; } = false;
    public bool AutoRevealUndiscoveredAreasEnabled { get; set; } = false;
    public bool AutoClearTeleportationLockEnabled { get; set; } = false;
    public bool DozeSitAnywhereEnabled { get; set; } = false;
    public bool DozeSitAnywhereAllowDoze { get; set; } = true;
    public bool DozeSitAnywhereAllowSit { get; set; } = true;
    public bool InfiniteSprintEnabled { get; set; } = false;
    public float InfiniteSprintDelaySeconds { get; set; } = 2.0f;
    public bool InstantLogoutEnabled { get; set; } = false;
    public bool ItemCommandsEnabled { get; set; } = false;
    public bool XAPeepEnabled { get; set; } = false;
    public bool XAPeepWindowOpen { get; set; } = false;
    public bool XAPeepHistoryWindowOpen { get; set; } = false;
    public bool XAPeepAutoOpenWindowOnPluginLoad { get; set; } = false;
    public bool XAPeepWindowLocked { get; set; } = false;
    public bool XAPeepLogParty { get; set; } = true;
    public bool XAPeepLogAlliance { get; set; } = false;
    public bool XAPeepLogInCombat { get; set; } = false;
    public bool XAPeepLogInDuty { get; set; } = false;
    public bool XAPeepDisplayLineWhenTargetingMe { get; set; } = true;
    public bool XAPeepShowTargeterLine { get; set; } = true;
    public bool XAPeepShowTargeterDot { get; set; } = true;
    public bool XAPeepShowTargetersCard { get; set; } = true;
    public bool XAPeepShowCenterNotification { get; set; } = false;
    public bool XAPeepShowChatNotification { get; set; } = false;
    public bool XAPeepPlaySound { get; set; } = false;
    public int XAPeepSoundEffectId { get; set; } = 0;
    public float XAPeepSoundVolume { get; set; } = 0.45f;
    public Vector4 XAPeepTargeterLineColor { get; set; } = new(0.68f, 0.35f, 0.94f, 0.95f);
    public Vector4 XAPeepTargeterDotColor { get; set; } = new(0.68f, 0.35f, 0.94f, 0.95f);
    public float XAPeepTargeterDotSize { get; set; } = 4f;
    public bool MoveableAfterDeathEnabled { get; set; } = false;
    public List<ToonModSavedList> ToonModsSavedLists { get; set; } = new();
    public bool ForcePeepingTomEnabled { get; set; } = false;

    // -- City Chat Flooder --
    public List<string> FloorderSelectedWorlds { get; set; } = new();
    public List<string> FloorderSelectedCities { get; set; } = new();
    public List<string> FloorderCustomCities { get; set; } = new();
    public List<string> FloorderAnnouncements { get; set; } = new();
    public string FloorderChatChannel { get; set; } = "/echo";
    public float FloorderWaitBetweenCities { get; set; } = 3.0f;
    public float FloorderWaitAfterAnnounce { get; set; } = 1.0f;
    public bool FloorderEnableLooping { get; set; } = false;
    public float FloorderLoopDelayMinutes { get; set; } = 5.0f;
    public bool FloorderInitialized { get; set; } = false;

    // -- Export Data --
    public bool ExportDataAlwaysOn { get; set; } = false;
    public int ExportDataRunEveryHours { get; set; } = 24;
    public string ExportDataOutputPath { get; set; } = string.Empty;
    public bool ExportDataOverwriteFile { get; set; } = false;
    public string ExportDataLastSuccessfulRunUtc { get; set; } = string.Empty;

    public bool MenuAutomatedTasksExpanded { get; set; } = true;
    public bool MenuCityShenanigansExpanded { get; set; } = true;
    public bool MenuFcRelationsExpanded { get; set; } = true;
    public bool MenuFieldOperationsExpanded { get; set; } = true;
    public bool MenuUtilityExpanded { get; set; } = true;
    public bool MenuReferenceExpanded { get; set; } = true;
    public bool ToonModsGameModsExpanded { get; set; } = true;
    public bool ToonModsGraphicModsExpanded { get; set; } = true;
    public bool ToonModsPlayerModsExpanded { get; set; } = true;
    public bool ToonModsIllegalModsExpanded { get; set; } = true;
    public bool ToonModsPluginModsExpanded { get; set; } = true;
    public bool ToonModsEurekaExpanded { get; set; } = true;
    public float TaskMenuWidth { get; set; } = 180f;
    public string LastSelectedBuiltInTask { get; set; } = "";
    public string LastSelectedExternalTaskName { get; set; } = "";

    // -- Titlebar Favourite Buttons --
    public bool TitleBarFavKillGameEnabled { get; set; } = false;
    public bool TitleBarFavDisableAllModsEnabled { get; set; } = false;
    public bool TitleBarFavModListEnabled { get; set; } = false;
    public string TitleBarFavModListName { get; set; } = string.Empty;
    public bool TitleBarFavGlamWeatherEnabled { get; set; } = false;
    public bool TitleBarFavArPreProcessEnabled { get; set; } = false;
    public bool TitleBarFavArPostProcessEnabled { get; set; } = false;
    public List<TitleBarFavCustomItem> TitleBarFavCustomItems { get; set; } = new();
    public List<TitleBarFavResolutionItem> TitleBarFavResolutionItems { get; set; } = new();

    public void InitializeFloorderDefaults()
    {
        if (FloorderInitialized) return;
        FloorderInitialized = true;
        if (FloorderSelectedCities.Count == 0)
        {
            FloorderSelectedCities.AddRange(new[] { "Limsa Lominsa Lower Decks", "New Gridania", "Ul'dah - Steps of Nald" });
        }
        if (FloorderAnnouncements.Count == 0)
        {
            FloorderAnnouncements.AddRange(new[]
            {
                "I'm the real warrior of light",
                "What are you talking about, I'm totally real.",
                "I'm just looking for a new sidequest.",
            });
        }
        Save();
    }

    public bool InitializeAutoGlamWeatherDefaults()
    {
        var changed = false;
        if (!AutoGlamWeatherOptionsInitialized)
        {
            AutoGlamWeatherOptionsInitialized = true;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherClassJobOptions)
            || (AutoGlamWeatherClassJobOptions == "1" && AutoGlamWeatherClassJob != 1))
        {
            AutoGlamWeatherClassJobOptions = AutoGlamWeatherClassJob > 0 ? AutoGlamWeatherClassJob.ToString() : "1";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherSunnyPlateOptions)
            || (AutoGlamWeatherSunnyPlateOptions == "2" && AutoGlamWeatherSunnyPlate != 2))
        {
            AutoGlamWeatherSunnyPlateOptions = AutoGlamWeatherSunnyPlate > 0 ? AutoGlamWeatherSunnyPlate.ToString() : "2";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherRainPlateOptions)
            || (AutoGlamWeatherRainPlateOptions == "3" && AutoGlamWeatherRainPlate != 3))
        {
            AutoGlamWeatherRainPlateOptions = AutoGlamWeatherRainPlate > 0 ? AutoGlamWeatherRainPlate.ToString() : "3";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(AutoGlamWeatherFreezePlateOptions)
            || (AutoGlamWeatherFreezePlateOptions == "1" && AutoGlamWeatherFreezePlate != 1))
        {
            AutoGlamWeatherFreezePlateOptions = AutoGlamWeatherFreezePlate > 0 ? AutoGlamWeatherFreezePlate.ToString() : "1";
            changed = true;
        }

        AutoGlamWeatherPlateOptionsByWeatherId ??= new Dictionary<int, string>();
        foreach (var weatherId in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 15, 16, 17, 49, 50, 148, 149 })
        {
            if (AutoGlamWeatherPlateOptionsByWeatherId.TryGetValue(weatherId, out var options)
                && !string.IsNullOrWhiteSpace(options))
                continue;

            AutoGlamWeatherPlateOptionsByWeatherId[weatherId] = GetLegacyAutoGlamWeatherPlateOptions(weatherId);
            changed = true;
        }

        return changed;
    }

    private string GetLegacyAutoGlamWeatherPlateOptions(int weatherId)
    {
        return weatherId switch
        {
            4 or 6 or 7 or 8 or 9 or 10 => AutoGlamWeatherRainPlateOptions,
            15 or 16 => AutoGlamWeatherFreezePlateOptions,
            _ => AutoGlamWeatherSunnyPlateOptions,
        };
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
