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
