using System;
using System.Collections.Generic;
using System.Text.Json;

namespace XASlave.Data;

public static class ToonModsPresetSerialization
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

[Serializable]
public sealed class ToonModSavedList
{
    public string Name { get; set; } = string.Empty;
    public List<string> ModKeys { get; set; } = new();
    public Dictionary<string, JsonElement> ModSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[Serializable]
public sealed class ToonModsListPackage
{
    public int SchemaVersion { get; set; } = 2;
    public string ListId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime ExportedAtUtc { get; set; }
    public List<string> ModKeys { get; set; } = new();
    public Dictionary<string, JsonElement> ModSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[Serializable]
public sealed class XAModResolutionPreset
{
    public int Width { get; set; }
    public int Height { get; set; }
}

[Serializable]
public sealed class XAModDisableBackgroundRenderingSettings
{
    public bool OnlyWhenMinimized { get; set; }
    public bool DisableWhenArMultiIsOn { get; set; }
}

[Serializable]
public sealed class XAModAutoHideGameObjectsSettings
{
    public bool HidePlayer { get; set; }
    public bool HideUnimportantEnpc { get; set; }
    public bool HidePet { get; set; }
    public bool HideChocobo { get; set; }
    public bool DisableInDuties { get; set; }
    public bool DisableInIslandSanctuary { get; set; }
    public bool UseOccultCrescentRules { get; set; }
}

[Serializable]
public sealed class XAModAutoSkipCutscenesSettings
{
    public bool UseZoneWhitelist { get; set; }
    public List<uint> WhitelistTerritories { get; set; } = new();
    public List<uint> BlacklistTerritories { get; set; } = new();
    public bool SkipNormalCutscenes { get; set; }
    public bool SkipMsqRoulette { get; set; }
    public bool AutoEnableMsqFourPlayer { get; set; }
    public bool ExemptPraetorium { get; set; }
    public bool ExemptCastrum { get; set; }
    public bool ExemptPortaDecumana { get; set; }
    public bool SkipMassivePc { get; set; }
    public bool SkipGoldSaucer { get; set; }
    public bool GoldSaucerMahjong { get; set; }
    public bool GoldSaucerAirForceOne { get; set; }
    public bool GoldSaucerChocoboRacing { get; set; }
    public bool GoldSaucerLordOfVerminion { get; set; }
    public bool GoldSaucerTripleTriad { get; set; }
    public bool GoldSaucerBlunderville { get; set; }
    public bool GoldSaucerFashionReport { get; set; }
    public bool SkipCustomTalk { get; set; }
    public bool SkipFeedBuddy { get; set; }
    public bool SkipOceanFishing { get; set; }
    public bool SkipCrystallineConflict { get; set; }
    public bool SkipFrontlineRivalWings { get; set; }
    public bool SkipInn { get; set; }
}

[Serializable]
public sealed class XAModCustomResolutionsSettings
{
    public List<XAModResolutionPreset> Presets { get; set; } = new();
}

[Serializable]
public sealed class XAModLowResolutionSettings
{
    public float Scale { get; set; }
}

[Serializable]
public sealed class XAModPopupCleanerSettings
{
    public bool HideHowToNotice { get; set; }
}

[Serializable]
public sealed class XAModDalamudNotificationsSuckSettings
{
    public bool HideAll { get; set; }
    public bool HideDalamudUpdates { get; set; }
    public bool HidePluginLifecycle { get; set; }
    public bool HidePluginErrors { get; set; }
    public bool HideModManagerAlerts { get; set; }
    public bool HideSuccessInfo { get; set; }
    public bool HideWarningsErrors { get; set; }
}

[Serializable]
public sealed class XAModBetterHighlightPotentialTargetsSettings
{
    public int Color { get; set; } = 6;
}

[Serializable]
public sealed class XAModShowTravelerWorldNamesSettings
{
    public bool DisableInDuties { get; set; } = true;
}

[Serializable]
public sealed class XAModCustomTimestampFormatSettings
{
    public string Format { get; set; } = string.Empty;
}

[Serializable]
public sealed class XAModAutoDisplayIdsSettings
{
    public bool? ShowItemId { get; set; }
    public bool ShowActionId { get; set; }
    public bool ShowTargetDataId { get; set; }
    public bool ShowWeatherId { get; set; }
    public bool ShowZoneInfo { get; set; }
}

[Serializable]
public sealed class XAModNetworkLatencySettings
{
    public string Format { get; set; } = "Ping: {0} ms";
}

[Serializable]
public sealed class XAModNotifyWhenFriendIsNearSettings
{
    public List<string> Patterns { get; set; } = new();
    public int CooldownSeconds { get; set; }
}

[Serializable]
public sealed class XAModBetterCastBarSettings
{
    public float InterruptedTextX { get; set; }
    public float InterruptedTextY { get; set; }
    public int InterruptedTextSize { get; set; }
    public float ActionNameTextX { get; set; }
    public float ActionNameTextY { get; set; }
    public int ActionNameTextSize { get; set; }
    public float CastingTextX { get; set; }
    public float CastingTextY { get; set; }
    public int CastingTextSize { get; set; }
    public float CastTimeTextX { get; set; }
    public float CastTimeTextY { get; set; }
    public int CastTimeTextSize { get; set; }
    public int IconAlpha { get; set; }
    public float IconX { get; set; }
    public float IconY { get; set; }
    public float IconScaleX { get; set; }
    public float IconScaleY { get; set; }
    public int SlidecastMode { get; set; }
    public int SlidecastThresholdMs { get; set; }
    public int SlidecastLineWidth { get; set; }
    public int SlidecastLineHeight { get; set; }
    public XAModColorSettings SlidecastNotReadyColor { get; set; } = new();
    public XAModColorSettings SlidecastReadyColor { get; set; } = new();
}

[Serializable]
public sealed class XAModBetterInventoryMoverSettings
{
    public string QuickMoveModifier { get; set; } = "LeftShift";
}

[Serializable]
public sealed class XAModBetterCompanyChestSettings
{
    public int DefaultPage { get; set; }
    public bool QuickMoveEnabled { get; set; }
    public bool AutoConfirmNumericInput { get; set; }
    public bool ShowExchangeableValue { get; set; }
}

[Serializable]
public sealed class XAModSpecialRenderModesSettings
{
    public float BackgroundColorR { get; set; }
    public float BackgroundColorG { get; set; }
    public float BackgroundColorB { get; set; }
    public float BackgroundColorA { get; set; }
    public bool HideAddonsKeepNameplates { get; set; }
    public bool HideAddonsKeepChat { get; set; }
    public bool HideChat { get; set; }
    public bool HideActionBars { get; set; }
    public bool HideTargetInfo { get; set; }
    public bool HideNameplates { get; set; }
}

[Serializable]
public sealed class XAModPlayerSearchSettings
{
    public bool FflogsEnabled { get; set; }
    public bool LodestoneEnabled { get; set; }
    public bool LalachievementsEnabled { get; set; }
    public bool OpenAllEnabled { get; set; }
}

[Serializable]
public sealed class XAModExpertDeliverySettings
{
    public bool AutoSwitchWhenOpen { get; set; }
    public int DefaultPage { get; set; }
    public bool SkipHq { get; set; }
    public bool SkipMateria { get; set; }
    public bool IgnoreSealCap { get; set; }
}

[Serializable]
public sealed class XAModUnlockExpertDeliverySettings
{
    public int ForcedRankFloor { get; set; }
}

[Serializable]
public sealed class XAModBailoutEscMenuSettings
{
    public int TimeoutSeconds { get; set; }
}

[Serializable]
public sealed class XAModTradeRefusalSettings
{
    public bool ShowNotification { get; set; }
    public bool SendEcho { get; set; }
    public string ExtraCommands { get; set; } = string.Empty;
}

[Serializable]
public sealed class XAModTeleportHelperSettings
{
    public bool SelectYes { get; set; }
}

[Serializable]
public sealed class XAModAutoLeaveDutySettings
{
    public int DelaySeconds { get; set; }
}

[Serializable]
public sealed class XAModEurekaInstanceIdSettings
{
    public int? Zone { get; set; }
    public int? BaselineInstanceId { get; set; }
    public bool? ShowInDtr { get; set; }
    public int? LeaveDutyDelaySeconds { get; set; }
    public bool? AnemosEnabled { get; set; }
    public int? AnemosBaselineInstanceId { get; set; }
    public bool? PagosEnabled { get; set; }
    public int? PagosBaselineInstanceId { get; set; }
    public bool? PyrosEnabled { get; set; }
    public int? PyrosBaselineInstanceId { get; set; }
    public bool? HydatosEnabled { get; set; }
    public int? HydatosBaselineInstanceId { get; set; }
    public bool PlaySound { get; set; }
    public int SoundEffectId { get; set; }
    public float SoundVolume { get; set; }
}

[Serializable]
public sealed class XAModCustomSightDistanceSettings
{
    public float MaxDistance { get; set; }
    public float MinDistance { get; set; }
    public float MaxRotation { get; set; }
    public float MinRotation { get; set; }
    public float MaxFoV { get; set; }
    public float MinFoV { get; set; }
    public float CurrentFoV { get; set; }
    public bool IgnoreCollision { get; set; }
}

[Serializable]
public sealed class XAModInfiniteSprintSettings
{
    public float DelaySeconds { get; set; }
}

[Serializable]
public sealed class XAModColorSettings
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }
}

[Serializable]
public sealed class XAModXAPeepSettings
{
    public bool AutoOpenWindowOnPluginLoad { get; set; }
    public bool WindowLocked { get; set; }
    public bool LogParty { get; set; }
    public bool LogAlliance { get; set; }
    public bool LogInCombat { get; set; }
    public bool LogInDuty { get; set; }
    public bool ShowCardWhenTargeted { get; set; }
    public bool ShowTargeterLine { get; set; }
    public XAModColorSettings TargeterLineColor { get; set; } = new();
    public bool ShowTargeterDot { get; set; }
    public XAModColorSettings TargeterDotColor { get; set; } = new();
    public float TargeterDotSize { get; set; }
    public bool ShowTargetersCard { get; set; }
    public bool ShowCenterNotification { get; set; }
    public bool ShowChatNotification { get; set; }
    public bool PlaySound { get; set; }
    public int SoundEffectId { get; set; }
    public float SoundVolume { get; set; }
}
