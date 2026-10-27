using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SysAction = System.Action;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;
namespace XASlave.Windows;
public partial class SlaveWindow
{
    // Semantic sort IDs keep each header tied to its data when columns are added.
    // The v4 table IDs add the optional Builds column after Restock.
    private enum XagmanRosterColumn : uint
    {
        Character = 1,
        Region = 2,
        DataCenter = 3,
        Homeworld = 4,
        Inventory = 5,
        Gil = 6,
        Treasure = 7,
        Kits = 8,
        Tanks = 9,
        Retainers = 10,
        Submarines = 11,
        Delete = 12,
        Restock = 13,
        Builds = 14,
    }

    private enum XagmanTonyMeetCommandPhase
    {
        None,
        AwaitingTargetWorld,
        SettlingTargetWorld,
    }

    private readonly HashSet<int> xagmanTonySelectedIndices = new();
    private readonly HashSet<int> xagmanFranchiseSelectedIndices = new();
    private string xagmanTonySearchFilter = string.Empty;
    private string xagmanFranchiseSearchFilter = string.Empty;
    private bool xagmanTonyShowOnlySelected;
    private bool xagmanFranchiseShowOnlySelected;
    private string xagmanWorldFilter = string.Empty;
    private string xagmanAetheryteFilter = string.Empty;
    private string xagmanTonyNewChar = string.Empty;
    private string xagmanFranchiseNewChar = string.Empty;
    private string xagmanItemSearch = string.Empty;
    private string xagmanItemQueryCache = string.Empty;
    private string xagmanItemImportJson = string.Empty;
    private string xagmanSavedItemListName = string.Empty;
    private string xagmanRestockBuild = string.Empty;
    private string xagmanRestockDays = string.Empty;
    private bool xagmanShowLog;
    private bool xagmanShowPeers = true;
    private bool xagmanShowQueue = true;
    private bool xagmanPendingHubEndpointInitialized;
    private string xagmanPendingHubAddress = string.Empty;
    private int xagmanPendingHubPort = XagmanPeerService.DefaultHubPort;
    private readonly Dictionary<string, List<XagmanDbSearchMatch>> xagmanCharacterMatchQueryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> xagmanMatchingCharacterKeysCache = new(StringComparer.Ordinal);
    private XagmanRole xagmanActiveRole = XagmanRole.Tony;
    private XagmanTonyMode xagmanTonyMode = XagmanTonyMode.Collection;
    private XagmanStatus xagmanStatus = XagmanStatus.Idle;
    private string xagmanStatusText = "Idle";
    private global::XASlave.Localization.UiMessage? xagmanStatusTextUiMessage = global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441");
    private string xagmanActiveCharacter = string.Empty;
    private string xagmanPreferredTonyCharacter = string.Empty;
    private string xagmanActiveMeetWorld = string.Empty;
    private string xagmanActiveMeetAetheryte = string.Empty;
    private string xagmanActiveTradePartner = string.Empty;
    private string xagmanActiveTradePartnerInstanceId = string.Empty;
    private bool xagmanRunning;
    internal bool IsXagmanMessageLoggingRequired => xagmanRunning;
    private bool xagmanTradeSafetySessionActive;
    private bool xagmanExpectedLogout;
    private System.Action<global::XASlave.Localization.UiMessage>? xagmanOwnerRelogLogoutFailure;
    private XagmanTravelFailureMonitor? xagmanTravelFailureMonitor;
    private long xagmanTravelFailureToken;
    private bool xagmanTonyCharacterFailurePending;
    private string xagmanSkipFcReturnAfterFailureCharacter = string.Empty;
    private readonly Dictionary<string, global::XASlave.Localization.UiMessage> xagmanCharacterFailureReasons = new(StringComparer.OrdinalIgnoreCase);
    private const float XagmanExpectedTravelLogoutWindowSeconds = 60f;
    private DateTime xagmanExpectedTravelLogoutUntilUtc = DateTime.MinValue;
    private string xagmanExpectedTravelLogoutContext = string.Empty;
    private string xagmanExpectedTravelLogoutCharacter = string.Empty;
    private string xagmanExpectedTravelLogoutLocalCharacter = string.Empty;
    private ulong xagmanExpectedTravelLogoutContentId;
    private string xagmanExpectedTravelLogoutCommand = string.Empty;
    private XagmanRole xagmanExpectedTravelLogoutRole;
    private XagmanStatus xagmanExpectedTravelLogoutStatus = XagmanStatus.Idle;
    private bool xagmanExpectedTravelLogoutTaskRunnerActive;
    private bool xagmanExpectedTravelLogoutSawBusy;
    private bool? xagmanDropboxAutoAcceptState;
    private string xagmanLastTradeSafetyFailure = string.Empty;
    private bool xagmanTonyOpportunisticSellArmed = true;
    private bool xagmanObservedDropboxBusy;
    private bool xagmanTonyObservedOwnerWork;
    private bool xagmanTonyRotationRequestedByOwnerStandby;
    private string xagmanLastConsumedOwnerStandbyRotationRequestKey = string.Empty;
    private bool xagmanOwnerStartRequested;
    private bool xagmanOwnerStandbyPending;
    private string xagmanOwnerStandbyTonyCharacter = string.Empty;
    private string xagmanOwnerStandbyTonyInstanceId = string.Empty;
    private bool xagmanOwnerStandbyPriorTonyCallReleased;
    private bool xagmanOwnerPauseForTonyRotationRequested;
    private bool xagmanTonySellLocationActive;
    private uint xagmanTonySellLocationTerritoryId;
    private string xagmanTonySellLocationName = string.Empty;
    private Vector3 xagmanTonySellLocationPosition;
    private readonly Dictionary<string, int> xagmanTradeQuantitySnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XagmanOwnerPolicyCapability> xagmanOwnerPolicyRunCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private bool xagmanOwnerPolicyRunCapabilitiesPinned;
    private bool xagmanOwnerPolicyRegistrationRefreshFailed;
    private readonly List<XagmanTradeRequestEntry> xagmanOwnerRequestedItems = new();
    private readonly Dictionary<string, (int StartingQuantity, int TargetQuantity)> xagmanFiniteTakeGoals = new(StringComparer.OrdinalIgnoreCase);
    private DateTime xagmanQueueRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
    private DateTime xagmanLastPresencePublishUtc = DateTime.MinValue;
    private DateTime xagmanLastTonyActionAtUtc = DateTime.MinValue;
    private string xagmanTonyApproachWaitPartnerKey = string.Empty;
    private DateTime xagmanTonyApproachWaitStartedAtUtc = DateTime.MinValue;
    private DateTime xagmanRecentFcReturnAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyRunStartedAtUtc = DateTime.MinValue;
    private DateTime xagmanTonyLastMeetRetryUtc = DateTime.MinValue;
    private DateTime xagmanTonyMeetCommandDeadlineUtc = DateTime.MinValue;
    private XagmanTonyMeetCommandPhase xagmanTonyMeetCommandPhase;
    private int xagmanTonyCompletedCharacters;
    private int xagmanTonyTotalCharacters;
    private int xagmanTonyMeetRetryCount;
    private int xagmanOwnerCompletedCharacters;
    private int xagmanOwnerTotalCharacters;
    private int xagmanOwnerCurrentCharacterIndex = -1;
    private int xagmanCurrentTonyIndex = -1;
    private IReadOnlyList<string> xagmanOwnerRunPlan = Array.Empty<string>();
    private IReadOnlyList<string> xagmanTonyRunPlan = Array.Empty<string>();
    private List<string> xagmanOwnerRunList = new();
    private List<string> xagmanTonyRunList = new();
    private const int XagmanCollectionFirstCoordinationProtocolRevision = 3;
    private const string XagmanGreenGcSealsName = "Green Item GC Seals";
    private const string XagmanGreenFcCreditsName = "Green Item FC Credits / Rank Progress";
    private const double XagmanCollectionBarrierWarningSeconds = 30.0;
    private const double XagmanMissingCohortPeerFailSeconds = 60.0;
    private const int XagmanRelogMaxAttempts = 3;
    private const float XagmanRelogTimeoutSeconds = 600f;
    private const double XagmanCompletionAckTimeoutSeconds = 30.0;
    private const double XagmanCompletionRebroadcastSeconds = 2.0;
    private int xagmanStartAllInFlight;
    private string xagmanRunId = string.Empty;
    private bool xagmanCollectionFirstActive;
    private bool xagmanCollectionFirstStartupModeNegotiated;
    private XagmanRunPhase xagmanRunPhase = XagmanRunPhase.Legacy;
    private bool xagmanPhaseComplete;
    private bool xagmanTravelRouteFatalError;
    private bool xagmanCompletionDirectiveAcknowledged;
    private int xagmanPhaseTotalCharacters;
    private int xagmanPhaseResolvedCharacters;
    private DateTime xagmanCollectionBarrierStartedAtUtc = DateTime.MinValue;
    private DateTime xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
    private bool xagmanCollectionBarrierWarningLogged;
    private IReadOnlyList<string> xagmanCollectionFirstOwnerFullPlan = Array.Empty<string>();
    private IReadOnlyList<string> xagmanCollectionFirstCollectionPlan = Array.Empty<string>();
    private IReadOnlyList<string> xagmanCollectionFirstRestockPlan = Array.Empty<string>();
    private readonly HashSet<string> xagmanExpectedFranchiseOwnerInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanCollectionPhaseAcknowledgedInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanRestockPhaseAcknowledgedInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> xagmanCollectionFirstFailedCharacters = new(StringComparer.OrdinalIgnoreCase);
    private string xagmanGreenSupplyValidationError = string.Empty;
    private global::XASlave.Localization.UiMessage? xagmanGreenSupplyValidationErrorUiMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    private List<string>? xagmanAetheryteNames;
    private string xagmanRecentFcReturnCharacter = string.Empty;
    private List<XagmanItemSearchEntry> xagmanItemResults = new();
    private List<XagmanItemSearchEntry>? xagmanLuminaItemCatalogCache;
    private Dictionary<string, XagmanItemSearchEntry>? xagmanItemNameLookupCache;
    private static readonly object xagmanItemHqCapabilityCacheLock = new();
    private static readonly Dictionary<uint, bool> xagmanItemHqCapabilityCache = new();
    private int xagmanItemSearchDisplayCount = XagmanItemSearchPageSize;
    private const int XagmanItemSearchPageSize = 100;
    private static readonly (string Label, XagmanItemMode Mode, XagmanItemApplicability Applicability)[] xagmanItemPolicyOptions =
    {
        (global::XASlave.Localization.UiLocale.Native("SC6402106C8212060"), XagmanItemMode.Give, XagmanItemApplicability.All),
        (global::XASlave.Localization.UiLocale.Native("SD2BA95139CB9779D"), XagmanItemMode.Give, XagmanItemApplicability.HasSubmarines),
        (global::XASlave.Localization.UiLocale.Native("SEC055F2E2CB184BA"), XagmanItemMode.Give, XagmanItemApplicability.HasRetainers),
        (global::XASlave.Localization.UiLocale.Native("S8B862BA45DCEDDF7"), XagmanItemMode.Take, XagmanItemApplicability.All),
        (global::XASlave.Localization.UiLocale.Native("S32E86C4986D01C72"), XagmanItemMode.Take, XagmanItemApplicability.HasSubmarines),
        (global::XASlave.Localization.UiLocale.Native("S4C7EAC43BD75E3B1"), XagmanItemMode.Take, XagmanItemApplicability.HasRetainers),
        (global::XASlave.Localization.UiLocale.Native("SD05E07B7C14E596A"), XagmanItemMode.Balance, XagmanItemApplicability.All),
        (global::XASlave.Localization.UiLocale.Native("SE0BB0CFDD549B742"), XagmanItemMode.Balance, XagmanItemApplicability.HasSubmarines),
        (global::XASlave.Localization.UiLocale.Native("SB7EB8A42DEB3B3A2"), XagmanItemMode.Balance, XagmanItemApplicability.HasRetainers),
        (global::XASlave.Localization.UiLocale.Native("S2EC76BC0576DB080"), XagmanItemMode.TopUp, XagmanItemApplicability.All),
        (global::XASlave.Localization.UiLocale.Native("S909DBA6D731A8115"), XagmanItemMode.TopUp, XagmanItemApplicability.HasSubmarines),
        (global::XASlave.Localization.UiLocale.Native("S28B6E331FEA57A34"), XagmanItemMode.TopUp, XagmanItemApplicability.HasRetainers),
    };
    private static readonly string[] xagmanItemPolicyLabels = xagmanItemPolicyOptions
        .Select(option => option.Label)
        .ToArray();
    private static readonly (string Label, XagmanItemMode Mode, XagmanItemApplicability Applicability)[] xagmanGreenItemPolicyOptions =
        xagmanItemPolicyOptions
            .Where(option => option.Mode == XagmanItemMode.TopUp)
            .ToArray();
    private static readonly string[] xagmanGreenItemPolicyLabels = xagmanGreenItemPolicyOptions
        .Select(option => option.Label)
        .ToArray();
    private static readonly Regex xagmanTeamcraftItemLineRegex = new(
        @"^\s*(?<quantity>[0-9][0-9,]*)\s*x\s+(?<itemName>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions xagmanItemListJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private enum XagmanTradeFailureKind
    {
        None,
        TooFarAway,
        TradeCanceled,
        TradeNotComplete,
    }
    private static readonly (XagmanTradeFailureKind Kind, string Text)[] xagmanTradeFailureTexts =
    {
        (XagmanTradeFailureKind.TooFarAway, "Too far away"),
        (XagmanTradeFailureKind.TradeCanceled, "Trade canceled"),
        (XagmanTradeFailureKind.TradeNotComplete, "Trade not complete"),
    };
    private const int XagmanTonySellGilLimit = 990_000_000;
    private const float XagmanTonySellVendorStopDistance = 0f;
    private const float XagmanTonySellVendorRandomRadius = 0.5f;
    private const float XagmanTonySellOwnerPreApproachStopDistance = 2f;
    private const float XagmanTonyCalledCoordinateStopDistance = 0.5f;
    private const float XagmanTonyLivePositionRandomRadius = 0.5f;
    private const float XagmanTonySellDestinationArrivalTolerance = 1.5f;
    private const double XagmanTonyApproachWaitTimeoutSeconds = 600.0;
    private const string XagmanTonySellGilCapText = "Unable to complete transaction. You cannot carry any more gil.";
    private static readonly Vector4 XagmanTonySellSupportedLocationColor = new(0.4f, 1.0f, 0.4f, 1.0f);
    // Territory IDs mirror AetheryteData.AetheryteToZoneIdFallback for the supported meet aetherytes.
    private static readonly Dictionary<uint, XagmanTonySellDestination> xagmanTonySellDestinationsByTerritoryId = new()
    {
        [129] = new("Limsa Lominsa Lower Decks", "Limsa Lominsa Lower Decks", "Bango Zango", new Vector3(-63.306f, 18.000f, 7.785f)),
        [134] = new("Summerford Farms", "Middle La Noscea", "Merchant & Mender", new Vector3(198.881f, 98.496f, -205.225f)),
        [135] = new("Moraby Drydocks", "Lower La Noscea", "Merchant & Mender", new Vector3(198.738f, 14.096f, 676.284f)),
        [132] = new("New Gridania", "New Gridania", "Maisenta", new Vector3(11.118f, 0.100f, 3.024f)),
        [148] = new("Bentbranch Meadows", "Central Shroud", "Merchant & Mender", new Vector3(16.428f, -8.012f, -12.510f)),
        [152] = new("The Hawthorne Hut", "East Shroud", "Merchant & Mender", new Vector3(-211.940f, 2.209f, 298.551f)),
        [130] = new("Ul'dah - Steps of Nald", "Ul'dah - Steps of Nald", "Rianne", new Vector3(-70.080f, 4.612f, -109.449f)),
        [140] = new("Horizon", "Western Thanalan", "Independent Armorfitter", new Vector3(53.286f, 45.143f, -231.799f)),
        [141] = new("Black Brush Station", "Central Thanalan", "Merchant & Mender", new Vector3(-10.591f, -2.048f, -151.254f)),
    };
    private static readonly IReadOnlyList<string> xagmanTonySellSupportedLocationNames = xagmanTonySellDestinationsByTerritoryId
        .Values
        .Select(destination => destination.LocationName)
        .ToList();
    private sealed class XagmanItemSearchEntry
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool CanBeHq { get; init; }
    }
    private sealed class XagmanTonySellDestination
    {
        public XagmanTonySellDestination(string locationName, string zoneName, string npcName, Vector3 position)
        {
            LocationName = locationName;
            ZoneName = zoneName;
            NpcName = npcName;
            Position = position;
        }

        public string LocationName { get; }
        public string ZoneName { get; }
        public string NpcName { get; }
        public Vector3 Position { get; }
    }
    private sealed class XagmanItemListPackage
    {
        public int SchemaVersion { get; set; } = 3;
        public string ListId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; }
        public List<XagmanItemEntry> Items { get; set; } = new();
    }
    private sealed class XagmanOwnerPolicyCapability
    {
        public bool IsKnown { get; init; }
        public bool HasRetainers { get; init; }
        public bool HasSubmarines { get; init; }
    }
    private sealed class XagmanDbSearchMatch
    {
        public string CharacterNameWorld { get; init; } = string.Empty;
        public string ContainerName { get; init; } = string.Empty;
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public int Quantity { get; init; }
        public bool IsHq { get; init; }
    }
    private enum XagmanMatchSelectionTarget
    {
        Tony,
        FranchiseOwner,
    }
    private enum XagmanAutoRetainerMatchScope
    {
        All,
        Retainers,
        Submarines,
        WithoutRetainers,
        WithoutSubmarines,
    }
    private sealed class XagmanMatchQueryRequest
    {
        public string Query { get; init; } = string.Empty;
        public HashSet<string> ItemKeys { get; init; } = new(StringComparer.Ordinal);
    }
    private sealed class XagmanPendingMatchSelectionRequest
    {
        public XagmanMatchSelectionTarget Target { get; init; }
        public XagmanAutoRetainerMatchScope AutoRetainerScope { get; init; } = XagmanAutoRetainerMatchScope.All;
        public string ItemsKey { get; init; } = string.Empty;
        public HashSet<string> VisibleCharacterKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<XagmanMatchQueryRequest> Queries { get; init; } = new();
        public HashSet<string> MatchKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int NextQueryIndex { get; set; }
    }
    private XagmanPendingMatchSelectionRequest? xagmanPendingMatchSelection;
    private int xagmanManualXaDatabasePullRunning;
    private void DrawXagmanTask()
    {
        var cfg = plugin.Configuration;
        xagmanSubmarineRates = SubmarineBuildSupply.GetEffectiveRates(cfg.SubmarineBuildConsumptionRates);
        var runner = plugin.TaskRunner;
        if (!xagmanPendingHubEndpointInitialized)
        {
            xagmanPendingHubAddress = XagmanPeerService.NormalizeHubAddress(cfg.XagmanHubAddress);
            xagmanPendingHubPort = XagmanPeerService.NormalizePort(cfg.XagmanHubPort);
            xagmanPendingHubEndpointInitialized = true;
        }
        xagmanPendingHubPort = XagmanPeerService.NormalizePort(xagmanPendingHubPort);
        ImGui.TextColored(new Vector4(0.8f, 0.6f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S0A1690A11A18319C"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S14EB7118562A0DF3"));
        if (global::XASlave.Localization.UiNative.SmallButton($"{(cfg.XagmanWarningDetailsExpanded ? global::XASlave.Localization.UiLocale.Native("SD2FCCBC3142FA82C") : global::XASlave.Localization.UiLocale.Native("S272DECBEECF06DB1"))}##xagmanWarningDetailsToggle"))
        {
            cfg.XagmanWarningDetailsExpanded = !cfg.XagmanWarningDetailsExpanded;
            cfg.Save();
        }
        if (cfg.XagmanWarningDetailsExpanded)
            DrawXagmanBetaWarningBlock();
        ImGui.Spacing();
        var arOk = plugin.IpcClient.IsAutoRetainerAvailable();
        var lsOk = plugin.IpcClient.IsLifestreamAvailable();
        var xaDbOk = plugin.IpcClient.IsXaDatabaseAvailable();
        var dbxOk = plugin.IpcClient.IsDropboxAvailable();
        var vnavOk = plugin.IpcClient.VnavIsReady();
        var allRequired = arOk && lsOk && xaDbOk && dbxOk && vnavOk;
        DrawTaskPluginStatus(true);
        ImGui.Text(global::XASlave.Localization.UiLocale.Get("SF4A76B7F75502CAC"));
        ImGui.SameLine();
        ImGui.TextColored(dbxOk ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 0.4f, 0.4f, 1.0f), ((dbxOk ? global::XASlave.Localization.UiMessage.Key("S3F12372CAE3A5EBA") : global::XASlave.Localization.UiMessage.Key("S53BA73B619EECDF9"))).Display);
        ImGui.Spacing();
        var role = cfg.XagmanRole;
        var onh = cfg.XagmanOutsideNetworkHelper;
        if (global::XASlave.Localization.UiNative.RadioButton((global::XASlave.Localization.UiLocale.Native("SC5A8D95238CD3EE8") + "##xagmanRoleTony"), role == XagmanRole.Tony))
        {
            cfg.XagmanRole = XagmanRole.Tony;
            InvalidateXagmanTradeCapacityForecast();
            cfg.Save();
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.RadioButton((global::XASlave.Localization.UiLocale.Native("SAD5B29877C321F10") + "##xagmanRoleOwner"), role == XagmanRole.FranchiseOwner))
        {
            cfg.XagmanRole = XagmanRole.FranchiseOwner;
            InvalidateXagmanTradeCapacityForecast();
            cfg.Save();
        }
        ImGui.Spacing();
        DrawXagmanOnhModeCheckbox(cfg);
        onh = cfg.XagmanOutsideNetworkHelper;
        ImGui.Spacing();
        if (global::XASlave.Localization.UiNative.SmallButton($"{(cfg.XagmanRoleInstructionsExpanded ? global::XASlave.Localization.UiLocale.Native("S5C5DBEDE5B63D9B4") : global::XASlave.Localization.UiLocale.Native("S111C113A082681EA"))}##xagmanSetupGuideToggle"))
        {
            cfg.XagmanRoleInstructionsExpanded = !cfg.XagmanRoleInstructionsExpanded;
            cfg.Save();
        }
        if (cfg.XagmanRoleInstructionsExpanded)
            DrawXagmanRoleInstructions(cfg.XagmanRole);
        ImGui.Spacing();
        if (!onh)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S35CA56638CB54F54", plugin.XagmanPeers.HubEndpoint));
            ImGui.SetNextItemWidth(Scale(140f));
            if (global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S56EF8F20955F2564") + "##xagmanAddress"), ref xagmanPendingHubAddress, 128))
                xagmanPendingHubAddress = xagmanPendingHubAddress.Trim();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Scale(72f));
            if (global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("S72E9A59F5A1D6289") + "##xagmanPort"), ref xagmanPendingHubPort))
                xagmanPendingHubPort = XagmanPeerService.NormalizePort(xagmanPendingHubPort);
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S31E392D1C0378BEC") + "##xagmanHubEndpoint")))
            {
                var appliedEndpoint = plugin.ApplyXagmanHubEndpoint(xagmanPendingHubAddress, xagmanPendingHubPort);
                xagmanPendingHubAddress = appliedEndpoint.Address;
                xagmanPendingHubPort = appliedEndpoint.Port;
            }
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SBE6E5191AA0ACD01"));
            var xagmanPeerConnectionsEnabled = plugin.XagmanPeers.IsStarted;
            ImGui.Spacing();
            var normalizedPendingHubAddress = XagmanPeerService.NormalizeHubAddress(xagmanPendingHubAddress);
            var xagmanHasPendingHubEndpointChange = xagmanPendingHubPort != cfg.XagmanHubPort
                || !normalizedPendingHubAddress.Equals(XagmanPeerService.NormalizeHubAddress(cfg.XagmanHubAddress), StringComparison.OrdinalIgnoreCase);
            if (global::XASlave.Localization.UiNative.Button(xagmanPeerConnectionsEnabled
                    ? (global::XASlave.Localization.UiLocale.Native("SACFC5BE785A9BB3D") + "##xagmanPeerConnectionToggle")
                    : (global::XASlave.Localization.UiLocale.Native("S1A2303EDE07493AC") + "##xagmanPeerConnectionToggle")))
            {
                plugin.SetXagmanPeerConnectionsEnabled(!xagmanPeerConnectionsEnabled);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(((xagmanPeerConnectionsEnabled ? global::XASlave.Localization.UiMessage.Key("SCFA749297EB08351") : (xagmanHasPendingHubEndpointChange ? global::XASlave.Localization.UiMessage.Key("S4A0F59C132F0D876") : global::XASlave.Localization.UiMessage.Key("S8A64C82FD10F7DF0")))).Display);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.XagmanPeers.LastStatus);
        }
        ImGui.Spacing();
        var arConfigExists = plugin.ArConfigReader.ConfigFileExists();
        using (ImRaii.Disabled(!arConfigExists))
        {
            if (cfg.XagmanRole == XagmanRole.Tony)
            {
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S74BC538828226264") + "##xagmanImportTony")))
                    ImportXagmanTonyCharactersFromAr();
            }
            else
            {
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S74BC538828226264") + "##xagmanImportOwner")))
                    ImportXagmanFranchiseCharactersFromAr();
            }
        }
        DrawHonorAutoRetainerExclusions("xagman", cfg.XagmanHonorArExclusions,
            value => cfg.XagmanHonorArExclusions = value,
            PruneXagmanAutoRetainerExclusions,
            () =>
            {
                ClearXagmanMatchingSelectionCaches();
                StopXagmanTonyAutoSelection(global::XASlave.Localization.UiMessage.Key("S1F5F86B7FA43CC05"));
                InvalidateXagmanTradeCapacityForecast();
            });
        var manualXaDatabasePullRunning = System.Threading.Volatile.Read(ref xagmanManualXaDatabasePullRunning) != 0;
        using (ImRaii.Disabled(manualXaDatabasePullRunning))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S658431DD798692FF") + "##xagmanPullXa")))
            {
                if (Plugin.PlayerState.IsLoaded)
                    BeginXagmanXaDatabaseRefreshAndPull();
                else
                    PullXaDatabaseInfo(ClearXagmanMatchingSelectionCaches);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(((manualXaDatabasePullRunning ? global::XASlave.Localization.UiMessage.Key("S6BCF21682E7F4FBF") : (Plugin.PlayerState.IsLoaded ? global::XASlave.Localization.UiMessage.Key("SF3152FE8E728F646") : global::XASlave.Localization.UiMessage.Key("SD25B66C2217877F1")))).Display);
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(GetXagmanStatusColor(arImportStatus), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            DrawXagmanWorldSelector(cfg);
            DrawXagmanAetheryteSelector(cfg);
            DrawXagmanServerMatchingPicker(cfg);
            var tonyGilMinimum = Math.Max(0, cfg.XagmanTonyGilMinimum);
            ImGui.SetNextItemWidth(Scale(140f));
            if (global::XASlave.Localization.UiNative.InputInt((global::XASlave.Localization.UiLocale.Native("S35E80134A5A3DBA9") + "##xagmanTonyGilMinimum"), ref tonyGilMinimum))
            {
                cfg.XagmanTonyGilMinimum = Math.Max(0, tonyGilMinimum);
                cfg.Save();
            }
            var sellWhenInventoryFull = cfg.XagmanSellWhenInventoryFull;
            if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SCBB86EAC61703012") + "##xagmanSellWhenInventoryFull"), ref sellWhenInventoryFull))
            {
                cfg.XagmanSellWhenInventoryFull = sellWhenInventoryFull;
                cfg.Save();
            }
            ImGui.SameLine();
            ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S86303AA595DE54D3")).Display);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    global::XASlave.Localization.UiLocale.FormatKey("SBE326A5C7179107F", GetXagmanTonySellSupportedLocationTooltipText()));
            }
            if (sellWhenInventoryFull)
            {
                var directSell = cfg.XagmanUseDirectNpcSell;
                if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SA4784BDA652C25B2") + "##xagmanDirectSell"), ref directSell))
                { cfg.XagmanUseDirectNpcSell = directSell; cfg.Save(); }
                ImGui.SameLine();
                ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S86303AA595DE54D3")).Display);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S24716020E6761986"));
                }
            }
            ImGui.Spacing();
            if (cfg.XagmanServerMatchingEnabled)
            {
                var configuredServers = GetXagmanConfiguredSweepServers();
                ImGui.TextDisabled(((configuredServers.Count > 0 && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte) ? global::XASlave.Localization.UiMessage.Format("SA15ADD67CCE17A3D", configuredServers.Count, cfg.XagmanTargetAetheryte) : global::XASlave.Localization.UiMessage.Key("S467ACD02813FCC8B"))).Display);
            }
            else if (!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld))
            {
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S98442318ED153345", GetPrepLogisticsDestinationLabel(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte)));
            }
            else
            {
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA1DBA6095619BD75"));
            }
        }
        else if (onh)
        {
            // In Outside Network Helper mode each side sets its own meet spot (there is no Tony relay).
            DrawXagmanWorldSelector(cfg);
            DrawXagmanAetheryteSelector(cfg);
            ImGui.Spacing();
            ImGui.TextDisabled(((!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) ? global::XASlave.Localization.UiMessage.Format("S98442318ED153345", GetPrepLogisticsDestinationLabel(cfg.XagmanTargetWorld, cfg.XagmanTargetAetheryte)) : global::XASlave.Localization.UiMessage.Key("SA1DBA6095619BD75"))).Display);
        }
        else
        {
            var meetWorld = xagmanRunning ? xagmanActiveMeetWorld : string.Empty;
            var meetAetheryte = xagmanRunning ? xagmanActiveMeetAetheryte : string.Empty;
            if (string.IsNullOrWhiteSpace(meetWorld))
                TryGetXagmanMeetDestinationForOwner(out meetWorld, out meetAetheryte);
            ImGui.Spacing();
            ImGui.TextDisabled(((string.IsNullOrWhiteSpace(meetWorld) ? global::XASlave.Localization.UiMessage.Key("S89444D268E0723F5") : global::XASlave.Localization.UiMessage.Format("S98442318ED153345", GetPrepLogisticsDestinationLabel(meetWorld, meetAetheryte)))).Display);
        }
        ImGui.Spacing();
        var autoReturnToFc = cfg.XagmanAutoReturnToFc;
        DrawXagmanCustomMeetingCoordinates(cfg);
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SF60569A1AE53A5DE") + "##xagmanReturnFc"), ref autoReturnToFc))
        {
            cfg.XagmanAutoReturnToFc = autoReturnToFc;
            cfg.Save();
        }
        var ignoreGilInMatchSelection = cfg.XagmanIgnoreGilInMatchingSelection;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S8E3A49EFB547FB94") + "##xagmanIgnoreGilMatch"), ref ignoreGilInMatchSelection))
        {
            cfg.XagmanIgnoreGilInMatchingSelection = ignoreGilInMatchSelection;
            cfg.Save();
        }
        var refuseTradesWhenIdle = cfg.XagmanRefuseTradesWhenIdle;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SB4D89239D30EE020") + "##xagmanRefuseTradesWhenIdle"), ref refuseTradesWhenIdle))
        {
            cfg.XagmanRefuseTradesWhenIdle = refuseTradesWhenIdle;
            cfg.Save();
            if (!ReconcileXagmanTradeSafetyOption() && xagmanTradeSafetySessionActive)
            {
                plugin.TaskRunner.AddLog("Xagman: stopping because the updated Refuse Trades When Idle state could not be applied safely.");
                StopXagmanTask();
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Key("SA47F65ACE2D5BA65") : global::XASlave.Localization.UiMessage.Key("S5D1898460F9F5BDE")));
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                global::XASlave.Localization.UiLocale.Get("S0A3FD6E02EA79EE2"));
        }
        if (cfg.XagmanRefuseTradesWhenIdle || xagmanTradeSafetySessionActive)
        {
            var refusalStatus = plugin.AutoRefuseTrade.XagmanOverride switch
            {
                XagmanTradeRefusalOverride.IdleDemand when plugin.AutoRefuseTrade.IsEnabled => global::XASlave.Localization.UiMessage.Key("S4173574456EF69F3").Display,
                XagmanTradeRefusalOverride.IdleDemand => global::XASlave.Localization.UiMessage.Key("S2F2465A2D5F04FE0").Display,
                XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression => global::XASlave.Localization.UiMessage.Key("S393A8F8997CD1F3C").Display,
                _ when xagmanTradeSafetySessionActive && plugin.AutoRefuseTrade.IsEnabled => global::XASlave.Localization.UiMessage.Key("SA391102E1D7302B1").Display,
                _ when xagmanTradeSafetySessionActive => global::XASlave.Localization.UiMessage.Key("SA19897FB9BB59978").Display,
                _ => global::XASlave.Localization.UiMessage.Key("S8F61BA72C6D08D46").Display,
            };
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("S7F883B8692773228", refusalStatus));
        }
        ImGui.Spacing();
        var regionFilter = cfg.XagmanRegionFilter;
        if (DrawRegionFilterCombo((global::XASlave.Localization.UiLocale.Native("SD3A008EF1335692D") + "##xagmanRegion"), ref regionFilter))
        {
            cfg.XagmanRegionFilter = regionFilter;
            cfg.Save();
        }
        ImGui.Spacing();
        var selectedTonyChars = GetSelectedXagmanTonyCharacters();
        var selectedFranchiseChars = GetSelectedXagmanFranchiseCharacters();
        var localXagmanPeerConnected = plugin.XagmanPeers.IsConnected;
        var hasMeetDestination = HasXagmanServerMatchingMeetConfig()
            || (!string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte));
        // Outside Network Helper needs no peer hub and no Server Matching - just this side's own
        // meet world + location (each partner picks their own and meets in game).
        var hasOnhMeet = !string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte);
        var hasGreenValueSelectors = HasXagmanGreenValueSelectors(cfg.XagmanItems);
        var canStartTony = !xagmanRunning && allRequired && selectedTonyChars.Count > 0
            && !(onh && hasGreenValueSelectors)
            && (onh ? hasOnhMeet : (localXagmanPeerConnected && hasMeetDestination));
        var canStartOwners = !xagmanRunning && allRequired && selectedFranchiseChars.Count > 0
            && !(onh && hasGreenValueSelectors)
            && (onh ? hasOnhMeet : localXagmanPeerConnected);
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            var started = DrawPriorityTaskActionButton(
                SlaveTask.Xagman,
                (string.Format(global::XASlave.Localization.UiLocale.Native("S6FE36D883C6BE04C"), selectedTonyChars.Count) + $"##xagmanTonyStart"),
                canStartTony,
                onh ? (SysAction)StartXagmanOnhRun : StartXagmanTonyTask,
                onh
                    ? (hasGreenValueSelectors
                        ? global::XASlave.Localization.UiLocale.Get("S52E2A9B2856BFE07")
                        : !allRequired
                        ? global::XASlave.Localization.UiLocale.Get("S0A4C6C0B785E7FB1")
                        : !hasOnhMeet
                            ? global::XASlave.Localization.UiLocale.Get("S9E70E13B0B474D59")
                            : global::XASlave.Localization.UiLocale.Get("S8C300AF250DC48A3"))
                    : !allRequired
                        ? global::XASlave.Localization.UiLocale.Get("S0A4C6C0B785E7FB1")
                        : !localXagmanPeerConnected
                            ? global::XASlave.Localization.UiLocale.Get("SB651A90292565045")
                        : !hasMeetDestination
                            ? global::XASlave.Localization.UiLocale.Get("S6AA8A6AED71D256F")
                            : global::XASlave.Localization.UiLocale.Get("S8C300AF250DC48A3"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("S6FE36D883C6BE04C", selectedTonyChars.Count));
            DrawXagmanAutoCleanQueueOption();
            if (started)
                AutoOpenTaskLogIfVerbose(ref xagmanShowLog);

            if (!onh)
            {
                // Add peer control buttons
                ImGui.Spacing();
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SC4AB1D2AEF57E1E7") + "##xagmanStartAllPeers")))
                {
                    StartAllXagmanPeers();
                }
                ImGui.SameLine();
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S404B4D3917C0CFEA") + "##xagmanStopAllPeers")))
                {
                    StopAllXagmanPeers();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SE3C2F1C384BD2E37"));
                ImGui.SameLine();
                if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SD6DF469A85E67BE1") + "##xagmanStopAllClientsAndResults")))
                {
                    StopAllXagmanClientsAndResults();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S341C72442B2D7241"));

                if (CanRotateXagmanTonyInCurrentScope())
                {
                    ImGui.SameLine();
                    if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SBD6DAF146EF6D533") + "##xagmanRotateTony")))
                        RotateXagmanTony();
                }
            }
        }
        else
        {
            var started = DrawPriorityTaskActionButton(
                SlaveTask.Xagman,
                (string.Format(global::XASlave.Localization.UiLocale.Native("S5AF33D7B4B186DD9"), selectedFranchiseChars.Count) + $"##xagmanOwnerStart"),
                canStartOwners,
                onh ? (SysAction)StartXagmanOnhRun : () => StartXagmanFranchiseTask(),
                onh
                    ? (hasGreenValueSelectors
                        ? global::XASlave.Localization.UiLocale.Get("S52E2A9B2856BFE07")
                        : !allRequired
                        ? global::XASlave.Localization.UiLocale.Get("S0A4C6C0B785E7FB1")
                        : !hasOnhMeet
                            ? global::XASlave.Localization.UiLocale.Get("S9E70E13B0B474D59")
                            : global::XASlave.Localization.UiLocale.Get("S36EDE4A8B3BF6619"))
                    : !allRequired
                        ? global::XASlave.Localization.UiLocale.Get("S0A4C6C0B785E7FB1")
                        : !localXagmanPeerConnected
                            ? global::XASlave.Localization.UiLocale.Get("SB651A90292565045")
                            : global::XASlave.Localization.UiLocale.Get("S36EDE4A8B3BF6619"), displayButtonLabel: global::XASlave.Localization.UiLocale.FormatKey("S5AF33D7B4B186DD9", selectedFranchiseChars.Count));
            DrawXagmanAutoCleanQueueOption();
            if (started)
                AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        }
        if (xagmanRunning)
        {
            var (total, completed) = GetXagmanDisplayProgressCounts();
            var progress = total > 0 ? (float)completed / total : 0f;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S20E02708F89F8F8C", xagmanStatus, global::XASlave.Localization.UiMessage.Resolve(ref xagmanStatusTextUiMessage, xagmanStatusText)));
            if (total > 0)
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{completed}/{total}");
            if (!string.IsNullOrWhiteSpace(runner.CurrentItemLabel))
                ImGui.TextDisabled(runner.CurrentItemLabelMessage.Display);
            ImGui.Spacing();
        }
        DrawXagmanProcessingLists(runner);
        ImGui.Spacing();
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            DrawXagmanTonyTable(cfg);
            DrawXagmanTradeCapacityForecast(cfg);
        }
        else
        {
            DrawXagmanFranchiseTable(cfg);
            DrawXagmanOwnerForecasts(cfg);
        }
        DrawXagmanGreenValueForecasts(cfg);
        DrawXagmanSubmarineRates(cfg);
        if (onh)
        {
            ImGui.Spacing();
            DrawXagmanOnhExportRow(cfg);
        }
        ImGui.Spacing();
        if (cfg.XagmanRole == XagmanRole.Tony)
        {
            if (onh)
                DrawXagmanOnhFriendListSection(cfg);
            else
                DrawXagmanItemSection(global::XASlave.Localization.UiLocale.Get("S0D55051FC2DE3A39"), cfg.XagmanTonyItems, "xagmanTonyItems", searchOnly: true, allowGil: false);
        }
        else
        {
            DrawXagmanItemSection(global::XASlave.Localization.UiLocale.Get("S9B98DDD4B1568472"), cfg.XagmanItems, "xagmanItems");
            if (onh && hasGreenValueSelectors)
                ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.25f, 1.0f), global::XASlave.Localization.UiLocale.Get("S3168A58264B0E7E8"));
            if (onh)
            {
                ImGui.Spacing();
                DrawXagmanOnhFriendListSection(cfg);
            }
        }
        ImGui.Spacing();
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S13D2848ADEE8E26E") + "##xagmanQueue"), ref xagmanShowQueue);
        if (xagmanShowQueue)
        {
            if (onh)
                DrawXagmanOnhQueueView(cfg);
            else
                DrawXagmanQueueTable();
        }
        ImGui.Spacing();
        if (!onh)
        {
            global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S8C0A37BFE0D06573") + "##xagmanPeers"), ref xagmanShowPeers);
            if (xagmanShowPeers)
                DrawXagmanPeersTable();
            ImGui.Spacing();
        }
        var logoutOnComplete = cfg.XagmanLogoutOnComplete;
        var killGameOnComplete = cfg.XagmanKillGameOnComplete;
        var enableArMultiOnComplete = cfg.XagmanEnableArMultiOnComplete;
        if (DrawSharedCompletionAndLogFooter("xagman", "xagman", ref logoutOnComplete, ref killGameOnComplete, ref enableArMultiOnComplete, ref xagmanShowLog, runner))
        {
            cfg.XagmanLogoutOnComplete = logoutOnComplete;
            cfg.XagmanKillGameOnComplete = killGameOnComplete;
            cfg.XagmanEnableArMultiOnComplete = enableArMultiOnComplete;
            cfg.Save();
        }
    }

    private void DrawXagmanBetaWarningBlock()
    {
        var warningColor = new Vector4(1.0f, 0.45f, 0.25f, 1.0f);
        var infoColor = new Vector4(1.0f, 0.85f, 0.35f, 1.0f);

        ImGui.TextColored(warningColor, global::XASlave.Localization.UiLocale.Get("S1D90665758BA6903"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SDA9CB76F7C66CAA7"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SE358E0AF6A531566"));
        ImGui.Spacing();
        ImGui.TextColored(infoColor, global::XASlave.Localization.UiLocale.Get("SB3C0DDC8947BFA71"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S159BA5E249706E5C"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S19C3F238D7D25851"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SCF7C8FFFD7533741"));
        ImGui.Spacing();
        ImGui.TextColored(infoColor, global::XASlave.Localization.UiLocale.Get("S37A9182EF3087DC8"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S534D3B2DEEE93D1F"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S16EC2729B4AF4F6E"));
    }

    private void DrawXagmanRoleInstructions(XagmanRole role)
    {
        var sectionColor = new Vector4(0.4f, 0.8f, 1.0f, 1.0f);
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SE81D3197447638E5"));
        if (role == XagmanRole.Tony)
        {
            ImGui.TextColored(sectionColor, global::XASlave.Localization.UiLocale.Get("S9C899168376281C8"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S42119AF5126B81A6"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S70081452E9ABEBFC"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S274CE7AF6374379C"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S71DFC676F35DD52B"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S8AEA6C7A1DE6E971"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S4CA8491BC6423C27"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S11D3E001BF606E15"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SB497CA734953A84E"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S3A9E9630F85B1913"));
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SA0F4DD110191A18E"));
            return;
        }

        ImGui.TextColored(sectionColor, global::XASlave.Localization.UiLocale.Get("SC86D5B89CCA3DDD9"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S7449BCB2B03A5B4A"));
        ImGui.Indent();
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S4426AA36977E437B"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SCCCF5B2D861B9923"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SBB6635E4E40A075F"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S0159BB14F7671B74"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SD4632FB72A6E3E0E"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S5BA1B6E1E420E64D"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S07FA7AA7916FB21D"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S028258E1192AACC6"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S260E158CF0D42505"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF92F6105A2F142A3"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S6C4A1F8427CB6EAC"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S5153A4A9AFBC33DD"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S1C416F7C4E0EB3F9"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SF91A275517F55491"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S0810940C02949B81"));
        ImGui.Unindent();
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S8684FC7630BD74B2"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S85A2922684B3447E"));
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S55509511107989E6"));
    }

    private void DrawXagmanTonyTable(Configuration cfg)
    {
        var chars = cfg.XagmanTonyCharacters;
        var charInfo = cfg.ReloggerCharacterInfo;
        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("S09DFAE3D07430B24"), global::XASlave.Localization.UiLocale.FormatKey("S261FA25B9E69A2B0", chars.Count), "xagmanTonyAnonymize");
        var anonymizeTonyCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S726E20DB00096A31") + "##xagmanTonyAll")))
            SelectVisibleXagmanTonyCharacters();
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S83B12C2216EFB4FD") + "##xagmanTonyNone")))
        {
            xagmanTonySelectedIndices.Clear();
            InvalidateXagmanTradeCapacityForecast();
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S75D7036745455F64") + "##xagmanTonyMatching")))
            SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.All);
        var tonyMatchingButtonHovered = ImGui.IsItemHovered();
        using (var imguiScope808 = ImRaii.ContextPopupItem("##xagmanTonyMatchingContext"))
        if (imguiScope808)
        {
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SE0DCC8DAB98DEDD8")))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Retainers);
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SF196D8C4A5BAA603")))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Submarines);
            ImGui.Separator();
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SE87B93418ADE7971")))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutRetainers);
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SB3A4CF3453680810")))
                SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutSubmarines);

        }
        if (tonyMatchingButtonHovered)
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SB2D7E684D684571D"));
        if (TryGetLoggedInXagmanCurrentCharacter(out var currentTonyCharacter))
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S94DB1ED4DFBA7E2A") + "##xagmanTonyCurrent")))
                SelectXagmanCurrentCharacter(XagmanMatchSelectionTarget.Tony, currentTonyCharacter);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SAD8B8917FC47678C"));
        }
        ImGui.SameLine();
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SA343573832C8F9FD") + "##xagmanTonySelOnly"), ref xagmanTonyShowOnlySelected);
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanTonySearch", global::XASlave.Localization.UiLocale.Get("S84ADF5C1B3F29058"), ref xagmanTonySearchFilter, 128);
        DrawXagmanTonyAutoSelection();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S91A6789C4B36A6C6"));
        using (var imguiScope836 = ImRaii.Table("XagmanTonyTable.ColumnsV4", 14,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody,
            ScaledVector(0f, 175f)))
        if (imguiScope836)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide, 0f, (uint)XagmanRosterColumn.Character);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SD3A008EF1335692D"), ImGuiTableColumnFlags.WidthFixed, Scale(65f), (uint)XagmanRosterColumn.Region);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S730DA5D73C76044B"), ImGuiTableColumnFlags.WidthFixed, Scale(95f), (uint)XagmanRosterColumn.DataCenter);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S5733E35FD7609001"), ImGuiTableColumnFlags.WidthFixed, Scale(95f), (uint)XagmanRosterColumn.Homeworld);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S758F487FE8E9FCC5"), ImGuiTableColumnFlags.WidthFixed, Scale(80f), (uint)XagmanRosterColumn.Inventory);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1C41C2A178AA300D"), ImGuiTableColumnFlags.WidthFixed, Scale(95f), (uint)XagmanRosterColumn.Gil);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFBBBF4992FA5817C"), ImGuiTableColumnFlags.WidthFixed, Scale(90f), (uint)XagmanRosterColumn.Treasure);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1CD93608DEB92CF9"), ImGuiTableColumnFlags.WidthFixed, Scale(50f), (uint)XagmanRosterColumn.Kits);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCA604D552D44CFD4"), ImGuiTableColumnFlags.WidthFixed, Scale(55f), (uint)XagmanRosterColumn.Tanks);
            global::XASlave.Localization.UiNative.TableSetupColumn((global::XASlave.Localization.UiLocale.Native("S3F91391122918036") + "##xagmanTonyRetainers"), ImGuiTableColumnFlags.WidthFixed, Scale(72f), (uint)XagmanRosterColumn.Retainers);
            global::XASlave.Localization.UiNative.TableSetupColumn((global::XASlave.Localization.UiLocale.Native("S358FD9EED262500E") + "##xagmanTonySubmarines"), ImGuiTableColumnFlags.WidthFixed, Scale(84f), (uint)XagmanRosterColumn.Submarines);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S5A79B9596F4EDE06"), ImGuiTableColumnFlags.WidthFixed, Scale(65f), (uint)XagmanRosterColumn.Restock);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S6A3FE8876B563B46"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, Scale(240f), (uint)XagmanRosterColumn.Builds);
            global::XASlave.Localization.UiNative.TableSetupColumn((global::XASlave.Localization.UiLocale.Native("SE2D0A54968EAD24E") + "##xagmanTonyDelete"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, Scale(30f), (uint)XagmanRosterColumn.Delete);
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

            var filtered = new List<(int OrigIdx, string CharacterName, string World, string Region, string DataCenter, ReloggerCharacterData? Info)>();
            for (var i = 0; i < chars.Count; i++)
            {
                var entry = chars[i];
                var charName = entry.CharacterNameWorld;
                var world = GetWorldFromKey(charName);
                var worldInfo = WorldData.GetByName(world);
                if (!IsXagmanTonyCharacterVisible(cfg, entry, i))
                    continue;
                charInfo.TryGetValue(charName, out var info);
                filtered.Add((i, charName, world, worldInfo?.Region ?? "Unknown", worldInfo?.DataCenter ?? "Unknown", info));
            }

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
                sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var column = (XagmanRosterColumn)spec.ColumnUserID;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        var cmp = column switch
                        {
                            XagmanRosterColumn.Character => string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase),
                            XagmanRosterColumn.Region => (WorldData.GetSweepOrdinalForWorld(a.World) / 100).CompareTo(WorldData.GetSweepOrdinalForWorld(b.World) / 100),
                            XagmanRosterColumn.DataCenter => WorldData.GetSweepOrdinalForWorld(a.World).CompareTo(WorldData.GetSweepOrdinalForWorld(b.World)),
                            XagmanRosterColumn.Homeworld => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
                            XagmanRosterColumn.Inventory => (a.Info?.MainInventoryFreeSlots ?? 0).CompareTo(b.Info?.MainInventoryFreeSlots ?? 0),
                            XagmanRosterColumn.Gil => (a.Info?.Gil ?? 0).CompareTo(b.Info?.Gil ?? 0),
                            XagmanRosterColumn.Treasure => (a.Info?.TreasureValue ?? 0).CompareTo(b.Info?.TreasureValue ?? 0),
                            XagmanRosterColumn.Kits => (a.Info?.MagitekRepairKits ?? 0).CompareTo(b.Info?.MagitekRepairKits ?? 0),
                            XagmanRosterColumn.Tanks => (a.Info?.CeruleumTanks ?? 0).CompareTo(b.Info?.CeruleumTanks ?? 0),
                            XagmanRosterColumn.Retainers => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: false).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: false)),
                            XagmanRosterColumn.Submarines => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: true).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: true)),
                            XagmanRosterColumn.Builds => string.Compare(GetXagmanBuildsLabel(a.Info), GetXagmanBuildsLabel(b.Info), StringComparison.Ordinal),
                            XagmanRosterColumn.Restock => (GetXagmanRestockDays(a.Info) ?? int.MaxValue).CompareTo(GetXagmanRestockDays(b.Info) ?? int.MaxValue),
                            _ => a.OrigIdx.CompareTo(b.OrigIdx),
                        };

                        if (cmp == 0)
                        {
                            cmp = string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase);
                            if (cmp == 0)
                                cmp = a.OrigIdx.CompareTo(b.OrigIdx);
                        }

                        return ascending ? cmp : -cmp;
                    });
                }
            }

            foreach (var row in filtered)
            {
                var i = row.OrigIdx;
                var charName = row.CharacterName;
                var info = row.Info;
                var displayName = GetDisplayCharacterName(charName, anonymizeTonyCharacters);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = xagmanTonySelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##xagmanTonySel{i}", ref selected))
                {
                    if (selected) xagmanTonySelectedIndices.Add(i);
                    else xagmanTonySelectedIndices.Remove(i);
                    InvalidateXagmanTradeCapacityForecast();
                }
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                DrawXagmanRelogCharacterName(displayName, charName);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.Region == "Unknown" ? global::XASlave.Localization.UiLocale.Get("SB764CDC0EAB71374") : row.Region);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.DataCenter == "Unknown" ? global::XASlave.Localization.UiLocale.Get("SB764CDC0EAB71374") : row.DataCenter);
                ImGui.TableNextColumn();
                var homeworld = GetDisplayWorldFromKey(charName, anonymizeTonyCharacters);
                ImGui.TextDisabled(((string.IsNullOrWhiteSpace(homeworld) ? global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374") : global::XASlave.Localization.UiMessage.Raw(homeworld))).Display);
                ImGui.TableNextColumn();
                var inventoryLabel = GetInventoryFreeSlotsLabel(info);
                if (!string.IsNullOrWhiteSpace(inventoryLabel))
                    ImGui.TextDisabled(inventoryLabel);
                else
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9")).Display);
                ImGui.TableNextColumn();
                if (info != null && info.Gil > 0)
                    ImGui.TextDisabled(info.Gil.ToString("N0", CultureInfo.InvariantCulture));
                else
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9")).Display);
                DrawXagmanCharacterCountCell(info?.TreasureValue ?? 0);
                DrawXagmanCharacterCountCell(info?.MagitekRepairKits ?? 0);
                DrawXagmanCharacterCountCell(info?.CeruleumTanks ?? 0);
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: false), (global::XASlave.Localization.UiMessage.Key("S6BD8FAB90EB8E022")).Display);
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: true), (global::XASlave.Localization.UiMessage.Key("S3A48C2443B07FAA2")).Display);
                DrawXagmanRestockCell(info);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetXagmanBuildsLabel(info));
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##xagmanTonyRemove{i}")))
                    {
                        chars.RemoveAt(i);
                        ReindexSelectionSet(xagmanTonySelectedIndices, i);
                        InvalidateXagmanTradeCapacityForecast();
                        cfg.Save();
                        break;
                    }
                }
            }

        }
        ImGui.SetNextItemWidth(Scale(260f));
        var enterPressed = ImGui.InputTextWithHint("##xagmanTonyAdd", global::XASlave.Localization.UiLocale.Get("SF32C73F5DCC35D82"), ref xagmanTonyNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SC0B783B46EB80152") + "##xagmanTonyAddBtn")) || enterPressed) && !string.IsNullOrWhiteSpace(xagmanTonyNewChar))
        {
            var trimmed = xagmanTonyNewChar.Trim();
            if (!chars.Any(entry => entry.CharacterNameWorld.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                chars.Add(new XagmanTonyCharacterEntry
                {
                    CharacterNameWorld = trimmed,
                    Mode = XagmanTonyMode.Collection,
                });
                InvalidateXagmanTradeCapacityForecast();
                cfg.Save();
            }
            xagmanTonyNewChar = string.Empty;
        }
    }
    private void DrawXagmanFranchiseTable(Configuration cfg)
    {
        var chars = cfg.XagmanFranchiseCharacters;
        var charInfo = cfg.ReloggerCharacterInfo;
        DrawCharacterListHeader(global::XASlave.Localization.UiLocale.Get("SF2913870F9E2B4AE"), global::XASlave.Localization.UiLocale.FormatKey("S261FA25B9E69A2B0", chars.Count), "xagmanOwnerAnonymize");
        var anonymizeFranchiseCharacters = IsCharacterListAnonymizationEnabled();
        ImGui.Spacing();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S726E20DB00096A31") + "##xagmanOwnerAll")))
            SelectVisibleXagmanFranchiseCharacters();
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S83B12C2216EFB4FD") + "##xagmanOwnerNone")))
        {
            xagmanFranchiseSelectedIndices.Clear();
            InvalidateXagmanTradeCapacityForecast();
        }
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S75D7036745455F64") + "##xagmanOwnerMatching")))
            SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.All);
        var ownerMatchingButtonHovered = ImGui.IsItemHovered();
        using (var imguiScope994 = ImRaii.ContextPopupItem("##xagmanOwnerMatchingContext"))
        if (imguiScope994)
        {
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SE0DCC8DAB98DEDD8")))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Retainers);
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SF196D8C4A5BAA603")))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.Submarines);
            ImGui.Separator();
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SE87B93418ADE7971")))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutRetainers);
            if (global::XASlave.Localization.UiNative.MenuItem(global::XASlave.Localization.UiLocale.Native("SB3A4CF3453680810")))
                SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope.WithoutSubmarines);

        }
        if (ownerMatchingButtonHovered)
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S337B532AC95B2A6C"));
        if (TryGetLoggedInXagmanCurrentCharacter(out var currentOwnerCharacter))
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S94DB1ED4DFBA7E2A") + "##xagmanOwnerCurrent")))
                SelectXagmanCurrentCharacter(XagmanMatchSelectionTarget.FranchiseOwner, currentOwnerCharacter);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S2F7376D8947CA031"));
        }
        ImGui.SameLine();
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SA343573832C8F9FD") + "##xagmanOwnerSelOnly"), ref xagmanFranchiseShowOnlySelected);
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanOwnerSearch", global::XASlave.Localization.UiLocale.Get("SF46724B1F2CE62A0"), ref xagmanFranchiseSearchFilter, 128);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SE0DCC8DAB98DEDD8") + "##xagmanOwnerRetainersOnly")))
            SelectXagmanFranchiseCharactersWithRegisteredAutoRetainerData(submarines: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SCE79B8A72673EA01"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SF196D8C4A5BAA603") + "##xagmanOwnerSubsOnly")))
            SelectXagmanFranchiseCharactersWithRegisteredAutoRetainerData(submarines: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S4FE1E7DF25CB4CB8"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S91A6789C4B36A6C6"));
        using (var imguiScope1032 = ImRaii.Table("XagmanOwnerTable.ColumnsV4", 14,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody,
            ScaledVector(0f, 175f)))
        if (imguiScope1032)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide, 0f, (uint)XagmanRosterColumn.Character);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SD3A008EF1335692D"), ImGuiTableColumnFlags.WidthFixed, Scale(65f), (uint)XagmanRosterColumn.Region);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S730DA5D73C76044B"), ImGuiTableColumnFlags.WidthFixed, Scale(95f), (uint)XagmanRosterColumn.DataCenter);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S5733E35FD7609001"), ImGuiTableColumnFlags.WidthFixed, Scale(95f), (uint)XagmanRosterColumn.Homeworld);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S758F487FE8E9FCC5"), ImGuiTableColumnFlags.WidthFixed, Scale(80f), (uint)XagmanRosterColumn.Inventory);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1C41C2A178AA300D"), ImGuiTableColumnFlags.WidthFixed, Scale(95f), (uint)XagmanRosterColumn.Gil);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SFBBBF4992FA5817C"), ImGuiTableColumnFlags.WidthFixed, Scale(90f), (uint)XagmanRosterColumn.Treasure);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1CD93608DEB92CF9"), ImGuiTableColumnFlags.WidthFixed, Scale(50f), (uint)XagmanRosterColumn.Kits);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCA604D552D44CFD4"), ImGuiTableColumnFlags.WidthFixed, Scale(55f), (uint)XagmanRosterColumn.Tanks);
            global::XASlave.Localization.UiNative.TableSetupColumn((global::XASlave.Localization.UiLocale.Native("S3F91391122918036") + "##xagmanOwnerRetainers"), ImGuiTableColumnFlags.WidthFixed, Scale(72f), (uint)XagmanRosterColumn.Retainers);
            global::XASlave.Localization.UiNative.TableSetupColumn((global::XASlave.Localization.UiLocale.Native("S358FD9EED262500E") + "##xagmanOwnerSubmarines"), ImGuiTableColumnFlags.WidthFixed, Scale(84f), (uint)XagmanRosterColumn.Submarines);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S5A79B9596F4EDE06"), ImGuiTableColumnFlags.WidthFixed, Scale(65f), (uint)XagmanRosterColumn.Restock);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S6A3FE8876B563B46"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, Scale(240f), (uint)XagmanRosterColumn.Builds);
            global::XASlave.Localization.UiNative.TableSetupColumn((global::XASlave.Localization.UiLocale.Native("SE2D0A54968EAD24E") + "##xagmanOwnerDelete"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, Scale(30f), (uint)XagmanRosterColumn.Delete);
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();

            var filtered = new List<(int OrigIdx, string CharacterName, string World, string Region, string DataCenter, ReloggerCharacterData? Info)>();
            for (var i = 0; i < chars.Count; i++)
            {
                var charName = chars[i];
                var world = GetWorldFromKey(charName);
                var worldInfo = WorldData.GetByName(world);
                if (!IsXagmanFranchiseCharacterVisible(cfg, charName, i))
                    continue;
                charInfo.TryGetValue(charName, out var info);
                filtered.Add((i, charName, world, worldInfo?.Region ?? "Unknown", worldInfo?.DataCenter ?? "Unknown", info));
            }

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
                sortSpecs.SpecsDirty = false;
            if (sortSpecs.SpecsCount > 0)
            {
                unsafe
                {
                    var spec = sortSpecs.Specs;
                    var column = (XagmanRosterColumn)spec.ColumnUserID;
                    var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                    filtered.Sort((a, b) =>
                    {
                        var cmp = column switch
                        {
                            XagmanRosterColumn.Character => string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase),
                            XagmanRosterColumn.Region => (WorldData.GetSweepOrdinalForWorld(a.World) / 100).CompareTo(WorldData.GetSweepOrdinalForWorld(b.World) / 100),
                            XagmanRosterColumn.DataCenter => WorldData.GetSweepOrdinalForWorld(a.World).CompareTo(WorldData.GetSweepOrdinalForWorld(b.World)),
                            XagmanRosterColumn.Homeworld => string.Compare(a.World, b.World, StringComparison.OrdinalIgnoreCase),
                            XagmanRosterColumn.Inventory => (a.Info?.MainInventoryFreeSlots ?? 0).CompareTo(b.Info?.MainInventoryFreeSlots ?? 0),
                            XagmanRosterColumn.Gil => (a.Info?.Gil ?? 0).CompareTo(b.Info?.Gil ?? 0),
                            XagmanRosterColumn.Treasure => (a.Info?.TreasureValue ?? 0).CompareTo(b.Info?.TreasureValue ?? 0),
                            XagmanRosterColumn.Kits => (a.Info?.MagitekRepairKits ?? 0).CompareTo(b.Info?.MagitekRepairKits ?? 0),
                            XagmanRosterColumn.Tanks => (a.Info?.CeruleumTanks ?? 0).CompareTo(b.Info?.CeruleumTanks ?? 0),
                            XagmanRosterColumn.Retainers => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: false).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: false)),
                            XagmanRosterColumn.Submarines => GetXagmanRegisteredAutoRetainerCount(a.Info, submarines: true).CompareTo(GetXagmanRegisteredAutoRetainerCount(b.Info, submarines: true)),
                            XagmanRosterColumn.Builds => string.Compare(GetXagmanBuildsLabel(a.Info), GetXagmanBuildsLabel(b.Info), StringComparison.Ordinal),
                            XagmanRosterColumn.Restock => (GetXagmanRestockDays(a.Info) ?? int.MaxValue).CompareTo(GetXagmanRestockDays(b.Info) ?? int.MaxValue),
                            _ => a.OrigIdx.CompareTo(b.OrigIdx),
                        };

                        if (cmp == 0)
                        {
                            cmp = string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase);
                            if (cmp == 0)
                                cmp = a.OrigIdx.CompareTo(b.OrigIdx);
                        }

                        return ascending ? cmp : -cmp;
                    });
                }
            }

            foreach (var row in filtered)
            {
                var i = row.OrigIdx;
                var charName = row.CharacterName;
                var info = row.Info;
                var displayName = GetDisplayCharacterName(charName, anonymizeFranchiseCharacters);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = xagmanFranchiseSelectedIndices.Contains(i);
                if (ImGui.Checkbox($"##xagmanOwnerSel{i}", ref selected))
                {
                    if (selected) xagmanFranchiseSelectedIndices.Add(i);
                    else xagmanFranchiseSelectedIndices.Remove(i);
                    InvalidateXagmanTradeCapacityForecast();
                }
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                DrawXagmanRelogCharacterName(displayName, charName);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.Region == "Unknown" ? global::XASlave.Localization.UiLocale.Get("SB764CDC0EAB71374") : row.Region);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.DataCenter == "Unknown" ? global::XASlave.Localization.UiLocale.Get("SB764CDC0EAB71374") : row.DataCenter);
                ImGui.TableNextColumn();
                var homeworld = GetDisplayWorldFromKey(charName, anonymizeFranchiseCharacters);
                ImGui.TextDisabled(((string.IsNullOrWhiteSpace(homeworld) ? global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374") : global::XASlave.Localization.UiMessage.Raw(homeworld))).Display);
                ImGui.TableNextColumn();
                var inventoryLabel = GetInventoryFreeSlotsLabel(info);
                if (!string.IsNullOrWhiteSpace(inventoryLabel))
                    ImGui.TextDisabled(inventoryLabel);
                else
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9")).Display);
                ImGui.TableNextColumn();
                if (info != null && info.Gil > 0)
                    ImGui.TextDisabled(info.Gil.ToString("N0", CultureInfo.InvariantCulture));
                else
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9")).Display);
                DrawXagmanCharacterCountCell(info?.TreasureValue ?? 0);
                DrawXagmanCharacterCountCell(info?.MagitekRepairKits ?? 0);
                DrawXagmanCharacterCountCell(info?.CeruleumTanks ?? 0);
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: false), (global::XASlave.Localization.UiMessage.Key("S6BD8FAB90EB8E022")).Display);
                DrawXagmanAutoRetainerCountCell(GetXagmanRegisteredAutoRetainerCount(info, submarines: true), (global::XASlave.Localization.UiMessage.Key("S3A48C2443B07FAA2")).Display);
                DrawXagmanRestockCell(info);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetXagmanBuildsLabel(info));
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##xagmanOwnerRemove{i}")))
                    {
                        chars.RemoveAt(i);
                        ReindexSelectionSet(xagmanFranchiseSelectedIndices, i);
                        InvalidateXagmanTradeCapacityForecast();
                        cfg.Save();
                        break;
                    }
                }
            }

        }
        ImGui.SetNextItemWidth(Scale(260f));
        var enterPressed = ImGui.InputTextWithHint("##xagmanOwnerAdd", global::XASlave.Localization.UiLocale.Get("SF32C73F5DCC35D82"), ref xagmanFranchiseNewChar, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3996259F30984D1B") + "##xagmanOwnerAddBtn")) || enterPressed) && !string.IsNullOrWhiteSpace(xagmanFranchiseNewChar))
        {
            var trimmed = xagmanFranchiseNewChar.Trim();
            if (!chars.Any(entry => entry.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                chars.Add(trimmed);
                InvalidateXagmanTradeCapacityForecast();
                cfg.Save();
            }
            xagmanFranchiseNewChar = string.Empty;
        }
    }
    private void DrawXagmanRelogCharacterName(string displayName, string characterNameWorld)
    {
        ImGui.TextUnformatted(displayName);
        var hovered = ImGui.IsItemHovered();
        var relogBlocked = xagmanRunning || xagmanTradeSafetySessionActive || plugin.TaskRunner.IsRunning;
        var ctrlClicked = hovered && ImGui.GetIO().KeyCtrl && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        if (hovered)
            ImGui.SetTooltip(((relogBlocked ? global::XASlave.Localization.UiMessage.Key("S1EC2C81BFBBEAE3E") : global::XASlave.Localization.UiMessage.Key("S2C262E48B71F9D74"))).Display);

        if (!ctrlClicked || relogBlocked)
            return;

        if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy)
        {
            plugin.TaskRunner.AddLog("Xagman: Ctrl+Click relog was not sent because Lifestream is busy or unavailable.");
            return;
        }

        var relogTarget = characterNameWorld.Trim();
        if (!string.IsNullOrWhiteSpace(relogTarget))
            ChatHelper.SendMessage($"/li {relogTarget}");
    }
    private static void DrawXagmanCharacterCountCell(int value)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(((value > 0 ? global::XASlave.Localization.UiMessage.Raw(value.ToString("N0", CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
    }
    private static int GetXagmanRegisteredAutoRetainerCount(ReloggerCharacterData? info, bool submarines)
    {
        if (info?.FoundInAutoRetainer != true)
            return 0;

        return Math.Max(0, submarines ? info.SubmarineCount : info.RetainerCount);
    }
    private int? GetXagmanRestockDays(ReloggerCharacterData? info)
    {
        if (info == null || info.XaDatabaseSnapshotUpdatedUtc == DateTime.MinValue)
            return null;

        return SubmarineBuildSupply.CalculateDays(
            GetXagmanRegisteredAutoRetainerCount(info, submarines: true),
            info.SubmarineBuilds, info.CeruleumTanks, info.MagitekRepairKits, xagmanSubmarineRates, out _);
    }
    private static string GetXagmanBuildsLabel(ReloggerCharacterData? info)
    {
        var count = Math.Min(4, GetXagmanRegisteredAutoRetainerCount(info, submarines: true));
        if (count == 0)
            return "-";
        var builds = new List<string>();
        for (var i = 0; i < count; i++)
            builds.Add(info?.SubmarineBuilds != null && i < info.SubmarineBuilds.Count
                ? info.SubmarineBuilds[i] : "????");
        return string.Join(" / ", builds);
    }
    private void DrawXagmanRestockCell(ReloggerCharacterData? info)
    {
        ImGui.TableNextColumn();
        var days = GetXagmanRestockDays(info);
        var fallbackCount = 0;
        if (days.HasValue && info != null)
            SubmarineBuildSupply.CalculateDays(GetXagmanRegisteredAutoRetainerCount(info, submarines: true),
                info.SubmarineBuilds, info.CeruleumTanks, info.MagitekRepairKits, xagmanSubmarineRates, out fallbackCount);
        ImGui.TextDisabled(((days.HasValue ? global::XASlave.Localization.UiMessage.Format("S8391DA809DD9CA6B", days.Value.ToString("N0", CultureInfo.InvariantCulture), ((fallbackCount > 0 ? global::XASlave.Localization.UiMessage.Key("S684888C0EBB17F37") : global::XASlave.Localization.UiMessage.Value(string.Empty)))) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(((days.HasValue ? global::XASlave.Localization.UiMessage.Format("S5F142AED30117AAF", (fallbackCount > 0 ? global::XASlave.Localization.UiMessage.Format("S1AD64C00B2B500F9", fallbackCount) : global::XASlave.Localization.UiMessage.Value(string.Empty))) : global::XASlave.Localization.UiMessage.Key("S85B856994AD2F32F"))).Display);
    }
    private static void DrawXagmanAutoRetainerCountCell(int value, string resourceName)
    {
        ImGui.TableNextColumn();
        if (value <= 0)
            return;

        ImGui.TextDisabled(value.ToString("N0", CultureInfo.InvariantCulture));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.FormatKey("S4DF4650174E7A2E8", value, resourceName, (value == 1 ? string.Empty : global::XASlave.Localization.UiLocale.Get("S043A718774C572BD"))));
    }
    private static bool HasXagmanConditionalItemPolicies(IEnumerable<XagmanItemEntry> items)
    {
        return items.Any(item => item.Applicability != XagmanItemApplicability.All);
    }
    private static bool IsXagmanGreenValueSelector(XagmanItemSelectorKind selectorKind)
    {
        return selectorKind is XagmanItemSelectorKind.GreenItemGcSeals
            or XagmanItemSelectorKind.GreenItemFcCreditsRankProgress;
    }
    private static bool HasXagmanGreenValueSelectors(IEnumerable<XagmanItemEntry> items)
    {
        return items.Any(item => IsXagmanGreenValueSelector(item.SelectorKind));
    }
    private static string GetXagmanGreenValueSelectorName(XagmanItemSelectorKind selectorKind)
    {
        return selectorKind switch
        {
            XagmanItemSelectorKind.GreenItemGcSeals => XagmanGreenGcSealsName,
            XagmanItemSelectorKind.GreenItemFcCreditsRankProgress => XagmanGreenFcCreditsName,
            _ => string.Empty,
        };
    }
    private static bool CanXagmanItemBeHq(uint itemId)
    {
        if (itemId <= 1)
            return false;
        lock (xagmanItemHqCapabilityCacheLock)
        {
            if (xagmanItemHqCapabilityCache.TryGetValue(itemId, out var cachedCanBeHq))
                return cachedCanBeHq;
        }
        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            if (!itemSheet.TryGetRow(itemId, out var itemRow))
                return false;
            var canBeHq = itemRow.CanBeHq;
            lock (xagmanItemHqCapabilityCacheLock)
                xagmanItemHqCapabilityCache[itemId] = canBeHq;
            return canBeHq;
        }
        catch
        {
            return false;
        }
    }
    private static bool IsValidXagmanItemEntry(XagmanItemEntry item, bool searchOnly = false)
    {
        if (!Enum.IsDefined(item.SelectorKind)
            || !Enum.IsDefined(item.Mode)
            || !Enum.IsDefined(item.Applicability))
        {
            return false;
        }
        if (item.SelectorKind == XagmanItemSelectorKind.ExactItem)
        {
            return item.ItemId > 0
                && !string.IsNullOrWhiteSpace(item.ItemName)
                && (!item.IsHq || CanXagmanItemBeHq(item.ItemId));
        }
        return !searchOnly
            && IsXagmanGreenValueSelector(item.SelectorKind)
            && item.ItemId == 0
            && !item.IsHq
            && item.Mode == XagmanItemMode.TopUp;
    }
    private static bool IsValidXagmanTradeRequest(XagmanTradeRequestEntry entry)
    {
        if (!Enum.IsDefined(entry.SelectorKind)
            || !Enum.IsDefined(entry.Mode))
            return false;
        if (entry.SelectorKind == XagmanItemSelectorKind.ExactItem)
        {
            if (entry.ItemId == 0
                || string.IsNullOrWhiteSpace(entry.ItemName)
                || entry.Mode is not (XagmanItemMode.Take or XagmanItemMode.Balance or XagmanItemMode.TopUp)
                || entry.Quantity < 0
                || entry.TargetQuantity < 0
                || entry.CurrentQuantity < 0
                || entry.GreenValueProtocolRevision != 0
                || entry.TargetValueScaled2 != 0
                || entry.CurrentValueScaled2 != 0
                || entry.ValueDeficitScaled2 != 0
                || (entry.IsHq && !CanXagmanItemBeHq(entry.ItemId))
                || !entry.GreenScanComplete
                || !string.IsNullOrEmpty(entry.GreenScanError))
            {
                return false;
            }

            if (entry.Mode == XagmanItemMode.Take)
            {
                // Take 0 keeps its established all-available shape. A finite Take request
                // carries the original owner quantity in CurrentQuantity, the one-pass
                // ending quantity in TargetQuantity, and only the remaining deficit in
                // Quantity. This lets Tony reject any request that expands after capture.
                if (entry.TargetQuantity == 0)
                    return true;

                var takeDeficit = entry.TargetQuantity - entry.CurrentQuantity;
                return takeDeficit > 0
                    && entry.Quantity > 0
                    && entry.Quantity <= takeDeficit;
            }

            var deficit = entry.TargetQuantity - entry.CurrentQuantity;
            return deficit > 0
                && entry.Quantity > 0
                && entry.Quantity <= deficit
                && (IsXagmanGilItem(entry.ItemId) || entry.Quantity == deficit);
        }
        return IsXagmanGreenValueSelector(entry.SelectorKind)
            && entry.ItemId == 0
            && !entry.IsHq
            && entry.Mode == XagmanItemMode.TopUp
            && !string.IsNullOrWhiteSpace(entry.ItemName);
    }

    private static bool HasDuplicateXagmanExactTradeRequests(
        IEnumerable<XagmanTradeRequestEntry> requests)
    {
        return requests
            .Where(entry => entry.SelectorKind == XagmanItemSelectorKind.ExactItem)
            .GroupBy(entry => (entry.ItemId, entry.IsHq))
            .Any(group => group.Count() > 1);
    }
    private static int GetXagmanItemPolicyOptionIndex(XagmanItemEntry item)
    {
        var options = IsXagmanGreenValueSelector(item.SelectorKind)
            ? xagmanGreenItemPolicyOptions
            : xagmanItemPolicyOptions;
        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i];
            if (option.Mode == item.Mode && option.Applicability == item.Applicability)
                return i;
        }

        return 0;
    }
    private static string GetXagmanItemPolicyLabel(XagmanItemEntry item)
    {
        var optionIndex = GetXagmanItemPolicyOptionIndex(item);
        var options = IsXagmanGreenValueSelector(item.SelectorKind)
            ? xagmanGreenItemPolicyOptions
            : xagmanItemPolicyOptions;
        return options[optionIndex].Label;
    }
    private static bool HasXagmanItemIdentityConflict(
        IReadOnlyList<XagmanItemEntry> items,
        XagmanItemEntry current,
        uint itemId,
        bool isHq,
        XagmanItemApplicability applicability)
    {
        return items.Any(item => !ReferenceEquals(item, current)
            && item.SelectorKind == current.SelectorKind
            && item.ItemId == itemId
            && item.IsHq == isHq
            && item.Applicability == applicability);
    }
    private static bool CanAddXagmanItem(
        IReadOnlyList<XagmanItemEntry> items,
        uint itemId,
        bool isHq,
        bool searchOnly)
    {
        if (isHq && !CanXagmanItemBeHq(itemId))
            return false;
        if (searchOnly)
            return !items.Any(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                && item.ItemId == itemId
                && item.IsHq == isHq);

        return !items.Any(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                && item.ItemId == itemId
                && item.IsHq == isHq
                && item.Applicability == XagmanItemApplicability.All)
            || !items.Any(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                && item.ItemId == itemId
                && item.IsHq == isHq
                && item.Applicability == XagmanItemApplicability.HasSubmarines)
            || !items.Any(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                && item.ItemId == itemId
                && item.IsHq == isHq
                && item.Applicability == XagmanItemApplicability.HasRetainers);
    }
    private static bool CanAddXagmanGreenValueSelector(
        IReadOnlyList<XagmanItemEntry> items,
        XagmanItemSelectorKind selectorKind)
    {
        return IsXagmanGreenValueSelector(selectorKind)
            && (!items.Any(item => item.SelectorKind == selectorKind
                    && item.Applicability == XagmanItemApplicability.All)
                || !items.Any(item => item.SelectorKind == selectorKind
                    && item.Applicability == XagmanItemApplicability.HasSubmarines)
                || !items.Any(item => item.SelectorKind == selectorKind
                    && item.Applicability == XagmanItemApplicability.HasRetainers));
    }
    private void ClearXagmanOwnerPolicyRunCapabilities()
    {
        xagmanOwnerPolicyRunCapabilities.Clear();
        xagmanOwnerPolicyRunCapabilitiesPinned = false;
    }
    private bool TryPrepareXagmanOwnerPolicyRunCapabilities(
        IReadOnlyList<string> ownerCharacters,
        string context)
    {
        ClearXagmanOwnerPolicyRunCapabilities();
        if (!HasXagmanConditionalItemPolicies(plugin.Configuration.XagmanItems))
            return true;

        if (!TryRefreshXagmanAutoRetainerData(XagmanMatchSelectionTarget.FranchiseOwner, out _))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S87464C8968E11E83", context));
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText} No owner task was started.");
            return false;
        }

        var cfg = plugin.Configuration;
        foreach (var ownerCharacter in ownerCharacters)
        {
            if (cfg.ReloggerCharacterInfo.TryGetValue(ownerCharacter, out var info)
                && info.FoundInAutoRetainer.HasValue)
            {
                xagmanOwnerPolicyRunCapabilities[ownerCharacter] = new XagmanOwnerPolicyCapability
                {
                    IsKnown = true,
                    HasRetainers = info.FoundInAutoRetainer == true && info.RetainerCount > 0,
                    HasSubmarines = info.FoundInAutoRetainer == true && info.SubmarineCount > 0,
                };
            }
            else
            {
                xagmanOwnerPolicyRunCapabilities[ownerCharacter] = new XagmanOwnerPolicyCapability();
            }
        }

        xagmanOwnerPolicyRunCapabilitiesPinned = true;
        Plugin.Log.Information($"[XASlave] Xagman pinned AutoRetainer registration for {ownerCharacters.Count} owner character(s) using conditional Shared Item policies.");
        return true;
    }
    private XagmanOwnerPolicyCapability GetXagmanOwnerPolicyCapability(string ownerCharacter)
    {
        if (xagmanOwnerPolicyRunCapabilitiesPinned)
        {
            return xagmanOwnerPolicyRunCapabilities.TryGetValue(ownerCharacter, out var pinned)
                ? pinned
                : new XagmanOwnerPolicyCapability();
        }
        if (xagmanOwnerPolicyRegistrationRefreshFailed)
            return new XagmanOwnerPolicyCapability();

        if (plugin.Configuration.ReloggerCharacterInfo.TryGetValue(ownerCharacter, out var info)
            && info.FoundInAutoRetainer.HasValue)
        {
            return new XagmanOwnerPolicyCapability
            {
                IsKnown = true,
                HasRetainers = info.FoundInAutoRetainer == true && info.RetainerCount > 0,
                HasSubmarines = info.FoundInAutoRetainer == true && info.SubmarineCount > 0,
            };
        }

        return new XagmanOwnerPolicyCapability();
    }
    private List<XagmanItemEntry> ResolveXagmanItemsForOwner(
        IEnumerable<XagmanItemEntry> items,
        string ownerCharacter,
        out bool skippedUnknownConditionalGroup)
    {
        skippedUnknownConditionalGroup = false;
        var capability = GetXagmanOwnerPolicyCapability(ownerCharacter);
        var resolved = new List<XagmanItemEntry>();
        foreach (var group in items
                     .Where(item => IsValidXagmanItemEntry(item))
                     .GroupBy(item => new
                     {
                         item.SelectorKind,
                         ItemId = item.SelectorKind == XagmanItemSelectorKind.ExactItem ? item.ItemId : 0u,
                         IsHq = item.SelectorKind == XagmanItemSelectorKind.ExactItem && item.IsHq,
                     }))
        {
            var conditional = group.Any(item => item.Applicability != XagmanItemApplicability.All);
            if (conditional && !capability.IsKnown)
            {
                skippedUnknownConditionalGroup = true;
                continue;
            }

            XagmanItemEntry? effective = null;
            if (capability.HasSubmarines)
                effective = group.FirstOrDefault(item => item.Applicability == XagmanItemApplicability.HasSubmarines);
            if (effective == null && capability.HasRetainers)
                effective = group.FirstOrDefault(item => item.Applicability == XagmanItemApplicability.HasRetainers);
            effective ??= group.FirstOrDefault(item => item.Applicability == XagmanItemApplicability.All);
            if (effective != null)
                resolved.Add(effective);
        }

        return resolved;
    }
    private List<XagmanItemEntry> ResolveXagmanItemsForOwner(
        IEnumerable<XagmanItemEntry> items,
        string ownerCharacter)
    {
        return ResolveXagmanItemsForOwner(items, ownerCharacter, out _);
    }

    private bool IsXagmanCollectionFirstRequestedForRole(
        XagmanRole role,
        bool hasConditionalItemPolicies)
    {
        return role == XagmanRole.FranchiseOwner
            && hasConditionalItemPolicies
            && plugin.Configuration.XagmanPrioritizeCharactersGivingItemsFirst
            && !plugin.Configuration.XagmanOutsideNetworkHelper;
    }

    private bool TryValidateXagmanGreenValueOwnerStart(XagmanPeerMessage? startDirective)
    {
        if (!HasXagmanGreenValueSelectors(plugin.Configuration.XagmanItems))
            return true;
        if (plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S75675CEA8C544620"));
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            return false;
        }
        if (startDirective != null)
        {
            if (startDirective.GreenValueProtocolRevision == XagmanGreenValueProtocolRevision)
                return true;
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText =
                global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SBB5B049D3ED10B29", XagmanGreenValueProtocolRevision));
            plugin.TaskRunner.AddLog(
                $"Xagman: {xagmanStatusText} Reload Tony and every Franchise Owner from the same source.");
            return false;
        }

        var liveTony = GetXagmanLiveTonyPeer();
        if (liveTony != null
            && IsXagmanPeerFresh(liveTony, 15.0)
            && liveTony.GreenValueProtocolRevision == XagmanGreenValueProtocolRevision)
        {
            return true;
        }
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText =
            global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE2FE4E08BE069C79", XagmanGreenValueProtocolRevision));
        plugin.TaskRunner.AddLog(
            $"Xagman: {xagmanStatusText} Start/reload the same source on every client before retrying.");
        return false;
    }

    private bool IsXagmanCollectionFirstRunActive()
    {
        return xagmanCollectionFirstActive
            && !string.IsNullOrWhiteSpace(xagmanRunId)
            && xagmanRunPhase is XagmanRunPhase.Collection or XagmanRunPhase.Restock;
    }

    private bool IsXagmanCollectionFirstCollectionPhase()
        => IsXagmanCollectionFirstRunActive() && xagmanRunPhase == XagmanRunPhase.Collection
            && !IsXagmanCapacityDrainActive();

    private bool IsXagmanCollectionFirstRestockPhase()
        => IsXagmanCollectionFirstRunActive() && xagmanRunPhase == XagmanRunPhase.Restock;

    private void ResetXagmanCollectionFirstRunState()
    {
        xagmanRunId = string.Empty;
        xagmanCollectionFirstActive = false;
        xagmanCollectionFirstStartupModeNegotiated = false;
        xagmanRunPhase = XagmanRunPhase.Legacy;
        xagmanPhaseComplete = false;
        xagmanTravelRouteFatalError = false;
        xagmanCompletionDirectiveAcknowledged = false;
        xagmanPhaseTotalCharacters = 0;
        xagmanPhaseResolvedCharacters = 0;
        xagmanCollectionBarrierStartedAtUtc = DateTime.MinValue;
        xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
        xagmanCollectionBarrierWarningLogged = false;
        xagmanCollectionFirstOwnerFullPlan = Array.Empty<string>();
        xagmanCollectionFirstCollectionPlan = Array.Empty<string>();
        xagmanCollectionFirstRestockPlan = Array.Empty<string>();
        xagmanExpectedFranchiseOwnerInstanceIds.Clear();
        xagmanCollectionPhaseAcknowledgedInstanceIds.Clear();
        xagmanRestockPhaseAcknowledgedInstanceIds.Clear();
        xagmanCollectionFirstFailedCharacters.Clear();
        ClearXagmanPriorityTradeCapacityForecastBaseline();
    }

    private bool TryConfigureXagmanCollectionFirstOwnerRun(XagmanPeerMessage? startDirective)
    {
        ResetXagmanCollectionFirstRunState();
        if (startDirective?.CollectionFirstEnabled != true)
            return true;
        if (startDirective.CoordinationProtocolRevision != XagmanCollectionFirstCoordinationProtocolRevision
            || string.IsNullOrWhiteSpace(startDirective.RunId)
            || startDirective.RunPhase != XagmanRunPhase.Collection)
        {
            plugin.TaskRunner.AddLog(
                "Xagman: refused collection-first start because the Tony directive was incomplete or used an unsupported coordination protocol.");
            return false;
        }
        var hasConditionalItemPolicies = HasXagmanConditionalItemPolicies(plugin.Configuration.XagmanItems);
        if (!hasConditionalItemPolicies)
        {
            plugin.TaskRunner.AddLog(
                "Xagman: refused collection-first start because this client has no conditional Shared Item policies.");
            return false;
        }
        if (!IsXagmanCollectionFirstRequestedForRole(
                XagmanRole.FranchiseOwner,
                hasConditionalItemPolicies))
        {
            plugin.TaskRunner.AddLog(
                "Xagman: refused collection-first start because this Franchise Owner client has not enabled Prioritize Characters Giving Items First.");
            return false;
        }
        if (startDirective.ExpectedFranchiseOwnerInstanceIds.Count == 0
            || !startDirective.ExpectedFranchiseOwnerInstanceIds.Contains(plugin.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            plugin.TaskRunner.AddLog(
                "Xagman: ignored collection-first start because this FO client was not part of Tony's frozen expected-client cohort.");
            return false;
        }

        xagmanRunId = startDirective.RunId.Trim();
        xagmanCollectionFirstActive = true;
        xagmanCollectionFirstStartupModeNegotiated = true;
        xagmanRunPhase = XagmanRunPhase.Collection;
        xagmanExpectedFranchiseOwnerInstanceIds.UnionWith(
            startDirective.ExpectedFranchiseOwnerInstanceIds
                .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId)));
        return true;
    }

    private IReadOnlyList<string> BuildXagmanCollectionFirstOwnerPhasePlan(
        IReadOnlyList<string> fullPlan,
        XagmanRunPhase phase)
    {
        bool IsRelevantMode(XagmanItemMode mode)
        {
            return phase switch
            {
                XagmanRunPhase.Collection => mode is XagmanItemMode.Give or XagmanItemMode.Balance,
                XagmanRunPhase.Restock => mode is XagmanItemMode.Take or XagmanItemMode.Balance or XagmanItemMode.TopUp,
                _ => true,
            };
        }

        var configuredItems = plugin.Configuration.XagmanItems;
        var result = new List<string>();
        foreach (var owner in fullPlan)
        {
            var effectiveItems = ResolveXagmanItemsForOwner(
                configuredItems,
                owner,
                out var skippedUnknownConditionalGroup);
            var hasPhasePolicy = effectiveItems.Any(item => IsRelevantMode(item.Mode));
            if (!hasPhasePolicy && skippedUnknownConditionalGroup)
            {
                // Registration uncertainty must never classify a possible giver or receiver out of a
                // phase. The live character is checked again after login before any Dropbox action.
                hasPhasePolicy = configuredItems
                    .Where(item => item.Applicability != XagmanItemApplicability.All)
                    .Any(item => IsRelevantMode(item.Mode));
            }
            if (hasPhasePolicy)
                result.Add(owner);
        }
        return result;
    }

    private static bool IsXagmanPeerInRun(
        XagmanPeerPresence peer,
        string runId,
        XagmanRunPhase phase)
    {
        return !string.IsNullOrWhiteSpace(runId)
            && peer.CollectionFirstEnabled
            && peer.CoordinationProtocolRevision == XagmanCollectionFirstCoordinationProtocolRevision
            && peer.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase)
            && peer.RunPhase == phase;
    }

    private bool IsXagmanPeerInCurrentRunPhase(XagmanPeerPresence peer)
    {
        return !IsXagmanCollectionFirstRunActive()
            || IsXagmanPeerInRun(peer, xagmanRunId, xagmanRunPhase);
    }

    private void ObserveXagmanCollectionFirstPhaseAcknowledgements()
    {
        if (!IsXagmanCollectionFirstRunActive() || xagmanActiveRole != XagmanRole.Tony
            || IsXagmanCapacityDrainActive())
            return;

        var acknowledgements = xagmanRunPhase == XagmanRunPhase.Collection
            ? xagmanCollectionPhaseAcknowledgedInstanceIds
            : xagmanRestockPhaseAcknowledgedInstanceIds;
        foreach (var peer in plugin.XagmanPeers.Peers
                     .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
                     .Where(peer => xagmanExpectedFranchiseOwnerInstanceIds.Contains(peer.InstanceId))
                     .Where(peer => IsXagmanPeerInRun(peer, xagmanRunId, xagmanRunPhase))
                     .Where(peer => peer.PhaseComplete))
        {
            acknowledgements.Add(peer.InstanceId);
        }

        xagmanPhaseTotalCharacters = xagmanExpectedFranchiseOwnerInstanceIds.Count;
        xagmanPhaseResolvedCharacters = acknowledgements.Count(instanceId =>
            xagmanExpectedFranchiseOwnerInstanceIds.Contains(instanceId));
    }

    private bool AreAllExpectedXagmanFranchiseOwnersAcknowledged(XagmanRunPhase phase)
    {
        if (!IsXagmanCollectionFirstRunActive())
            return true;
        var acknowledgements = phase == XagmanRunPhase.Collection
            ? xagmanCollectionPhaseAcknowledgedInstanceIds
            : xagmanRestockPhaseAcknowledgedInstanceIds;
        return xagmanExpectedFranchiseOwnerInstanceIds.Count > 0
            && xagmanExpectedFranchiseOwnerInstanceIds.All(acknowledgements.Contains);
    }

    private void UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase phase)
    {
        ObserveXagmanCollectionFirstPhaseAcknowledgements();
        var acknowledgements = phase == XagmanRunPhase.Collection
            ? xagmanCollectionPhaseAcknowledgedInstanceIds
            : xagmanRestockPhaseAcknowledgedInstanceIds;
        var missing = xagmanExpectedFranchiseOwnerInstanceIds
            .Where(instanceId => !acknowledgements.Contains(instanceId))
            .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var completed = xagmanExpectedFranchiseOwnerInstanceIds.Count - missing.Count;
        var phaseLabel = (phase == XagmanRunPhase.Collection ? global::XASlave.Localization.UiMessage.Key("SFA7496E4AE840306") : global::XASlave.Localization.UiMessage.Key("SEEEBB883F0F725C1"));
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (missing.Count == 0 ? global::XASlave.Localization.UiMessage.Format("SF99A79414935164E", phaseLabel) : global::XASlave.Localization.UiMessage.Format("SC4DC52EE8062697D", phaseLabel, completed, xagmanExpectedFranchiseOwnerInstanceIds.Count)));

        var availableExpectedIds = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(peer => IsXagmanPeerInRun(peer, xagmanRunId, phase))
            .Where(peer => peer.Status != XagmanStatus.Error)
            .Where(peer => IsXagmanPeerFresh(peer, 15.0))
            .Select(peer => peer.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailableMissing = missing
            .Where(instanceId => !availableExpectedIds.Contains(instanceId))
            .ToList();
        if (unavailableMissing.Count == 0)
        {
            xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
        }
        else
        {
            if (xagmanMissingCohortPeerSinceUtc == DateTime.MinValue)
                xagmanMissingCohortPeerSinceUtc = DateTime.UtcNow;
            else if ((DateTime.UtcNow - xagmanMissingCohortPeerSinceUtc).TotalSeconds >= XagmanMissingCohortPeerFailSeconds)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText =
                    global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S043BEA030CF5AF63", phaseLabel, unavailableMissing.Count));
                plugin.TaskRunner.AddLog(
                    $"Xagman: collection-first run {xagmanRunId} failed closed after waiting {XagmanMissingCohortPeerFailSeconds:0} seconds for unavailable expected FO client(s): {string.Join(", ", unavailableMissing)}. Restock was not advanced; reconnect/reload every client and start a new run.");
                PublishXagmanPresence();
                return;
            }
        }

        if (missing.Count == 0
            || xagmanCollectionBarrierWarningLogged
            || xagmanCollectionBarrierStartedAtUtc == DateTime.MinValue
            || (DateTime.UtcNow - xagmanCollectionBarrierStartedAtUtc).TotalSeconds < XagmanCollectionBarrierWarningSeconds)
        {
            return;
        }

        xagmanCollectionBarrierWarningLogged = true;
        plugin.TaskRunner.AddLog(
            $"Xagman: {phaseLabel} barrier is still waiting for {missing.Count} expected FO client(s): {string.Join(", ", missing)}.");
    }

    private bool TryAdvanceXagmanCollectionFirstTonyToRestock()
    {
        if (!IsXagmanCollectionFirstCollectionPhase()
            || xagmanActiveRole != XagmanRole.Tony
            || plugin.TaskRunner.IsRunning
            || !string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
            || xagmanObservedDropboxBusy
            || plugin.IpcClient.DropboxIsBusy()
            || GetXagmanQueueForTony(xagmanActiveCharacter).Count > 0)
        {
            return false;
        }

        ObserveXagmanCollectionFirstPhaseAcknowledgements();
        if (!AreAllExpectedXagmanFranchiseOwnersAcknowledged(XagmanRunPhase.Collection))
            return false;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("SA2CA07ADBFDEFD3E")))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S429BDA965FBFF7B1"));
            PublishXagmanPresence();
            return true;
        }
        ClearXagmanDropbox();

        xagmanRunPhase = XagmanRunPhase.Restock;
        xagmanPhaseComplete = false;
        xagmanPhaseTotalCharacters = xagmanExpectedFranchiseOwnerInstanceIds.Count;
        xagmanPhaseResolvedCharacters = 0;
        xagmanCollectionBarrierStartedAtUtc = DateTime.UtcNow;
        xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
        xagmanCollectionBarrierWarningLogged = false;
        xagmanRestockPhaseAcknowledgedInstanceIds.Clear();
        xagmanTonyRunList = xagmanTonyRunPlan.ToList();
        xagmanTonyTotalCharacters = xagmanTonyRunPlan.Count;
        xagmanTonyCompletedCharacters = 0;
        xagmanCurrentTonyIndex = xagmanTonyRunList.Count > 0 ? 0 : -1;
        xagmanActiveCharacter = string.Empty;
        xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanOwnerRequestedItems.Clear();
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
        xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
        xagmanTonyObservedOwnerWork = false;
        xagmanTonyRunStartedAtUtc = DateTime.UtcNow;
        ResetXagmanTonyApproachWait();
        ResetXagmanTonyMeetRetryState();
        ResetXagmanTonySellLocation();
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        ClearXagmanPriorityTradeCapacityForecastBaseline();
        InitXagmanSweepState(xagmanTonyRunPlan);
        plugin.TaskRunner.AddLog(
            $"Xagman: all {xagmanExpectedFranchiseOwnerInstanceIds.Count} expected FO client(s) completed collection; starting the second full Tony sweep for restock.");

        if (xagmanTonyRunPlan.Count == 0)
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("SA115419BC7E4B639"));
            PublishXagmanPresence();
            return true;
        }

        if (xagmanServerMatchingActive)
        {
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S9037E56B8AD7EDE9"));
            PublishXagmanPresence();
            return true;
        }

        var firstTonyKey = xagmanTonyRunPlan[0];
        var firstTony = plugin.Configuration.XagmanTonyCharacters
            .FirstOrDefault(entry => entry.CharacterNameWorld.Equals(firstTonyKey, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry
            {
                CharacterNameWorld = firstTonyKey,
                Mode = xagmanTonyMode,
            };
        StartXagmanTonyStartup(firstTony, true);
        return true;
    }

    private bool TryStartXagmanCollectionFirstOwnerRestockPhase()
    {
        if (!IsXagmanCollectionFirstCollectionPhase()
            || xagmanActiveRole != XagmanRole.FranchiseOwner
            || !xagmanPhaseComplete
            || plugin.TaskRunner.IsRunning)
        {
            return false;
        }

        var restockTony = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.Tony && peer.XagmanEnabled)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => IsXagmanPeerInRun(peer, xagmanRunId, XagmanRunPhase.Restock))
            .OrderByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
        if (restockTony == null)
            return false;

        xagmanRunPhase = XagmanRunPhase.Restock;
        xagmanPhaseComplete = false;
        xagmanPhaseResolvedCharacters = 0;
        xagmanPhaseTotalCharacters = xagmanCollectionFirstRestockPlan.Count;
        xagmanOwnerRunPlan = xagmanCollectionFirstRestockPlan.ToList();
        xagmanOwnerRunList = xagmanCollectionFirstRestockPlan.ToList();
        xagmanOwnerTotalCharacters = xagmanOwnerRunList.Count;
        xagmanOwnerCompletedCharacters = 0;
        xagmanOwnerCurrentCharacterIndex = 0;
        xagmanOwnerStartRequested = true;
        ResetXagmanOwnerTimings();
        xagmanOwnerStandbyPending = false;
        xagmanOwnerPauseForTonyRotationRequested = false;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanPreferredTonyCharacter = restockTony.ActiveCharacter;
        xagmanTradeQuantitySnapshot.Clear();
        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanOwnerSweepPendingDataCenter = xagmanOwnerRunList.Count > 0
            ? GetXagmanDataCenterOfChar(xagmanOwnerRunList[0])
            : string.Empty;
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        TryResolveXagmanMeetDestinationForOwner();
        plugin.TaskRunner.AddLog(
            $"Xagman: Tony entered restock phase for run {xagmanRunId}; starting {xagmanOwnerRunList.Count} saved restock candidate(s) from the beginning.");

        if (xagmanOwnerRunList.Count == 0)
        {
            // Empty clients still reach the menu and join the frozen barrier without waiting
            // for a supplier login that an entirely empty restock phase will never need.
            FinalizeXagmanCollectionFirstOwnerSelections();
            xagmanOwnerSupplyTonyInstance = restockTony.InstanceId;
            xagmanOwnerSupplyPassId = restockTony.SupplyPassId;
            FinishXagmanOwnerSupplyPass();
            StartXagmanSupplyMenuWait();
            return true;
        }

        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S8BF1C9538787D086"));
        PublishXagmanPresence();
        foreach (var failedCharacter in plugin.TaskRunner.FailedCharacters)
        {
            if (!string.IsNullOrWhiteSpace(failedCharacter))
                xagmanCollectionFirstFailedCharacters.Add(failedCharacter);
        }
        var restockSteps = BuildXagmanFranchiseSteps(xagmanOwnerRunList.ToList(), 0);
        plugin.TaskRunner.Start(
            global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"),
            restockSteps,
            onFinished: OnXagmanFranchiseTaskFinished,
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"),
            preserveRunHistory: true);
        foreach (var failedCharacter in xagmanCollectionFirstFailedCharacters)
        {
            if (!plugin.TaskRunner.FailedCharacters.Contains(failedCharacter))
                plugin.TaskRunner.RecordFailedCharacter(failedCharacter);
        }
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = 0;
        return true;
    }

    private void FinalizeXagmanCollectionFirstOwnerSelections()
    {
        if (!IsXagmanCollectionFirstRunActive() || xagmanCollectionFirstOwnerFullPlan.Count == 0)
            return;

        var failed = plugin.TaskRunner.FailedCharacters
            .Where(character => !string.IsNullOrWhiteSpace(character))
            .Concat(xagmanCollectionFirstFailedCharacters)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var owner in xagmanCollectionFirstOwnerFullPlan)
        {
            if (failed.Contains(owner) || xagmanSkippedCharacters.Contains(owner) || xagmanPartialOwners.ContainsKey(owner))
                continue;
            if (xagmanOwnerCompletedKeys.Add(owner))
                changed = true;
            for (var index = 0; index < plugin.Configuration.XagmanFranchiseCharacters.Count; index++)
            {
                if (!plugin.Configuration.XagmanFranchiseCharacters[index].Equals(owner, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (xagmanFranchiseSelectedIndices.Remove(index))
                    changed = true;
                break;
            }
        }
        if (changed)
            InvalidateXagmanTradeCapacityForecast();
    }

    private void DrawXagmanRestockFarmersPopup(List<XagmanItemEntry> items, string id)
    {
        ImGui.SameLine();
        var locked = xagmanRunning || IsXagmanTaskRunnerActive();
        using (ImRaii.Disabled(locked))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S36B7D9F936B1BC7B") + $"##{id}RestockFarmers")))
            {
                xagmanRestockBuild = string.Empty;
                xagmanRestockDays = string.Empty;
                ImGui.OpenPopup($"{id}RestockFarmersPopup");
            }
        }
        using var popup = ImRaii.Popup($"{id}RestockFarmersPopup");
        if (!popup)
            return;
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S373F8C345AEFFAD8"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA1453B4CEB5444CA"));
        if (string.IsNullOrEmpty(xagmanRestockBuild))
        {
            foreach (var build in xagmanSubmarineRates.Keys.OrderBy(build => build, StringComparer.Ordinal))
            {
                if (ImGui.Selectable(build, false, ImGuiSelectableFlags.DontClosePopups))
                    xagmanRestockBuild = build;
            }
        }
        else
        {
            ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S4139488E0B23BE5A", xagmanRestockBuild));
            if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S967A86EEE73131C3") + $"##{id}RestockBuild")))
                xagmanRestockBuild = string.Empty;
            ImGui.SetNextItemWidth(Scale(180f));
            var enter = ImGui.InputTextWithHint($"##{id}RestockDays", global::XASlave.Localization.UiLocale.Get("SF64E1FC42B6DD972"), ref xagmanRestockDays, 10,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
            xagmanRestockDays = new string(xagmanRestockDays.Where(c => c >= '0' && c <= '9').ToArray());
            var valid = SubmarineBuildSupply.TryCalculateStock(xagmanRestockBuild, xagmanRestockDays, xagmanSubmarineRates, out var tanks, out var kits);
            if (valid)
                ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("SEA548396456E228E", tanks, kits));
            else
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S8ACA272338FD45B7"));
            using (ImRaii.Disabled(!valid || locked))
            {
                var apply = global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S31E392D1C0378BEC") + $"##{id}RestockApply"));
                if ((apply || enter) && valid && !locked)
                {
                    items.Clear();
                    items.Add(new XagmanItemEntry
                    {
                        ItemId = SubmarineSupplyCalculator.CeruleumTankItemId,
                        ItemName = "Ceruleum Tank", Mode = XagmanItemMode.Balance, Quantity = tanks,
                    });
                    items.Add(new XagmanItemEntry
                    {
                        ItemId = SubmarineSupplyCalculator.MagitekRepairKitItemId,
                        ItemName = "Magitek Repair Materials", Mode = XagmanItemMode.Balance, Quantity = kits,
                    });
                    SaveXagmanSharedItemsState();
                    ClearXagmanItemSearch();
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S19766ED6CCB2F4A3") + $"##{id}RestockCancel")))
            ImGui.CloseCurrentPopup();
    }

    private void DrawXagmanItemSection(string title, List<XagmanItemEntry> items, string id, bool searchOnly = false, bool allowGil = true)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), title);
        if (searchOnly)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S063B8072048B0B1F"));
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S054BCBBE578E1D87") + $"##{id}AddItem")))
            ImGui.OpenPopup($"{id}AddItemPopup");
        using (var imguiScope2000 = ImRaii.Popup($"{id}AddItemPopup"))
        if (imguiScope2000)
        {
            if (!searchOnly)
            {
                ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7EC7BE8BB725DBE7"));
                foreach (var selectorKind in new[]
                         {
                             XagmanItemSelectorKind.GreenItemGcSeals,
                             XagmanItemSelectorKind.GreenItemFcCreditsRankProgress,
                         })
                {
                    var selectorName = GetXagmanGreenValueSelectorNameMessage(selectorKind);
                    if (CanAddXagmanGreenValueSelector(items, selectorKind))
                    {
                        if (global::XASlave.Localization.UiNative.Selectable(
                                $"{selectorName}##{id}{selectorKind}",
                                false,
                                ImGuiSelectableFlags.DontClosePopups, display: selectorName.Display))
                        {
                            AddXagmanGreenValueSelector(items, selectorKind);
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SA8506217CBA987FC", selectorName.Display));
                    }
                }
                ImGui.Separator();
            }
            ImGui.SetNextItemWidth(Scale(280f));
            var searchChanged = ImGui.InputTextWithHint($"##{id}Search", global::XASlave.Localization.UiLocale.Get("SA0241849A87B9A67"), ref xagmanItemSearch, 128);
            if (searchChanged || !string.Equals(xagmanItemQueryCache, xagmanItemSearch, StringComparison.Ordinal))
            {
                xagmanItemQueryCache = xagmanItemSearch;
                xagmanItemResults = SearchXagmanItems(xagmanItemSearch);
                xagmanItemSearchDisplayCount = XagmanItemSearchPageSize;
            }
            ImGui.Separator();
            if (xagmanItemResults.Count == 0)
            {
                ImGui.TextDisabled(((string.IsNullOrWhiteSpace(xagmanItemSearch) ? global::XASlave.Localization.UiMessage.Key("S6552C370264334C5") : global::XASlave.Localization.UiMessage.Key("SC9A4FF4843DBAB2B"))).Display);
            }
            else
            {
                var availableResultCount = xagmanItemResults.Count(result =>
                    CanAddXagmanItem(items, result.ItemId, false, searchOnly));
                if (availableResultCount == 0)
                {
                    ImGui.TextDisabled(((searchOnly ? global::XASlave.Localization.UiMessage.Key("SEF503DE266AC3BE4") : global::XASlave.Localization.UiMessage.Key("SF173FD831CA93AEE"))).Display);
                }
                else
                {
                    var displayedResultCount = 0;
                    foreach (var result in xagmanItemResults)
                    {
                        if (!CanAddXagmanItem(items, result.ItemId, false, searchOnly))
                            continue;
                        if (displayedResultCount >= xagmanItemSearchDisplayCount)
                            break;

                        var label = $"{result.ItemName}##{id}{result.ItemId}";
                        if (ImGui.Selectable(label, false, ImGuiSelectableFlags.DontClosePopups))
                            AddXagmanItem(items, result.ItemId, result.ItemName, false, searchOnly);
                        displayedResultCount++;
                    }

                    if (xagmanItemSearchDisplayCount < availableResultCount)
                    {
                        var remainingResultCount = availableResultCount - xagmanItemSearchDisplayCount;
                        if (global::XASlave.Localization.UiNative.Button((string.Format(global::XASlave.Localization.UiLocale.Native("S8C6D9CEAC8223035"), Math.Min(XagmanItemSearchPageSize, remainingResultCount), remainingResultCount) + $"##{id}ShowMoreItems"), display: global::XASlave.Localization.UiLocale.FormatKey("S8C6D9CEAC8223035", Math.Min(XagmanItemSearchPageSize, remainingResultCount), remainingResultCount)))
                            xagmanItemSearchDisplayCount += XagmanItemSearchPageSize;
                    }
                }
            }

        }

        if (allowGil)
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S1C41C2A178AA300D") + $"##{id}Gil")))
                AddXagmanItem(items, 1, "Gil", false, searchOnly);
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S308D5A0903F2C571") + $"##{id}Lists")))
            ImGui.OpenPopup($"{id}ListsPopup");

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S2CFF9BAABF56CA00") + $"##{id}Import")))
        {
            xagmanItemImportJson = ImGui.GetClipboardText();
            var imported = TryImportXagmanItemList(title, xagmanItemImportJson, items, out var importMessage, searchOnly);
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Capture(importMessage, importMessage.Original));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            if (imported)
            {
                xagmanItemImportJson = string.Empty;
                ClearXagmanItemSearch();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S4B0D275D98629C40"));

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3664895579F0A7E6") + $"##{id}Export")))
            ImGui.OpenPopup($"{id}ExportPopup");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SFCAC9E1CFE13EA04"));

        if (!searchOnly)
        {
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S63CA100A355C5C9A") + $"##{id}SetAll")))
                ImGui.OpenPopup($"{id}SetAllPopup");
        }

        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S83B12C2216EFB4FD") + $"##{id}Clear")))
        {
            items.Clear();
            SaveXagmanSharedItemsState();
            ClearXagmanItemSearch();
        }
        if (!searchOnly && plugin.Configuration.XagmanRole == XagmanRole.FranchiseOwner)
            DrawXagmanRestockFarmersPopup(items, id);
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(GetXagmanStatusColor(arImportStatus), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }
        if (!searchOnly && HasXagmanConditionalItemPolicies(items))
        {
            var cfg = plugin.Configuration;
            var disableCollectionFirstToggle = xagmanRunning || cfg.XagmanOutsideNetworkHelper;
            using (ImRaii.Disabled(disableCollectionFirstToggle))
            {
                var prioritizeGivingFirst = cfg.XagmanPrioritizeCharactersGivingItemsFirst;
                if (global::XASlave.Localization.UiNative.Checkbox(
                        (global::XASlave.Localization.UiLocale.Native("S6997FF2777DC3E74") + $"##{id}PrioritizeGivingFirst"),
                        ref prioritizeGivingFirst))
                {
                    cfg.XagmanPrioritizeCharactersGivingItemsFirst = prioritizeGivingFirst;
                    cfg.Save();
                    InvalidateXagmanTradeCapacityForecast();
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(((cfg.XagmanOutsideNetworkHelper ? global::XASlave.Localization.UiMessage.Key("S4DA0C715611E0163") : (xagmanRunning ? global::XASlave.Localization.UiMessage.Key("SA8E0AFE26FDEA46E") : global::XASlave.Localization.UiMessage.Key("S565A7F39A3955E7C")))).Display);
            }
        }

        DrawXagmanSavedListsPopup(title, items, id, searchOnly);
        DrawXagmanExportPopup(title, items, id, searchOnly);
        if (!searchOnly)
            DrawXagmanMassModePopup(items, id);

        var tableColumnCount = searchOnly ? 6 : 8;
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3B9F6E0AF28BBA41"));
        using (var imguiScope2164 = ImRaii.Table($"{id}Table", tableColumnCount,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody, ScaledVector(0f, 150f)))
        if (imguiScope2164)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S652BCC3A47842889"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S3843971DCFDEE508"), ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SA688D83AE8586526"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SDF843AE1F7D87F76"), ImGuiTableColumnFlags.WidthFixed, Scale(85f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SAF0471978CBE2F77"), ImGuiTableColumnFlags.WidthFixed, Scale(95f));
            if (!searchOnly)
            {
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S5E23EC6A300DC60A"), ImGuiTableColumnFlags.WidthFixed, Scale(160f));
                global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S0CA90781F8517987"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            }
            global::XASlave.Localization.UiNative.TableSetupColumn($"Delete##{id}Delete", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, Scale(30f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                var isGreenSelector = IsXagmanGreenValueSelector(item.SelectorKind);
                ImGui.TextDisabled(((isGreenSelector ? global::XASlave.Localization.UiMessage.Key("SBDA050585A00F0F6") : global::XASlave.Localization.UiMessage.Raw(item.ItemId.ToString(CultureInfo.InvariantCulture)))).Display);
                ImGui.TableNextColumn();
                if (isGreenSelector)
                {
                    ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SF340B6D6EDEFF462"));
                }
                else if (!CanXagmanItemBeHq(item.ItemId))
                {
                    if (item.IsHq)
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("S47ED80745966812B"));
                    else
                        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SACF8F7780683D2D4"));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(((item.IsHq ? global::XASlave.Localization.UiMessage.Key("S8CD898411FBFCC5E") : global::XASlave.Localization.UiMessage.Key("S882894860617A688"))).Display);
                    }
                }
                else
                {
                    var isHq = item.IsHq;
                    if (ImGui.Checkbox($"##{id}Hq{i}", ref isHq))
                    {
                        if (!HasXagmanItemIdentityConflict(items, item, item.ItemId, isHq, item.Applicability))
                        {
                            item.IsHq = isHq;
                            SaveXagmanSharedItemsState();
                        }
                        else
                        {
                            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S51BF86D98CC157AF", item.ItemName, global::XASlave.Localization.UiMessage.Authored(GetXagmanItemPolicyLabel(item)), ((isHq ? global::XASlave.Localization.UiMessage.Key("SA688D83AE8586526") : global::XASlave.Localization.UiMessage.Key("SACF8F7780683D2D4")))));
                            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                        }
                    }
                }
                ImGui.TableNextColumn();
                if (isGreenSelector)
                {
                    ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SF340B6D6EDEFF462"));
                }
                else if (TryGetXagmanGreenItemDisplayValues(item.ItemId, item.IsHq, out var seals, out _))
                {
                    ImGui.TextUnformatted(seals.ToString("N0", CultureInfo.InvariantCulture));
                }
                else
                {
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("SBDA050585A00F0F6")).Display);
                }
                ImGui.TableNextColumn();
                if (isGreenSelector)
                {
                    ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SF340B6D6EDEFF462"));
                }
                else if (TryGetXagmanGreenItemDisplayValues(item.ItemId, item.IsHq, out _, out var fcScaled2))
                {
                    ImGui.TextUnformatted(FormatXagmanScaled2(fcScaled2));
                }
                else
                {
                    ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("SBDA050585A00F0F6")).Display);
                }
                if (!searchOnly)
                {
                    ImGui.TableNextColumn();
                    var policyIndex = GetXagmanItemPolicyOptionIndex(item);
                    ImGui.SetNextItemWidth(-1f);
                    var policyOptions = isGreenSelector ? xagmanGreenItemPolicyOptions : xagmanItemPolicyOptions;
                    var policyLabels = isGreenSelector ? xagmanGreenItemPolicyLabels : xagmanItemPolicyLabels;
                    if (global::XASlave.Localization.UiNative.Combo($"##{id}Mode{i}", ref policyIndex, policyLabels, policyLabels.Length))
                    {
                        var option = policyOptions[Math.Clamp(policyIndex, 0, policyOptions.Length - 1)];
                        if (!HasXagmanItemIdentityConflict(items, item, item.ItemId, item.IsHq, option.Applicability))
                        {
                            item.Mode = option.Mode;
                            item.Applicability = option.Applicability;
                            SaveXagmanSharedItemsState();
                        }
                        else
                        {
                            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S13ED1AB11A48825F", item.ItemName, option.Applicability));
                            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                        }
                    }
                    ImGui.TableNextColumn();
                    var qty = item.Quantity;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputInt($"##{id}Qty{i}", ref qty))
                    {
                        item.Quantity = Math.Max(0, qty);
                        SaveXagmanSharedItemsState();
                    }
                }
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##{id}Rm{i}")))
                    {
                        items.RemoveAt(i);
                        SaveXagmanSharedItemsState();
                        break;
                    }
                }
            }

        }
    }

    private float GetXagmanButtonWidth(string label)
    {
        return ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);
    }
    private static Vector4 GetXagmanStatusColor(string status)
    {
        return status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || status.Contains("cannot", StringComparison.OrdinalIgnoreCase)
            || status.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || status.Contains("already has", StringComparison.OrdinalIgnoreCase)
            || status.Contains("0 matching", StringComparison.OrdinalIgnoreCase)
            ? new Vector4(1.0f, 0.4f, 0.4f, 1.0f)
            : new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
    }
    private void ExportXagmanItemList(string title, IReadOnlyList<XagmanItemEntry> items, bool searchOnly = false)
    {
        var package = new XagmanItemListPackage
        {
            ListId = Guid.NewGuid().ToString("N"),
            Title = title,
            ExportedAtUtc = DateTime.UtcNow,
            Items = CloneXagmanItems(items, searchOnly),
        };
        var json = JsonSerializer.Serialize(package, xagmanItemListJsonOptions);
        ImGui.SetClipboardText(json);
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S30B2CD29261AC2F3", title, package.ListId));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void ExportXagmanTeamcraftItemList(string title, IReadOnlyList<XagmanItemEntry> items, bool searchOnly = false)
    {
        if (!searchOnly && HasXagmanGreenValueSelectors(items))
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S7F7E0EEB490562A4"));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(10);
            return;
        }
        if (!searchOnly && HasXagmanConditionalItemPolicies(items))
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SE7273BBF5875B726"));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(10);
            return;
        }

        var exportedItems = CloneXagmanItemsForTeamcraftExport(items, searchOnly);
        var text = BuildXagmanTeamcraftItemListText(title, exportedItems, searchOnly);
        ImGui.SetClipboardText(text);
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S67230661F7E79DE9", title, exportedItems.Count));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void DrawXagmanExportPopup(string title, IReadOnlyList<XagmanItemEntry> items, string id, bool searchOnly = false)
    {
        using var popup = ImRaii.Popup($"{id}ExportPopup");
        if (!popup)
            return;
        if (global::XASlave.Localization.UiNative.Selectable((global::XASlave.Localization.UiLocale.Native("SB25E5BAD663E489B") + $"##{id}XagmanExport")))
            ExportXagmanItemList(title, items, searchOnly);
        if (global::XASlave.Localization.UiNative.Selectable((global::XASlave.Localization.UiLocale.Native("S57CC1322214D7726") + $"##{id}TeamcraftExport")))
            ExportXagmanTeamcraftItemList(title, items, searchOnly);
    }

    private static List<XagmanItemEntry> CloneXagmanItemsForTeamcraftExport(IEnumerable<XagmanItemEntry> items, bool searchOnly = false)
    {
        return items
            .Where(item => item.ItemId > 0
                && !string.IsNullOrWhiteSpace(item.ItemName)
                && IsValidXagmanItemEntry(item, searchOnly))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group =>
            {
                var first = group.First();
                return new XagmanItemEntry
                {
                    ItemId = group.Key.ItemId,
                    ItemName = first.ItemName,
                    IsHq = group.Key.IsHq,
                    Mode = searchOnly ? XagmanItemMode.Give : first.Mode,
                    Quantity = Math.Max(1, searchOnly ? 1 : group.Sum(item => Math.Max(0, item.Quantity))),
                };
            })
            .OrderBy(item => item.Mode)
            .ThenBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToList();
    }

    private static string BuildXagmanTeamcraftItemListText(string title, IReadOnlyList<XagmanItemEntry> items, bool searchOnly = false)
    {
        var lines = new List<string>();
        if (searchOnly)
        {
            lines.Add($"{title} :");
            foreach (var item in items)
                lines.Add(GetXagmanTeamcraftItemLine(item));
            return string.Join(Environment.NewLine, lines);
        }

        foreach (var mode in new[] { XagmanItemMode.Give, XagmanItemMode.Take, XagmanItemMode.Balance, XagmanItemMode.TopUp })
        {
            var modeItems = items.Where(item => item.Mode == mode).ToList();
            if (modeItems.Count == 0)
                continue;
            if (lines.Count > 0)
                lines.Add(string.Empty);
            lines.Add($"{mode} :");
            foreach (var item in modeItems)
                lines.Add(GetXagmanTeamcraftItemLine(item));
        }
        if (lines.Count == 0)
            lines.Add($"{title} :");
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetXagmanTeamcraftItemLine(XagmanItemEntry item)
    {
        var itemName = item.IsHq ? $"{item.ItemName} (HQ)" : item.ItemName;
        return $"{Math.Max(1, item.Quantity).ToString(CultureInfo.InvariantCulture)}x {itemName}";
    }

    private bool TryImportXagmanItemList(string title, string clipboardText, List<XagmanItemEntry> items, out global::XASlave.Localization.UiMessage message, bool searchOnly = false)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
            return false;
        }
        return clipboardText.TrimStart().StartsWith("{", StringComparison.Ordinal)
            ? TryImportXagmanJsonItemList(title, clipboardText, items, out message, searchOnly)
            : TryImportXagmanTeamcraftItemList(title, clipboardText, items, out message, searchOnly);
    }

    private bool TryImportXagmanJsonItemList(string title, string json, List<XagmanItemEntry> items, out global::XASlave.Localization.UiMessage message, bool searchOnly = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
                return false;
            }
            if (TryGetXagmanJsonProperty(root, "items", out var xagmanItems) && xagmanItems.ValueKind == JsonValueKind.Array)
                return TryImportXagmanSlaveItemList(title, json, items, out message, searchOnly);
            if (TryGetXagmanJsonProperty(root, "recipes", out var artisanRecipes) && artisanRecipes.ValueKind == JsonValueKind.Array)
                return TryImportXagmanArtisanItemList(title, root, items, out message, searchOnly);
        }
        catch
        {
            message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
            return false;
        }

        message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
        return false;
    }

    private bool TryImportXagmanSlaveItemList(string title, string json, List<XagmanItemEntry> items, out global::XASlave.Localization.UiMessage message, bool searchOnly = false)
    {
        XagmanItemListPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<XagmanItemListPackage>(json, xagmanItemListJsonOptions);
        }
        catch
        {
            message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
            return false;
        }
        if (package == null || package.Items == null)
        {
            message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
            return false;
        }
        if (package.SchemaVersion > 3)
        {
            message = global::XASlave.Localization.UiMessage.Format("SFD6D7B3022C3E219", package.SchemaVersion);
            return false;
        }
        if (package.Items.Any(item => !Enum.IsDefined(item.SelectorKind))
            || (!searchOnly && package.Items.Any(item => !Enum.IsDefined(item.Mode) || !Enum.IsDefined(item.Applicability))))
        {
            message = global::XASlave.Localization.UiMessage.Key("SD2FB3C2A7883E36F");
            return false;
        }
        if (package.Items.Any(item => !IsValidXagmanItemEntry(item, searchOnly)))
        {
            message = global::XASlave.Localization.UiMessage.Key("S7A5EF78290F1D3B0");
            return false;
        }
        if (searchOnly && package.Items.Any(item => item.SelectorKind != XagmanItemSelectorKind.ExactItem))
        {
            message = global::XASlave.Localization.UiMessage.Key("S30B6DC1FFD0E78DC");
            return false;
        }
        if (!searchOnly)
        {
            var duplicatePolicy = package.Items
                .Where(item => IsValidXagmanItemEntry(item))
                .GroupBy(item => new
                {
                    item.SelectorKind,
                    ItemId = item.SelectorKind == XagmanItemSelectorKind.ExactItem ? item.ItemId : 0u,
                    IsHq = item.SelectorKind == XagmanItemSelectorKind.ExactItem && item.IsHq,
                    item.Applicability,
                })
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicatePolicy != null)
            {
                var duplicateLabel = (IsXagmanGreenValueSelector(duplicatePolicy.Key.SelectorKind) ? GetXagmanGreenValueSelectorNameMessage(duplicatePolicy.Key.SelectorKind) : global::XASlave.Localization.UiMessage.Format("SAB642B28C0E05BC6", duplicatePolicy.Key.ItemId, ((duplicatePolicy.Key.IsHq ? global::XASlave.Localization.UiMessage.Key("SA688D83AE8586526") : global::XASlave.Localization.UiMessage.Key("SACF8F7780683D2D4")))));
                message = global::XASlave.Localization.UiMessage.Format("S8F0DB1DF54560F29", duplicateLabel, duplicatePolicy.Key.Applicability);
                return false;
            }
        }
        var importedItems = CloneXagmanItems(package.Items, searchOnly);
        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        var listId = string.IsNullOrWhiteSpace(package.ListId) ? "no-list-id" : package.ListId;
        message = global::XASlave.Localization.UiMessage.Format("S834E7E438F476B00", importedItems.Count, title, listId);
        return true;
    }

    private bool TryImportXagmanArtisanItemList(string title, JsonElement root, List<XagmanItemEntry> items, out global::XASlave.Localization.UiMessage message, bool searchOnly = false)
    {
        if (!TryGetXagmanJsonProperty(root, "recipes", out var recipesElement) || recipesElement.ValueKind != JsonValueKind.Array)
        {
            message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
            return false;
        }

        var unresolvedIds = new HashSet<uint>();
        var resolvedItems = new List<XagmanItemEntry>();
        foreach (var recipeElement in recipesElement.EnumerateArray())
        {
            if (recipeElement.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryGetXagmanJsonProperty(recipeElement, "id", out var itemIdElement)
                || !TryGetXagmanJsonUInt(itemIdElement, out var itemId)
                || itemId == 0)
                continue;
            if (!TryGetXagmanJsonProperty(recipeElement, "quantity", out var quantityElement)
                || !TryGetXagmanJsonInt(quantityElement, out var quantity)
                || quantity <= 0)
                continue;
            if (!TryResolveXagmanItemById(itemId, out var resolvedItem))
            {
                unresolvedIds.Add(itemId);
                continue;
            }
            resolvedItems.Add(new XagmanItemEntry
            {
                ItemId = resolvedItem.ItemId,
                ItemName = resolvedItem.ItemName,
                IsHq = false,
                Mode = searchOnly ? XagmanItemMode.Give : XagmanItemMode.Balance,
                Quantity = searchOnly ? 0 : quantity,
            });
        }

        var importedItems = resolvedItems
            .GroupBy(item => item.ItemId)
            .Select(group => new XagmanItemEntry
            {
                ItemId = group.Key,
                ItemName = group.First().ItemName,
                IsHq = false,
                Mode = searchOnly ? XagmanItemMode.Give : XagmanItemMode.Balance,
                Quantity = searchOnly ? 0 : Math.Max(0, group.Sum(item => item.Quantity)),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
        if (importedItems.Count == 0)
        {
            message = global::XASlave.Localization.UiMessage.Key("S2F736EA82D51D2BF");
            return false;
        }

        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        var listName = TryGetXagmanJsonProperty(root, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : string.Empty;
        var sourceLabel = (string.IsNullOrWhiteSpace(listName) ? global::XASlave.Localization.UiMessage.Key("S91117818F38F25E7") : global::XASlave.Localization.UiMessage.Format("S3A978EA526BD6476", listName));
        message = (unresolvedIds.Count == 0 ? global::XASlave.Localization.UiMessage.Format("S3F7D22A6A0DD953B", importedItems.Count, title, sourceLabel) : global::XASlave.Localization.UiMessage.Format("S6D8A15DA69CD9737", importedItems.Count, title, sourceLabel, unresolvedIds.Count));
        return true;
    }

    private bool TryImportXagmanTeamcraftItemList(string title, string text, List<XagmanItemEntry> items, out global::XASlave.Localization.UiMessage message, bool searchOnly = false)
    {
        var parsedLines = ParseXagmanTeamcraftItemLines(text);
        if (parsedLines.Count == 0)
        {
            message = global::XASlave.Localization.UiMessage.Key("SD06469AAB4B097E5");
            return false;
        }

        var unresolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupportedHqNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedItems = new List<XagmanItemEntry>();
        foreach (var hqGroup in parsedLines.GroupBy(line => line.IsHq))
        {
            foreach (var group in hqGroup.GroupBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryResolveXagmanItemByName(group.Key, out var resolvedItem))
                {
                    unresolvedNames.Add(group.Key);
                    continue;
                }
                if (hqGroup.Key && !resolvedItem.CanBeHq)
                {
                    unsupportedHqNames.Add(resolvedItem.ItemName);
                    continue;
                }
                var quantity = group.Sum(line => line.Quantity);
                resolvedItems.Add(new XagmanItemEntry
                {
                    ItemId = resolvedItem.ItemId,
                    ItemName = resolvedItem.ItemName,
                    IsHq = hqGroup.Key,
                    Mode = searchOnly ? XagmanItemMode.Give : XagmanItemMode.Balance,
                    Quantity = searchOnly ? 0 : Math.Max(0, quantity),
                });
            }
        }

        if (unsupportedHqNames.Count > 0)
        {
            message = global::XASlave.Localization.UiMessage.Format("SE901E5623EA6F47A", string.Join(", ", unsupportedHqNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
            return false;
        }

        var importedItems = resolvedItems
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => new XagmanItemEntry
            {
                ItemId = group.Key.ItemId,
                ItemName = group.First().ItemName,
                IsHq = group.Key.IsHq,
                Mode = searchOnly ? XagmanItemMode.Give : XagmanItemMode.Balance,
                Quantity = searchOnly ? 0 : Math.Max(0, group.Sum(item => item.Quantity)),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
        if (importedItems.Count == 0)
        {
            message = global::XASlave.Localization.UiMessage.Key("S1B9A278F43E05833");
            return false;
        }

        items.Clear();
        items.AddRange(importedItems);
        SaveXagmanSharedItemsState();
        message = (unresolvedNames.Count == 0 ? global::XASlave.Localization.UiMessage.Format("S472AA2DAE2EAA65D", importedItems.Count, title) : global::XASlave.Localization.UiMessage.Format("S47DD7F7F807453AE", importedItems.Count, title, unresolvedNames.Count));
        return true;
    }

    private static List<(int Quantity, string ItemName, bool IsHq)> ParseXagmanTeamcraftItemLines(string text)
    {
        var items = new List<(int Quantity, string ItemName, bool IsHq)>();
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var match = xagmanTeamcraftItemLineRegex.Match(line);
            if (!match.Success)
                continue;
            if (!int.TryParse(match.Groups["quantity"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                continue;
            var itemName = match.Groups["itemName"].Value.Trim();
            if (itemName.EndsWith(" (HQ)", StringComparison.OrdinalIgnoreCase))
            {
                itemName = itemName.Substring(0, itemName.Length - 5).Trim();
                if (!string.IsNullOrWhiteSpace(itemName))
                    items.Add((quantity, itemName, true));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(itemName))
                items.Add((quantity, itemName, false));
        }
        return items;
    }

    private bool TryResolveXagmanItemByName(string itemName, out XagmanItemSearchEntry item)
    {
        var lookup = GetXagmanItemNameLookup();
        if (lookup.TryGetValue(itemName.Trim(), out var resolvedItem))
        {
            item = resolvedItem;
            return true;
        }
        item = new XagmanItemSearchEntry();
        return false;
    }

    private bool TryResolveXagmanItemById(uint itemId, out XagmanItemSearchEntry item)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet.TryGetRow(itemId, out var row))
        {
            var itemName = row.Name.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(itemName))
            {
                item = new XagmanItemSearchEntry
                {
                    ItemId = itemId,
                    ItemName = itemName,
                    CanBeHq = row.CanBeHq,
                };
                return true;
            }
        }

        item = new XagmanItemSearchEntry();
        return false;
    }

    private static bool TryGetXagmanJsonProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static bool TryGetXagmanJsonUInt(JsonElement element, out uint value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetUInt32(out value);
            case JsonValueKind.String:
                return uint.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryGetXagmanJsonInt(JsonElement element, out int value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private Dictionary<string, XagmanItemSearchEntry> GetXagmanItemNameLookup()
    {
        if (xagmanItemNameLookupCache != null)
            return xagmanItemNameLookupCache;

        var lookup = new Dictionary<string, XagmanItemSearchEntry>(StringComparer.OrdinalIgnoreCase);
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var row in itemSheet)
        {
            var itemName = row.Name.ToString().Trim();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(itemName) || lookup.ContainsKey(itemName))
                continue;
            lookup[itemName] = new XagmanItemSearchEntry
            {
                ItemId = row.RowId,
                ItemName = itemName,
                CanBeHq = row.CanBeHq,
            };
        }
        xagmanItemNameLookupCache = lookup;
        return lookup;
    }
    private void ClearXagmanItemSearch()
    {
        xagmanItemSearch = string.Empty;
        xagmanItemQueryCache = string.Empty;
        xagmanItemResults = new List<XagmanItemSearchEntry>();
        xagmanItemSearchDisplayCount = XagmanItemSearchPageSize;
    }

    private void DrawXagmanQueueTable()
    {
        var focusTony = GetXagmanQueueFocusTony();
        var queue = GetXagmanQueueForTony(focusTony);
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S3B2FE03E36893916"));
        ImGui.TextDisabled(((string.IsNullOrWhiteSpace(focusTony) ? global::XASlave.Localization.UiMessage.Key("SCAE8B5BD665F9249") : global::XASlave.Localization.UiMessage.Format("SEE3C6444DB9F3C02", focusTony))).Display);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3B9F6E0AF28BBA41"));
        using (var imguiScope2806 = ImRaii.Table("XagmanQueueTable", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody, ScaledVector(0f, 100f)))
        if (imguiScope2806)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(35f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S4B1B8AA3608A26DA"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S920E413C7D411B61"), ImGuiTableColumnFlags.WidthFixed, Scale(120f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S2D9E28289FACAB94"), ImGuiTableColumnFlags.WidthFixed, Scale(150f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S29035E784F9305C0"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableHeadersRow();
            for (var i = 0; i < queue.Count; i++)
            {
                var peer = queue[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled((i + 1).ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(peer.ActiveCharacter) ? peer.CharacterName : peer.ActiveCharacter);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.Status.ToString());
                ImGui.TableNextColumn();
                ImGui.TextDisabled(((peer.QueueRequestedAtUtc == DateTime.MinValue ? global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9") : global::XASlave.Localization.UiMessage.Raw(peer.QueueRequestedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)))).Display);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(((string.IsNullOrWhiteSpace(peer.ActiveTradePartner) ? global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9") : global::XASlave.Localization.UiMessage.Raw(peer.ActiveTradePartner))).Display);
            }

        }
    }
    private int GetXagmanLocalOwnerTotalCharacters()
    {
        if (xagmanOwnerTotalCharacters > 0)
            return Math.Max(0, xagmanOwnerTotalCharacters);
        if (xagmanOwnerRunPlan.Count > 0)
            return xagmanOwnerRunPlan.Count;
        return Math.Max(0, xagmanOwnerRunList.Count);
    }
    private int GetXagmanLocalTonyTotalCharacters()
    {
        if (xagmanTonyTotalCharacters > 0)
            return Math.Max(0, xagmanTonyTotalCharacters);
        if (xagmanTonyRunPlan.Count > 0)
            return xagmanTonyRunPlan.Count;
        return Math.Max(0, xagmanTonyRunList.Count);
    }
    private void DrawXagmanAutoCleanQueueOption()
    {
        ImGui.SameLine();
        var enabled = plugin.Configuration.XagmanAutoCleanQueueList;
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S2A8195BDEFFE6427") + "##xagmanAutoCleanQueue"), ref enabled))
        {
            plugin.Configuration.XagmanAutoCleanQueueList = enabled;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SA0BC3E2FD4F4EE31"));
    }

    private void AutoCleanXagmanQueueResults()
    {
        if (!plugin.Configuration.XagmanAutoCleanQueueList || xagmanQueueResultsCleaned)
            return;
        xagmanQueueResultsCleaned = true;
        CaptureXagmanRunSnapshot();
        plugin.TaskRunner.AddLog($"Xagman: auto-cleaned queue results; {xagmanLastRunTonyPlan.Count + xagmanLastRunOwnerPlan.Count} character(s) needing attention retained.");
    }

    private void DrawXagmanProcessingLists(TaskRunner runner)
    {
        if (xagmanRunning && !xagmanQueueResultsCleaned)
        {
            var hasTonyPlan = xagmanTonyRunPlan.Count > 0;
            var hasOwnerPlan = xagmanOwnerRunPlan.Count > 0;
            if (!hasTonyPlan && !hasOwnerPlan)
                return;

            if (hasTonyPlan)
            {
                if (xagmanActiveRole == XagmanRole.Tony && !plugin.Configuration.XagmanOutsideNetworkHelper)
                    ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), GetXagmanTonyEtaLabel().Display);
                DrawXagmanProcessingList(
                    (global::XASlave.Localization.UiMessage.Key("S373665EAB6D993FF")).Display,
                    xagmanTonyRunPlan,
                    GetXagmanLocalTonyCompletedCharacters(),
                    xagmanActiveRole == XagmanRole.Tony
                        && xagmanTonyRunList.Any(key => key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase))
                        ? xagmanActiveCharacter : string.Empty,
                    runner.FailedCharacters,
                    xagmanSkippedCharacters);
            }

            if (hasTonyPlan && hasOwnerPlan)
                ImGui.Spacing();

            if (hasOwnerPlan)
            {
                if (TryGetXagmanOwnerRouteWait(out var waitingState, out var waitingEstimate))
                {
                    ImGui.TextUnformatted(waitingState.Display);
                    ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), waitingEstimate.Display);
                }
                var etaLabel = GetXagmanOwnerEtaLabel(xagmanOwnerRunPlan, xagmanCharDurationSeconds);
                if (!string.IsNullOrWhiteSpace(etaLabel))
                    ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), etaLabel);
                DrawXagmanProcessingList(
                    (global::XASlave.Localization.UiMessage.Key("S74DB8FC4A469630B")).Display,
                    xagmanOwnerRunPlan,
                    GetXagmanLocalOwnerCompletedCharacters(),
                    xagmanActiveRole == XagmanRole.FranchiseOwner ? xagmanActiveCharacter : string.Empty,
                    runner.FailedCharacters,
                    xagmanSkippedCharacters,
                    xagmanCharDurationSeconds,
                    xagmanOwnerCompletedKeys,
                    xagmanPartialOwners);
            }

            if (xagmanActiveRole == XagmanRole.Tony)
            {
                var partialOwners = plugin.XagmanPeers.Peers
                    .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.FranchiseOwner)
                    .Where(IsXagmanPeerInCurrentRunPhase)
                    .Where(IsXagmanPeerInSupplyCoordinatorScope)
                    .SelectMany(peer => peer.PartialOwners ?? new List<XagmanPartialOwnerState>())
                    .Where(owner => owner != null && !string.IsNullOrWhiteSpace(owner.CharacterNameWorld))
                    .Select(owner => owner.CharacterNameWorld)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (partialOwners.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1.0f, 0.65f, 0.2f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("SBFC38B7184D1BD64", partialOwners.Count));
                    foreach (var characterNameWorld in partialOwners)
                        ImGui.TextUnformatted(characterNameWorld);
                }
            }

            return;
        }

        // Keep failed, skipped and partially filled owners reviewable after completion cleanup.
        if (!xagmanHasLastRunSnapshot)
            return;

        var hasTonySnapshot = xagmanLastRunTonyPlan.Count > 0;
        var hasOwnerSnapshot = xagmanLastRunOwnerPlan.Count > 0;
        if (!hasTonySnapshot && !hasOwnerSnapshot)
            return;

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S335174013C7FF708"));
        if (hasTonySnapshot)
        {
            DrawXagmanProcessingList(
                (global::XASlave.Localization.UiMessage.Key("S373665EAB6D993FF")).Display,
                xagmanLastRunTonyPlan,
                xagmanLastRunTonyCompleted,
                string.Empty,
                xagmanLastRunFailedCharacters,
                xagmanLastRunSkippedCharacters);
        }

        if (hasTonySnapshot && hasOwnerSnapshot)
            ImGui.Spacing();

        if (hasOwnerSnapshot)
        {
            var etaLabel = GetXagmanOwnerEtaLabel(xagmanLastRunOwnerPlan, xagmanLastRunCharDurations);
            if (!string.IsNullOrWhiteSpace(etaLabel))
                ImGui.TextDisabled(etaLabel);
            DrawXagmanProcessingList(
                (global::XASlave.Localization.UiMessage.Key("S74DB8FC4A469630B")).Display,
                xagmanLastRunOwnerPlan,
                xagmanLastRunOwnerCompleted,
                string.Empty,
                xagmanLastRunFailedCharacters,
                xagmanLastRunSkippedCharacters,
                xagmanLastRunCharDurations,
                xagmanLastRunOwnerCompletedKeys,
                xagmanLastRunPartialOwners);
        }

        var failedCount = xagmanLastRunFailedCharacters.Count;
        var skippedCount = xagmanLastRunSkippedCharacters.Count;
        var partialCount = xagmanLastRunPartialOwners.Count;
        if (failedCount > 0 || skippedCount > 0 || partialCount > 0)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SB38AAD4320E976C1", failedCount, skippedCount, partialCount));
        if (!xagmanRunning && !IsXagmanTaskRunnerActive() && global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9D613B6C8ECDCA7D") + "##xagmanClearResults")))
            ClearXagmanRunSnapshot();
    }

    private void DrawXagmanProcessingList(
        string label,
        IReadOnlyList<string> runPlan,
        int completed,
        string activeCharacter,
        IReadOnlyCollection<string> failedKeys,
        IReadOnlyCollection<string> skippedKeys,
        IReadOnlyDictionary<string, double>? durations = null,
        IReadOnlyCollection<string>? completedKeys = null,
        IReadOnlyDictionary<string, XagmanPartialOwnerState>? partialOwners = null)
    {
        if (runPlan.Count == 0)
            return;

        bool ContainsKey(IReadOnlyCollection<string> keys, string character)
        {
            if (keys == null || keys.Count == 0)
                return false;
            if (keys is HashSet<string> set)
                return set.Contains(character);
            foreach (var key in keys)
            {
                if (key.Equals(character, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        var safeCompleted = completedKeys == null
            ? Math.Max(0, Math.Min(completed, runPlan.Count))
            : runPlan.Count(character => ContainsKey(completedKeys, character)
                && !(partialOwners?.ContainsKey(character) ?? false)
                && !ContainsKey(failedKeys, character)
                && !ContainsKey(skippedKeys, character));
        var partialCount = runPlan.Count(character => partialOwners?.ContainsKey(character) ?? false);
        ImGui.TextDisabled(((partialCount > 0 ? global::XASlave.Localization.UiMessage.Format("S69F99D8667AAAF13", label, safeCompleted, runPlan.Count, partialCount) : global::XASlave.Localization.UiMessage.Format("S8B4157F20FD802BB", label, safeCompleted, runPlan.Count))).Display);
        for (var i = 0; i < runPlan.Count; i++)
        {
            var character = runPlan[i];
            XagmanPartialOwnerState? partialOwner = null;
            partialOwners?.TryGetValue(character, out partialOwner);
            var skipped = ContainsKey(skippedKeys, character);
            var failed = !skipped && ContainsKey(failedKeys, character);
            var isActive = !skipped
                && !failed
                && !string.IsNullOrWhiteSpace(activeCharacter)
                && character.Equals(activeCharacter, StringComparison.OrdinalIgnoreCase);
            var timeSuffix = durations != null && durations.TryGetValue(character, out var charSeconds)
                ? $"  ({FormatXagmanDuration(charSeconds)})"
                : string.Empty;

            if (skipped)
                ImGui.TextColored(new Vector4(0.72f, 0.45f, 1.0f, 1.0f), (global::XASlave.Localization.UiMessage.Format("S406858EEEBD4C9A6", i + 1, character, timeSuffix)).Display);
            else if (failed)
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S5DA1913597476EC7", i + 1, character, timeSuffix));
            else if (partialOwner != null)
                ImGui.TextColored(new Vector4(1.0f, 0.65f, 0.2f, 1.0f), (global::XASlave.Localization.UiMessage.Format("SBD32E444E3C9D901", ((isActive ? global::XASlave.Localization.UiMessage.Key("S62B67E1F685B7FEF") : global::XASlave.Localization.UiMessage.Key("S7ACE431CB61584CB"))), i + 1, character, timeSuffix, GetXagmanPartialOwnerLabelMessage(partialOwner).Display)).Display);
            else if (isActive)
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), (global::XASlave.Localization.UiMessage.Format("S7B9E77EC5C5454DB", i + 1, character, timeSuffix)).Display);
            else if (completedKeys != null ? ContainsKey(completedKeys, character) : i < safeCompleted)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("SE431A6CB411C6493", i + 1, character, timeSuffix));
            else
                ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Format("SB023584B6E9AAA07", i + 1, character, timeSuffix)).Display);

            if (partialOwner != null)
                ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Format("S53EDFD573CDAAD81", GetXagmanPartialOwnerDetailMessage(partialOwner)).Display);
        }
    }

    private int GetXagmanLocalTonyCompletedCharacters()
    {
        var total = GetXagmanLocalTonyTotalCharacters();
        if (total <= 0)
            return 0;
        return Math.Max(0, Math.Min(xagmanTonyCompletedCharacters, total));
    }
    private (int Total, int Completed, int Remaining) GetXagmanOwnerProgressSnapshot(bool enabledOnly = true)
    {
        var ownerPeers = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => !enabledOnly || peer.XagmanEnabled)
            .ToList();
        var total = ownerPeers.Sum(peer => Math.Max(0, peer.TotalCharacters));
        var completed = ownerPeers.Sum(peer => Math.Max(0, peer.CompletedCharacters));
        if (total <= 0)
            total = GetSelectedXagmanFranchiseCharacters().Count;
        completed = Math.Min(Math.Max(0, completed), total);
        var remaining = total > 0
            ? Math.Max(0, total - completed)
            : 0;
        return (total, completed, remaining);
    }
    private (int Total, int Completed) GetXagmanDisplayProgressCounts()
    {
        if (xagmanActiveRole == XagmanRole.Tony)
        {
            var ownerProgress = GetXagmanOwnerProgressSnapshot();
            if (ownerProgress.Total > 0)
                return (ownerProgress.Total, ownerProgress.Completed);
            return (GetXagmanLocalTonyTotalCharacters(), GetXagmanLocalTonyCompletedCharacters());
        }

        return (GetXagmanLocalOwnerTotalCharacters(), GetXagmanLocalOwnerCompletedCharacters());
    }
    private int GetXagmanLocalTonyCurrentCharacterNumber()
    {
        var total = GetXagmanLocalTonyTotalCharacters();
        if (total <= 0)
            return 0;
        var completed = GetXagmanLocalTonyCompletedCharacters();
        return !xagmanRunning || xagmanActiveRole != XagmanRole.Tony || string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? completed
            : Math.Min(total, completed + 1);
    }
    private void UpdateXagmanTonyTaskRunnerProgress(string? partnerName = null)
    {
        var activeTony = (string.IsNullOrWhiteSpace(xagmanActiveCharacter) ? global::XASlave.Localization.UiMessage.Key("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiMessage.Raw(xagmanActiveCharacter));
        var ownerProgress = GetXagmanOwnerProgressSnapshot();
        if (ownerProgress.Total > 0)
        {
            plugin.TaskRunner.TotalItems = ownerProgress.Total;
            plugin.TaskRunner.CompletedItems = ownerProgress.Completed;
            var remainingText = global::XASlave.Localization.UiMessage.Format("SDC8D32FC49E1E099", ownerProgress.Remaining);
            plugin.TaskRunner.CurrentItemLabelMessage = (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Format("S975464F184C71026", ownerProgress.Completed, ownerProgress.Total, activeTony, remainingText) : global::XASlave.Localization.UiMessage.Format("S281D93DCCE67B26F", ownerProgress.Completed, ownerProgress.Total, activeTony, partnerName, remainingText));
            return;
        }

        var total = GetXagmanLocalTonyTotalCharacters();
        if (total <= 0)
            return;
        var current = Math.Max(1, GetXagmanLocalTonyCurrentCharacterNumber());
        plugin.TaskRunner.TotalItems = total;
        plugin.TaskRunner.CompletedItems = current;
        plugin.TaskRunner.CurrentItemLabelMessage = (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Format("S5FB6F2415218C668", current, total, activeTony) : global::XASlave.Localization.UiMessage.Format("S5FC1AA1B763FD57F", current, total, activeTony, partnerName));
    }
    private int GetXagmanLocalOwnerCompletedCharacters()
    {
        var total = GetXagmanLocalOwnerTotalCharacters();
        if (total <= 0)
            return 0;
        return Math.Max(0, Math.Min(xagmanOwnerCompletedCharacters, total));
    }
    private static int GetXagmanPeerCompletedCharacterCount(XagmanPeerPresence peer)
    {
        var total = Math.Max(0, peer.TotalCharacters);
        if (total <= 0)
            return 0;
        return Math.Max(0, Math.Min(peer.CompletedCharacters, total));
    }
    private static int GetXagmanPeerCurrentCharacterNumber(XagmanPeerPresence peer)
    {
        var total = Math.Max(0, peer.TotalCharacters);
        if (total <= 0)
            return 0;
        var completed = GetXagmanPeerCompletedCharacterCount(peer);
        return peer.Status == XagmanStatus.Completed
            ? total
            : Math.Min(total, completed + 1);
    }
    private static string GetXagmanPeerProgressText(XagmanPeerPresence peer)
    {
        var total = Math.Max(0, peer.TotalCharacters);
        if (total <= 0)
            return "-";
        return $"{GetXagmanPeerCurrentCharacterNumber(peer)}/{total}";
    }

    private static string GetXagmanPeerLocationText(XagmanPeerPresence peer)
    {
        var territory = string.IsNullOrWhiteSpace(peer.TerritoryName) ? "-" : peer.TerritoryName;
        if (!peer.LocalPositionAvailable)
            return territory;
        return $"{territory} @ {peer.LocalPositionX:0.000}, {peer.LocalPositionY:0.000}, {peer.LocalPositionZ:0.000}";
    }

    private static string GetXagmanPeerCollectionFirstPreferenceText(XagmanPeerPresence peer)
    {
        if (peer.Role != XagmanRole.FranchiseOwner)
            return "-";
        if (peer.CoordinationProtocolRevision != XagmanCollectionFirstCoordinationProtocolRevision)
            return global::XASlave.Localization.UiLocale.Get("SF72D9AA37B52BDE3");
        if (!peer.CollectionFirstRequested)
            return global::XASlave.Localization.UiLocale.Get("SCA7981B46ECF2C17");
        return peer.HasConditionalItemPolicies ? global::XASlave.Localization.UiLocale.Get("S130011756125313C") : global::XASlave.Localization.UiLocale.Get("S96C34A0719EB565C");
    }

    private static string GetXagmanPeerConfigurationLabel(XagmanPeerPresence peer)
    {
        var displayName = GetXagmanPeerDisplayName(peer);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = peer.ProcessId > 0 ? $"PID {peer.ProcessId}" : "Unnamed client";
        if (string.IsNullOrWhiteSpace(peer.InstanceId))
            return displayName;
        var shortInstanceId = peer.InstanceId.Substring(0, Math.Min(8, peer.InstanceId.Length));
        return $"{displayName} [{shortInstanceId}]";
    }

    private void DrawXagmanPeersTable()
    {
        var peers = plugin.XagmanPeers.Peers;
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S64B769BCAF8F5574"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SA9F1E01E4677CA42", peers.Count));

        // Add remaining characters section
        if (xagmanRunning)
        {
            var ownerProgress = GetXagmanOwnerProgressSnapshot();
            var totalFranchiseOwners = ownerProgress.Total;
            var completedFranchiseOwners = ownerProgress.Completed;
            var remainingFranchiseOwners = ownerProgress.Remaining;

            ImGui.Spacing();
            if (remainingFranchiseOwners > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("S98E5C4A72F817339", remainingFranchiseOwners));
                ImGui.TextDisabled(((totalFranchiseOwners > 0 ? global::XASlave.Localization.UiMessage.Format("SA2AF42E9D445DCFB", completedFranchiseOwners, totalFranchiseOwners) : global::XASlave.Localization.UiMessage.Key("SC731237A3630F203"))).Display);

                var showManualReadyToTradeButton = false;

                // Add "Ready to Trade" button for Tony when remaining characters exist
                if (showManualReadyToTradeButton && xagmanActiveRole == XagmanRole.Tony && xagmanStatus is XagmanStatus.AtMeetSpot or XagmanStatus.ReadyForQueue)
                {
                    if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0D973B4FA1EAB15D") + "##xagmanReadyToTrade")))
                    {
                        SendReadyToTradeSignal();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SED065F4E949A73BB"));
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.8f, 1.0f), global::XASlave.Localization.UiLocale.Get("SCCC4A8DC64C64A0F"));
            }
            ImGui.Spacing();
        }

        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3B9F6E0AF28BBA41"));
        using (var imguiScope3169 = ImRaii.Table("XagmanPeersTable", 10,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody, ScaledVector(0f, 100f)))
        if (imguiScope3169)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(55f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S14736A2EB9F4159F"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SD60DBBA079223254"), ImGuiTableColumnFlags.WidthFixed, Scale(70f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S920E413C7D411B61"), ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S78AE647DC5544D22"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S15B61974B2707A7B"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S3B2FE03E36893916"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S29035E784F9305C0"), ImGuiTableColumnFlags.WidthStretch);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8894CF30AED05A78"), ImGuiTableColumnFlags.WidthFixed, Scale(90f));
            global::XASlave.Localization.UiNative.TableHeadersRow();
            foreach (var peer in peers.OrderBy(peer => peer.CharacterName, StringComparer.OrdinalIgnoreCase).ThenBy(peer => peer.ProcessId))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(peer.ActiveCharacter) ? peer.CharacterName : peer.ActiveCharacter);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetXagmanPeerProgressText(peer));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.Role.ToString());
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetXagmanPeerCollectionFirstPreferenceText(peer));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.Status.ToString());
                ImGui.TableNextColumn();
                ImGui.TextDisabled(peer.CurrentWorld);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(GetXagmanPeerLocationText(peer));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(((peer.QueueNumber > 0 ? global::XASlave.Localization.UiMessage.Raw(peer.QueueNumber.ToString(CultureInfo.InvariantCulture)) : global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9"))).Display);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(((string.IsNullOrWhiteSpace(peer.ActiveTradePartner) ? global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9") : global::XASlave.Localization.UiMessage.Raw(peer.ActiveTradePartner))).Display);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(((peer.LastSeenUtc == DateTime.MinValue ? global::XASlave.Localization.UiMessage.Key("S3973E022E93220F9") : global::XASlave.Localization.UiMessage.Raw(peer.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)))).Display);
            }

        }
    }

    private void SendReadyToTradeSignal()
    {
        if (xagmanActiveRole != XagmanRole.Tony || xagmanStatus is not XagmanStatus.AtMeetSpot and not XagmanStatus.ReadyForQueue)
            return;

        // Update Tony status to signal ready for trading
        xagmanStatus = XagmanStatus.ReadyForQueue;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S9EACF2B7EA39F451"));

        // Broadcast the ready signal to all peers
        PublishXagmanPresence();
        StartAllXagmanPeers();

        // Log the action
        plugin.TaskRunner.AddLog("Xagman: Tony is attempting to signal ready-to-trade to all Franchise Owners.");
    }

    private List<XagmanPeerPresence> GetXagmanCommandTargetPeers(bool includeIdleClients = false)
    {
        if (plugin.XagmanPeers == null || plugin.XagmanPeers.IsDisposed)
            return new List<XagmanPeerPresence>();

        return plugin.XagmanPeers.Peers
            .Where(peer => includeIdleClients || peer.XagmanEnabled)
            .Where(peer => IsXagmanPeerFresh(peer, 10.0))
            .ToList();
    }

    private System.Threading.Tasks.Task RunXagmanPeerUiActionAsync(System.Action action)
    {
        return Plugin.RunOnGameThread(action);
    }

    private System.Threading.Tasks.Task AddXagmanPeerLogAsync(string message)
    {
        return RunXagmanPeerUiActionAsync(() => plugin.TaskRunner.AddLog(message));
    }

    private async System.Threading.Tasks.Task<bool> EnsureXagmanPeerCommandChannelAsync(string commandLabel)
    {
        if (plugin.XagmanPeers == null || plugin.XagmanPeers.IsDisposed || !plugin.XagmanPeers.IsStarted)
        {
            await AddXagmanPeerLogAsync($"Xagman: Cannot send {commandLabel} command - peer service is not available").ConfigureAwait(false);
            return false;
        }

        if (plugin.XagmanPeers.IsConnected)
            return true;

        await AddXagmanPeerLogAsync($"Xagman: Waiting for local TCP peer connection before sending {commandLabel} command...").ConfigureAwait(false);
        if (await plugin.XagmanPeers.WaitForConnectionAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            return true;

        await AddXagmanPeerLogAsync($"Xagman: Cannot send {commandLabel} command - local TCP peer connection is not ready ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
        return false;
    }

    private async System.Threading.Tasks.Task<List<XagmanPeerPresence>> WaitForXagmanCommandTargetPeersAsync(
        double timeoutSeconds,
        bool includeIdleClients = false)
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadlineUtc)
        {
            var peers = GetXagmanCommandTargetPeers(includeIdleClients);
            if (peers.Count > 0)
                return peers;
            await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
        }

        return GetXagmanCommandTargetPeers(includeIdleClients);
    }

    private void StartAllXagmanPeers()
    {
        _ = StartAllXagmanPeersAsync();
    }

    private async System.Threading.Tasks.Task StartAllXagmanPeersAsync()
    {
        if (xagmanRunning && IsXagmanCapacityRecoveryActive()) return;
        if (System.Threading.Interlocked.CompareExchange(ref xagmanStartAllInFlight, 1, 0) != 0)
        {
            await AddXagmanPeerLogAsync(
                "Xagman: Start All Peers is already freezing or rebroadcasting the current run; the overlapping request was ignored.").ConfigureAwait(false);
            return;
        }

        try
        {
            if (!await EnsureXagmanPeerCommandChannelAsync("start").ConfigureAwait(false))
                return;

            var peers = await WaitForXagmanCommandTargetPeersAsync(3.0).ConfigureAwait(false);
            if (peers.Count == 0)
            {
                await AddXagmanPeerLogAsync("Xagman: No connected peers to send start command to").ConfigureAwait(false);
                return;
            }

            var collectionFirst = xagmanRunning
                && xagmanActiveRole == XagmanRole.Tony
                && IsXagmanCollectionFirstCollectionPhase();
            if (collectionFirst && xagmanStatus == XagmanStatus.Error)
            {
                await AddXagmanPeerLogAsync(
                    "Xagman: collection-first Start All was refused because Tony is in Error. Explicitly stop/recover Tony before starting a new frozen run.").ConfigureAwait(false);
                return;
            }
            if (xagmanRunning
                && xagmanActiveRole == XagmanRole.Tony
                && IsXagmanCollectionFirstRestockPhase())
            {
                // Restock is released by Tony presence after the frozen collection barrier. A Tony
                // rotation in phase two must not send another start directive or mutate the cohort.
                return;
            }

            var franchisePeers = peers
                .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
                .Where(peer => !string.IsNullOrWhiteSpace(peer.InstanceId))
                .Where(peer => string.IsNullOrWhiteSpace(peer.SupplyCoordinatorInstanceId)
                    || peer.SupplyCoordinatorInstanceId == plugin.InstanceId)
                .ToList();
            if (xagmanRunning && xagmanActiveRole == XagmanRole.Tony
                && franchisePeers.Any(peer => peer.SupplyCycleRevision != XagmanSupplyCycleRevision))
            {
                await RunXagmanPeerUiActionAsync(() => FailXagmanSupplyCycle(
                    global::XASlave.Localization.UiMessage.Key("S18EACC0161B3AF1F"))).ConfigureAwait(false);
                return;
            }
            var negotiateStartupMode = !collectionFirst
                && !xagmanCollectionFirstStartupModeNegotiated;
            var requestingCollectionFirst = negotiateStartupMode
                ? franchisePeers
                    .Where(peer => peer.CollectionFirstRequested)
                    .ToList()
                : new List<XagmanPeerPresence>();
            if (requestingCollectionFirst.Count > 0)
            {
                var incompatiblePeers = franchisePeers
                    .Where(peer => peer.CoordinationProtocolRevision != XagmanCollectionFirstCoordinationProtocolRevision)
                    .ToList();
                if (incompatiblePeers.Count > 0)
                {
                    var incompatibleNames = string.Join(", ",
                        incompatiblePeers.Select(GetXagmanPeerConfigurationLabel));
                    await AddXagmanPeerLogAsync(
                        $"Xagman: collection-first start refused before freezing the run because these Franchise Owner clients do not advertise coordination protocol {XagmanCollectionFirstCoordinationProtocolRevision}: {incompatibleNames}. Reload every participating client from the same build.").ConfigureAwait(false);
                    return;
                }

                var invalidPeers = requestingCollectionFirst
                    .Where(peer => !peer.HasConditionalItemPolicies)
                    .ToList();
                if (invalidPeers.Count > 0)
                {
                    var invalidNames = string.Join(", ",
                        invalidPeers.Select(GetXagmanPeerConfigurationLabel));
                    await AddXagmanPeerLogAsync(
                        $"Xagman: collection-first start refused before freezing the run because these Franchise Owner clients enabled Prioritize Characters Giving Items First but have no conditional Shared Item policies: {invalidNames}.").ConfigureAwait(false);
                    return;
                }

                if (requestingCollectionFirst.Count != franchisePeers.Count)
                {
                    var enabledNames = string.Join(", ",
                        requestingCollectionFirst.Select(GetXagmanPeerConfigurationLabel));
                    var disabledNames = string.Join(", ",
                        franchisePeers
                            .Where(peer => !peer.CollectionFirstRequested)
                            .Select(GetXagmanPeerConfigurationLabel));
                    await AddXagmanPeerLogAsync(
                        $"Xagman: collection-first start refused before freezing the run because Franchise Owner preferences are mixed. Enabled: {enabledNames}. Disabled: {disabledNames}.").ConfigureAwait(false);
                    return;
                }

                if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony)
                {
                    var enabledNames = string.Join(", ",
                        requestingCollectionFirst.Select(GetXagmanPeerConfigurationLabel));
                    await AddXagmanPeerLogAsync(
                        $"Xagman: collection-first start needs Tony to be running before the unanimous FO preference can be frozen. Enabled FOs: {enabledNames}. Tony's local Prioritize setting is ignored.").ConfigureAwait(false);
                    return;
                }
                if (xagmanStatus == XagmanStatus.Error)
                {
                    await AddXagmanPeerLogAsync(
                        "Xagman: collection-first start was refused because Tony is in Error. Explicitly stop/recover Tony before starting a new frozen run.").ConfigureAwait(false);
                    return;
                }
            }

            var greenValueEnabled = HasXagmanGreenValueSelectors(plugin.Configuration.XagmanItems);
            if (greenValueEnabled)
            {
                if (franchisePeers.Count == 0)
                {
                    await AddXagmanPeerLogAsync(
                        "Xagman: green-value start needs at least one connected Franchise Owner client.").ConfigureAwait(false);
                    return;
                }
                var incompatibleGreenPeers = franchisePeers
                    .Where(peer => peer.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
                    .ToList();
                if (incompatibleGreenPeers.Count > 0)
                {
                    await AddXagmanPeerLogAsync(
                        $"Xagman: green-value start refused because {incompatibleGreenPeers.Count} Franchise Owner client(s) do not advertise green-value protocol {XagmanGreenValueProtocolRevision}. Reload every participating client from the same source.").ConfigureAwait(false);
                    return;
                }
            }
            if (negotiateStartupMode)
            {
                if (requestingCollectionFirst.Count > 0)
                {
                    await RunXagmanPeerUiActionAsync(() =>
                    {
                        xagmanCollectionFirstStartupModeNegotiated = true;
                        xagmanCollectionFirstActive = true;
                        xagmanRunId = Guid.NewGuid().ToString("N");
                        xagmanRunPhase = XagmanRunPhase.Collection;
                        xagmanPhaseComplete = false;
                        xagmanCollectionBarrierStartedAtUtc = DateTime.UtcNow;
                        plugin.TaskRunner.AddLog(
                            $"Xagman: all {requestingCollectionFirst.Count} participating Franchise Owner client(s) requested collection-first; Tony ignored its local setting and created run {xagmanRunId}.");
                    }).ConfigureAwait(false);
                    collectionFirst = true;
                }
                else if (franchisePeers.Count > 0
                    && xagmanRunning
                    && xagmanActiveRole == XagmanRole.Tony)
                {
                    await RunXagmanPeerUiActionAsync(() =>
                    {
                        xagmanCollectionFirstStartupModeNegotiated = true;
                        plugin.TaskRunner.AddLog(
                            $"Xagman: all {franchisePeers.Count} participating Franchise Owner client(s) have collection-first effectively disabled; starting the legacy flow. Tony's local Prioritize setting was ignored.");
                    }).ConfigureAwait(false);
                }
            }
            var cohortAlreadyFrozen = collectionFirst
                && xagmanExpectedFranchiseOwnerInstanceIds.Count > 0;
            var expectedFranchiseOwnerInstanceIds = cohortAlreadyFrozen
                ? xagmanExpectedFranchiseOwnerInstanceIds
                    .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : franchisePeers
                    .Select(peer => peer.InstanceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            if (collectionFirst)
            {
                if (expectedFranchiseOwnerInstanceIds.Count == 0)
                {
                    await AddXagmanPeerLogAsync(
                        "Xagman: collection-first start needs at least one connected Franchise Owner client; no run cohort was frozen.").ConfigureAwait(false);
                    return;
                }
                var incompatiblePeers = franchisePeers
                    .Where(peer => expectedFranchiseOwnerInstanceIds.Contains(peer.InstanceId, StringComparer.OrdinalIgnoreCase))
                    .Where(peer => peer.CoordinationProtocolRevision != XagmanCollectionFirstCoordinationProtocolRevision)
                    .ToList();
                if (incompatiblePeers.Count > 0)
                {
                    var incompatibleNames = string.Join(", ",
                        incompatiblePeers.Select(GetXagmanPeerConfigurationLabel));
                    await AddXagmanPeerLogAsync(
                        $"Xagman: collection-first start refused because these Franchise Owner clients do not advertise coordination protocol {XagmanCollectionFirstCoordinationProtocolRevision}: {incompatibleNames}. Reload every participating client from the same build.").ConfigureAwait(false);
                    return;
                }

                if (!cohortAlreadyFrozen)
                {
                    await RunXagmanPeerUiActionAsync(() =>
                    {
                        xagmanExpectedFranchiseOwnerInstanceIds.Clear();
                        xagmanExpectedFranchiseOwnerInstanceIds.UnionWith(
                            expectedFranchiseOwnerInstanceIds);
                        xagmanCollectionPhaseAcknowledgedInstanceIds.Clear();
                        xagmanRestockPhaseAcknowledgedInstanceIds.Clear();
                        xagmanPhaseTotalCharacters = xagmanExpectedFranchiseOwnerInstanceIds.Count;
                        xagmanPhaseResolvedCharacters = 0;
                        xagmanCollectionBarrierStartedAtUtc = DateTime.UtcNow;
                        xagmanMissingCohortPeerSinceUtc = DateTime.MinValue;
                        xagmanCollectionBarrierWarningLogged = false;
                        FreezeXagmanPriorityTradeCapacityForecastBaseline();
                        PublishXagmanPresence();
                    }).ConfigureAwait(false);
                }
            }

            if (IsXagmanSupplyCycleActive() && xagmanActiveRole == XagmanRole.Tony)
            {
                await RunXagmanPeerUiActionAsync(() => xagmanSupplyOwnerCohort.UnionWith(
                    franchisePeers.Select(peer => peer.InstanceId))).ConfigureAwait(false);
            }
            await AddXagmanPeerLogAsync(cohortAlreadyFrozen
                ? $"Xagman: rebroadcasting the frozen run directive to {peers.Count} connected peer(s); the {expectedFranchiseOwnerInstanceIds.Count}-client cohort and acknowledgments are unchanged."
                : $"Xagman: Sending start command to {peers.Count} connected peers...").ConfigureAwait(false);

            if (await plugin.XagmanPeers.SendStartTaskToAllPeersAsync(
                    collectionFirst ? xagmanRunId : string.Empty,
                    collectionFirst,
                    collectionFirst ? XagmanRunPhase.Collection : XagmanRunPhase.Legacy,
                    XagmanCollectionFirstCoordinationProtocolRevision,
                    greenValueEnabled ? XagmanGreenValueProtocolRevision : 0,
                    collectionFirst ? expectedFranchiseOwnerInstanceIds : Array.Empty<string>())
                .ConfigureAwait(false))
                await AddXagmanPeerLogAsync("Xagman: Start command sent successfully").ConfigureAwait(false);
            else
                await AddXagmanPeerLogAsync($"Xagman: Failed to send start command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send start command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] StartAllXagmanPeers failed");
        }
        finally
        {
            System.Threading.Volatile.Write(ref xagmanStartAllInFlight, 0);
        }
    }

    private void StopAllXagmanPeers()
    {
        _ = StopAllXagmanPeersAsync();
    }

    private async System.Threading.Tasks.Task StopAllXagmanPeersAsync()
    {
        try
        {
            if (await EnsureXagmanPeerCommandChannelAsync("stop").ConfigureAwait(false))
            {
                var peers = await WaitForXagmanCommandTargetPeersAsync(1.0).ConfigureAwait(false);
                if (peers.Count == 0)
                {
                    await AddXagmanPeerLogAsync("Xagman: No connected peers to send stop command to").ConfigureAwait(false);
                }
                else
                {
                    await AddXagmanPeerLogAsync($"Xagman: Sending stop command to {peers.Count} connected peers...").ConfigureAwait(false);
                    if (await plugin.XagmanPeers.SendStopTaskToAllPeersAsync().ConfigureAwait(false))
                        await AddXagmanPeerLogAsync("Xagman: Stop command sent successfully").ConfigureAwait(false);
                    else
                        await AddXagmanPeerLogAsync($"Xagman: Failed to send stop command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
                }
            }

            await RunXagmanPeerUiActionAsync(() =>
            {
                if (!xagmanRunning
                    && !xagmanTradeSafetySessionActive
                    && !(plugin.TaskRunner.IsRunning
                        && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase)))
                    return;

                StopXagmanTask();
                if (xagmanStatus != XagmanStatus.Error)
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S91BC96B526D1C64C"));
                plugin.TaskRunner.AddLog("Xagman: Stopped task via peer command");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send stop command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] StopAllXagmanPeers failed");
        }
    }

    private void StopAllXagmanClientsAndResults()
    {
        _ = StopAllXagmanClientsAndResultsAsync();
    }

    private async System.Threading.Tasks.Task StopAllXagmanClientsAndResultsAsync()
    {
        try
        {
            await RunXagmanPeerUiActionAsync(() =>
                StopXagmanTaskAndClearResults("all-client command")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Local stop and result clear failed");
            try
            {
                await AddXagmanPeerLogAsync($"Xagman: Failed to stop this client and clear results: {ex.Message}").ConfigureAwait(false);
            }
            catch (Exception logEx)
            {
                Plugin.Log.Error(logEx, "[Xagman] Failed to record local stop and result clear failure");
            }
        }

        try
        {
            if (await EnsureXagmanPeerCommandChannelAsync("stop and clear results").ConfigureAwait(false))
            {
                var peers = await WaitForXagmanCommandTargetPeersAsync(1.0, includeIdleClients: true).ConfigureAwait(false);
                if (peers.Count == 0)
                    await AddXagmanPeerLogAsync("Xagman: No fresh remote clients are currently listed; broadcasting stop and clear results to the connected hub anyway").ConfigureAwait(false);
                else
                    await AddXagmanPeerLogAsync($"Xagman: Broadcasting stop and clear results to all connected clients ({peers.Count} fresh remote clients currently listed)...").ConfigureAwait(false);

                if (await plugin.XagmanPeers.SendStopTaskAndClearResultsToAllClientsAsync().ConfigureAwait(false))
                    await AddXagmanPeerLogAsync("Xagman: Stop and clear results broadcast sent successfully").ConfigureAwait(false);
                else
                    await AddXagmanPeerLogAsync($"Xagman: Failed to send stop and clear results broadcast ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] StopAllXagmanClientsAndResults failed");
            try
            {
                await AddXagmanPeerLogAsync($"Xagman: Failed to send stop and clear results command: {ex.Message}").ConfigureAwait(false);
            }
            catch (Exception logEx)
            {
                Plugin.Log.Error(logEx, "[Xagman] Failed to record stop and clear results send failure");
            }
        }
    }

    private void StopXagmanTaskAndClearResults(string source)
    {
        StopXagmanTask();
        ClearXagmanRunSnapshot();
        if (xagmanStatus != XagmanStatus.Error)
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA02A8A341FF81BBE", source));
        plugin.TaskRunner.AddLog($"Xagman: Stopped task and cleared saved results via {source}");
    }

    private async System.Threading.Tasks.Task<bool> RecallAllXagmanPeersForFailureAsync()
    {
        try
        {
            if (!await EnsureXagmanPeerCommandChannelAsync("recall").ConfigureAwait(false))
                return false;

            var peers = await WaitForXagmanCommandTargetPeersAsync(1.0).ConfigureAwait(false);
            if (peers.Count == 0)
            {
                await AddXagmanPeerLogAsync("Xagman: No connected peers to send recall command to").ConfigureAwait(false);
                return true;
            }

            await AddXagmanPeerLogAsync($"Xagman: Sending recall command to {peers.Count} connected peers...").ConfigureAwait(false);
            var sent = await plugin.XagmanPeers.SendRecallTaskToAllPeersAsync().ConfigureAwait(false);
            if (sent)
                await AddXagmanPeerLogAsync("Xagman: Recall command sent successfully").ConfigureAwait(false);
            else
                await AddXagmanPeerLogAsync($"Xagman: Failed to send recall command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
            return sent;
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send recall command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] RecallAllXagmanPeersForFailure failed");
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> CompleteAllXagmanPeersAsync(
        string supplyPassId = "", string capacityDrainId = "", int capacityRecoveryEpoch = 0)
    {
        var capacityCompletion = IsXagmanCapacityWarningCompletion()
            && capacityDrainId == xagmanCapacityDrainId && capacityRecoveryEpoch == xagmanCapacityRecoveryEpoch;
        var coordinatedCompletion = IsXagmanCollectionFirstRestockPhase() || capacityCompletion;
        var completionPhase = capacityCompletion ? XagmanRunPhase.Collection : XagmanRunPhase.Restock;
        var completionRunId = coordinatedCompletion ? xagmanRunId : string.Empty;
        var completionExpectedOwners = coordinatedCompletion
            ? xagmanExpectedFranchiseOwnerInstanceIds
                .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        // Snapshot on the game thread before awaits. Early departures retain their place
        // in the immutable cohort; their acknowledged local result satisfies delivery.
        var completedOwnerIds = xagmanCompletedOwnerClients.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allSupplyOwnersCompleted = xagmanSupplyOwnerCohort.Count > 0
            && xagmanSupplyOwnerCohort.All(completedOwnerIds.Contains);
        try
        {
            if (coordinatedCompletion ? completionExpectedOwners.Count > 0 && completionExpectedOwners.All(completedOwnerIds.Contains)
                : allSupplyOwnersCompleted)
                return true;
            if (!await EnsureXagmanPeerCommandChannelAsync("completion").ConfigureAwait(false))
                return false;

            if (!coordinatedCompletion)
            {
                var peers = await WaitForXagmanCommandTargetPeersAsync(1.0).ConfigureAwait(false);
                if (peers.Count == 0)
                {
                    await AddXagmanPeerLogAsync("Xagman: No connected peers to send completion command to").ConfigureAwait(false);
                    return false;
                }

                await AddXagmanPeerLogAsync($"Xagman: Sending completion command to {peers.Count} connected peers...").ConfigureAwait(false);
                var sent = await plugin.XagmanPeers.SendCompleteTaskToAllPeersAsync(
                    supplyPassId: supplyPassId).ConfigureAwait(false);
                await AddXagmanPeerLogAsync(sent
                    ? "Xagman: Completion command sent successfully"
                    : $"Xagman: Failed to send completion command to peers ({plugin.XagmanPeers.LastStatus})").ConfigureAwait(false);
                return sent;
            }

            if (completionExpectedOwners.Count == 0)
            {
                await AddXagmanPeerLogAsync(
                    "Xagman: scoped collection-first completion refused because the frozen FO cohort is empty.").ConfigureAwait(false);
                return false;
            }

            var acknowledgedOwners = new HashSet<string>(completedOwnerIds, StringComparer.OrdinalIgnoreCase);
            var deadlineUtc = DateTime.UtcNow.AddSeconds(XagmanCompletionAckTimeoutSeconds);
            var nextBroadcastUtc = DateTime.MinValue;
            var broadcastAttempt = 0;
            while (DateTime.UtcNow < deadlineUtc)
            {
                foreach (var peer in plugin.XagmanPeers.Peers
                             .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
                             .Where(peer => completionExpectedOwners.Contains(
                                 peer.InstanceId,
                                 StringComparer.OrdinalIgnoreCase))
                             .Where(peer => IsXagmanPeerInRun(
                                 peer,
                                 completionRunId,
                                  completionPhase))
                              .Where(peer => IsXagmanPeerFresh(peer))
                              .Where(peer => peer.SupplyPassId == supplyPassId)
                              .Where(peer => !capacityCompletion || (peer.CapacityDrainId == capacityDrainId
                                  && peer.CapacityRecoveryEpoch == capacityRecoveryEpoch
                                  && peer.CapacityRecoveryCoordinatorInstanceId == plugin.InstanceId
                                  && peer.SupplyPassComplete))
                              .Where(peer => peer.CompletionDirectiveAcknowledged))
                {
                    acknowledgedOwners.Add(peer.InstanceId);
                }

                if (completionExpectedOwners.All(acknowledgedOwners.Contains))
                {
                    await AddXagmanPeerLogAsync(
                        $"Xagman: every frozen FO client acknowledged scoped completion for run {completionRunId}.").ConfigureAwait(false);
                    return true;
                }

                if (DateTime.UtcNow >= nextBroadcastUtc)
                {
                    broadcastAttempt++;
                    var sent = await plugin.XagmanPeers.SendCompleteTaskToAllPeersAsync(
                            completionRunId,
                            true,
                            completionPhase,
                            XagmanCollectionFirstCoordinationProtocolRevision,
                            completionExpectedOwners,
                            supplyPassId: supplyPassId,
                            capacityDrainId: capacityDrainId,
                            capacityRecoveryEpoch: capacityRecoveryEpoch)
                        .ConfigureAwait(false);
                    if (broadcastAttempt == 1)
                    {
                        await AddXagmanPeerLogAsync(
                            $"Xagman: sent scoped completion for run {completionRunId}; waiting for {completionExpectedOwners.Count} frozen FO acknowledgement(s).").ConfigureAwait(false);
                    }
                    else if (!sent)
                    {
                        await AddXagmanPeerLogAsync(
                            $"Xagman: scoped completion rebroadcast {broadcastAttempt} was not accepted by the hub ({plugin.XagmanPeers.LastStatus}); retrying within the bounded acknowledgement window.").ConfigureAwait(false);
                    }

                    nextBroadcastUtc = DateTime.UtcNow.AddSeconds(XagmanCompletionRebroadcastSeconds);
                }

                await System.Threading.Tasks.Task.Delay(250).ConfigureAwait(false);
            }

            var missingOwners = completionExpectedOwners
                .Where(instanceId => !acknowledgedOwners.Contains(instanceId))
                .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            await AddXagmanPeerLogAsync(
                $"Xagman: scoped completion failed closed after {XagmanCompletionAckTimeoutSeconds:0} seconds; {missingOwners.Count} frozen FO client(s) did not acknowledge: {string.Join(", ", missingOwners)}. Tony will remain connected in Error until explicitly stopped.").ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await AddXagmanPeerLogAsync($"Xagman: Failed to send completion command to peers: {ex.Message}").ConfigureAwait(false);
            Plugin.Log.Error(ex, "[Xagman] CompleteAllXagmanPeers failed");
            return false;
        }
    }

    private void InitializeXagmanPeerEventHandlers()
    {
        // Subscribe to peer service task control events
        plugin.XagmanPeers.OnTaskStartRequested -= HandlePeerTaskStartRequest;
        plugin.XagmanPeers.OnTaskStopRequested -= HandlePeerTaskStopRequest;
        plugin.XagmanPeers.OnTaskStopAndClearResultsRequested -= HandlePeerTaskStopAndClearResultsRequest;
        plugin.XagmanPeers.OnTaskRecallRequested -= HandlePeerTaskRecallRequest;
        plugin.XagmanPeers.OnTaskCompleteRequested -= HandlePeerTaskCompleteRequest;
        plugin.XagmanPeers.OnTaskStartRequested += HandlePeerTaskStartRequest;
        plugin.XagmanPeers.OnTaskStopRequested += HandlePeerTaskStopRequest;
        plugin.XagmanPeers.OnTaskStopAndClearResultsRequested += HandlePeerTaskStopAndClearResultsRequest;
        plugin.XagmanPeers.OnTaskRecallRequested += HandlePeerTaskRecallRequest;
        plugin.XagmanPeers.OnTaskCompleteRequested += HandlePeerTaskCompleteRequest;
    }

    private void HandlePeerTaskStartRequest(XagmanPeerMessage startDirective)
    {
        try
        {
            // Schedule on main thread to avoid "Not on main thread!" error
            Plugin.RunOnGameThread(() =>
            {
                try
                {
                    if (xagmanRunning && IsXagmanCapacityRecoveryActive()) return;
                    // Check if this is a Franchise Owner and can start
                    if (plugin.Configuration.XagmanRole == XagmanRole.FranchiseOwner)
                    {
                        if ((xagmanRunning || plugin.TaskRunner.IsRunning) && !string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken)) return;
                        if (HasXagmanGreenValueSelectors(plugin.Configuration.XagmanItems)
                            && startDirective.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
                        {
                            plugin.TaskRunner.AddLog(
                                $"Xagman: refused peer start because Tony did not advertise green-value protocol {XagmanGreenValueProtocolRevision}. Reload every participating client from the same source.");
                            return;
                        }
                        if (xagmanRunning)
                        {
                            var samePriorityRun = !startDirective.CollectionFirstEnabled
                                || (IsXagmanCollectionFirstRunActive()
                                    && startDirective.RunId.Equals(xagmanRunId, StringComparison.OrdinalIgnoreCase));
                            if (!samePriorityRun)
                            {
                                plugin.TaskRunner.AddLog($"Xagman: ignored start for run {startDirective.RunId} because this client is already handling run {xagmanRunId}.");
                                return;
                            }
                            if (!TryRememberXagmanOwnerSupplyStart(startDirective)) return;
                        }
                        xagmanOwnerStartRequested = true;
                        if (!xagmanRunning)
                        {
                            xagmanPreferredTonyCharacter = string.Empty;
                            SetXagmanActiveMeetDestination(string.Empty, string.Empty);
                        }
                        TryResolveXagmanMeetDestinationForOwner();
                        TryBindXagmanFranchiseTonyForMeetup();

                        if (xagmanRunning)
                        {
                            plugin.TaskRunner.AddLog("Xagman: Received start signal from Tony.");
                            if (xagmanOwnerStandbyPending && !plugin.TaskRunner.IsRunning)
                            {
                                if (!TryResumeXagmanOwnerStandbyFromCallingTony())
                                    plugin.TaskRunner.AddLog("Xagman: standby owner is waiting for a replacement Tony's explicit call before resuming.");
                            }
                            PublishXagmanPresence();
                            return;
                        }

                        var selectedFranchiseChars = GetSelectedXagmanFranchiseCharacters();

                        // If no characters selected, auto-select all available ones
                        if (selectedFranchiseChars.Count == 0)
                        {
                            var allFranchiseChars = plugin.Configuration.XagmanFranchiseCharacters;
                            if (allFranchiseChars.Count > 0)
                            {
                                // Select all available Franchise Owner characters
                                for (int i = 0; i < allFranchiseChars.Count; i++)
                                {
                                    if (IsAutoRetainerCharacterAllowed(allFranchiseChars[i], plugin.Configuration.XagmanHonorArExclusions))
                                        xagmanFranchiseSelectedIndices.Add(i);
                                }
                                selectedFranchiseChars = GetSelectedXagmanFranchiseCharacters();
                                plugin.TaskRunner.AddLog($"Xagman: Auto-selected {selectedFranchiseChars.Count} Franchise Owner characters via peer command");
                            }
                        }

                        if (selectedFranchiseChars.Count > 0)
                        {
                            if (StartXagmanFranchiseTask(true, false, startDirective))
                                plugin.TaskRunner.AddLog("Xagman: Started task via peer command");
                            else
                                plugin.TaskRunner.AddLog("Xagman: Cannot start via peer command - owner task prerequisites are not satisfied");
                        }
                        else
                        {
                            plugin.TaskRunner.AddLog("Xagman: Cannot start via peer command - no Franchise Owner characters configured");
                        }
                    }
                }
                catch (Exception ex)
                {
                    plugin.TaskRunner.AddLog($"Xagman: Failed to start task via peer command: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            plugin.TaskRunner.AddLog($"Xagman: Failed to schedule task start via peer command: {ex.Message}");
        }
    }

    private void HandlePeerTaskStopRequest()
    {
        try
        {
            // Schedule on main thread to avoid thread issues
            Plugin.RunOnGameThread(() =>
            {
                try
                {
                    // Stop any live Xagman lifecycle, including completion cleanup after
                    // xagmanRunning has already been cleared.
                    if (xagmanRunning
                        || xagmanTradeSafetySessionActive
                        || (plugin.TaskRunner.IsRunning
                            && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase)))
                    {
                        StopXagmanTask();
                        if (xagmanStatus != XagmanStatus.Error)
                            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S91BC96B526D1C64C"));
                        plugin.TaskRunner.AddLog("Xagman: Stopped task via peer command");
                    }
                }
                catch (Exception ex)
                {
                    plugin.TaskRunner.AddLog($"Xagman: Failed to stop task via peer command: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            plugin.TaskRunner.AddLog($"Xagman: Failed to schedule task stop via peer command: {ex.Message}");
        }
    }

    private void HandlePeerTaskStopAndClearResultsRequest()
    {
        try
        {
            Plugin.RunOnGameThread(() =>
            {
                try
                {
                    StopXagmanTaskAndClearResults("peer command");
                }
                catch (Exception ex)
                {
                    plugin.TaskRunner.AddLog($"Xagman: Failed to stop task and clear results via peer command: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            plugin.TaskRunner.AddLog($"Xagman: Failed to schedule task stop and result clear via peer command: {ex.Message}");
        }
    }

    private void HandlePeerTaskRecallRequest()
    {
        try
        {
            Plugin.RunOnGameThread(() =>
            {
                try
                {
                    StartXagmanFailureRecallTask(global::XASlave.Localization.UiMessage.Key("SB9578B6ADEB600BB"), forceLogout: true);
                    plugin.TaskRunner.AddLog("Xagman: Started failure recall via peer command.");
                }
                catch (Exception ex)
                {
                    plugin.TaskRunner.AddLog($"Xagman: Failed to recall task via peer command: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            plugin.TaskRunner.AddLog($"Xagman: Failed to schedule task recall via peer command: {ex.Message}");
        }
    }

    private void HandlePeerTaskCompleteRequest(XagmanPeerMessage completionDirective)
    {
        try
        {
            Plugin.RunOnGameThread(() =>
            {
                try
                {
                    if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner)
                        return;
                    if (!string.IsNullOrWhiteSpace(xagmanOwnerCompletionToken)) return;
                    var capacityCompletion = false;
                    if (completionDirective.CollectionFirstEnabled)
                    {
                        var matchesFrozenRestockRun = IsXagmanCollectionFirstRestockPhase()
                            && completionDirective.RunPhase == XagmanRunPhase.Restock
                            && string.IsNullOrWhiteSpace(completionDirective.CapacityDrainId)
                            && completionDirective.CapacityRecoveryEpoch == 0;
                        capacityCompletion = MatchesXagmanCapacityWarningCompletion(completionDirective);
                        var matchesFrozenCompletion = (matchesFrozenRestockRun || capacityCompletion)
                            && completionDirective.CoordinationProtocolRevision == XagmanCollectionFirstCoordinationProtocolRevision
                            && completionDirective.RunId.Equals(xagmanRunId, StringComparison.OrdinalIgnoreCase)
                            && completionDirective.ExpectedFranchiseOwnerInstanceIds.Contains(
                                plugin.InstanceId,
                                StringComparer.OrdinalIgnoreCase);
                        if (!matchesFrozenCompletion)
                        {
                            plugin.TaskRunner.AddLog(
                                $"Xagman: ignored completion for collection-first run {completionDirective.RunId}; this client is handling {xagmanRunId} phase {xagmanRunPhase}.");
                            return;
                        }


                    }
                    else if (IsXagmanCollectionFirstRunActive())
                    {
                        plugin.TaskRunner.AddLog(
                            "Xagman: ignored an unscoped legacy completion command while a collection-first run is active.");
                        return;
                    }

                    if (IsXagmanSupplyCycleActive())
                    {
                        if (completionDirective.SenderInstanceId != xagmanOwnerSupplyTonyInstance
                            || completionDirective.SupplyPassId != xagmanOwnerSupplyPassId
                            || !xagmanOwnerSupplyPassComplete)
                        {
                            plugin.TaskRunner.AddLog("Xagman: ignored completion from an unbound Tony or before the owner supply pass finished.");
                            return;
                        }
                        if (capacityCompletion)
                        {
                            if (!PrepareXagmanCapacityOwnerWarningCompletion()) return;
                        }
                        else FinalizeXagmanOpenEndedSupplyRequests();
                        foreach (var state in xagmanPartialOwners.Values)
                        {
                            if (!capacityCompletion)
                                state.ReasonMessage = global::XASlave.Localization.UiMessage.Key("S47EB5BE17B4AEB75");
                            plugin.TaskRunner.AddLog($"Xagman: partial owner {state.CharacterNameWorld}: {GetXagmanPartialOwnerDetail(state)}");
                        }
                        xagmanPhaseComplete = IsXagmanCollectionFirstRestockPhase();
                        if (xagmanPhaseComplete) FinalizeXagmanCollectionFirstOwnerSelections();
                    }
                    if (completionDirective.CollectionFirstEnabled)
                    {
                        xagmanCompletionDirectiveAcknowledged = true;
                        PublishXagmanPresence();
                    }
                    global::XASlave.Localization.UiMessage? completionReasonUiPresentation = null;
                    var completionReason = global::XASlave.Localization.UiMessage.Store(ref completionReasonUiPresentation, (capacityCompletion ? global::XASlave.Localization.UiMessage.Format("S2F17CF371FE9B0BB", xagmanPartialOwners.Count) : (IsXagmanSupplyCycleActive() ? global::XASlave.Localization.UiMessage.Format("S5E906A2E5F899753", xagmanPartialOwners.Count) : (completionDirective.CollectionFirstEnabled ? global::XASlave.Localization.UiMessage.Key("S07050FF080B402B5") : global::XASlave.Localization.UiMessage.Key("SCE4A2282E03A694E")))));
                    StartXagmanFranchiseCompletionTask(global::XASlave.Localization.UiMessage.Capture(ref completionReasonUiPresentation, completionReason));
                    plugin.TaskRunner.AddLog("Xagman: Started owner completion cleanup via peer command.");
                }
                catch (Exception ex)
                {
                    plugin.TaskRunner.AddLog($"Xagman: Failed to start completion cleanup via peer command: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            plugin.TaskRunner.AddLog($"Xagman: Failed to schedule completion cleanup via peer command: {ex.Message}");
        }
    }

    private void DrawXagmanWorldSelector(Configuration cfg)
    {
        var label = cfg.XagmanServerMatchingEnabled
            ? "Server Matching##xagmanWorldButton"
            : (string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld)
                ? "Select World##xagmanWorldButton"
                : $"{cfg.XagmanTargetWorld}##xagmanWorldButton");
        if (global::XASlave.Localization.UiNative.Button(label))
            ImGui.OpenPopup("XagmanWorldPopup");
        using var popup = ImRaii.Popup("XagmanWorldPopup");
        if (!popup)
            return;
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanWorldFilter", global::XASlave.Localization.UiLocale.Get("S2F41446BE02C17D6"), ref xagmanWorldFilter, 128);
        ImGui.Separator();
        if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("S3FE3F253317A649E"), cfg.XagmanServerMatchingEnabled))
        {
            cfg.XagmanServerMatchingEnabled = true;
            cfg.Save();
            xagmanWorldFilter = string.Empty;
            ImGui.CloseCurrentPopup();
            return;
        }
        ImGui.Separator();
        foreach (var region in WorldData.RegionOrder)
        {
            var regionWorlds = WorldData.Worlds.Where(world => world.Region == region).ToList();
            if (!regionWorlds.Any(world => string.IsNullOrWhiteSpace(xagmanWorldFilter) || world.Name.Contains(xagmanWorldFilter, StringComparison.OrdinalIgnoreCase)))
                continue;
            using var regionNode = ImRaii.TreeNode(region);
            if (!regionNode)
                continue;
            if (!WorldData.DataCenterOrder.TryGetValue(region, out var dataCenters))
                continue;
            foreach (var dc in dataCenters)
            {
                var dcWorlds = regionWorlds
                    .Where(world => world.DataCenter == dc)
                    .Where(world => string.IsNullOrWhiteSpace(xagmanWorldFilter) || world.Name.Contains(xagmanWorldFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(world => world.Name)
                    .ToList();
                if (dcWorlds.Count == 0)
                    continue;
                using var dataCenterNode = ImRaii.TreeNode(dc);
                if (!dataCenterNode)
                    continue;
                foreach (var world in dcWorlds)
                {
                    if (!ImGui.Selectable(world.Name, string.Equals(cfg.XagmanTargetWorld, world.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    cfg.XagmanTargetWorld = world.Name;
                    cfg.XagmanServerMatchingEnabled = false;
                    cfg.Save();
                    xagmanWorldFilter = string.Empty;
                    ImGui.CloseCurrentPopup();
                    break;
                }
            }
        }
    }

    // Expandable in-place picker shown when "Server Matching" is selected: one meet world per server
    // (data center), grouped Region -> Server. The shared meet location reuses XagmanTargetAetheryte.
    private void DrawXagmanServerMatchingPicker(Configuration cfg)
    {
        if (!cfg.XagmanServerMatchingEnabled)
            return;

        if (cfg.XagmanRole == XagmanRole.Tony && !cfg.XagmanOutsideNetworkHelper)
            DrawXagmanServerStatus();

        ImGui.Indent(Scale(8f));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S249FBDBCDB2C1DAA"));
        foreach (var region in WorldData.RegionOrder)
        {
            if (!WorldData.DataCenterOrder.TryGetValue(region, out var servers))
                continue;
            using var regionNode = ImRaii.TreeNode($"{region}##xagmanSm_{region}", ImGuiTreeNodeFlags.DefaultOpen);
            if (!regionNode)
                continue;
            foreach (var dc in servers)
            {
                var current = GetXagmanServerMeetWorld(dc);
                ImGui.TextUnformatted(dc);
                ImGui.SameLine(Scale(110f));
                ImGui.SetNextItemWidth(Scale(170f));
                var comboLabel = ((string.IsNullOrWhiteSpace(current) ? global::XASlave.Localization.UiMessage.Key("S552E423045FCBD94") : global::XASlave.Localization.UiMessage.Raw(current))).Display;
                using (var imguiScope4096 = ImRaii.Combo($"##xagmanSmWorld_{dc}", comboLabel))
                if (imguiScope4096)
                {
                    if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("S552E423045FCBD94"), string.IsNullOrWhiteSpace(current)))
                    {
                        cfg.XagmanServerMeetWorlds.Remove(dc);
                        cfg.Save();
                    }

                    foreach (var world in WorldData.Worlds.Where(w => w.DataCenter == dc).OrderBy(w => w.Name))
                    {
                        if (ImGui.Selectable(world.Name, string.Equals(current, world.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            cfg.XagmanServerMeetWorlds[dc] = world.Name;
                            cfg.Save();
                        }
                    }


                }
            }
        }

        ImGui.Unindent(Scale(8f));
    }

    private static bool IsXagmanTonySellSupportedLocation(string locationName)
    {
        return !string.IsNullOrWhiteSpace(locationName)
            && xagmanTonySellSupportedLocationNames.Any(supportedLocation => supportedLocation.Equals(locationName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetXagmanTonySellSupportedLocationTooltipText()
    {
        return string.Join("\n", xagmanTonySellSupportedLocationNames.Select(locationName => $"- {locationName}"));
    }

    private void DrawXagmanAetheryteSelector(Configuration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !cfg.XagmanServerMatchingEnabled)
        {
            using (ImRaii.Disabled())
            {
                global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S3C82033F8EADE216") + "##xagmanAetheryteButton"));
            }
            return;
        }
        ImGui.SameLine();
        var locationButtonHint = cfg.XagmanServerMatchingEnabled ? "Shared Meet Location" : "Select Location";
        var label = string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte)
            ? $"{locationButtonHint}##xagmanAetheryteButton"
            : $"{cfg.XagmanTargetAetheryte}##xagmanAetheryteButton";
        if (global::XASlave.Localization.UiNative.Button(label))
            ImGui.OpenPopup("XagmanAetherytePopup");
        using var popup = ImRaii.Popup("XagmanAetherytePopup");
        if (!popup)
            return;
        ImGui.SetNextItemWidth(Scale(240f));
        ImGui.InputTextWithHint("##xagmanAetheryteFilter", global::XASlave.Localization.UiLocale.Get("S93A486F4363AC018"), ref xagmanAetheryteFilter, 128);
        ImGui.Separator();
        foreach (var aetheryte in GetXagmanAetheryteNames())
        {
            if (!string.IsNullOrWhiteSpace(xagmanAetheryteFilter)
                && !aetheryte.Contains(xagmanAetheryteFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            var supportedVendorLocation = IsXagmanTonySellSupportedLocation(aetheryte);
            bool selected;
            using (ImRaii.PushColor(ImGuiCol.Text, XagmanTonySellSupportedLocationColor, supportedVendorLocation))
            {
                selected = ImGui.Selectable(aetheryte, string.Equals(cfg.XagmanTargetAetheryte, aetheryte, StringComparison.OrdinalIgnoreCase));
            }
            if (!selected)
                continue;
            cfg.XagmanTargetAetheryte = aetheryte;
            cfg.Save();
            xagmanAetheryteFilter = string.Empty;
            ImGui.CloseCurrentPopup();
            break;
        }
    }
    private List<string> GetXagmanAetheryteNames()
    {
        if (xagmanAetheryteNames != null)
            return xagmanAetheryteNames;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte)
                    continue;
                var name = row.PlaceName.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch
        {
        }
        xagmanAetheryteNames = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return xagmanAetheryteNames;
    }
    private void PruneXagmanAutoRetainerExclusions()
    {
        if (xagmanRunning || plugin.TaskRunner.IsRunning || xagmanTradeSafetySessionActive)
            return;
        var cfg = plugin.Configuration;
        PruneAutoRetainerExcludedSelections(
            cfg.XagmanTonyCharacters.Select(entry => entry.CharacterNameWorld).ToList(),
            xagmanTonySelectedIndices, cfg.XagmanHonorArExclusions);
        PruneAutoRetainerExcludedSelections(
            cfg.XagmanFranchiseCharacters, xagmanFranchiseSelectedIndices, cfg.XagmanHonorArExclusions);
    }
    private void ImportXagmanTonyCharactersFromAr()
    {
        try
        {
            var cfg = plugin.Configuration;
            var arChars = ReadAutoRetainerCharactersForTask(cfg.XagmanHonorArExclusions);
            var added = 0;
            foreach (var c in arChars)
            {
                var key = $"{c.Name}@{c.World}";
                if (!cfg.XagmanTonyCharacters.Any(entry => entry.CharacterNameWorld.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    cfg.XagmanTonyCharacters.Add(new XagmanTonyCharacterEntry
                    {
                        CharacterNameWorld = key,
                        Mode = XagmanTonyMode.Collection,
                    });
                    added++;
                }
                UpdateCharacterInfo(cfg, key, c);
            }
            MigrateLegacyLastSeen(cfg, arChars);
            PruneXagmanAutoRetainerExclusions();
            cfg.Save();
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S5F7D25B94035C625", added), ((cfg.XagmanHonorArExclusions ? plugin.ArConfigReader.ExclusionStatusMessage : global::XASlave.Localization.UiMessage.Value(string.Empty)))));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception ex)
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("SFDF7B36C696AD671", ex.Message));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
    }
    private void ImportXagmanFranchiseCharactersFromAr()
    {
        try
        {
            var cfg = plugin.Configuration;
            var (added, total) = ImportCharactersFromArToList(cfg.XagmanFranchiseCharacters, cfg.XagmanHonorArExclusions);
            PruneXagmanAutoRetainerExclusions();
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("SACFC511198BCCD3D", added, total), ((cfg.XagmanHonorArExclusions ? plugin.ArConfigReader.ExclusionStatusMessage : global::XASlave.Localization.UiMessage.Value(string.Empty)))));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception ex)
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S21B87ED207878E36", ex.Message));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
    }
    private bool IsXagmanTonyCharacterVisible(Configuration cfg, XagmanTonyCharacterEntry entry, int index)
    {
        var charName = entry.CharacterNameWorld;
        if (!IsAutoRetainerCharacterAllowed(charName, cfg.XagmanHonorArExclusions))
            return false;
        var world = GetWorldFromKey(charName);
        var regionDc = WorldData.GetRegionDcLabel(world);
        if (!MatchesRegionFilter(world, cfg.XagmanRegionFilter))
            return false;
        if (xagmanTonyShowOnlySelected && !xagmanTonySelectedIndices.Contains(index))
            return false;
        return string.IsNullOrWhiteSpace(xagmanTonySearchFilter)
            || charName.Contains(xagmanTonySearchFilter, StringComparison.OrdinalIgnoreCase)
            || world.Contains(xagmanTonySearchFilter, StringComparison.OrdinalIgnoreCase)
            || regionDc.Contains(xagmanTonySearchFilter, StringComparison.OrdinalIgnoreCase);
    }
    private bool MatchesXagmanFranchiseCharacterFilters(Configuration cfg, string charName)
    {
        if (!IsAutoRetainerCharacterAllowed(charName, cfg.XagmanHonorArExclusions))
            return false;
        var world = GetWorldFromKey(charName);
        var regionDc = WorldData.GetRegionDcLabel(world);
        if (!MatchesRegionFilter(world, cfg.XagmanRegionFilter))
            return false;
        return string.IsNullOrWhiteSpace(xagmanFranchiseSearchFilter)
            || charName.Contains(xagmanFranchiseSearchFilter, StringComparison.OrdinalIgnoreCase)
            || world.Contains(xagmanFranchiseSearchFilter, StringComparison.OrdinalIgnoreCase)
            || regionDc.Contains(xagmanFranchiseSearchFilter, StringComparison.OrdinalIgnoreCase);
    }
    private bool IsXagmanFranchiseCharacterVisible(Configuration cfg, string charName, int index)
    {
        return MatchesXagmanFranchiseCharacterFilters(cfg, charName)
            && (!xagmanFranchiseShowOnlySelected || xagmanFranchiseSelectedIndices.Contains(index));
    }
    private static bool TryGetLoggedInXagmanCurrentCharacter(out string currentCharacter)
    {
        currentCharacter = string.Empty;
        if (!Plugin.ClientState.IsLoggedIn
            || !Plugin.PlayerState.IsLoaded
            || Plugin.PlayerState.ContentId == 0)
        {
            return false;
        }

        var characterNameWorld = MonthlyReloggerTask.GetCurrentCharacterNameWorld().Trim();
        var separatorIndex = characterNameWorld.LastIndexOf('@');
        if (separatorIndex <= 0
            || separatorIndex >= characterNameWorld.Length - 1
            || characterNameWorld.EndsWith("@Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        currentCharacter = characterNameWorld;
        return true;
    }
    private void SelectXagmanCurrentCharacter(
        XagmanMatchSelectionTarget target,
        string currentCharacter)
    {
        ResetXagmanMatchingCharacterSelection();
        var cfg = plugin.Configuration;
        if (!IsAutoRetainerCharacterAllowed(currentCharacter, cfg.XagmanHonorArExclusions))
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SDA2D30D088F9EC83"));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        var index = target == XagmanMatchSelectionTarget.Tony
            ? cfg.XagmanTonyCharacters.FindIndex(entry =>
                entry.CharacterNameWorld.Equals(currentCharacter, StringComparison.OrdinalIgnoreCase))
            : cfg.XagmanFranchiseCharacters.FindIndex(entry =>
                entry.Equals(currentCharacter, StringComparison.OrdinalIgnoreCase));
        var selectionLabel = (target == XagmanMatchSelectionTarget.Tony ? global::XASlave.Localization.UiMessage.Key("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiMessage.Key("SAD5B29877C321F10"));
        if (index < 0)
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("SE854AC870D81A7AD", selectionLabel));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }

        var selectedIndices = target == XagmanMatchSelectionTarget.Tony
            ? xagmanTonySelectedIndices
            : xagmanFranchiseSelectedIndices;
        var added = selectedIndices.Add(index);
        if (added)
            InvalidateXagmanTradeCapacityForecast();
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (added ? global::XASlave.Localization.UiMessage.Format("S0E77E670A6CDBF15", selectionLabel) : global::XASlave.Localization.UiMessage.Format("S7E933AC1B1ECD8A3", selectionLabel)));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
    }
    private List<int> GetVisibleXagmanTonyCharacterIndices(Configuration cfg)
    {
        var indices = new List<int>();
        var chars = cfg.XagmanTonyCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            if (IsXagmanTonyCharacterVisible(cfg, chars[i], i))
                indices.Add(i);
        }
        return indices;
    }
    private List<int> GetVisibleXagmanFranchiseCharacterIndices(Configuration cfg)
    {
        var indices = new List<int>();
        var chars = cfg.XagmanFranchiseCharacters;
        for (var i = 0; i < chars.Count; i++)
        {
            if (IsXagmanFranchiseCharacterVisible(cfg, chars[i], i))
                indices.Add(i);
        }
        return indices;
    }
    private HashSet<string> GetVisibleXagmanCharacterKeys(Configuration cfg, XagmanMatchSelectionTarget target)
    {
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (target == XagmanMatchSelectionTarget.Tony)
        {
            foreach (var index in GetVisibleXagmanTonyCharacterIndices(cfg))
                visible.Add(cfg.XagmanTonyCharacters[index].CharacterNameWorld);
        }
        else
        {
            foreach (var index in GetVisibleXagmanFranchiseCharacterIndices(cfg))
                visible.Add(cfg.XagmanFranchiseCharacters[index]);
        }
        return visible;
    }
    private void SelectVisibleXagmanTonyCharacters()
    {
        var cfg = plugin.Configuration;
        foreach (var index in GetVisibleXagmanTonyCharacterIndices(cfg))
            xagmanTonySelectedIndices.Add(index);
        InvalidateXagmanTradeCapacityForecast();
    }
    private void SelectVisibleXagmanFranchiseCharacters()
    {
        var cfg = plugin.Configuration;
        foreach (var index in GetVisibleXagmanFranchiseCharacterIndices(cfg))
            xagmanFranchiseSelectedIndices.Add(index);
        InvalidateXagmanTradeCapacityForecast();
    }
    private void SelectXagmanFranchiseCharactersWithRegisteredAutoRetainerData(bool submarines)
    {
        if (!TryRefreshXagmanAutoRetainerData(XagmanMatchSelectionTarget.FranchiseOwner, out var arByCharacter))
            return;

        var cfg = plugin.Configuration;
        var chars = cfg.XagmanFranchiseCharacters;
        var matchingIndices = new HashSet<int>();

        for (var i = 0; i < chars.Count; i++)
        {
            var charName = chars[i];
            if (!arByCharacter.TryGetValue(charName, out var arInfo))
                continue;

            var registeredCount = submarines ? arInfo.SubmarineCount : arInfo.RetainerCount;
            if (registeredCount > 0 && MatchesXagmanFranchiseCharacterFilters(cfg, charName))
                matchingIndices.Add(i);
        }

        xagmanFranchiseSelectedIndices.Clear();
        xagmanFranchiseSelectedIndices.UnionWith(matchingIndices);
        InvalidateXagmanTradeCapacityForecast();

        var resourceName = submarines ? global::XASlave.Localization.UiMessage.Key("SE34261E10E7B9211") : global::XASlave.Localization.UiMessage.Key("S17642A16F49FEE36");
        var selectedCount = matchingIndices.Count;
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("SA4E9553B5857AF34", selectedCount, ((selectedCount == 1 ? global::XASlave.Localization.UiMessage.Value(string.Empty) : global::XASlave.Localization.UiMessage.Key("S043A718774C572BD"))), resourceName));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
    }
    private bool TryRefreshXagmanAutoRetainerData(
        XagmanMatchSelectionTarget target,
        out Dictionary<string, AutoRetainerConfigReader.ArCharacterInfo> arByCharacter)
    {
        arByCharacter = new Dictionary<string, AutoRetainerConfigReader.ArCharacterInfo>(StringComparer.OrdinalIgnoreCase);
        var selectionLabel = (target == XagmanMatchSelectionTarget.Tony ? global::XASlave.Localization.UiMessage.Key("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiMessage.Key("SAD5B29877C321F10"));
        try
        {
            // Registration refresh also runs during active plans; preserve their exclusion snapshot.
            var arCharacters = plugin.ArConfigReader.ReadCharacters(honorExclusions: false);
            if (arCharacters.Count == 0)
            {
                if (target == XagmanMatchSelectionTarget.FranchiseOwner)
                {
                    xagmanOwnerPolicyRegistrationRefreshFailed = true;
                    InvalidateXagmanTradeCapacityForecast();
                }
                arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S5DEB99BFDC374F05", selectionLabel));
                arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
                return false;
            }

            var cfg = plugin.Configuration;
            IEnumerable<string> chars = target == XagmanMatchSelectionTarget.Tony
                ? cfg.XagmanTonyCharacters.Select(entry => entry.CharacterNameWorld)
                : cfg.XagmanFranchiseCharacters;
            arByCharacter = arCharacters
                .GroupBy(character => $"{character.Name}@{character.World}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var charName in chars)
            {
                if (!arByCharacter.TryGetValue(charName, out var arInfo))
                {
                    if (!cfg.ReloggerCharacterInfo.TryGetValue(charName, out var missingInfo))
                    {
                        missingInfo = new ReloggerCharacterData();
                        cfg.ReloggerCharacterInfo[charName] = missingInfo;
                    }
                    missingInfo.FoundInAutoRetainer = false;
                    missingInfo.RetainerCount = 0;
                    missingInfo.SubmarineCount = 0;
                    continue;
                }

                UpdateCharacterInfo(cfg, charName, arInfo);
            }

            PruneXagmanAutoRetainerExclusions();
            cfg.Save();
            if (target == XagmanMatchSelectionTarget.FranchiseOwner)
            {
                xagmanOwnerPolicyRegistrationRefreshFailed = false;
                InvalidateXagmanTradeCapacityForecast();
            }
            return true;
        }
        catch (Exception ex)
        {
            if (target == XagmanMatchSelectionTarget.FranchiseOwner)
            {
                xagmanOwnerPolicyRegistrationRefreshFailed = true;
                InvalidateXagmanTradeCapacityForecast();
            }
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S4903C876EEC91484", selectionLabel, ex.Message));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return false;
        }
    }
    private List<XagmanTonyCharacterEntry> GetSelectedXagmanTonyCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.XagmanTonyCharacters;
        return xagmanTonySelectedIndices
            .Where(index => index >= 0 && index < chars.Count)
            .OrderBy(index => index)
            .Select(index => chars[index])
            .Where(entry => IsAutoRetainerCharacterAllowed(entry.CharacterNameWorld, cfg.XagmanHonorArExclusions))
            .ToList();
    }
    private List<string> GetSelectedXagmanFranchiseCharacters()
    {
        var cfg = plugin.Configuration;
        var chars = cfg.XagmanFranchiseCharacters;
        return xagmanFranchiseSelectedIndices
            .Where(index => index >= 0 && index < chars.Count)
            .OrderBy(index => index)
            .Select(index => chars[index])
            .Where(character => IsAutoRetainerCharacterAllowed(character, cfg.XagmanHonorArExclusions))
            .ToList();
    }
    private void SelectXagmanTonyCharactersWithMatchingItems(XagmanAutoRetainerMatchScope scope)
    {
        QueueXagmanMatchingSelection(XagmanMatchSelectionTarget.Tony, scope);
    }
    private void SelectXagmanFranchiseCharactersWithMatchingItems(XagmanAutoRetainerMatchScope scope)
    {
        ClearXagmanMatchingSelectionCaches();
        var cfg = plugin.Configuration;
        var hasConditionalPolicies = HasXagmanConditionalItemPolicies(cfg.XagmanItems);
        var arByCharacter = new Dictionary<string, AutoRetainerConfigReader.ArCharacterInfo>(StringComparer.OrdinalIgnoreCase);
        if ((scope != XagmanAutoRetainerMatchScope.All || hasConditionalPolicies) &&
            !TryRefreshXagmanAutoRetainerData(XagmanMatchSelectionTarget.FranchiseOwner, out arByCharacter))
            return;

        var ignoreGil = cfg.XagmanIgnoreGilInMatchingSelection;
        var candidateIndices = new List<int>();
        for (var i = 0; i < cfg.XagmanFranchiseCharacters.Count; i++)
        {
            var characterNameWorld = cfg.XagmanFranchiseCharacters[i];
            if (!MatchesXagmanFranchiseCharacterFilters(cfg, characterNameWorld))
                continue;
            if (scope != XagmanAutoRetainerMatchScope.All)
            {
                var registrationCount = arByCharacter.TryGetValue(characterNameWorld, out var arInfo)
                    ? GetXagmanMatchRegistrationCount(arInfo, scope)
                    : 0;
                if (!DoesXagmanRegistrationMatchScope(scope, registrationCount))
                    continue;
            }

            candidateIndices.Add(i);
        }

        if (candidateIndices.Count == 0)
        {
            xagmanFranchiseSelectedIndices.Clear();
            var scopeQualifier = GetXagmanMatchScopeQualifier(scope);
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (scope == XagmanAutoRetainerMatchScope.All ? global::XASlave.Localization.UiMessage.Key("S5D68DBC4E8AEAFB6") : global::XASlave.Localization.UiMessage.Format("S30BBBDB63BCD5647", scopeQualifier)));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        var items = cfg.XagmanItems
            .Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
            .ToList();
        if (items.Count == 0)
        {
            xagmanFranchiseSelectedIndices.Clear();
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (ignoreGil ? global::XASlave.Localization.UiMessage.Key("SF1100AF029B09220") : global::XASlave.Localization.UiMessage.Key("S4A99856388122343")));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }

        xagmanFranchiseSelectedIndices.Clear();
        var selectedCount = 0;
        foreach (var index in candidateIndices)
        {
            var characterNameWorld = cfg.XagmanFranchiseCharacters[index];
            if (!DoesXagmanFranchiseCharacterNeedItemChanges(characterNameWorld, items, ignoreGil))
                continue;

            xagmanFranchiseSelectedIndices.Add(index);
            selectedCount++;
        }

        var selectionDescription = (scope == XagmanAutoRetainerMatchScope.All ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Format("S30ADA5A3C2EB2AE0", GetXagmanMatchScopeQualifier(scope)));
        var refreshDescription = (scope == XagmanAutoRetainerMatchScope.All && !hasConditionalPolicies ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Key("SEF0DBF1F7046377F"));
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S5B18DCDD63E1F5D9", refreshDescription, selectedCount, ((selectedCount == 1 ? global::XASlave.Localization.UiMessage.Value(string.Empty) : global::XASlave.Localization.UiMessage.Key("S043A718774C572BD"))), selectionDescription));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
    }
    private static int GetXagmanMatchRegistrationCount(
        AutoRetainerConfigReader.ArCharacterInfo arInfo,
        XagmanAutoRetainerMatchScope scope)
    {
        return scope switch
        {
            XagmanAutoRetainerMatchScope.Retainers or XagmanAutoRetainerMatchScope.WithoutRetainers
                => Math.Max(0, arInfo.RetainerCount),
            XagmanAutoRetainerMatchScope.Submarines or XagmanAutoRetainerMatchScope.WithoutSubmarines
                => Math.Max(0, arInfo.SubmarineCount),
            _ => 0,
        };
    }
    private static bool DoesXagmanRegistrationMatchScope(
        XagmanAutoRetainerMatchScope scope,
        int registrationCount)
    {
        return scope switch
        {
            XagmanAutoRetainerMatchScope.Retainers or XagmanAutoRetainerMatchScope.Submarines
                => registrationCount > 0,
            XagmanAutoRetainerMatchScope.WithoutRetainers or XagmanAutoRetainerMatchScope.WithoutSubmarines
                => registrationCount <= 0,
            _ => true,
        };
    }
    private static global::XASlave.Localization.UiMessage GetXagmanMatchScopeQualifier(XagmanAutoRetainerMatchScope scope)
    {
        return scope switch
        {
            XagmanAutoRetainerMatchScope.Retainers => global::XASlave.Localization.UiMessage.Key("S4F6C8C64C9EA1477"),
            XagmanAutoRetainerMatchScope.Submarines => global::XASlave.Localization.UiMessage.Key("S52DD732C3E0DE75A"),
            XagmanAutoRetainerMatchScope.WithoutRetainers => global::XASlave.Localization.UiMessage.Key("S63A20856C823A60F"),
            XagmanAutoRetainerMatchScope.WithoutSubmarines => global::XASlave.Localization.UiMessage.Key("S9E58B508DE01A9A1"),
            _ => global::XASlave.Localization.UiMessage.Raw(string.Empty),
        };
    }
    private bool DoesXagmanFranchiseCharacterNeedItemChanges(string characterNameWorld, IReadOnlyList<XagmanItemEntry> items, bool ignoreGil)
    {
        var effectiveItems = ResolveXagmanItemsForOwner(items, characterNameWorld, out _);
        foreach (var item in effectiveItems)
        {
            if (IsXagmanGreenValueSelector(item.SelectorKind))
            {
                // XA Database cannot prove gearset, materia, glamour, binding, or AR protection
                // state. Keep the owner selected so the live fail-closed scanner can decide.
                return true;
            }
            if (!ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
                continue;

            var currentQuantity = GetXagmanCharacterItemQuantity(characterNameWorld, item.ItemId, item.IsHq, item.ItemName);
            var isGil = IsXagmanGilItem(item.ItemId);
            switch (item.Mode)
            {
                case XagmanItemMode.Give:
                    // Gil Give selects anyone holding at least 1 gil, but only when a give amount of 1+ is set.
                    if (isGil ? (item.Quantity >= 1 && currentQuantity >= 1) : currentQuantity > 0)
                        return true;
                    break;
                case XagmanItemMode.Balance:
                    // Balance selects characters that have more or less than the target amount.
                    if (currentQuantity != Math.Max(0, item.Quantity))
                        return true;
                    break;
                case XagmanItemMode.TopUp:
                    // TopUp selects only characters that are below the target amount.
                    if (currentQuantity < Math.Max(0, item.Quantity))
                        return true;
                    break;
                case XagmanItemMode.Take:
                    // Gil Take selects the character only when a non-zero take amount is set.
                    if (!isGil || item.Quantity != 0)
                        return true;
                    break;
            }
        }

        return false;
    }
    private static string BuildXagmanMatchItemKey(uint itemId, bool isHq)
    {
        return $"{itemId}:{(isHq ? 1 : 0)}";
    }
    private static bool ShouldIncludeXagmanMatchingSelectionItem(XagmanItemEntry item, bool ignoreGil)
    {
        return item.ItemId > 0 && (!ignoreGil || item.ItemId > 1);
    }
    private static string BuildXagmanMatchingItemsKey(IReadOnlyList<XagmanItemEntry> items, bool ignoreGil)
    {
        return string.Join(",",
            items
                .Where(item => IsXagmanGreenValueSelector(item.SelectorKind)
                    || ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
                .Select(item => IsXagmanGreenValueSelector(item.SelectorKind)
                    ? $"selector:{(int)item.SelectorKind}"
                    : BuildXagmanMatchItemKey(item.ItemId, item.IsHq))
                .Distinct()
                .OrderBy(itemKey => itemKey, StringComparer.Ordinal));
    }
    private void QueueXagmanMatchingSelection(
        XagmanMatchSelectionTarget target,
        XagmanAutoRetainerMatchScope autoRetainerScope)
    {
        ClearXagmanMatchingSelectionCaches();
        var arByCharacter = new Dictionary<string, AutoRetainerConfigReader.ArCharacterInfo>(StringComparer.OrdinalIgnoreCase);
        if (autoRetainerScope != XagmanAutoRetainerMatchScope.All &&
            !TryRefreshXagmanAutoRetainerData(target, out arByCharacter))
            return;

        var cfg = plugin.Configuration;
        var ignoreGil = cfg.XagmanIgnoreGilInMatchingSelection;
        var selectionLabel = (target == XagmanMatchSelectionTarget.Tony ? global::XASlave.Localization.UiMessage.Key("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiMessage.Key("SAD5B29877C321F10"));
        var sourceItems = target == XagmanMatchSelectionTarget.Tony
            ? cfg.XagmanTonyItems
            : cfg.XagmanItems;
        var hasGreenValueSelector = HasXagmanGreenValueSelectors(sourceItems);
        var matchingItems = sourceItems
            .Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => group.First())
            .ToList();
        var visibleCharacterKeys = GetVisibleXagmanCharacterKeys(cfg, target);
        if (autoRetainerScope != XagmanAutoRetainerMatchScope.All)
        {
            visibleCharacterKeys.RemoveWhere(characterNameWorld =>
            {
                var registrationCount = arByCharacter.TryGetValue(characterNameWorld, out var arInfo)
                    ? GetXagmanMatchRegistrationCount(arInfo, autoRetainerScope)
                    : 0;
                return !DoesXagmanRegistrationMatchScope(autoRetainerScope, registrationCount);
            });
        }
        if (visibleCharacterKeys.Count == 0)
        {
            if (target == XagmanMatchSelectionTarget.Tony)
                xagmanTonySelectedIndices.Clear();
            else
                xagmanFranchiseSelectedIndices.Clear();
            xagmanPendingMatchSelection = null;
            var scopeDescription = (autoRetainerScope == XagmanAutoRetainerMatchScope.All ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Format("S30ADA5A3C2EB2AE0", GetXagmanMatchScopeQualifier(autoRetainerScope)));
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("SE31712D1E08F5D90", selectionLabel, scopeDescription));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
            return;
        }
        var itemsKey = BuildXagmanMatchingItemsKey(sourceItems, ignoreGil);
        if (string.IsNullOrWhiteSpace(itemsKey))
        {
            if (target == XagmanMatchSelectionTarget.Tony)
                xagmanTonySelectedIndices.Clear();
            else
                xagmanFranchiseSelectedIndices.Clear();
            xagmanPendingMatchSelection = null;
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, (ignoreGil ? global::XASlave.Localization.UiMessage.Key("S0C268B720148E7E2") : global::XASlave.Localization.UiMessage.Key("S80BA27FAB25C9E01")));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        var matches = hasGreenValueSelector
            ? new HashSet<string>(visibleCharacterKeys, StringComparer.OrdinalIgnoreCase)
            : GetXagmanMatchingCharacterKeys(matchingItems, ignoreGil);
        ApplyXagmanMatchingSelection(target, visibleCharacterKeys, matches, autoRetainerScope);
    }
    private void ApplyXagmanMatchingSelection(
        XagmanMatchSelectionTarget target,
        IReadOnlyCollection<string> visibleCharacterKeys,
        HashSet<string> matches,
        XagmanAutoRetainerMatchScope autoRetainerScope)
    {
        var visibleMatches = new HashSet<string>(visibleCharacterKeys.Where(matches.Contains), StringComparer.OrdinalIgnoreCase);
        var selectedCount = 0;
        if (target == XagmanMatchSelectionTarget.Tony)
        {
            xagmanTonySelectedIndices.Clear();
            var chars = plugin.Configuration.XagmanTonyCharacters;
            for (var i = 0; i < chars.Count; i++)
            {
                if (!visibleMatches.Contains(chars[i].CharacterNameWorld)
                    || !IsAutoRetainerCharacterAllowed(chars[i].CharacterNameWorld, plugin.Configuration.XagmanHonorArExclusions))
                    continue;
                xagmanTonySelectedIndices.Add(i);
                selectedCount++;
            }
        }
        else
        {
            xagmanFranchiseSelectedIndices.Clear();
            var chars = plugin.Configuration.XagmanFranchiseCharacters;
            for (var i = 0; i < chars.Count; i++)
            {
                if (!visibleMatches.Contains(chars[i])
                    || !IsAutoRetainerCharacterAllowed(chars[i], plugin.Configuration.XagmanHonorArExclusions))
                    continue;
                xagmanFranchiseSelectedIndices.Add(i);
                selectedCount++;
            }
        }
        var selectionLabel = (target == XagmanMatchSelectionTarget.Tony ? global::XASlave.Localization.UiMessage.Key("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiMessage.Key("SAD5B29877C321F10"));
        var scopeDescription = (autoRetainerScope == XagmanAutoRetainerMatchScope.All ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Format("S30ADA5A3C2EB2AE0", GetXagmanMatchScopeQualifier(autoRetainerScope)));
        var refreshDescription = (autoRetainerScope == XagmanAutoRetainerMatchScope.All ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Key("SEF0DBF1F7046377F"));
        InvalidateXagmanTradeCapacityForecast();
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S8D800126E07EE2AC", refreshDescription, selectedCount, selectionLabel, ((selectedCount == 1 ? global::XASlave.Localization.UiMessage.Value(string.Empty) : global::XASlave.Localization.UiMessage.Key("S043A718774C572BD"))), scopeDescription));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
    }
    private void ProcessXagmanPendingMatchSelection()
    {
        var request = xagmanPendingMatchSelection;
        if (request == null)
            return;
        if (request.NextQueryIndex < request.Queries.Count)
        {
            var queryRequest = request.Queries[request.NextQueryIndex];
            foreach (var result in SearchXagmanCharacterMatches(queryRequest.Query))
            {
                if (!queryRequest.ItemKeys.Contains(BuildXagmanMatchItemKey(result.ItemId, result.IsHq)))
                    continue;
                request.MatchKeys.Add(result.CharacterNameWorld);
            }
            request.NextQueryIndex++;
            var selectionLabel = (request.Target == XagmanMatchSelectionTarget.Tony ? global::XASlave.Localization.UiMessage.Key("SC5A8D95238CD3EE8") : global::XASlave.Localization.UiMessage.Key("SAD5B29877C321F10"));
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S3B6FF0B55412C277", selectionLabel, request.NextQueryIndex, request.Queries.Count));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            if (request.NextQueryIndex < request.Queries.Count)
                return;
        }
        xagmanMatchingCharacterKeysCache[request.ItemsKey] = new HashSet<string>(request.MatchKeys, StringComparer.OrdinalIgnoreCase);
        ApplyXagmanMatchingSelection(request.Target, request.VisibleCharacterKeys, request.MatchKeys, request.AutoRetainerScope);
        xagmanPendingMatchSelection = null;
    }
    private HashSet<string> GetXagmanMatchingCharacterKeys(IReadOnlyList<XagmanItemEntry> items, bool ignoreGil)
    {
        var itemKeys = items
            .Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil))
            .Select(item => BuildXagmanMatchItemKey(item.ItemId, item.IsHq))
            .Distinct()
            .OrderBy(itemKey => itemKey, StringComparer.Ordinal)
            .ToList();
        if (itemKeys.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // XA.Database.GetMatchingCharactersForItems returns character keys only and deliberately
        // searches all globally indexed item containers, including retainers. Xagman uses detailed
        // rows so ordinary items remain main-bag-only while elemental crystals can use Crystals.
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => ShouldIncludeXagmanMatchingSelectionItem(item, ignoreGil)))
        {
            var query = GetXagmanItemSearchQuery(item.ItemId, item.ItemName);
            if (string.IsNullOrWhiteSpace(query))
                continue;
            foreach (var result in SearchXagmanCharacterMatches(query))
            {
                if (result.ItemId != item.ItemId || result.IsHq != item.IsHq)
                    continue;
                matches.Add(result.CharacterNameWorld);
            }
        }
        return matches;
    }
    private List<XagmanItemSearchEntry> SearchXagmanItems(string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length < 2)
            return new List<XagmanItemSearchEntry>();

        return GetXagmanLuminaItemCatalog()
            .Where(entry => entry.ItemName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => GetXagmanItemSearchRank(entry.ItemName, normalizedQuery))
            .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ItemId)
            .ToList();
    }
    private List<XagmanItemSearchEntry> GetXagmanLuminaItemCatalog()
    {
        if (xagmanLuminaItemCatalogCache != null)
            return xagmanLuminaItemCatalogCache;

        var catalog = new List<XagmanItemSearchEntry>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var row in itemSheet)
        {
            var itemName = row.Name.ToString().Trim();
            if (row.RowId <= 1 || row.IsUntradable || string.IsNullOrWhiteSpace(itemName))
                continue;

            catalog.Add(new XagmanItemSearchEntry
            {
                ItemId = row.RowId,
                ItemName = itemName,
                CanBeHq = row.CanBeHq,
            });
        }

        if (catalog.Count > 0)
            xagmanLuminaItemCatalogCache = catalog;
        return catalog;
    }
    private string GetXagmanItemSearchQuery(uint itemId, string itemName)
    {
        var normalizedName = itemName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedName))
            return normalizedName;
        try
        {
            if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var itemRow))
                return itemRow.Name.ToString().Trim();
        }
        catch
        {
        }
        return string.Empty;
    }
    private static int GetXagmanItemSearchRank(string itemName, string query)
    {
        if (itemName.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        return itemName.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }
    private List<XagmanDbSearchMatch> SearchXagmanCharacterMatches(string query)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return new List<XagmanDbSearchMatch>();
        if (xagmanCharacterMatchQueryCache.TryGetValue(normalizedQuery, out var cachedResults))
            return cachedResults;
        var raw = plugin.IpcClient.SearchItems(normalizedQuery);
        if (string.IsNullOrWhiteSpace(raw))
        {
            var emptyResults = new List<XagmanDbSearchMatch>();
            xagmanCharacterMatchQueryCache[normalizedQuery] = emptyResults;
            return emptyResults;
        }
        var results = new List<XagmanDbSearchMatch>();
        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (TryParseXagmanSearchMatch(line, out var match)
                && IsXagmanSupportedItemContainer(match.ItemId, match.ContainerName))
            {
                results.Add(match);
            }
        }
        xagmanCharacterMatchQueryCache[normalizedQuery] = results;
        return results;
    }
    private bool TryParseXagmanSearchMatch(string line, out XagmanDbSearchMatch match)
    {
        var parts = line.Split('|');
        if (parts.Length < 7)
        {
            match = new XagmanDbSearchMatch();
            return false;
        }
        if (!uint.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
            || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
            || !bool.TryParse(parts[6], out var isHq))
        {
            match = new XagmanDbSearchMatch();
            return false;
        }
        match = new XagmanDbSearchMatch
        {
            CharacterNameWorld = $"{parts[0]}@{parts[1]}",
            ContainerName = parts[2],
            ItemId = itemId,
            ItemName = parts[3],
            Quantity = quantity,
            IsHq = isHq,
        };
        return true;
    }
    private void AddXagmanItem(
        List<XagmanItemEntry> items,
        uint itemId,
        string itemName,
        bool isHq,
        bool searchOnly = false)
    {
        if ((isHq && !CanXagmanItemBeHq(itemId))
            || !CanAddXagmanItem(items, itemId, isHq, searchOnly))
            return;

        var applicability = XagmanItemApplicability.All;
        if (!searchOnly && items.Any(entry => entry.ItemId == itemId
                && entry.IsHq == isHq
                && entry.Applicability == XagmanItemApplicability.All))
        {
            applicability = items.Any(entry => entry.ItemId == itemId
                    && entry.IsHq == isHq
                    && entry.Applicability == XagmanItemApplicability.HasSubmarines)
                ? XagmanItemApplicability.HasRetainers
                : XagmanItemApplicability.HasSubmarines;
        }

        items.Add(new XagmanItemEntry
        {
            ItemId = itemId,
            ItemName = itemName,
            IsHq = isHq,
            Mode = XagmanItemMode.Give,
            Applicability = applicability,
            Quantity = 0,
        });
        SaveXagmanSharedItemsState();
    }

    private void AddXagmanGreenValueSelector(
        List<XagmanItemEntry> items,
        XagmanItemSelectorKind selectorKind)
    {
        if (!CanAddXagmanGreenValueSelector(items, selectorKind))
            return;

        var applicability = XagmanItemApplicability.All;
        if (items.Any(entry => entry.SelectorKind == selectorKind
                && entry.Applicability == XagmanItemApplicability.All))
        {
            applicability = items.Any(entry => entry.SelectorKind == selectorKind
                    && entry.Applicability == XagmanItemApplicability.HasSubmarines)
                ? XagmanItemApplicability.HasRetainers
                : XagmanItemApplicability.HasSubmarines;
        }

        items.Add(new XagmanItemEntry
        {
            SelectorKind = selectorKind,
            ItemId = 0,
            ItemName = GetXagmanGreenValueSelectorName(selectorKind),
            IsHq = false,
            Mode = XagmanItemMode.TopUp,
            Applicability = applicability,
            Quantity = 0,
        });
        SaveXagmanSharedItemsState();
    }

    private void DrawXagmanSavedListsPopup(string title, List<XagmanItemEntry> items, string id, bool searchOnly = false)
    {
        using var popup = ImRaii.Popup($"{id}ListsPopup");
        if (!popup)
            return;
        ImGui.SetNextItemWidth(Scale(220f));
        ImGui.InputTextWithHint($"##{id}SaveListName", global::XASlave.Localization.UiLocale.Get("SA3C674C0BAE2100E"), ref xagmanSavedItemListName, 128);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S7F0C9FF9BFED4770") + $"##{id}SaveCurrent")))
            SaveXagmanNamedItemList(title, items, searchOnly);
        ImGui.Separator();
        var savedLists = plugin.Configuration.XagmanSavedItemLists;
        if (savedLists.Count == 0)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SB908027C027C465D"));
            return;
        }
        foreach (var saved in savedLists.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ImGui.TextUnformatted(saved.Name);
            ImGui.SameLine();
            if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S8A6BDB6B18DA586F") + $"##{id}Load{saved.Name}")))
                LoadXagmanNamedItemList(saved, items, searchOnly);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
            {
                if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##{id}Delete{saved.Name}")))
                {
                    plugin.Configuration.XagmanSavedItemLists.RemoveAll(entry => entry.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
                    plugin.Configuration.Save();
                    break;
                }
            }
        }
    }

    private void DrawXagmanMassModePopup(List<XagmanItemEntry> items, string id)
    {
        using var popup = ImRaii.Popup($"{id}SetAllPopup");
        if (!popup)
            return;
        if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("SC6402106C8212060"), false))
            SetAllXagmanItemModes(items, XagmanItemMode.Give);
        if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("S8B862BA45DCEDDF7"), false))
            SetAllXagmanItemModes(items, XagmanItemMode.Take);
        if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("SD05E07B7C14E596A"), false))
            SetAllXagmanItemModes(items, XagmanItemMode.Balance);
        if (global::XASlave.Localization.UiNative.Selectable(global::XASlave.Localization.UiLocale.Native("S2EC76BC0576DB080"), false))
            SetAllXagmanItemModes(items, XagmanItemMode.TopUp);
    }

    private void SaveXagmanNamedItemList(string title, IReadOnlyList<XagmanItemEntry> items, bool searchOnly = false)
    {
        var name = xagmanSavedItemListName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S8FD2B9CA61EABBC9", title));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        var saved = plugin.Configuration.XagmanSavedItemLists.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var clonedItems = CloneXagmanItems(items, searchOnly);
        if (saved == null)
        {
            plugin.Configuration.XagmanSavedItemLists.Add(new XagmanNamedItemList
            {
                Name = name,
                Items = clonedItems,
            });
        }
        else
        {
            saved.Name = name;
            saved.Items = clonedItems;
        }
        plugin.Configuration.Save();
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S9D6A690169A29FE5", name));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        xagmanSavedItemListName = name;
    }

    private void LoadXagmanNamedItemList(XagmanNamedItemList saved, List<XagmanItemEntry> items, bool searchOnly = false)
    {
        items.Clear();
        items.AddRange(CloneXagmanItems(saved.Items, searchOnly));
        SaveXagmanSharedItemsState();
        xagmanSavedItemListName = saved.Name;
        ClearXagmanItemSearch();
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("SC8B8513AA5C91C9D", saved.Name));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private void SaveXagmanSharedItemsState()
    {
        ResetXagmanMatchingCharacterSelection();
        InvalidateXagmanTradeCapacityForecast();
        plugin.Configuration.XagmanSharedItemsMigrationComplete = true;
        plugin.Configuration.Save();
    }
    private void ResetXagmanMatchingCharacterSelection()
    {
        xagmanPendingMatchSelection = null;
        xagmanMatchingCharacterKeysCache.Clear();
    }
    private void ClearXagmanMatchingSelectionCaches()
    {
        ResetXagmanMatchingCharacterSelection();
        xagmanCharacterMatchQueryCache.Clear();
        RefreshXagmanTradeCapacityInventorySnapshots();
    }

    private void BeginXagmanXaDatabaseRefreshAndPull()
    {
        if (System.Threading.Interlocked.CompareExchange(ref xagmanManualXaDatabasePullRunning, 1, 0) != 0)
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Key("SF3AB942533E65921"));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        var expectedContentId = Plugin.PlayerState.ContentId;
        if (expectedContentId == 0)
        {
            System.Threading.Interlocked.Exchange(ref xagmanManualXaDatabasePullRunning, 0);
            SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage.Key("SB95322A82D33CEC6"));
            return;
        }

        if (!plugin.IpcClient.IsReady())
        {
            System.Threading.Interlocked.Exchange(ref xagmanManualXaDatabasePullRunning, 0);
            SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage.Key("S5F5878E47C3F194E"));
            return;
        }

        var expectedCharacterName = Plugin.PlayerState.CharacterName.ToString();
        var requestedAtUtc = DateTime.UtcNow;
        if (!plugin.IpcClient.Save())
        {
            System.Threading.Interlocked.Exchange(ref xagmanManualXaDatabasePullRunning, 0);
            SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage.Key("S3BA273A237250969"));
            return;
        }

        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S611DC3B46F2DFD77"));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(20);

        if (RunWorker("Xagman XA Database refresh and pull", async token =>
        {
            try
            {
                var deadlineUtc = requestedAtUtc.AddSeconds(15);
                while (DateTime.UtcNow < deadlineUtc)
                {
                    token.ThrowIfCancellationRequested();
                    var verification = await Plugin.RunOnGameThread(() =>
                    {
                        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId != expectedContentId)
                        {
                            return new XagmanSnapshotSaveVerification(
                                XagmanSnapshotSaveState.Failed,
                                global::XASlave.Localization.UiMessage.Key("S343F4C1B322FBE69"),
                                DateTime.MinValue,
                                DateTime.MinValue);
                        }

                        return XagmanSnapshotSaveVerifier.Evaluate(
                            plugin.IpcClient.GetLastSnapshotResultJson(),
                            expectedContentId,
                            requestedAtUtc);
                    });

                    if (verification.State == XagmanSnapshotSaveState.Waiting)
                    {
                        await System.Threading.Tasks.Task.Delay(100, token).ConfigureAwait(false);
                        continue;
                    }

                    if (verification.State == XagmanSnapshotSaveState.Failed)
                    {
                        await Plugin.RunOnGameThread(() => SetXagmanXaDatabaseRefreshFailure(verification.DetailMessage));
                        return;
                    }

                    await Plugin.RunOnGameThread(() =>
                    {
                        plugin.SlaveDatabase.RecordLastSyncedToXaDb(
                            expectedContentId,
                            expectedCharacterName,
                            verification.SavedAtUtc);
                        Plugin.Log.Information(
                            $"[XASlave] Xagman confirmed XA Database Refresh + Save for content ID {expectedContentId}; pulling committed character rows.");
                        PullXaDatabaseInfo(ClearXagmanMatchingSelectionCaches);
                    });
                    return;
                }

                await Plugin.RunOnGameThread(() =>
                    SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage.Key("S07AF75A427E2275D")));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await Plugin.RunOnGameThread(() =>
                    SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage.Format("S259F213FC3F57980", ex.Message)));
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref xagmanManualXaDatabasePullRunning, 0);
            }
        }))
        {
            return;
        }

        System.Threading.Interlocked.Exchange(ref xagmanManualXaDatabasePullRunning, 0);
        SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage.Key("SBE700CDF6F84CED9"));
    }

    private bool SetXagmanXaDatabaseRefreshFailure(global::XASlave.Localization.UiMessage detail)
    {
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S449ACB73151B16F2", detail));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(10);
        Plugin.Log.Warning($"[XASlave] Xagman Pull XA Database Info stopped before database reload: {detail}");
        return false;
    }

    private static List<XagmanItemEntry> CloneXagmanItems(IEnumerable<XagmanItemEntry> items, bool searchOnly = false)
    {
        var validItems = items
            .Where(item => IsValidXagmanItemEntry(item, searchOnly))
            .ToList();
        if (searchOnly)
        {
            return validItems
                .Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem)
                .GroupBy(item => new { item.ItemId, item.IsHq })
                .Select(group => new XagmanItemEntry
                {
                    SelectorKind = XagmanItemSelectorKind.ExactItem,
                    ItemId = group.Key.ItemId,
                    ItemName = group.First().ItemName,
                    IsHq = group.Key.IsHq,
                    Mode = XagmanItemMode.Give,
                    Applicability = XagmanItemApplicability.All,
                    Quantity = 0,
                })
                .OrderBy(item => item.ItemId)
                .ThenBy(item => item.IsHq)
                .ToList();
        }

        return validItems
            .GroupBy(item => new
            {
                item.SelectorKind,
                ItemId = item.SelectorKind == XagmanItemSelectorKind.ExactItem ? item.ItemId : 0u,
                IsHq = item.SelectorKind == XagmanItemSelectorKind.ExactItem && item.IsHq,
                item.Applicability,
            })
            .Select(group => new XagmanItemEntry
            {
                SelectorKind = group.Key.SelectorKind,
                ItemId = group.Key.ItemId,
                ItemName = IsXagmanGreenValueSelector(group.Key.SelectorKind)
                    ? GetXagmanGreenValueSelectorName(group.Key.SelectorKind)
                    : group.First().ItemName,
                IsHq = group.Key.IsHq,
                Mode = IsXagmanGreenValueSelector(group.Key.SelectorKind)
                    ? XagmanItemMode.TopUp
                    : group.First().Mode,
                Applicability = group.Key.Applicability,
                Quantity = Math.Max(0, group.First().Quantity),
            })
            .OrderBy(item => item.SelectorKind)
            .ThenBy(item => item.ItemId)
            .ThenBy(item => item.IsHq)
            .ThenBy(item => item.Applicability)
            .ToList();
    }

    private void SetAllXagmanItemModes(IEnumerable<XagmanItemEntry> items, XagmanItemMode mode)
    {
        var changed = false;
        foreach (var item in items)
        {
            if (IsXagmanGreenValueSelector(item.SelectorKind))
                continue;
            if (item.Mode == mode)
                continue;
            item.Mode = mode;
            changed = true;
        }
        if (changed)
            SaveXagmanSharedItemsState();
    }

    private void StartXagmanTonyTask()
    {
        var cfg = plugin.Configuration;
        var selected = GetSelectedXagmanTonyCharacters();
        ResetXagmanMeetRouteSnapshot();
        var serverMatching = HasXagmanServerMatchingMeetConfig();
        var hasSingleMeet = !string.IsNullOrWhiteSpace(cfg.XagmanTargetWorld) && !string.IsNullOrWhiteSpace(cfg.XagmanTargetAetheryte);
        if (selected.Count == 0 || (!serverMatching && !hasSingleMeet))
            return;
        if (!TryCaptureXagmanCustomMeetingSettings())
            return;
        PinXagmanMeetRouteSnapshot(serverMatching);
        if (serverMatching)
        {
            if (!TryValidateXagmanServerMatchingTravelPlanMessage(selected, out var routeFailure))
            {
                ReportXagmanTravelRouteError(global::XASlave.Localization.UiMessage.Format("S985A37F4B8CFBFBE", routeFailure));
                ResetXagmanMeetRouteSnapshot();
                return;
            }
            // Sweep regions/servers in order, so Tony rotation stays within a region until it is exhausted.
            selected = selected
                .OrderBy(entry => WorldData.GetSweepOrdinalForWorld(GetWorldFromKey(entry.CharacterNameWorld)))
                .ThenBy(entry => entry.CharacterNameWorld, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            foreach (var entry in selected)
            {
                if (TryValidateXagmanMeetTravelMessage(
                        entry.CharacterNameWorld,
                        GetXagmanFixedMeetWorld(),
                        out var routeFailure))
                {
                    continue;
                }

                ReportXagmanTravelRouteError(
                    global::XASlave.Localization.UiMessage.Format("SAFE7E2AF16FAF5A8", entry.CharacterNameWorld, routeFailure));
                ResetXagmanMeetRouteSnapshot();
                return;
            }
        }
        ClearXagmanRunSnapshot();
        ClearXagmanOwnerPolicyRunCapabilities();
        ResetXagmanFiniteTakeGoals();
        ResetXagmanSupplyCycle();
        ResetXagmanCollectionFirstRunState();
        ResetXagmanServerMatchingRunState();
        HaltAutoCollectionForPriorityTask("Xagman");
        plugin.TaskRunner.ClearRunHistory();
        xagmanCharacterFailureReasons.Clear();
        xagmanSkipFcReturnAfterFailureCharacter = string.Empty;
        ResetXagmanTravelFailureMonitor();
        plugin.TaskRunner.AddLog($"Xagman: starting Tony run with {selected.Count} selected character(s).");
        foreach (var regionGroup in selected.GroupBy(entry => GetXagmanRegionOfChar(entry.CharacterNameWorld)))
        {
            var regionLabel = string.IsNullOrWhiteSpace(regionGroup.Key) ? "unknown" : regionGroup.Key;
            plugin.TaskRunner.AddLog(
                $"Xagman: Tony roster region {regionLabel}: {regionGroup.Count()} character(s): {string.Join(", ", regionGroup.Select(entry => entry.CharacterNameWorld))}.");
        }
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession(global::XASlave.Localization.UiMessage.Key("S5C98FB91C701B669")))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Key("S8E0A134261ADF6DD") : global::XASlave.Localization.UiMessage.Key("SACA9B3777895B541")));
            }
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            ResetXagmanMeetRouteSnapshot();
            return;
        }
        SetXagmanRunning(true);
        xagmanActiveRole = XagmanRole.Tony;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S077DEADB366CFFD7"));
        xagmanOwnerStartRequested = false;
        xagmanOwnerStandbyPending = false;
        xagmanOwnerPauseForTonyRotationRequested = false;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        xagmanLastConsumedOwnerStandbyRotationRequestKey = string.Empty;
        xagmanOwnerCurrentCharacterIndex = -1;
        xagmanOwnerRunList.Clear();
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        ResetXagmanTonyApproachWait();
        xagmanObservedDropboxBusy = false;
        xagmanTonyObservedOwnerWork = false;
        xagmanTradeQuantitySnapshot.Clear();
        xagmanOwnerRequestedItems.Clear();
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
        xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
        ResetXagmanRecentFcReturn();
        xagmanTonyRunStartedAtUtc = DateTime.UtcNow;
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        xagmanOwnerRunPlan = Array.Empty<string>();
        xagmanTonyRunPlan = selected.Select(entry => entry.CharacterNameWorld).ToList();
        xagmanTonyRunList = xagmanTonyRunPlan.ToList();
        xagmanTonyTotalCharacters = xagmanTonyRunPlan.Count;
        xagmanTonyCompletedCharacters = 0;
        xagmanCurrentTonyIndex = 0;
        InitXagmanSweepState(xagmanTonyRunPlan);
        if (serverMatching)
        {
            // Do not commit to a region/Tony yet. Idle in discovery and let Franchise Owner presence
            // reveal which servers actually have owners, so empty servers/regions are never visited.
            xagmanActiveCharacter = string.Empty;
            xagmanPreferredTonyCharacter = string.Empty;
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("SBA182C05573D0771"));
            plugin.TaskRunner.AddLog("Xagman: Server Matching enabled; idling until Franchise Owners report which servers need processing.");
            PublishXagmanPresence();
        }
        else
        {
            xagmanPreferredTonyCharacter = selected[0].CharacterNameWorld;
            StartXagmanTonyStartup(selected[0], true);
        }
    }

    private bool StartXagmanFranchiseTask(
        bool startSignalReceived = false,
        bool resumeFromStandby = false,
        XagmanPeerMessage? startDirective = null)
    {
        var cfg = plugin.Configuration;
        var savedOwnerRunListCount = xagmanOwnerRunList.Count;
        var storedOwnerIndex = Math.Max(0, xagmanOwnerCurrentCharacterIndex);
        var selected = resumeFromStandby && savedOwnerRunListCount > 0
            ? xagmanOwnerRunList.ToList()
            : GetSelectedXagmanFranchiseCharacters();
        if (!resumeFromStandby)
        {
            if (!TryCaptureXagmanCustomMeetingSettings())
                return false;
            ClearXagmanRunSnapshot();
            ClearXagmanOwnerPolicyRunCapabilities();
            ResetXagmanFiniteTakeGoals();
            ResetXagmanSupplyCycle();
            ResetXagmanServerMatchingRunState();
            ResetXagmanOwnerTimings();
            if (!TryValidateXagmanGreenValueOwnerStart(startDirective))
                return false;
            if (!TryConfigureXagmanCollectionFirstOwnerRun(startDirective))
                return false;
            if (startDirective != null && !TryRememberXagmanOwnerSupplyStart(startDirective))
                return false;
            // When a Server Matching Tony is already advertising, process owners in Region -> Server
            // sweep order so each character waits the minimum time for its server's turn.
            if (selected.Count > 0)
            {
                selected = selected
                    .OrderBy(key => WorldData.GetSweepOrdinalForWorld(GetWorldFromKey(key)))
                    .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                xagmanOwnerSweepPendingDataCenter = selected.Count > 0 ? GetXagmanDataCenterOfChar(selected[0]) : string.Empty;
                if (IsXagmanOwnerServerMatchingActive())
                    xagmanServerMatchingActive = true;
            }
        }
        var startIndex = 0;

        if (resumeFromStandby)
        {
            if (!string.IsNullOrWhiteSpace(xagmanActiveCharacter))
            {
                startIndex = selected.FindIndex(character => character.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase));
                if (startIndex < 0)
                {
                    selected.Insert(0, xagmanActiveCharacter);
                    startIndex = 0;
                    plugin.TaskRunner.AddLog($"Xagman: standby owner {xagmanActiveCharacter} was missing from the resume list and was reinserted at the front.");
                }

                plugin.TaskRunner.AddLog($"Xagman: standby resume for {xagmanActiveCharacter} using selected {selected.Count}, saved run list {savedOwnerRunListCount}, stored index {storedOwnerIndex + 1}, rebound index {startIndex + 1}.");
                xagmanActiveCharacter = selected[startIndex];
            }
            else if (selected.Count > 0)
            {
                startIndex = Math.Min(storedOwnerIndex, selected.Count - 1);
                plugin.TaskRunner.AddLog($"Xagman: standby resume without an active owner using selected {selected.Count}, saved run list {savedOwnerRunListCount}, stored index {storedOwnerIndex + 1}, rebound index {startIndex + 1}.");
                xagmanActiveCharacter = selected[startIndex];
            }
        }

        if (selected.Count == 0)
            return false;
        if ((!resumeFromStandby || !xagmanOwnerPolicyRunCapabilitiesPinned)
            && !TryPrepareXagmanOwnerPolicyRunCapabilities(
                selected,
                resumeFromStandby ? "Franchise Owner standby resume" : "Franchise Owner start"))
        {
            if (IsXagmanCollectionFirstCollectionPhase())
            {
                ResetXagmanCollectionFirstRunState();
                PublishXagmanPresence();
            }
            return false;
        }
        if (!resumeFromStandby && IsXagmanCollectionFirstCollectionPhase())
        {
            var unknownCapabilityOwners = selected
                .Where(owner => !GetXagmanOwnerPolicyCapability(owner).IsKnown)
                .ToList();
            if (unknownCapabilityOwners.Count > 0)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText =
                    global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD8C91F393DA4470B", unknownCapabilityOwners.Count));
                plugin.TaskRunner.AddLog(
                    $"Xagman: {xagmanStatusText} Pull XA Database Info / refresh AutoRetainer registration, then start a new coordinated run. Unknown owners: {string.Join(", ", unknownCapabilityOwners)}.");
                ResetXagmanCollectionFirstRunState();
                PublishXagmanPresence();
                return false;
            }
        }
        if (!resumeFromStandby && IsXagmanCollectionFirstCollectionPhase())
        {
            xagmanCollectionFirstOwnerFullPlan = selected.ToList();
            xagmanCollectionFirstCollectionPlan = BuildXagmanCollectionFirstOwnerPhasePlan(
                xagmanCollectionFirstOwnerFullPlan,
                XagmanRunPhase.Collection);
            xagmanCollectionFirstRestockPlan = BuildXagmanCollectionFirstOwnerPhasePlan(
                xagmanCollectionFirstOwnerFullPlan,
                XagmanRunPhase.Restock);
            selected = xagmanCollectionFirstCollectionPlan.ToList();
            xagmanPhaseTotalCharacters = selected.Count;
            xagmanPhaseResolvedCharacters = 0;
            xagmanPhaseComplete = selected.Count == 0;
            plugin.TaskRunner.AddLog(
                $"Xagman: collection-first run {xagmanRunId} planned {xagmanCollectionFirstCollectionPlan.Count} collection candidate(s) and {xagmanCollectionFirstRestockPlan.Count} restock candidate(s) from {xagmanCollectionFirstOwnerFullPlan.Count} selected owner(s).");
        }
        HaltAutoCollectionForPriorityTask("Xagman");
        if (!resumeFromStandby)
        {
            plugin.TaskRunner.ClearRunHistory();
            xagmanCharacterFailureReasons.Clear();
            xagmanSkipFcReturnAfterFailureCharacter = string.Empty;
            ResetXagmanTravelFailureMonitor();
        }
        plugin.TaskRunner.AddLog(
            $"Xagman: {(resumeFromStandby ? "resuming" : "starting")} Franchise Owner run with {selected.Count} character(s); starting at {Math.Min(startIndex + 1, selected.Count)}/{selected.Count}, {Math.Max(0, selected.Count - startIndex)} remaining.");
        foreach (var regionGroup in selected.Skip(startIndex).GroupBy(GetXagmanRegionOfChar))
        {
            var regionLabel = string.IsNullOrWhiteSpace(regionGroup.Key) ? "unknown" : regionGroup.Key;
            plugin.TaskRunner.AddLog(
                $"Xagman: remaining Franchise Owner roster region {regionLabel}: {regionGroup.Count()} character(s): {string.Join(", ", regionGroup)}.");
        }
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        if (!resumeFromStandby && IsXagmanCollectionFirstCollectionPhase())
        {
            plugin.TaskRunner.AddLog(
                $"Xagman: collection-first run {xagmanRunId} planned {xagmanCollectionFirstCollectionPlan.Count} collection candidate(s) and {xagmanCollectionFirstRestockPlan.Count} restock candidate(s) from {xagmanCollectionFirstOwnerFullPlan.Count} selected owner(s).");
        }
        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession((resumeFromStandby ? global::XASlave.Localization.UiMessage.Key("S038018BA3F633FC9") : global::XASlave.Localization.UiMessage.Key("S3AF5D5D4C3B9B723"))))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Key("S8E0A134261ADF6DD") : global::XASlave.Localization.UiMessage.Key("SACA9B3777895B541")));
            }
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            if (IsXagmanCollectionFirstCollectionPhase())
            {
                ResetXagmanCollectionFirstRunState();
                PublishXagmanPresence();
            }
            return false;
        }
        SetXagmanRunning(true);
        xagmanActiveRole = XagmanRole.FranchiseOwner;
        xagmanStatus = XagmanStatus.Paused;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S9BC9B696FB7B7A03"));
        if (!resumeFromStandby || xagmanOwnerRunPlan.Count == 0)
            xagmanOwnerRunPlan = selected.ToList();
        if (!resumeFromStandby)
            xagmanTonyRunPlan = Array.Empty<string>();
        xagmanOwnerRunList = selected.ToList();
        InvalidateXagmanTradeCapacityForecast();
        xagmanOwnerTotalCharacters = selected.Count;
        xagmanOwnerCompletedCharacters = Math.Max(0, Math.Min(startIndex, xagmanOwnerTotalCharacters));
        xagmanOwnerCurrentCharacterIndex = startIndex;
        if (!startSignalReceived)
            xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanOwnerStandbyPending = false;
        xagmanOwnerStandbyTonyCharacter = string.Empty;
        xagmanOwnerStandbyTonyInstanceId = string.Empty;
        xagmanOwnerStandbyPriorTonyCallReleased = false;
        xagmanOwnerPauseForTonyRotationRequested = false;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        xagmanOwnerStartRequested = startSignalReceived;
        xagmanTradeQuantitySnapshot.Clear();
        if (!resumeFromStandby)
            xagmanOwnerRequestedItems.Clear();
        if (!resumeFromStandby)
            xagmanQueueRequestedAtUtc = DateTime.MinValue;
        if (!resumeFromStandby)
            ResetXagmanRecentFcReturn();
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanTonyRunStartedAtUtc = DateTime.MinValue;
        if (IsXagmanCollectionFirstCollectionPhase() && selected.Count == 0)
        {
            xagmanOwnerRunPlan = Array.Empty<string>();
            xagmanOwnerRunList.Clear();
            xagmanOwnerTotalCharacters = 0;
            xagmanOwnerCompletedCharacters = 0;
            xagmanOwnerCurrentCharacterIndex = -1;
            xagmanOwnerSweepPendingDataCenter = string.Empty;
            xagmanStatus = XagmanStatus.Paused;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S0EB7C8EA67B7D63C"));
            plugin.TaskRunner.AddLog($"Xagman: collection-first run {xagmanRunId} has no collection candidates on this client; collection phase acknowledged.");
            PublishXagmanPresence();
            return true;
        }
        if (!resumeFromStandby)
            SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        TryResolveXagmanMeetDestinationForOwner();
        if (ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(xagmanActiveCharacter)
            && !TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Format("SF31A1118FA0BCF92", xagmanActiveCharacter)))
        {
            return false;
        }
        PublishXagmanPresence();
        var steps = BuildXagmanFranchiseSteps(selected, startIndex);
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onFinished: OnXagmanFranchiseTaskFinished, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), preserveRunHistory: true);
        plugin.TaskRunner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
        plugin.TaskRunner.CompletedItems = GetXagmanLocalOwnerCompletedCharacters();
        return true;
    }

    private void SetXagmanRunning(bool value)
    {
        if (!value)
        {
            StopXagmanCustomMeetingMovement();
            if (xagmanRunning && (xagmanMiniSnapshot.Running || !xagmanMiniSnapshot.HasRun))
                UpdateXagmanMiniSnapshot(true);
            ResetXagmanRelogDatabasePull();
            xagmanArMultiGuardActive = false;
            EndXagmanLobbyOperation();
            ClearXagmanExpectedTravelLogoutWindow();
            ResetXagmanTravelFailureMonitor();
        }

        if (xagmanRunning == value)
        {
            UpdateXagmanTeleportHelperRequirement();
            plugin.TargetCommandFix.SetRequiredByXagman(value);
            return;
        }

        xagmanRunning = value;
        UpdateXagmanTeleportHelperRequirement();
        InvalidateXagmanTradeCapacityForecast();
        plugin.TargetCommandFix.SetRequiredByXagman(value);
        if (value)
        {
            plugin.TaskRunner.AddLog("Xagman: Fix /target Command is required while Xagman is running.");
            plugin.TaskRunner.AddLog("Xagman: Teleport Helper is required while operating; aetheryte ticket prompts select No regardless of saved mod settings.");
        }
    }

    private void UpdateXagmanTeleportHelperRequirement()
    {
        // Completion can clear xagmanRunning before its final FC-return task finishes.
        plugin.TeleportHelper.SetRequiredByXagman(!isDisposed && (xagmanRunning
            || (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))));
    }

    private void OnXagmanFranchiseTaskFinished()
    {
        if (xagmanOwnerStandbyPending || xagmanStatus != XagmanStatus.Completed)
            return;
        if (xagmanTonyCompletionRequestedAtUtc == DateTime.MinValue
            && plugin.XagmanPeers != null
            && !plugin.XagmanPeers.IsDisposed
            && plugin.XagmanPeers.IsStarted)
        {
            if (RequestXagmanTonyCompletion())
            {
                plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
                    ? "Xagman: sent Tony completion signal."
                    : $"Xagman: sent Tony completion signal to {xagmanPreferredTonyCharacter}.");
            }
            else
            {
                plugin.TaskRunner.AddLog("Xagman: could not send Tony completion signal before shutdown.");
            }
        }
        ScheduleXagmanLocalShutdown("Franchise Owner completion");
    }

    private void StartXagmanTonyStartup(XagmanTonyCharacterEntry entry, bool includePreflight)
    {
        var meetWorld = xagmanServerMatchingActive
            ? GetXagmanTonySweepMeetWorld()
            : GetXagmanFixedMeetWorld();
        var meetAetheryte = GetXagmanSharedMeetLocation();
        if (xagmanServerMatchingActive
            && !TryValidateXagmanServerMatchingTonyRouteMessage(
                entry.CharacterNameWorld,
                xagmanSweepRegion,
                xagmanSweepDataCenter,
                out var serverRouteFailure))
        {
            ReportXagmanTravelRouteError(
                global::XASlave.Localization.UiMessage.Format("SF9CD82FA2ED7DC70", entry.CharacterNameWorld, serverRouteFailure));
            return;
        }
        if (!TryValidateXagmanMeetTravelMessage(entry.CharacterNameWorld, meetWorld, out var routeFailure))
        {
            ReportXagmanTravelRouteError(
                global::XASlave.Localization.UiMessage.Format("S5D3A48CB2A878DEF", entry.CharacterNameWorld, meetWorld, routeFailure));
            return;
        }

        xagmanActiveCharacter = entry.CharacterNameWorld;
        ResetXagmanTonySupplyDrain();
        // Standby belongs to the outgoing receiver. Keep the consumed request key so
        // replayed acknowledgements cannot rotate this fresh replacement either.
        xagmanTonyRotationRequestedByOwnerStandby = false;
        BeginXagmanTonySupplyPass();
        xagmanPreferredTonyCharacter = entry.CharacterNameWorld;
        xagmanTonyMode = entry.Mode;
        xagmanTonyObservedOwnerWork = false;
        ResetXagmanTonySellLocation();
        ResetXagmanTonyMeetRetryState();
        ClearXagmanExpectedTravelLogoutWindow();
        SetXagmanActiveMeetDestination(meetWorld, meetAetheryte);
        xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
        xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
        PublishXagmanPresence();
        var steps = BuildXagmanTonyStartupSteps(entry, includePreflight);
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true, preserveRunHistory: true);
        UpdateXagmanTonyTaskRunnerProgress();
    }

    private void ResetXagmanTonyMeetRetryState()
    {
        EndXagmanTravelFailure(xagmanTravelFailureToken);
        xagmanTonyMeetRetryCount = 0;
        xagmanTonyLastMeetRetryUtc = DateTime.MinValue;
        xagmanTonyMeetCommandPhase = XagmanTonyMeetCommandPhase.None;
        xagmanTonyMeetCommandDeadlineUtc = DateTime.MinValue;
    }

    private void SetXagmanTonySellLocation(XagmanTonySellDestination destination, Vector3 fallbackPosition)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        xagmanTonySellLocationActive = true;
        xagmanTonySellLocationTerritoryId = Plugin.ClientState.TerritoryType;
        xagmanTonySellLocationName = $"{destination.NpcName} at {destination.LocationName}";
        xagmanTonySellLocationPosition = local == null ? fallbackPosition : local.Position;
    }

    private void ResetXagmanTonySellLocation()
    {
        xagmanTonySellLocationActive = false;
        xagmanTonySellLocationTerritoryId = 0;
        xagmanTonySellLocationName = string.Empty;
        xagmanTonySellLocationPosition = Vector3.Zero;
    }

    private static bool IsValidXagmanPosition(Vector3 position)
    {
        return !float.IsNaN(position.X) && !float.IsNaN(position.Y) && !float.IsNaN(position.Z)
            && !float.IsInfinity(position.X) && !float.IsInfinity(position.Y) && !float.IsInfinity(position.Z);
    }

    private static Vector3 RandomizeXagmanPosition(Vector3 position, float radius)
    {
        if (radius <= 0f)
            return position;
        var angle = Random.Shared.NextDouble() * Math.PI * 2.0;
        var distance = Math.Sqrt(Random.Shared.NextDouble()) * radius;
        return new Vector3(
            position.X + (float)(Math.Cos(angle) * distance),
            position.Y,
            position.Z + (float)(Math.Sin(angle) * distance));
    }

    private static Vector3 RandomizeXagmanPositionDeterministic(Vector3 position, float radius, string seed)
    {
        if (radius <= 0f || string.IsNullOrWhiteSpace(seed))
            return position;

        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in seed)
            {
                hash ^= char.ToUpperInvariant(ch);
                hash *= 16777619u;
            }

            var angle = (hash & 0xFFFF) / 65535.0 * Math.PI * 2.0;
            var distance = Math.Sqrt(((hash >> 16) & 0xFFFF) / 65535.0) * radius;
            return new Vector3(
                position.X + (float)(Math.Cos(angle) * distance),
                position.Y,
                position.Z + (float)(Math.Sin(angle) * distance));
        }
    }

    private void RememberXagmanRecentFcReturn(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
        {
            ResetXagmanRecentFcReturn();
            return;
        }

        xagmanRecentFcReturnCharacter = characterNameWorld;
        xagmanRecentFcReturnAtUtc = DateTime.UtcNow;
    }

    private void ResetXagmanRecentFcReturn()
    {
        xagmanRecentFcReturnCharacter = string.Empty;
        xagmanRecentFcReturnAtUtc = DateTime.MinValue;
    }

    private bool ConsumeXagmanRecentFcReturn(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return false;

        if (string.IsNullOrWhiteSpace(xagmanRecentFcReturnCharacter)
            || !xagmanRecentFcReturnCharacter.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((DateTime.UtcNow - xagmanRecentFcReturnAtUtc).TotalSeconds > 120.0)
        {
            ResetXagmanRecentFcReturn();
            return false;
        }

        ResetXagmanRecentFcReturn();
        return true;
    }

    private List<TaskStep> BuildXagmanTonyStartupSteps(XagmanTonyCharacterEntry entry, bool includePreflight)
    {
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
        var startupFailed = false;

        void FailTonyStartup(global::XASlave.Localization.UiMessage messagePresentation)
        {
            var message = messagePresentation.Original;
            if (startupFailed)
                return;

            startupFailed = true;
            ResetXagmanTonyMeetRetryState();
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, messagePresentation);
            runner.AddLog(message);
            if (!TryEndXagmanTradeSafetySession("Tony startup failure"))
            {
                runner.AddLog(xagmanTradeSafetySessionActive
                    ? "Xagman: Dropbox auto-accept could not be confirmed off after the startup failure; trade refusal remains suppressed."
                    : "Xagman: Dropbox auto-accept is confirmed off after the startup failure, but the saved manual Refuse Trade Request preference could not be restored.");
                PublishXagmanPresence();
                return;
            }
            if (xagmanCharacterFailureReasons.ContainsKey(entry.CharacterNameWorld))
            {
                xagmanTonyCharacterFailurePending = true;
                runner.AddLog($"Xagman: Tony {entry.CharacterNameWorld} failed: {xagmanCharacterFailureReasons[entry.CharacterNameWorld]}; evaluating the next Tony after this sequence ends.");
                PublishXagmanPresence();
                return;
            }
            if (IsXagmanCollectionFirstRunActive())
            {
                runner.SuppressLogoutCancel = true;
                runner.AddLog(
                    $"Xagman: coordinated run {xagmanRunId} remains connected in Error so the frozen FO cohort is not silently orphaned. Use Stop All after inspecting the failure.");
                PublishXagmanPresence();
                return;
            }

            SetXagmanRunning(false);
            ResetXagmanServerMatchingRunState();
            ResetXagmanMeetRouteSnapshot();
            SetXagmanActiveMeetDestination(string.Empty, string.Empty);
            PublishXagmanPresence();
        }

        bool ShouldSkipTonyStartupPostTravel()
        {
            return startupFailed;
        }

        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SD1CAE0448D0AF198", entry.CharacterNameWorld),
            OnEnter = () =>
            {
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5C32B156B2CBC70B", entry.CharacterNameWorld));
                runner.AddLog($"Xagman: Tony {entry.CharacterNameWorld} is waiting for Franchise Owner meetup data.");
            },
            IsComplete = HasCompleteXagmanActiveMeetDestination,
            TimeoutSec = 86400f,
        });
        if (includePreflight)
            steps.AddRange(helper.BuildPreFlightOnlySteps(new List<string> { entry.CharacterNameWorld }, runner, RunXagmanPreflightArMultiGuard));
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SA59BE902908C8DF5", entry.CharacterNameWorld),
            OnEnter = () =>
            {
                runner.CurrentItemLabel = entry.CharacterNameWorld;
                runner.AddLog($"Xagman: starting Tony {entry.CharacterNameWorld}.");
                xagmanStatus = includePreflight ? XagmanStatus.Preflight : XagmanStatus.Relogging;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SDCED49B8C71F33C2", entry.CharacterNameWorld));
                UpdateXagmanTonyTaskRunnerProgress();
                PublishXagmanPresence();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        AddXagmanRelogSteps(
            steps,
            entry.CharacterNameWorld,
            runner,
            () =>
            {
                xagmanStatus = XagmanStatus.Relogging;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S7B20348EE6C12574", entry.CharacterNameWorld));
            },
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC6206531396E6801", entry.CharacterNameWorld, GetXagmanActiveMeetDestinationLabel()));
            },
            () =>
            {
                FailTonyStartup(global::XASlave.Localization.UiMessage.Format("SDEEBE8D719518017", entry.CharacterNameWorld));
            });
        AddXagmanTeleportSteps(
            steps,
            global::XASlave.Localization.UiMessage.Key("S74567D3512CD2C29"),
            GetXagmanActiveMeetDestinationCommand,
            runner,
            () => IsXagmanAtMeetDestination(GetXagmanActiveMeetWorld(), GetXagmanActiveMeetAetheryte()),
            false,
            () =>
            {
                xagmanStatus = XagmanStatus.Traveling;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC6206531396E6801", entry.CharacterNameWorld, GetXagmanActiveMeetDestinationLabel()));
            },
            () =>
            {
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SBCE18B2A2A111F23", entry.CharacterNameWorld, GetXagmanActiveMeetDestinationLabel()));
            },
            () =>
            {
                FailTonyStartup(global::XASlave.Localization.UiMessage.Format("S3DB25495FCE180B2", entry.CharacterNameWorld, GetXagmanActiveMeetDestinationLabel()));
            },
            ShouldSkipTonyStartupPostTravel,
            waitStartTimeoutSec: 600f,
            reissueWhileWaiting: true,
            expectCrossDataCenterLogout: true,
            travelSourceCharacterProvider: () => entry.CharacterNameWorld,
            travelDestinationWorldProvider: GetXagmanActiveMeetWorld);
        AddXagmanCustomMeetingStep(steps, entry.CharacterNameWorld, ShouldSkipTonyStartupPostTravel);
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S8C575B24EC12F119", entry.CharacterNameWorld),
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = () =>
            {
                runner.AddLog($"Xagman: Tony {entry.CharacterNameWorld} is ready for queue processing.");
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SAEC16B1104AE8A64", entry.CharacterNameWorld));
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanObservedDropboxBusy = false;
                ResetXagmanTonyMeetRetryState();
                UpdateXagmanTonyTaskRunnerProgress();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S1ED4869102AC29CE", entry.CharacterNameWorld),
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = OpenXagmanDropboxWindow,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SA09CBFC89FE7FDE3", entry.CharacterNameWorld),
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = OpenXagmanDropboxTradeTab,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SB7AF23C59FAE7A20", entry.CharacterNameWorld), 1.0f, ShouldSkipTonyStartupPostTravel));
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S305281BEE8E4C87E", entry.CharacterNameWorld),
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = ClearXagmanDropbox,
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SCFD498512EE8EF29", entry.CharacterNameWorld),
            ShouldSkip = ShouldSkipTonyStartupPostTravel,
            OnEnter = () =>
            {
                PublishXagmanPresence();
                StartAllXagmanPeers();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        return steps;
    }

    private void StopXagmanTask()
    {
        xagmanNpcSell?.Abort("Xagman stopped");
        xagmanNpcSell = null;
        xagmanOwnerRelogLogoutFailure = null;
        xagmanTonyCharacterFailurePending = false;
        ResetXagmanTravelFailureMonitor();
        xagmanExpectedLogout = false;
        if (xagmanRunning || xagmanOwnerRunPlan.Count > 0 || xagmanTonyRunPlan.Count > 0)
            CaptureXagmanRunSnapshot();
        RetainXagmanMiniResultBeforeStop();
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();
        ResetXagmanTonyMeetRetryState();
        var tradeSafetyClosed = TryEndXagmanTradeSafetySession("Xagman stop");
        ClearXagmanFocusTarget();
        SetXagmanRunning(false);
        xagmanActiveRole = plugin.Configuration.XagmanRole;
        xagmanStatus = tradeSafetyClosed ? XagmanStatus.Idle : XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (tradeSafetyClosed ? global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441") : (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Key("S921F55A662CF3E6D") : global::XASlave.Localization.UiMessage.Key("S675F5A0B12108472"))));
        xagmanActiveCharacter = string.Empty;
        xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveMeetWorld = string.Empty;
        xagmanActiveMeetAetheryte = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanTonyObservedOwnerWork = false;
        xagmanOwnerStartRequested = false;
        xagmanOwnerStandbyPending = false;
        xagmanOwnerPauseForTonyRotationRequested = false;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        xagmanLastConsumedOwnerStandbyRotationRequestKey = string.Empty;
        xagmanOwnerCompletedCharacters = 0;
        xagmanOwnerTotalCharacters = 0;
        xagmanTonyCompletedCharacters = 0;
        xagmanTonyTotalCharacters = 0;
        ResetXagmanTonySellLocation();
        xagmanTradeQuantitySnapshot.Clear();
        ClearXagmanOwnerPolicyRunCapabilities();
        xagmanOwnerRequestedItems.Clear();
        ResetXagmanFiniteTakeGoals();
        xagmanQueueRequestedAtUtc = DateTime.MinValue;
        xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
        xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
        xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
        xagmanTonyRunStartedAtUtc = DateTime.MinValue;
        xagmanOwnerCurrentCharacterIndex = -1;
        xagmanOwnerRunPlan = Array.Empty<string>();
        xagmanTonyRunPlan = Array.Empty<string>();
        xagmanOwnerRunList.Clear();
        xagmanTonyRunList.Clear();
        xagmanCurrentTonyIndex = -1;
        ResetXagmanServerMatchingRunState();
        ResetXagmanMeetRouteSnapshot();
        ResetXagmanOnhState();
        ResetXagmanCollectionFirstRunState();
        PublishXagmanPresence();
        UpdatePriorityTaskExternalStatus();
    }

    private bool DisconnectXagmanPeerService()
    {
        if (plugin.XagmanPeers == null || plugin.XagmanPeers.IsDisposed || !plugin.XagmanPeers.IsStarted)
            return false;
        plugin.SetXagmanPeerConnectionsEnabled(false);
        plugin.TaskRunner.AddLog("Xagman: disconnected local TCP peer service.");
        return true;
    }

    private void FinalizeXagmanLocalShutdown(string reason, bool disconnectBeforeStop = false)
    {
        if (disconnectBeforeStop)
            DisconnectXagmanPeerService();
        StopXagmanTask();
        if (!disconnectBeforeStop)
            DisconnectXagmanPeerService();
        plugin.TaskRunner.AddLog($"Xagman: finalized local shutdown after {reason}.");
    }

    private void AddXagmanTradeSafetyCompletionStep(
        List<TaskStep> steps,
        TaskRunner runner,
        string contextLabel,
        bool expectLogoutOrExit,
        bool allowAutoClean = true,
        Func<bool>? skipAutoClean = null)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S03404D388B339036"),
            OnEnter = () =>
            {
                xagmanExpectedLogout = false;
                if (!TryEndXagmanTradeSafetySession(contextLabel))
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (xagmanTradeSafetySessionActive ? global::XASlave.Localization.UiMessage.Key("S5A1ACAD23C0FECE9") : global::XASlave.Localization.UiMessage.Key("S5917CE09D92C0C06")));
                    runner.AddLog($"Xagman: {xagmanStatusText}");
                    runner.Cancel();
                    return;
                }

                // The explicit completion action may now hand character rotation back to AR.
                xagmanArMultiGuardActive = false;
                xagmanExpectedLogout = expectLogoutOrExit;
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S7A6A9E948F61245C"),
            ShouldSkip = () => !allowAutoClean || !plugin.Configuration.XagmanAutoCleanQueueList
                || xagmanOwnerStandbyPending || xagmanStatus == XagmanStatus.Error || (skipAutoClean?.Invoke() ?? false),
            OnEnter = AutoCleanXagmanQueueResults,
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    private void ScheduleXagmanLocalShutdown(string reason, int delayMs = 1000, bool disconnectBeforeStop = false)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(delayMs).ConfigureAwait(false);
                await Plugin.RunOnGameThread(() => FinalizeXagmanLocalShutdown(reason, disconnectBeforeStop)).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    private void MarkXagmanTonyConsumed(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return;
        var wasPresent = xagmanTonyRunList.Any(key => key.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase));
        xagmanTonyRunList = xagmanTonyRunList
            .Where(key => !key.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (wasPresent)
            xagmanTonyCompletedCharacters = Math.Min(GetXagmanLocalTonyTotalCharacters(), xagmanTonyCompletedCharacters + 1);
        xagmanCurrentTonyIndex = xagmanTonyRunList.Count > 0 ? 0 : -1;
        if (IsXagmanCollectionFirstCollectionPhase() || IsXagmanCapacityDrainActive())
            return;
        for (var index = 0; index < plugin.Configuration.XagmanTonyCharacters.Count; index++)
        {
            if (!plugin.Configuration.XagmanTonyCharacters[index].CharacterNameWorld.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase))
                continue;
            if (xagmanTonySelectedIndices.Remove(index))
                plugin.TaskRunner.AddLog($"Xagman: unchecked Tony {characterNameWorld} from the Tony list.");
            break;
        }
    }

    private static bool IsXagmanPeerFresh(XagmanPeerPresence peer, double maxAgeSeconds = 5.0)
    {
        return peer.LastSeenUtc > DateTime.MinValue
            && (DateTime.UtcNow - peer.LastSeenUtc).TotalSeconds <= maxAgeSeconds;
    }

    private List<XagmanPeerPresence> GetXagmanRelevantOwnerPeersForTony(string tonyCharacter, bool enabledOnly = false, bool freshOnly = false)
    {
        var query = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
            .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase));
        if (enabledOnly)
            query = query.Where(peer => peer.XagmanEnabled);
        if (freshOnly)
            query = query.Where(peer => IsXagmanPeerFresh(peer));
        return query.ToList();
    }

    private int GetXagmanRemainingFranchiseOwnerCountForTony(string tonyCharacter, bool freshOnly = false)
    {
        var ownerPeers = GetXagmanRelevantOwnerPeersForTony(tonyCharacter, true, freshOnly);
        return GetXagmanRemainingFranchiseOwnerCount(ownerPeers);
    }

    private static int GetXagmanRemainingFranchiseOwnerCount(IReadOnlyList<XagmanPeerPresence> ownerPeers)
    {
        var totalFranchiseOwners = ownerPeers.Sum(peer => Math.Max(0, peer.TotalCharacters));
        var completedFranchiseOwners = ownerPeers.Sum(peer => Math.Max(0, peer.CompletedCharacters));
        completedFranchiseOwners = Math.Min(completedFranchiseOwners, totalFranchiseOwners);
        return Math.Max(0, totalFranchiseOwners - completedFranchiseOwners);
    }

    private static string GetXagmanPeerDisplayName(XagmanPeerPresence peer)
    {
        return string.IsNullOrWhiteSpace(peer.ActiveCharacter)
            ? peer.CharacterName
            : peer.ActiveCharacter;
    }

    private List<string> BuildXagmanCompletionWarningSummaryLines(string tonyCharacter)
    {
        var lines = new List<string>();
        var failedCharacters = plugin.TaskRunner.FailedCharacters
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (failedCharacters.Count > 0)
            lines.Add($"Failed characters: {string.Join(", ", failedCharacters)}");

        var remainingTonys = xagmanTonyRunList
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (remainingTonys.Count > 0)
            lines.Add($"Unprocessed Tonys: {string.Join(", ", remainingTonys)}");

        var partialOwners = plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
            .OrderByDescending(peer => peer.LastSeenUtc)
            .SelectMany(peer => peer.PartialOwners ?? new List<XagmanPartialOwnerState>())
            .Concat(xagmanPartialOwners.Values)
            .Where(owner => owner != null && !string.IsNullOrWhiteSpace(owner.CharacterNameWorld))
            .GroupBy(owner => owner.CharacterNameWorld, StringComparer.OrdinalIgnoreCase)
            .Select(group => new XagmanPartialOwnerState
            {
                CharacterNameWorld = group.First().CharacterNameWorld,
                ReasonMessage = group.First().ReasonMessage,
                RequestedItems = CloneXagmanTradeRequests(group.First().RequestedItems.Where(request => !IsXagmanOpenEndedSupplyRequest(request)).ToList()),
                PendingGiveItems = CloneXagmanTradeRequests(group.First().PendingGiveItems),
            })
            .Where(owner => owner.RequestedItems.Count > 0 || owner.PendingGiveItems.Count > 0)
            .ToList();
        foreach (var owner in partialOwners)
            lines.Add($"{GetXagmanPartialOwnerLabel(owner)} owner {owner.CharacterNameWorld}: {GetXagmanPartialOwnerDetail(owner)}");

        var unresolvedOwnerStates = GetXagmanRelevantOwnerPeersForTony(tonyCharacter, true)
            .Select(peer =>
            {
                var name = GetXagmanPeerDisplayName(peer);
                var requestedCount = Math.Max(0, peer.RequestedItems?.Count ?? 0);
                var requestedUnits = peer.RequestedItems == null
                    ? 0
                    : peer.RequestedItems.Sum(request => Math.Max(0, request.Quantity));
                var remainingCharacters = GetXagmanFinalUnresolvedOwnerCount(peer);
                var hasQueuedWork = peer.QueueRequestedAtUtc > DateTime.MinValue;
                var hasActiveStatus = !peer.SupplyPassComplete && peer.Status is XagmanStatus.ReadyForQueue or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.Called or XagmanStatus.Trading or XagmanStatus.Standby or XagmanStatus.Paused;
                if (string.IsNullOrWhiteSpace(name) || (!hasQueuedWork && !hasActiveStatus && requestedCount == 0 && remainingCharacters == 0))
                    return string.Empty;

                var flags = new List<string> { $"status={peer.Status}" };
                if (remainingCharacters > 0)
                    flags.Add($"remaining={remainingCharacters}");
                if (hasQueuedWork)
                    flags.Add("queued");
                if (requestedCount > 0)
                {
                    flags.Add(requestedUnits > 0
                        ? $"requests={requestedCount}, units={requestedUnits}"
                        : $"requests={requestedCount}");
                }

                return $"{name} [{string.Join(", ", flags)}]";
            })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unresolvedOwnerStates.Count > 0)
            lines.Add($"Unresolved owner/item state: {string.Join("; ", unresolvedOwnerStates)}");

        return lines;
    }

    private static bool IsXagmanOpenEndedSupplyRequest(XagmanTradeRequestEntry request)
        => request.SelectorKind == XagmanItemSelectorKind.ExactItem
            && request.Mode == XagmanItemMode.Take && request.TargetQuantity == 0;

    private static int GetXagmanFinalUnresolvedOwnerCount(XagmanPeerPresence peer)
    {
        var allAvailableOnly = peer.PartialOwners.Count(state => state.PendingGiveItems.Count == 0 && state.RequestedItems.Count > 0
            && state.RequestedItems.All(IsXagmanOpenEndedSupplyRequest));
        return Math.Max(0, peer.TotalCharacters - peer.CompletedCharacters - allAvailableOnly);
    }

    private void FinalizeXagmanOpenEndedSupplyRequests()
    {
        // Take 0 remains eligible for every supplier, but has no finite shortage after the last pass.
        foreach (var state in xagmanPartialOwners.Values.ToList())
        {
            state.RequestedItems.RemoveAll(IsXagmanOpenEndedSupplyRequest);
            if (state.RequestedItems.Count != 0 || state.PendingGiveItems.Count != 0) continue;
            xagmanPartialOwners.Remove(state.CharacterNameWorld);
            if (plugin.TaskRunner.FailedCharacters.Contains(state.CharacterNameWorld)
                || xagmanSkippedCharacters.Contains(state.CharacterNameWorld)) continue;
            xagmanOwnerCompletedKeys.Add(state.CharacterNameWorld);
            if (xagmanActiveRole == XagmanRole.FranchiseOwner)
            {
                xagmanFranchiseSelectedIndices.RemoveWhere(index => index >= 0
                    && index < plugin.Configuration.XagmanFranchiseCharacters.Count
                    && plugin.Configuration.XagmanFranchiseCharacters[index].Equals(state.CharacterNameWorld, StringComparison.OrdinalIgnoreCase));
                plugin.TaskRunner.AddLog($"Xagman: owner {state.CharacterNameWorld} finished all-available requests across the selected suppliers; no finite shortage remains.");
            }
        }
        if (xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            xagmanOwnerCompletedCharacters = xagmanOwnerCompletedKeys.Count;
            plugin.TaskRunner.CompletedItems = xagmanOwnerCompletedCharacters;
        }
    }

    private static bool HasXagmanPendingOwnerWork(XagmanPeerPresence peer)
    {
        return peer.QueueRequestedAtUtc > DateTime.MinValue
            || peer.Status is XagmanStatus.ReadyForQueue or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.Called or XagmanStatus.Trading or XagmanStatus.Standby or XagmanStatus.Paused
            || (peer.RequestedItems?.Count ?? 0) > 0
            || peer.TotalCharacters > peer.CompletedCharacters;
    }

    private bool HasXagmanPendingOwnerWork(IReadOnlyList<XagmanPeerPresence> ownerPeers)
    {
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return true;
        return ownerPeers.Any(HasXagmanPendingOwnerWork);
    }

    private bool HasXagmanPendingOwnerWork()
    {
        return HasXagmanPendingOwnerWork(plugin.XagmanPeers.Peers
            .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => peer.XagmanEnabled)
            .ToList());
    }

    private void UpdateXagmanTonyOwnerDisconnectCompletionState(IReadOnlyList<XagmanPeerPresence> liveRelevantOwnerPeers)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony)
        {
            xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
            xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
            return;
        }

        if (liveRelevantOwnerPeers.Count > 0)
        {
            xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
            if (GetXagmanRemainingFranchiseOwnerCount(liveRelevantOwnerPeers) == 0
                && !HasXagmanPendingOwnerWork(liveRelevantOwnerPeers))
            {
                xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.UtcNow;
            }
            else
            {
                xagmanTonyAllOwnersCompletedObservedAtUtc = DateTime.MinValue;
            }

            return;
        }

        if (xagmanTonyAllOwnersCompletedObservedAtUtc == DateTime.MinValue)
        {
            xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.MinValue;
            return;
        }

        if (xagmanTonyNoConnectedOwnerPeersSinceUtc == DateTime.MinValue)
        {
            xagmanTonyNoConnectedOwnerPeersSinceUtc = DateTime.UtcNow;
            plugin.TaskRunner.AddLog("Xagman: all relevant Franchise Owner peers disconnected after reaching 0 remaining owners; waiting 30s before Tony completion.");
        }
    }

    private void StartXagmanFailureRecallTask(global::XASlave.Localization.UiMessage reasonPresentation, bool forceLogout = false, bool tonyCapacityExhausted = false)
    {
        var reason = reasonPresentation.Original;
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();

        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        if (runner.IsRunning)
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE32818833CBE5DC0", runner.CurrentTaskNameMessage));
            runner.AddLog($"Xagman: {xagmanStatusText}");
            return;
        }

        var role = xagmanRunning ? xagmanActiveRole : plugin.Configuration.XagmanRole;
        tonyCapacityExhausted = tonyCapacityExhausted && role == XagmanRole.Tony;
        forceLogout = forceLogout || (tonyCapacityExhausted && cfg.XagmanAutoReturnToFc);
        var shutdownContext = tonyCapacityExhausted ? "last Tony capacity cleanup" : "failure recall";
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        var wasRunningBeforeTradeSafetyStart = xagmanRunning;
        if (!TryBeginXagmanTradeSafetySession(global::XASlave.Localization.UiMessage.Key("S6E478053E941F61A")))
        {
            if (!wasRunningBeforeTradeSafetyStart || xagmanRunning || xagmanStatus != XagmanStatus.Error)
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S662F76CBCFFE8674"));
            }
            runner.AddLog($"Xagman: {xagmanStatusText}");
            return;
        }

        SetXagmanRunning(true);
        xagmanActiveRole = role;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, reasonPresentation);
        if (role == XagmanRole.Tony)
            MarkXagmanTonyConsumed(localCharacter);

        var steps = new List<TaskStep>();
        // A full/gil-capped Tony completed its receiving turn. The overall run still has
        // unfinished owners, but local shutdown must not turn this capacity result red.
        if (!tonyCapacityExhausted)
        {
            runner.RecordFailedCharacter(localCharacter);
            xagmanCharacterFailureReasons[localCharacter] = reasonPresentation;
        }
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SAA9F77C23D2B8C2F"),
            OnEnter = () =>
            {
                var stoppedQueue = TryStopXagmanDropboxTradeQueue();
                var sentEscape = TryAbortXagmanTradeWindow();
                ClearXagmanDropbox();
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S6E478053E941F61A")))
                    return;
                xagmanOwnerStartRequested = false;
                xagmanOwnerStandbyPending = false;
                xagmanOwnerPauseForTonyRotationRequested = false;
                xagmanTonyRotationRequestedByOwnerStandby = false;
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanObservedDropboxBusy = false;
                xagmanQueueRequestedAtUtc = DateTime.MinValue;
                xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
                xagmanTradeQuantitySnapshot.Clear();
                xagmanOwnerRequestedItems.Clear();
                runner.AddLog(reason);
                if (stoppedQueue)
                    runner.AddLog("Xagman: stopped Dropbox item trade queue before failure recall.");
                if (sentEscape)
                    runner.AddLog("Xagman: sent ESC to close Trade before failure recall.");
                PublishXagmanPresence();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (tonyCapacityExhausted)
        {
            System.Threading.Tasks.Task<bool>? peerRecallTask = null;
            void HaltRecall(global::XASlave.Localization.UiMessage messagePresentation)
            {
                var message = messagePresentation.Original;
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, messagePresentation);
                PublishXagmanPresence();
                runner.RequestHalt(message);
            }
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S1315803D618FDF5B"),
                OnEnter = () => peerRecallTask = RecallAllXagmanPeersForFailureAsync(),
                IsComplete = () =>
                {
                    if (peerRecallTask == null || !peerRecallTask.IsCompleted) return false;
                    try
                    {
                        // This proves transmission to the hub, not owner completion.
                        if (peerRecallTask.GetAwaiter().GetResult()) return true;
                    }
                    catch (Exception ex)
                    {
                        runner.AddLog($"Xagman: owner recall send failed: {ex.Message}");
                    }
                    HaltRecall(global::XASlave.Localization.UiMessage.Key("SE7F1369FE11FD806"));
                    return false;
                },
                TimeoutSec = 15f,
                OnTimeout = () => HaltRecall(global::XASlave.Localization.UiMessage.Key("SD3F81B3FDC4F2172")),
            });
        }
        if (!tonyCapacityExhausted || cfg.XagmanAutoReturnToFc)
        {
            AddXagmanTeleportSteps(
                steps,
                global::XASlave.Localization.UiMessage.Key("S2E1B82F9BFAC752B"),
                () => "fc",
                runner,
                null,
                true,
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S54C861E36FD3BFA5", localCharacter, shutdownContext));
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SF0F648D837A58B3A", shutdownContext, localCharacter));
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S377DBD25850FA70C", shutdownContext, localCharacter));
                    runner.AddLog($"Xagman: {shutdownContext} could not confirm /li fc return for {localCharacter}.");
                    if (forceLogout)
                        runner.RequestHalt("Failure recall could not confirm the home return; logout was not started.");
                },
                expectCrossDataCenterLogout: true);
        }
        AddXagmanTradeSafetyCompletionStep(
            steps,
            runner,
            "failure recall completion",
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(forceLogout || cfg.XagmanLogoutOnComplete, !forceLogout && cfg.XagmanKillGameOnComplete),
            allowAutoClean: false);
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, forceLogout || cfg.XagmanLogoutOnComplete, !forceLogout && cfg.XagmanKillGameOnComplete, !forceLogout && cfg.XagmanEnableArMultiOnComplete);
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onFinished: () => FinalizeXagmanLocalShutdown(shutdownContext), onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), preserveRunHistory: true);
    }

    private void StartXagmanFranchiseCompletionTask(global::XASlave.Localization.UiMessage reasonPresentation, bool localOwnersFinished = false)
    {
        var reason = reasonPresentation.Original;
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner)
            return;
        if (plugin.TaskRunner.IsRunning && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase))
            plugin.TaskRunner.Cancel();

        var completingSupplyCycle = IsXagmanSupplyCycleActive();
        var alreadyAtMenu = completingSupplyCycle && !Plugin.ClientState.IsLoggedIn;
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var steps = new List<TaskStep>();
        AutoOpenTaskLogIfVerbose(ref xagmanShowLog);
        SetXagmanRunning(true);
        xagmanActiveRole = XagmanRole.FranchiseOwner;
        xagmanStatus = XagmanStatus.Completed;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, reasonPresentation);
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SE5031710E32259D8"),
            OnEnter = () =>
            {
                var stoppedQueue = TryStopXagmanDropboxTradeQueue();
                var sentEscape = TryAbortXagmanTradeWindow();
                ClearXagmanDropbox();
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S779E3B14A4B02227")))
                    return;
                SetXagmanRunning(false);
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, reasonPresentation);
                xagmanOwnerStartRequested = false;
                xagmanOwnerStandbyPending = false;
                xagmanOwnerPauseForTonyRotationRequested = false;
                xagmanTonyRotationRequestedByOwnerStandby = false;
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanObservedDropboxBusy = false;
                xagmanQueueRequestedAtUtc = DateTime.MinValue;
                xagmanTonyCompletionRequestedAtUtc = DateTime.MinValue;
                xagmanTradeQuantitySnapshot.Clear();
                SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                runner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
                runner.CompletedItems = completingSupplyCycle ? xagmanOwnerCompletedKeys.Count : runner.TotalItems;
                runner.AddLog(reason);
                if (stoppedQueue)
                    runner.AddLog("Xagman: stopped Dropbox item trade queue before owner completion cleanup.");
                if (sentEscape)
                    runner.AddLog("Xagman: sent ESC to close Trade before owner completion cleanup.");
                PublishXagmanPresence();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if ((IsXagmanCollectionFirstRestockPhase() || IsXagmanCapacityWarningCompletion())
            && xagmanCompletionDirectiveAcknowledged)
        {
            // Keep the peer connected long enough for the hub and Tony to observe the scoped
            // acknowledgement before optional FC return or local peer shutdown begins.
            steps.Add(MonthlyReloggerTask.MakeDelay(
                global::XASlave.Localization.UiMessage.Key("SDD73548F1CCE0B6D"),
                2.5f));
        }
        if (cfg.XagmanAutoReturnToFc && !alreadyAtMenu && !localOwnersFinished)
        {
            AddXagmanTeleportSteps(
                steps,
                global::XASlave.Localization.UiMessage.Key("S08C867D648412A16"),
                () => "fc",
                runner,
                null,
                true,
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S0F4116A6867E3D64", localCharacter));
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S41A60232A6D93057", localCharacter));
                    RememberXagmanRecentFcReturn(localCharacter);
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SDC6AFF65DED1945E", localCharacter));
                },
                expectCrossDataCenterLogout: true);
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S08F239E59C053C25"),
            OnEnter = () =>
            {
                if (!DisconnectXagmanPeerService())
                    runner.AddLog("Xagman: local TCP peer service was already disconnected before owner completion cleanup.");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        AddXagmanTradeSafetyCompletionStep(
            steps,
            runner,
            "Franchise Owner peer completion",
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete && !alreadyAtMenu, cfg.XagmanKillGameOnComplete));
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete && !alreadyAtMenu, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onFinished: () => FinalizeXagmanLocalShutdown("Franchise Owner peer completion"), onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), preserveRunHistory: true);
    }

    private List<TaskStep> BuildXagmanFranchiseSteps(List<string> characters, int startIndex)
    {
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var helper = new MonthlyReloggerTask(plugin);
        var steps = new List<TaskStep>();
        var collectionOnly = IsXagmanCollectionFirstCollectionPhase();
        var capacityDrain = IsXagmanCapacityDrainActive();
        var restockOnly = IsXagmanCollectionFirstRestockPhase() || capacityDrain;
        var supplyCycle = IsXagmanSupplyCycleActive();
        runner.TotalItems = supplyCycle ? xagmanOwnerRunPlan.Count : characters.Count;
        runner.CompletedItems = supplyCycle ? xagmanOwnerCompletedKeys.Count : Math.Max(0, Math.Min(startIndex, characters.Count));
        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SBFB7DF0830B46650"),
            OnEnter = () =>
            {
                var partnerName = GetCharacterNameFromKey(xagmanPreferredTonyCharacter);
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Key("S2980D2285FE6A1E9") : global::XASlave.Localization.UiMessage.Format("S5DF123C9BC2AD409", partnerName)));
                runner.AddLog(string.IsNullOrWhiteSpace(partnerName)
                    ? "Xagman: Franchise Owner is waiting for Tony to start and relay meetup data."
                    : $"Xagman: Franchise Owner is waiting for Tony {partnerName} to start and relay meetup data.");
            },
            IsComplete = () => xagmanOwnerStartRequested
                && (!supplyCycle || TryBindXagmanOwnerSupplyPass())
                && ((supplyCycle && characters.Count == 0) || IsXagmanFranchiseStartupReady()),
            TimeoutSec = 86400f,
        });
        steps.AddRange(helper.BuildPreFlightOnlySteps(characters.Skip(startIndex).ToList(), runner, RunXagmanPreflightArMultiGuard));
        for (var i = startIndex; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;
            var relogFailed = false;
            var standbyRequested = false;
            var charSkipped = false;
            var passDeferred = false;
            var ownerPartial = false;
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S2C6B551F90D5E27D", charName),
                OnEnter = () => passDeferred = supplyCycle && ShouldDeferXagmanOwnerForSupplyPass(charName),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            var ownerStepsStart = steps.Count;
            var resumingStandbyOwner = xagmanQueueRequestedAtUtc > DateTime.MinValue
                && xagmanActiveCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase);
            const float ownerTradeStopDistance = 1.5f;
            const int maxOwnerCollectionTradePasses = 3;
            const int maxOwnerTradeRangeRetries = 3;
            const double ownerTradeRangeRetryCooldownSeconds = 2.0;
            var ownerCollectionRetryRequested = false;
            var ownerCollectionQueuedEntries = 0;
            var ownerSendoffVerified = false;
            var ownerCollectionTooFarAwayRetryCount = 0;
            var ownerCollectionTooFarAwayLastRetryUtc = DateTime.MinValue;
            var ownerRequestedTooFarAwayRetryCount = 0;
            var ownerRequestedTooFarAwayLastRetryUtc = DateTime.MinValue;

            void ResetOwnerCollectionRangeRetry()
            {
                ownerCollectionTooFarAwayRetryCount = 0;
                ownerCollectionTooFarAwayLastRetryUtc = DateTime.MinValue;
            }

            void ResetOwnerRequestedRangeRetry()
            {
                ownerRequestedTooFarAwayRetryCount = 0;
                ownerRequestedTooFarAwayLastRetryUtc = DateTime.MinValue;
            }

            bool FailOwnerOnIncompleteGreenScan(IReadOnlyList<XagmanTradeRequestEntry> requests)
            {
                var invalid = requests.FirstOrDefault(request =>
                    IsXagmanGreenValueSelector(request.SelectorKind)
                    && (!request.GreenScanComplete
                        || request.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision));
                if (invalid == null)
                    return false;

                relogFailed = true;
                var detail = (!invalid.GreenScanComplete ? invalid.GreenScanErrorMessage : global::XASlave.Localization.UiMessage.Format("S743E129A5CEEA47A", invalid.GreenValueProtocolRevision));
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S0D09316122454FA6", charName, detail));
                if (!runner.FailedCharacters.Contains(charName))
                    runner.RecordFailedCharacter(charName);
                SetXagmanOwnerRequestedItems(requests, false);
                runner.AddLog($"Xagman: {xagmanStatusText}");
                return true;
            }

            string GetActiveTradeLockLostMessage()
            {
                var tonyPeer = GetXagmanLiveTonyPeer();
                var activePartner = tonyPeer?.ActiveTradePartner ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(activePartner)
                    && !activePartner.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    return $"Xagman: owner {charName} yielded Tony because Tony is now trading with {activePartner}.";

                return $"Xagman: owner {charName} yielded Tony because Tony no longer advertises this owner as the active trade partner; owner inventory checks will decide remaining work.";
            }

            bool HandleOwnerTradeFailure(
                XagmanTradeFailureKind failureKind,
                string matchedText,
                string tradeContextLabel,
                string standbyContextLabel,
                ref int tooFarAwayRetryCount,
                ref DateTime tooFarAwayLastRetryUtc)
            {
                switch (failureKind)
                {
                    case XagmanTradeFailureKind.None:
                        return false;
                    case XagmanTradeFailureKind.TooFarAway:
                    {
                        var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                        if (string.IsNullOrWhiteSpace(partnerName))
                        {
                            return BeginStandbyForTonyRotation(
                                $"Xagman: owner {charName} detected '{matchedText}' during {tradeContextLabel}, but no active Tony target was available for recovery.");
                        }

                        xagmanStatus = XagmanStatus.Traveling;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S4820CA116EECAF4E", charName, partnerName));

                        if (IsXagmanMovementActive())
                            return false;
                        if ((DateTime.UtcNow - tooFarAwayLastRetryUtc).TotalSeconds < ownerTradeRangeRetryCooldownSeconds)
                            return false;
                        if (tooFarAwayRetryCount >= maxOwnerTradeRangeRetries)
                        {
                            var failedRetryCount = tooFarAwayRetryCount;
                            tooFarAwayRetryCount = 0;
                            tooFarAwayLastRetryUtc = DateTime.MinValue;
                            return BeginStandbyForTonyRotation(
                                $"Xagman: owner {charName} still detected '{matchedText}' after {failedRetryCount} {tradeContextLabel} recovery retries and is yielding for Tony rotation.");
                        }

                        tooFarAwayRetryCount++;
                        tooFarAwayLastRetryUtc = DateTime.UtcNow;
                        TryTargetCharacter(partnerName);
                        TryPathToCurrentTarget(ownerTradeStopDistance, partnerName);
                        runner.AddLog($"Xagman: owner {charName} detected '{matchedText}' during {tradeContextLabel}; retargeting Tony and repathing ({tooFarAwayRetryCount}/{maxOwnerTradeRangeRetries}).");
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S562D20C801E646B0", charName, partnerName, tooFarAwayRetryCount, maxOwnerTradeRangeRetries));
                        return false;
                    }
                    case XagmanTradeFailureKind.TradeCanceled:
                        return BeginStandbyForTonyRotation(
                            $"Xagman: owner {charName} detected '{matchedText}'{standbyContextLabel}; treating it as a likely Tony gil-cap cancel and yielding for Tony rotation.");
                    case XagmanTradeFailureKind.TradeNotComplete:
                        return BeginStandbyForTonyRotation(
                            $"Xagman: owner {charName} detected '{matchedText}'{standbyContextLabel} and is waiting for the next Tony rotation after inventory-full standby cleanup.");
                    default:
                        return false;
                }
            }

            bool TryEnterStandby()
            {
                if (ownerPartial || passDeferred)
                    return false;
                if (standbyRequested || relogFailed || !ShouldXagmanOwnerStandbyForTonyRotation(charName))
                    return standbyRequested;
                return BeginStandbyForTonyRotation(string.Empty);
            }

            bool TryDeferOwnerForSupplyPass()
            {
                if (ownerPartial)
                    return true;
                if (!supplyCycle || passDeferred || relogFailed || standbyRequested || charSkipped)
                    return false;
                ownerPartial = TryAcceptXagmanPartialDeferral(charName);
                return ownerPartial;
            }

            bool YieldOwnerTradeLockIfNeeded()
            {
                if (string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId) || xagmanObservedDropboxBusy)
                    return false;
                if (HasXagmanActiveTonyTradeLock(charName))
                    return false;
                return BeginStandbyForTonyRotation(GetActiveTradeLockLostMessage());
            }

            bool ShouldSkipTradeFlow()
            {
                if (!xagmanRunning || relogFailed || standbyRequested || charSkipped || passDeferred
                    || ownerPartial || xagmanOwnerPauseForTonyRotationRequested)
                    return true;
                if (TryDeferOwnerForSupplyPass())
                    return true;
                if (YieldOwnerTradeLockIfNeeded())
                    return true;
                return TryEnterStandby();
            }

            bool ShouldSkipOwnerCollectionTradeExecution()
            {
                return restockOnly || IsXagmanOwnerSupplyDraining() || ShouldSkipTradeFlow() || ownerCollectionQueuedEntries <= 0;
            }

            bool ShouldSkipOwnerCollectionSetup()
            {
                return restockOnly || IsXagmanOwnerSupplyDraining() || (supplyCycle && xagmanOwnerCollectionCompletedKeys.Contains(charName))
                    || ShouldSkipTradeFlow();
            }

            bool ShouldSkipRequestedTradeFlow()
            {
                return collectionOnly || ShouldSkipTradeFlow() || xagmanOwnerRequestedItems.Count == 0;
            }

            bool ShouldArmOwnerAutoAcceptForPendingTonySupply()
                => !collectionOnly && !ownerPartial && !passDeferred
                    && ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(charName);

            void AdvertiseOwnerTradeLockWait(string phaseLabel)
            {
                global::XASlave.Localization.UiMessage? waitingTextUiPresentation = null;
                var waitingText = global::XASlave.Localization.UiMessage.Store(ref waitingTextUiPresentation, global::XASlave.Localization.UiMessage.Format("S9384DC1CB35BA6FC", charName, phaseLabel));
                if (xagmanStatus == XagmanStatus.Called
                    && xagmanStatusText.Equals(waitingText, StringComparison.Ordinal))
                {
                    return;
                }

                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Capture(ref waitingTextUiPresentation, waitingText));
                runner.AddLog($"Xagman: owner {charName} is waiting for Tony to reacquire the explicit trade lock for {phaseLabel}.");
                PublishXagmanPresence();
            }

            void AddOwnerTradeLockWaitStep(string phaseLabel, Func<bool> shouldSkip)
            {
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S97231351872D5967", phaseLabel, charName),
                    ShouldSkip = shouldSkip,
                    OnEnter = () =>
                    {
                        if (!shouldSkip() && !HasXagmanActiveTonyTradeLock(charName))
                            AdvertiseOwnerTradeLockWait(phaseLabel);
                    },
                    IsComplete = () => shouldSkip()
                        || TryDeferOwnerForSupplyPass()
                        || HasXagmanActiveTonyTradeLock(charName)
                        || TryEnterStandby(),
                    TimeoutSec = 60f,
                    OnTimeout = () =>
                    {
                        if (shouldSkip() || standbyRequested)
                            return;
                        BeginStandbyForTonyRotation(
                            $"Xagman: owner {charName} did not receive a refreshed Tony trade lock for {phaseLabel} within 60 seconds; yielding for Tony recovery or rotation.");
                    },
                });
            }

            bool BeginStandbyForTonyRotation(string logMessage)
            {
                if (standbyRequested)
                    return true;

                standbyRequested = true;
                xagmanOwnerCurrentCharacterIndex = charIndex - 1;
                var dropboxBusy = plugin.IpcClient.DropboxIsBusy();
                var tradeWindowVisible = AddonHelper.IsAddonVisible("Trade");
                var shouldStopQueue = xagmanObservedDropboxBusy || dropboxBusy;
                var shouldCleanupTrade = shouldStopQueue
                    || tradeWindowVisible
                    || GetXagmanTradeFailureKind(out _) != XagmanTradeFailureKind.None;
                var stoppedQueue = shouldStopQueue && TryStopXagmanDropboxTradeQueue();
                var sentEscape = shouldCleanupTrade && TryAbortXagmanTradeWindow();

                if (shouldCleanupTrade)
                {
                    if (shouldStopQueue)
                    {
                        if (stoppedQueue)
                            runner.AddLog($"Xagman: stopped Dropbox item trade queue for {charName} before standby.");
                        else
                            runner.AddLog($"Xagman: could not confirm Dropbox item trade queue stop for {charName}; continuing into standby.");
                    }

                    if (sentEscape)
                        runner.AddLog($"Xagman: sent ESC to close Trade for {charName} before standby.");

                    ClearXagmanDropbox();
                }

                xagmanObservedDropboxBusy = false;
                EnterXagmanOwnerStandby(charName, logMessage);

                return true;
            }

            void EnterOwnerTonyQueue(bool logQueueEntry, global::XASlave.Localization.UiMessage? statusTextOverride = null)
            {
                if (xagmanQueueRequestedAtUtc == DateTime.MinValue)
                    xagmanQueueRequestedAtUtc = DateTime.UtcNow;
                xagmanPreferredTonyCharacter = string.Empty;
                if (xagmanStatus is not (XagmanStatus.Called or XagmanStatus.Trading))
                    xagmanStatus = resumingStandbyOwner ? XagmanStatus.Standby : XagmanStatus.WaitingRoom;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (statusTextOverride != null && !string.IsNullOrWhiteSpace(statusTextOverride.Original) ? statusTextOverride : (resumingStandbyOwner ? global::XASlave.Localization.UiMessage.Format("S2DBC7D5F876C83B6", charName) : global::XASlave.Localization.UiMessage.Format("S88E7FF103EFFB3E4", charName))));
                if (logQueueEntry)
                {
                    runner.AddLog(resumingStandbyOwner
                        ? $"Xagman: standby owner {charName} is waiting for the next Tony to resume them."
                        : $"Xagman: waiting for the next Tony to call {charName}.");
                }
                PublishXagmanPresence();
            }

            var ownerDrainCleanupSinceUtc = DateTime.MinValue;
            bool PollOwnerTradeWait()
            {
                if (IsXagmanOwnerSupplyDraining())
                {
                    BuildXagmanPendingGiveItems(charName);
                    if (ownerDrainCleanupSinceUtc == DateTime.MinValue) ownerDrainCleanupSinceUtc = DateTime.UtcNow;
                    if (!TryStopXagmanDropboxTradeQueue())
                    {
                        FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Format("S0381CAB311DA2AF1", charName));
                        return false;
                    }
                    if (AddonHelper.IsAddonVisible("Trade")) TryAbortXagmanTradeWindow();
                    if ((DateTime.UtcNow - ownerDrainCleanupSinceUtc).TotalSeconds > 15)
                    {
                        FailXagmanSupplyCycle(global::XASlave.Localization.UiMessage.Format("S7E334A909BC8A11B", charName));
                        return false;
                    }
                    ownerCollectionRetryRequested = false;
                    return !plugin.IpcClient.DropboxIsBusy() && !AddonHelper.IsAddonVisible("Trade");
                }
                var failureKind = GetXagmanTradeFailureKind(out var matchedText);
                if (failureKind != XagmanTradeFailureKind.None)
                {
                    return HandleOwnerTradeFailure(
                        failureKind,
                        matchedText,
                        "owner give trade",
                        string.Empty,
                        ref ownerCollectionTooFarAwayRetryCount,
                        ref ownerCollectionTooFarAwayLastRetryUtc);
                }

                var busy = plugin.IpcClient.DropboxIsBusy();
                if (busy)
                {
                    xagmanObservedDropboxBusy = true;
                    return false;
                }

                if (!xagmanObservedDropboxBusy)
                {
                    if (!HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName))
                        return true;
                    if (!HasXagmanActiveTonyTradeLock(charName))
                    {
                        AdvertiseOwnerTradeLockWait("remaining give-items");
                        return false;
                    }
                    return false;
                }

                if (HasXagmanOwnerCollectionTradeCompleted(cfg.XagmanItems, charName))
                    return true;
                if (!HasXagmanActiveTonyTradeLock(charName))
                {
                    AdvertiseOwnerTradeLockWait("the next give pass");
                    return false;
                }
                return true;
            }

            bool PollOwnerRequestedTradeWait()
            {
                var failureKind = GetXagmanTradeFailureKind(out var matchedText);
                if (failureKind != XagmanTradeFailureKind.None)
                {
                    return HandleOwnerTradeFailure(
                        failureKind,
                        matchedText,
                        "owner requested supply trade",
                        " during requested supply",
                        ref ownerRequestedTooFarAwayRetryCount,
                        ref ownerRequestedTooFarAwayLastRetryUtc);
                }

                if (plugin.IpcClient.DropboxIsBusy())
                {
                    xagmanObservedDropboxBusy = true;
                    return false;
                }

                if (TryDeferOwnerForSupplyPass())
                    return true;

                var previousTony = xagmanActiveTradePartner;
                var previousTonyInstanceId = xagmanActiveTradePartnerInstanceId;
                var hadPreviousTony = !string.IsNullOrWhiteSpace(previousTony)
                    || !string.IsNullOrWhiteSpace(previousTonyInstanceId);
                var wasRunningBeforeTonyAdoption = xagmanRunning;
                var hasActiveTonyTradeLock = TryAdoptXagmanCallingTonyPeer(charName, out var adoptedTonyCall);
                if (wasRunningBeforeTonyAdoption && !xagmanRunning)
                    return true;
                var adoptedReplacementTony = hasActiveTonyTradeLock && adoptedTonyCall && hadPreviousTony;
                if (hasActiveTonyTradeLock)
                {
                    // A Tony call is durable peer state. Keep re-running the owner-side approach while
                    // Dropbox is idle so a rejected/not-ready vnav request cannot strand a replacement handoff.
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (adoptedReplacementTony ? global::XASlave.Localization.UiMessage.Format("S8AB35666A1BD9681", charName, partnerName) : global::XASlave.Localization.UiMessage.Format("S3DFFCB73EE8F303A", charName, partnerName)));
                    if (adoptedReplacementTony)
                    {
                        runner.AddLog($"Xagman: owner {charName} adopted replacement Tony {xagmanActiveTradePartner}'s explicit call for pending requested supply and is reapproaching.");
                        PublishXagmanPresence();
                    }
                    if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, adoptedReplacementTony))
                        return false;
                    if (!IsCurrentTargetWithinStopDistanceAndStopped(partnerName, ownerTradeStopDistance))
                        return false;
                }

                if (!xagmanObservedDropboxBusy && !hasActiveTonyTradeLock)
                {
                    var remainingRequestsWithoutTradeLock = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
                    if (FailOwnerOnIncompleteGreenScan(remainingRequestsWithoutTradeLock))
                        return true;
                    if (remainingRequestsWithoutTradeLock.Count == 0)
                    {
                        SetXagmanOwnerRequestedItems(
                            Array.Empty<XagmanTradeRequestEntry>(),
                            false,
                            charName);
                        return true;
                    }
                    SetXagmanOwnerRequestedItemsIfChanged(
                        remainingRequestsWithoutTradeLock,
                        charName);
                    AdvertiseOwnerTradeLockWait("requested supply reconciliation");
                    return false;
                }

                var remainingRequests = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
                if (FailOwnerOnIncompleteGreenScan(remainingRequests))
                    return true;
                if (remainingRequests.Count == 0)
                {
                    SetXagmanOwnerRequestedItems(
                        Array.Empty<XagmanTradeRequestEntry>(),
                        false,
                        charName);
                    return true;
                }

                if (!hasActiveTonyTradeLock)
                {
                    SetXagmanOwnerRequestedItemsIfChanged(
                        remainingRequests,
                        charName);
                    AdvertiseOwnerTradeLockWait("remaining requested supply");
                    return false;
                }

                if (HasXagmanRequestedTradeProgress(xagmanOwnerRequestedItems, charName))
                {
                    SetXagmanOwnerRequestedItemsIfChanged(
                        remainingRequests,
                        charName);
                    return false;
                }

                return false;
            }

            void EvaluateOwnerCollectionRetry(int collectionPassNumber)
            {
                if (restockOnly || IsXagmanOwnerSupplyDraining())
                {
                    BuildXagmanPendingGiveItems(charName);
                    ownerCollectionRetryRequested = false;
                    return;
                }
                ownerCollectionRetryRequested = HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName);
                if (xagmanStatus == XagmanStatus.Error) return;
                if (!ownerCollectionRetryRequested)
                {
                    if (supplyCycle)
                        xagmanOwnerCollectionCompletedKeys.Add(charName);
                    return;
                }

                SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                if (collectionPassNumber >= maxOwnerCollectionTradePasses)
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5F3FF50B63A3A94B", charName, collectionPassNumber));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                    runner.AddLog($"Xagman: owner {charName} still has give items remaining after collection pass {collectionPassNumber}; stopping before Tony supply requests.");
                    return;
                }

                runner.AddLog($"Xagman: owner {charName} still has give items remaining after collection pass {collectionPassNumber}; retrying owner give trade before any Tony supply requests.");
            }

            bool EvaluateOwnerSendoffReconciliation(int verificationPassNumber, bool yieldOnFailure)
            {
                ownerSendoffVerified = false;
                if (TryDeferOwnerForSupplyPass()) return true;
                ownerCollectionRetryRequested = !restockOnly
                    && HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName);
                var remainingRequestedItems = collectionOnly
                    ? new List<XagmanTradeRequestEntry>()
                    : BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName, false);
                if (xagmanStatus == XagmanStatus.Error) return false;
                if (FailOwnerOnIncompleteGreenScan(remainingRequestedItems))
                    return true;
                if (!ownerCollectionRetryRequested && remainingRequestedItems.Count == 0)
                {
                    SetXagmanOwnerRequestedItems(
                        Array.Empty<XagmanTradeRequestEntry>(),
                        false,
                        charName);
                    ownerSendoffVerified = true;
                    runner.AddLog($"Xagman: owner {charName} completion verification {verificationPassNumber}/2 passed; no additional give or Tony supply work remains before sendoff.");
                    return true;
                }

                SetXagmanOwnerRequestedItems(
                    remainingRequestedItems,
                    false,
                    charName);
                var remainingRequestUnits = remainingRequestedItems.Sum(item => Math.Max(0, item.Quantity));
                var remainingSummary = ownerCollectionRetryRequested && remainingRequestedItems.Count > 0
                    ? $"owner give-items still remain and {remainingRequestedItems.Count} Tony supply entr{(remainingRequestedItems.Count == 1 ? "y" : "ies")} totaling {remainingRequestUnits} units are still needed"
                    : ownerCollectionRetryRequested
                        ? "owner give-items still remain"
                        : $"{remainingRequestedItems.Count} Tony supply entr{(remainingRequestedItems.Count == 1 ? "y" : "ies")} totaling {remainingRequestUnits} units are still needed";

                if (yieldOnFailure)
                {
                    return BeginStandbyForTonyRotation(
                        $"Xagman: owner {charName} completion verification {verificationPassNumber}/2 found unresolved work after the trade flow ({remainingSummary}); yielding before sendoff so the remaining work can be retried.");
                }

                runner.AddLog(
                    $"Xagman: owner {charName} completion verification {verificationPassNumber}/2 found unresolved work after the trade flow ({remainingSummary}); running one more full reconciliation check before sendoff.");
                return false;
            }

            void AddRepeatedOwnerCollectionPass(int collectionPassNumber)
            {
                var stepSuffix = global::XASlave.Localization.UiMessage.Format("S889D8102E88A2889", collectionPassNumber);
                var repeatedCollectionQueuedEntries = 0;

                bool ShouldSkipRepeatedTradeFlow()
                {
                    if (!ownerCollectionRetryRequested || IsXagmanOwnerSupplyDraining())
                        return true;
                    return ShouldSkipTradeFlow();
                }

                bool ShouldSkipRepeatedTradeExecution()
                {
                    return ShouldSkipRepeatedTradeFlow() || repeatedCollectionQueuedEntries <= 0;
                }

                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SD177B46F9DB8BCDC", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        xagmanStatus = XagmanStatus.Called;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S07EF14BC293A0B43", charName));
                        if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, true))
                            return;
                        var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                        TryTargetCharacter(partnerName);
                        TryPathToCurrentTarget(ownerTradeStopDistance, partnerName);
                    },
                    IsComplete = () =>
                    {
                        if (relogFailed || ShouldSkipRepeatedTradeFlow())
                            return true;
                        if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, false))
                            return false;
                        return IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(xagmanActiveTradePartner), ownerTradeStopDistance);
                    },
                    TimeoutSec = 60f,
                    OnTimeout = () =>
                    {
                        if (standbyRequested)
                            return;
                        relogFailed = true;
                        xagmanStatus = XagmanStatus.Error;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE41C019B3BCFEB97", charName));
                        if (!runner.FailedCharacters.Contains(charName))
                            runner.RecordFailedCharacter(charName);
                    },
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SE8F1C06476D6D434", charName, stepSuffix), 0.5f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S516805756F7192C2", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                        xagmanStatus = XagmanStatus.Trading;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9B87FB0A50515E67", charName, partnerName));
                        OpenXagmanDropboxWindow();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SFEFD65A5D3C091AC", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        OpenXagmanDropboxTradeTab();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S7ECAFA5B7B67329E", charName, stepSuffix), 1.0f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SB5B247B81EA40D16", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        ClearXagmanDropbox();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 2f,
                });
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SFADED38CB79E902C", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeFlow,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeFlow())
                            return;
                        repeatedCollectionQueuedEntries = QueueXagmanOwnerCollectionItems(cfg.XagmanItems);
                        if (repeatedCollectionQueuedEntries <= 0)
                            runner.AddLog($"Xagman: owner {charName} had nothing queued for collection pass {collectionPassNumber}; skipping Dropbox trade start and moving to Tony request evaluation.");
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S5B7A367E7BA352C7", charName, stepSuffix), 0.5f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("SDDCDB61442C04814", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeExecution,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeExecution())
                            return;
                        TryTargetCharacter(GetCharacterNameFromKey(xagmanActiveTradePartner));
                    },
                    IsComplete = () => true,
                    TimeoutSec = 1f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SBCDC8C76922237C5", charName, stepSuffix), 0.1f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S819CD27AF8F7C3CF", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeExecution,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeExecution())
                            return;
                        FocusXagmanCurrentTarget(GetCharacterNameFromKey(xagmanActiveTradePartner));
                    },
                    IsComplete = () => true,
                    TimeoutSec = 1f,
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SAA2032DBE5ED4099", charName, stepSuffix), 0.15f, ShouldSkipRepeatedTradeFlow));
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S5A17D486FAF132BB", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeExecution,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeExecution())
                            return;
                        xagmanStatus = XagmanStatus.Called;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S34CD84FA75D312BD", charName));
                        TryTargetCharacter(GetCharacterNameFromKey(xagmanActiveTradePartner));
                    },
                    IsComplete = () => relogFailed || ShouldSkipRepeatedTradeExecution() || IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(xagmanActiveTradePartner), ownerTradeStopDistance),
                    TimeoutSec = 5f,
                    OnTimeout = () =>
                    {
                        if (standbyRequested)
                            return;
                        relogFailed = true;
                        xagmanStatus = XagmanStatus.Error;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD8FD556344F14C25", charName));
                        if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                    },
                });
                steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S77A35A9BEDDAFE26", charName, stepSuffix), 0.5f, ShouldSkipRepeatedTradeExecution));
                AppendXagmanDropboxAutoAcceptStep(steps, global::XASlave.Localization.UiMessage.Format("SF58EC0AFC2D6F661", charName, stepSuffix), false, ShouldSkipRepeatedTradeExecution);
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S4E6A521AFBAD4E2F", charName, stepSuffix),
                    ShouldSkip = ShouldSkipRepeatedTradeExecution,
                    OnEnter = () =>
                    {
                        if (ShouldSkipRepeatedTradeExecution())
                            return;
                        ResetOwnerCollectionRangeRetry();
                        if (!StartXagmanDropboxTrade($"owner give trade {charName}{stepSuffix}"))
                        {
                            relogFailed = true;
                            xagmanStatus = XagmanStatus.Error;
                            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9A953F3AEA791B49", charName));
                            if (!runner.FailedCharacters.Contains(charName))
                                runner.RecordFailedCharacter(charName);
                            return;
                        }
                        xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                    },
                    IsComplete = () => true,
                    TimeoutSec = 3f,
                });
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S7E76452146D059D9", charName, stepSuffix),
                    ShouldSkip = () => relogFailed || standbyRequested || !ownerCollectionRetryRequested || repeatedCollectionQueuedEntries <= 0,
                    OnEnter = () => xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy(),
                    IsComplete = () => relogFailed || standbyRequested || !ownerCollectionRetryRequested || repeatedCollectionQueuedEntries <= 0 || PollOwnerTradeWait() || TryEnterStandby(),
                    TimeoutSec = 600f,
                    OnTimeout = () =>
                    {
                        if (standbyRequested)
                            return;
                        relogFailed = true;
                        xagmanStatus = XagmanStatus.Error;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC12A85A8CDC778F7", charName));
                        if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                        CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Format("SE1B16527453907BF", charName));
                    },
                });
                AppendXagmanDropboxAutoAcceptStep(steps, global::XASlave.Localization.UiMessage.Format("SF58EC0AFC2D6F661", charName, stepSuffix), false, () => standbyRequested || !ownerCollectionRetryRequested || repeatedCollectionQueuedEntries <= 0);
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S275C50B28950E9E4", charName, stepSuffix),
                    ShouldSkip = () => relogFailed || standbyRequested || !ownerCollectionRetryRequested,
                    OnEnter = () =>
                    {
                        xagmanObservedDropboxBusy = false;
                        EvaluateOwnerCollectionRetry(collectionPassNumber);
                    },
                    IsComplete = () => true,
                    TimeoutSec = 1f,
                });
            }

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S9880AA6F210F387E", charName),
                OnEnter = () =>
                {
                    if (IsXagmanCollectionFirstRunActive() && xagmanTravelRouteFatalError)
                    {
                        charSkipped = true;
                        return;
                    }

                    xagmanOwnerSweepPendingDataCenter = GetXagmanDataCenterOfChar(charName);
                    if (IsXagmanOwnerServerMatchingActive())
                        xagmanServerMatchingActive = true;
                },
                IsComplete = () =>
                {
                    if (supplyCycle && IsXagmanOwnerSupplyPassClosing(GetXagmanSupplyTonyPeer()))
                    {
                        passDeferred = true;
                        return true;
                    }

                    if (IsXagmanCollectionFirstRunActive() && xagmanTravelRouteFatalError)
                    {
                        charSkipped = true;
                        return true;
                    }

                    if (IsXagmanOwnerServerMatchingActive())
                        xagmanServerMatchingActive = true;

                    if (!xagmanServerMatchingActive)
                    {
                        if (TryResolveXagmanMeetDestinationForOwner(charName)
                            && HasCompleteXagmanActiveMeetDestination())
                        {
                            return true;
                        }

                        if (TryGetXagmanFixedMeetRouteFailureForOwnerMessage(charName, out var routeFailure))
                        {
                            relogFailed = true;
                            xagmanStatus = XagmanStatus.Error;
                            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S336D272D00DA6788", charName, routeFailure));
                            if (!runner.FailedCharacters.Contains(charName))
                                runner.RecordFailedCharacter(charName);
                            runner.AddLog($"Xagman: {xagmanStatusText}");
                            if (IsXagmanCollectionFirstRunActive())
                            {
                                ReportXagmanTravelRouteError(
                                    global::XASlave.Localization.UiMessage.Format("SB9D4702874F75986", charName, routeFailure));
                            }
                            return true;
                        }

                        if (xagmanStatus != XagmanStatus.Paused)
                            PublishXagmanPresence();
                        xagmanStatus = XagmanStatus.Paused;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S8E0B1C913C21F8E1", charName));
                        return false;
                    }

                    xagmanOwnerSweepPendingDataCenter = GetXagmanDataCenterOfChar(charName);
                    var decision = EvaluateXagmanOwnerServerGateMessage(charName, out var gateMeetWorld, out var gateMeetAetheryte, out var gateReason);
                    switch (decision)
                    {
                        case XagmanOwnerServerGate.Proceed:
                            if (!HasCompleteXagmanMeetDestination(gateMeetWorld, gateMeetAetheryte))
                            {
                                xagmanStatus = XagmanStatus.Paused;
                                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S51A2EFAD60D3C00E", charName));
                                return false;
                            }
                            SetXagmanActiveMeetDestination(gateMeetWorld, gateMeetAetheryte);
                            return true;
                        case XagmanOwnerServerGate.RouteError:
                            charSkipped = true;
                            ReportXagmanTravelRouteError(
                                global::XASlave.Localization.UiMessage.Format("S02421E7314C5A904", charName, gateReason));
                            return true;
                        case XagmanOwnerServerGate.Skip:
                            charSkipped = true;
                            MarkXagmanOwnerSkipped(charName, gateReason.Original);
                            return true;
                        default:
                            if (xagmanStatus != XagmanStatus.Paused)
                                PublishXagmanPresence();
                            xagmanStatus = XagmanStatus.Paused;
                            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5FDF45FDEE18BE83", charName, gateReason));
                            return false;
                    }
                },
                TimeoutSec = 86400f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SF1DD07FEFE66D07B", charName),
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    runner.CurrentItemLabelMessage = global::XASlave.Localization.UiMessage.Format("S5FB6F2415218C668", charIndex, charTotal, charName);
                    runner.AddLog($"Xagman: processing owner {charName} ({charIndex}/{charTotal}).");
                    xagmanActiveCharacter = charName;
                    xagmanOwnerCurrentCharacterIndex = charIndex - 1;
                    xagmanStatus = XagmanStatus.Relogging;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S1F3730629B586AEF", charName));
                    if (string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter))
                        TryBindXagmanFranchiseTonyForMeetup();
                    if (!resumingStandbyOwner)
                        xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
                    xagmanTradeQuantitySnapshot.Clear();
                    ownerCollectionRetryRequested = false;
                    if (resumingStandbyOwner
                        && xagmanOwnerRequestedItems.Count > 0
                        && !HasXagmanOwnerCollectionItemsRemaining(cfg.XagmanItems, charName))
                    {
                        var refreshedRequestedItems = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName);
                        if (FailOwnerOnIncompleteGreenScan(refreshedRequestedItems))
                            return;
                        SetXagmanOwnerRequestedItems(
                            refreshedRequestedItems,
                            false,
                            charName);
                    }
                    else
                    {
                        SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    }
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            AddXagmanRelogSteps(
                steps,
                charName,
                runner,
                () =>
                {
                    // Active-processing timer starts at the Lifestream relog attempt (after the wait-for-server
                    // gate), so idle time waiting for the Tony to reach this server is not counted.
                    RecordXagmanOwnerProcessingStart(charName);
                    xagmanStatus = XagmanStatus.Relogging;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S1F3730629B586AEF", charName));
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S1AF0554A9CD2DB11", charName, GetXagmanActiveMeetDestinationLabel()));
                },
                () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S88F4CE2C6F0E5C60", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
                () => relogFailed || charSkipped,
                recoverOwnerLogout: true,
                onDestinationSkip: reason =>
                {
                    charSkipped = true;
                    MarkXagmanOwnerSkipped(charName, reason);
                });
            AddXagmanOwnerMeetTravelSteps(
                steps,
                charName,
                runner,
                () =>
                {
                    if (relogFailed)
                        return;
                    xagmanStatus = XagmanStatus.ReadyForQueue;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S084BE65DAD229BF1", charName));
                },
                () =>
                {
                    if (relogFailed)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S66C926717A2A1E0B", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
                reason =>
                {
                    if (relogFailed || charSkipped)
                        return;
                    charSkipped = true;
                    MarkXagmanOwnerSkipped(charName, reason);
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S55D45A1533689CE7", charName, reason));
                },
                () => relogFailed || charSkipped);
            AddXagmanCustomMeetingStep(steps, charName, () => relogFailed || charSkipped);
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S15AA7E2951ED689E", charName),
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(GetXagmanOwnerQueueTonyCharacter());
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Format("SABCCEDE110BBA1E5", charName) : global::XASlave.Localization.UiMessage.Format("S9094E91AC9991FE7", charName, partnerName)));
                },
                IsComplete = () =>
                {
                    if (relogFailed || charSkipped)
                        return true;
                    if (IsXagmanOwnerCharPassedBySweep(charName))
                    {
                        charSkipped = true;
                        MarkXagmanOwnerSkipped(charName, "the sweep moved past this server while waiting");
                        return true;
                    }
                    TryResolveXagmanMeetDestinationForOwner();
                    TryBindXagmanFranchiseTonyForMeetup();
                    return !string.IsNullOrWhiteSpace(GetXagmanOwnerQueueTonyCharacter());
                },
                // Large fleets: the Tony can take a while to reach this server / become available, so
                // give owners 30 minutes at the meet spot before treating it as a failure.
                TimeoutSec = 1800f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S7A5D552445711D0C", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S30A8F3DDC3ABB551", charName),
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    var partnerName = GetCharacterNameFromKey(GetXagmanOwnerQueueTonyCharacter());
                    EnterOwnerTonyQueue(
                        false,
                        (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Format("SABCCEDE110BBA1E5", charName) : global::XASlave.Localization.UiMessage.Format("S8E5E3D70BAA5E2DD", charName, partnerName)));
                    if (string.IsNullOrWhiteSpace(partnerName))
                        return;
                    if (TryGetXagmanOwnerTonyApproachPosition(charName, out _, out _))
                    {
                        EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonySellOwnerPreApproachStopDistance, true);
                        return;
                    }
                    TryTargetCharacter(partnerName);
                    TryPathToCurrentTarget(ownerTradeStopDistance, partnerName);
                },
                IsComplete = () =>
                {
                    if (relogFailed || charSkipped)
                        return true;
                    if (IsXagmanOwnerCharPassedBySweep(charName))
                    {
                        charSkipped = true;
                        MarkXagmanOwnerSkipped(charName, "the sweep moved past this server while waiting");
                        return true;
                    }
                    TryResolveXagmanMeetDestinationForOwner();
                    TryBindXagmanFranchiseTonyForMeetup();
                    var queueTony = GetXagmanOwnerQueueTonyCharacter();
                    if (string.IsNullOrWhiteSpace(queueTony))
                    {
                        EnterOwnerTonyQueue(false, global::XASlave.Localization.UiMessage.Format("SABCCEDE110BBA1E5", charName));
                        return false;
                    }
                    if (IsXagmanOwnerCalled(charName))
                        return true;
                    if (TryGetXagmanOwnerTonyApproachPosition(charName, out _, out _))
                        return EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonySellOwnerPreApproachStopDistance, false);
                    return IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(queueTony), ownerTradeStopDistance);
                },
                // Large fleets: keep owners staged at the meet spot for up to 30 minutes while the Tony
                // works through the queue before treating it as a failure.
                TimeoutSec = 1800f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    var partnerName = GetCharacterNameFromKey(GetXagmanOwnerQueueTonyCharacter());
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Format("SE41C019B3BCFEB97", charName) : global::XASlave.Localization.UiMessage.Format("SED58F0DCC9CA2D39", partnerName, charName)));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S70019C056B1FC2B7", charName),
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () =>
                {
                    if (relogFailed)
                        return;
                    EnterOwnerTonyQueue(true);
                },
                IsComplete = () =>
                {
                    if (relogFailed || charSkipped)
                        return true;
                    if (IsXagmanOwnerCharPassedBySweep(charName))
                    {
                        charSkipped = true;
                        MarkXagmanOwnerSkipped(charName, "the sweep moved past this server while queued");
                        return true;
                    }
                    return IsXagmanOwnerCalled(charName);
                },
                TimeoutSec = 3600f,
                OnTimeout = () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD5323CEBE329601F", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
            });
            AppendXagmanDropboxAutoAcceptStep(
                steps,
                global::XASlave.Localization.UiMessage.Format("S3021D12594203B3C", charName),
                true,
                () => relogFailed || !ShouldArmOwnerAutoAcceptForPendingTonySupply(),
                requireSuccess: true,
                onFailure: () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S62158010DFA82A27", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB8F9FA39A448A9BB", charName),
                ShouldSkip = ShouldSkipTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipTradeFlow())
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S07EF14BC293A0B43", charName));
                    if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, true))
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    TryTargetCharacter(partnerName);
                    TryPathToCurrentTarget(ownerTradeStopDistance, partnerName);
                },
                IsComplete = () =>
                {
                    if (relogFailed || ShouldSkipTradeFlow())
                        return true;
                    if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, false))
                        return false;
                    return IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(xagmanActiveTradePartner), ownerTradeStopDistance);
                },
                TimeoutSec = 60f,
                OnTimeout = () =>
                {
                    if (standbyRequested)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE41C019B3BCFEB97", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SD4E886796C433727", charName), 0.5f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S6C98B641F565BDB9", charName),
                ShouldSkip = ShouldSkipOwnerCollectionSetup,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionSetup())
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9B87FB0A50515E67", charName, partnerName));
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S6839DAA2A54A2EF6", charName),
                ShouldSkip = ShouldSkipOwnerCollectionSetup,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionSetup())
                        return;
                    OpenXagmanDropboxTradeTab();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S0810CD52661F0FFC", charName), 1.0f, ShouldSkipOwnerCollectionSetup));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SF12FECAB4EDCE745", charName),
                ShouldSkip = ShouldSkipOwnerCollectionSetup,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionSetup())
                        return;
                    ClearXagmanDropbox();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SAB778C8C2D192168", charName),
                ShouldSkip = ShouldSkipOwnerCollectionSetup,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionSetup())
                        return;
                    ownerCollectionQueuedEntries = QueueXagmanOwnerCollectionItems(cfg.XagmanItems);
                    if (ownerCollectionQueuedEntries <= 0)
                        runner.AddLog($"Xagman: owner {charName} had nothing queued for the owner collection pass; skipping Dropbox trade start and moving to Tony request evaluation.");
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S60A979E5AE4F2062", charName), 0.5f, ShouldSkipOwnerCollectionSetup));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S081DDE172CD2DBA1", charName),
                ShouldSkip = ShouldSkipOwnerCollectionTradeExecution,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionTradeExecution())
                        return;
                    TryTargetCharacter(GetCharacterNameFromKey(xagmanActiveTradePartner));
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S7673701AB37DAB15", charName), 0.1f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SBB6619F2D393F59B", charName),
                ShouldSkip = ShouldSkipOwnerCollectionTradeExecution,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionTradeExecution())
                        return;
                    FocusXagmanCurrentTarget(GetCharacterNameFromKey(xagmanActiveTradePartner));
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SAEAE1FFAC9CFC684", charName), 0.15f, ShouldSkipTradeFlow));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SCB21F0C74CF4C180", charName),
                ShouldSkip = ShouldSkipOwnerCollectionTradeExecution,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionTradeExecution())
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S34CD84FA75D312BD", charName));
                    TryTargetCharacter(GetCharacterNameFromKey(xagmanActiveTradePartner));
                },
                IsComplete = () => relogFailed || ShouldSkipOwnerCollectionTradeExecution() || IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(xagmanActiveTradePartner), ownerTradeStopDistance),
                TimeoutSec = 5f,
                OnTimeout = () =>
                {
                    if (standbyRequested)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD8FD556344F14C25", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                    runner.RecordFailedCharacter(charName);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S7BC969B9477540AA", charName), 0.5f, ShouldSkipOwnerCollectionTradeExecution));
            AppendXagmanDropboxAutoAcceptStep(steps, global::XASlave.Localization.UiMessage.Format("S327D737BB1EDB548", charName), false, ShouldSkipOwnerCollectionTradeExecution);
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S86B1258DF703069D", charName),
                ShouldSkip = ShouldSkipOwnerCollectionTradeExecution,
                OnEnter = () =>
                {
                    if (ShouldSkipOwnerCollectionTradeExecution())
                        return;
                    ResetOwnerCollectionRangeRetry();
                    if (!StartXagmanDropboxTrade($"owner give trade {charName}"))
                    {
                        relogFailed = true;
                        xagmanStatus = XagmanStatus.Error;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9A953F3AEA791B49", charName));
                        if (!runner.FailedCharacters.Contains(charName))
                            runner.RecordFailedCharacter(charName);
                        return;
                    }
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S5E23029B52F079A7", charName),
                ShouldSkip = () => relogFailed || standbyRequested || ownerCollectionQueuedEntries <= 0,
                OnEnter = () => xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy(),
                IsComplete = () => relogFailed || standbyRequested || ownerCollectionQueuedEntries <= 0 || PollOwnerTradeWait() || TryEnterStandby(),
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    if (standbyRequested)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC12A85A8CDC778F7", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                    runner.RecordFailedCharacter(charName);
                    CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Format("S58D3AF15EF764895", charName));
                },
            });
            AppendXagmanDropboxAutoAcceptStep(steps, global::XASlave.Localization.UiMessage.Format("S327D737BB1EDB548", charName), false, () => standbyRequested || ownerCollectionQueuedEntries <= 0 || ShouldArmOwnerAutoAcceptForPendingTonySupply());
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SBB24AE07ED068010", charName),
                ShouldSkip = () => relogFailed || standbyRequested,
                OnEnter = () =>
                {
                    xagmanObservedDropboxBusy = false;
                    EvaluateOwnerCollectionRetry(1);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            for (var collectionPassNumber = 2; collectionPassNumber <= maxOwnerCollectionTradePasses; collectionPassNumber++)
                AddRepeatedOwnerCollectionPass(collectionPassNumber);
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SD182504CF3688CC8", charName),
                ShouldSkip = () => collectionOnly || relogFailed || standbyRequested || ownerCollectionRetryRequested,
                OnEnter = () =>
                {
                    xagmanObservedDropboxBusy = false;
                    var requestedItems = BuildXagmanOwnerTradeRequests(cfg.XagmanItems, charName);
                    if (FailOwnerOnIncompleteGreenScan(requestedItems))
                        return;
                    SetXagmanOwnerRequestedItems(requestedItems);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            AddOwnerTradeLockWaitStep(
                "requested supply",
                () => collectionOnly || relogFailed || standbyRequested || ownerCollectionRetryRequested || xagmanOwnerRequestedItems.Count == 0);
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S671CBEB73E68BAED", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (string.IsNullOrWhiteSpace(partnerName) ? global::XASlave.Localization.UiMessage.Format("S8A5DB912006FBF66", charName) : global::XASlave.Localization.UiMessage.Format("S762F7008DF3EAC06", charName, partnerName)));
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SBA8FCF854DDD528A", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    OpenXagmanDropboxTradeTab();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SC871531C008C19F1", charName), 1.0f, ShouldSkipRequestedTradeFlow));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S78C2D0734C4481B5", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    ClearXagmanDropbox();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SED72313A24210025", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    TryTargetCharacter(GetCharacterNameFromKey(xagmanActiveTradePartner));
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S827FD7398602DC39", charName), 0.1f, ShouldSkipRequestedTradeFlow));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S586B6B1CAE1427C4", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    FocusXagmanCurrentTarget(GetCharacterNameFromKey(xagmanActiveTradePartner));
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S4AF1361A4657E253", charName), 0.15f, ShouldSkipRequestedTradeFlow));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SBAD333CC3F305098", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SFB2F0DDF3A97CAFB", charName));
                    if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, true))
                        return;
                    var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
                    TryTargetCharacter(partnerName);
                    TryPathToCurrentTarget(ownerTradeStopDistance, partnerName);
                },
                IsComplete = () =>
                {
                    if (relogFailed || ShouldSkipRequestedTradeFlow())
                        return true;
                    if (!EnsureXagmanOwnerTonyCoordinateApproach(charName, XagmanTonyCalledCoordinateStopDistance, false))
                        return false;
                    return IsCurrentTargetWithinStopDistanceAndStopped(GetCharacterNameFromKey(xagmanActiveTradePartner), ownerTradeStopDistance);
                },
                TimeoutSec = 60f,
                OnTimeout = () =>
                {
                    if (standbyRequested)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5778A25B04A6EFE4", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S9DA57AEF5E562E45", charName), 0.5f, ShouldSkipRequestedTradeFlow));
            AppendXagmanDropboxAutoAcceptStep(
                steps,
                global::XASlave.Localization.UiMessage.Format("SFE189C246A0C5E79", charName),
                true,
                ShouldSkipRequestedTradeFlow,
                requireSuccess: true,
                onFailure: () =>
                {
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S90CBF4E345D8E525", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S8AF7C4C9669B9B61", charName),
                ShouldSkip = ShouldSkipRequestedTradeFlow,
                OnEnter = () =>
                {
                    if (ShouldSkipRequestedTradeFlow())
                        return;
                    ResetOwnerRequestedRangeRetry();
                    xagmanObservedDropboxBusy = false;
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SBA374CE0E82FD0CC", charName),
                ShouldSkip = () => relogFailed || standbyRequested || ownerPartial || xagmanOwnerRequestedItems.Count == 0,
                OnEnter = () => xagmanObservedDropboxBusy = false,
                IsComplete = () => relogFailed || standbyRequested || ownerPartial || xagmanOwnerRequestedItems.Count == 0 || PollOwnerRequestedTradeWait() || TryEnterStandby(),
                TimeoutSec = 600f,
                OnTimeout = () =>
                {
                    if (standbyRequested)
                        return;
                    relogFailed = true;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S0D56662A97C55864", charName));
                    if (!runner.FailedCharacters.Contains(charName))
                        runner.RecordFailedCharacter(charName);
                    CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage.Format("SE5BF5904252058DB", charName));
                },
            });
            AppendXagmanDropboxAutoAcceptStep(steps, global::XASlave.Localization.UiMessage.Format("SFE189C246A0C5E79", charName), false, () => standbyRequested || charSkipped);
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SB225D8588AFD001B", charName),
                ShouldSkip = () => relogFailed || standbyRequested || TryDeferOwnerForSupplyPass(),
                OnEnter = () =>
                {
                    xagmanObservedDropboxBusy = false;
                    EvaluateOwnerSendoffReconciliation(1, false);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("SD5668A524148DFE8", charName), 0.75f, () => relogFailed || standbyRequested || ownerPartial));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S221F04E15ACC7757", charName),
                ShouldSkip = () => relogFailed || standbyRequested || TryDeferOwnerForSupplyPass(),
                OnEnter = () =>
                {
                    xagmanObservedDropboxBusy = false;
                    EvaluateOwnerSendoffReconciliation(2, true);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SD96804B37E520CD5", charName),
                ShouldSkip = () => relogFailed || standbyRequested || (!ownerSendoffVerified && !ownerPartial),
                OnEnter = () =>
                {
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    ClearXagmanFocusTarget();
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            if (cfg.XagmanAutoReturnToFc)
            {
                AddXagmanTeleportSteps(
                    steps,
                    global::XASlave.Localization.UiMessage.Key("SB4777CCC172FB07E"),
                    () => "fc",
                    runner,
                    null,
                    true,
                    () =>
                    {
                        if (relogFailed || standbyRequested)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S0F4116A6867E3D64", charName));
                    },
                    () =>
                    {
                        if (relogFailed || standbyRequested)
                            return;
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S41A60232A6D93057", charName));
                        RememberXagmanRecentFcReturn(charName);
                    },
                    () =>
                    {
                        if (relogFailed || standbyRequested)
                            return;
                        if (xagmanCharacterFailureReasons.ContainsKey(charName))
                        {
                            relogFailed = true;
                            xagmanStatus = XagmanStatus.Error;
                            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA860756021F8C6B3", charName, xagmanCharacterFailureReasons[charName]));
                            return;
                        }
                        xagmanStatus = XagmanStatus.ReturningHome;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SDC6AFF65DED1945E", charName));
                    },
                    () => relogFailed || standbyRequested || charSkipped,
                    expectCrossDataCenterLogout: true);
            }
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SE34DBBDB1F63099D", charName),
                ShouldSkip = () => relogFailed || standbyRequested || charSkipped,
                OnEnter = () =>
                {
                    if (collectionOnly)
                    {
                        xagmanOwnerCollectionCompletedKeys.Add(charName);
                        if (xagmanPartialOwners.TryGetValue(charName, out var collectedState))
                        {
                            collectedState.PendingGiveItems.Clear();
                            if (collectedState.RequestedItems.Count == 0) xagmanPartialOwners.Remove(charName);
                        }
                    }
                    if (!ownerPartial && !collectionOnly && !capacityDrain && xagmanOwnerCompletedKeys.Add(charName))
                        InvalidateXagmanTradeCapacityForecast();
                    if (capacityDrain && (!ownerPartial || (xagmanPartialOwners.TryGetValue(charName, out var drainState)
                            && drainState.RequestedItems.Count == 0))) RecordXagmanCapacityDrainOwnerFilled(charName);
                    if (supplyCycle)
                        CompleteXagmanOwnerSupplyVisit(charName, ownerPartial);
                    xagmanOwnerCompletedCharacters = supplyCycle ? xagmanOwnerCompletedKeys.Count : charIndex;
                    if (IsXagmanCollectionFirstRunActive() && !supplyCycle)
                        xagmanPhaseResolvedCharacters = Math.Max(xagmanPhaseResolvedCharacters, charIndex);
                    runner.CompletedItems = xagmanOwnerCompletedCharacters;
                    runner.TotalItems = GetXagmanLocalOwnerTotalCharacters();
                    xagmanOwnerCurrentCharacterIndex = charIndex;
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
                    xagmanTradeQuantitySnapshot.Clear();
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    if (!relogFailed)
                    {
                        xagmanStatus = !supplyCycle && charIndex >= charTotal ? XagmanStatus.Completed : XagmanStatus.Idle;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (ownerPartial ? global::XASlave.Localization.UiMessage.Format("S4022A12A5AD684CC", charName) : global::XASlave.Localization.UiMessage.Format("SA41E34EFBED17784", charName)));
                    }
                    if (!collectionOnly && !capacityDrain && !ownerPartial)
                    {
                        try
                        {
                            for (var index = 0; index < plugin.Configuration.XagmanFranchiseCharacters.Count; index++)
                            {
                                if (!plugin.Configuration.XagmanFranchiseCharacters[index].Equals(charName, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                xagmanFranchiseSelectedIndices.Remove(index);
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                    PublishXagmanPresence();
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SADF10B10BB39FEFC", charName),
                ShouldSkip = () => standbyRequested,
                OnEnter = () =>
                {
                    if (relogFailed)
                    {
                        var reason = xagmanCharacterFailureReasons.GetValueOrDefault(charName, global::XASlave.Localization.UiMessage.Capture(ref xagmanStatusTextUiMessage, xagmanStatusText));
                        runner.AddLog($"Xagman: owner {charName} failed: {reason} Moving to the next owner.");
                        RecordXagmanOwnerProcessingEnd(charName);
                    }
                    else if (charSkipped)
                        runner.AddLog($"Xagman: owner {charName} skipped; continuing the remaining roster.");
                    else if (ownerPartial)
                        runner.AddLog($"Xagman: owner {charName} has unfinished trade work; continuing the remaining roster before retrying outstanding items with a later Tony.");
                    else if (capacityDrain)
                        runner.AddLog($"Xagman: owner {charName} supply pass finished; collection progress is retained for the collection phase.");
                    else
                        runner.AddLog($"Xagman: owner {charName} completed successfully.");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S81D95C1B60947BEF", charName),
                ShouldSkip = () => relogFailed || charSkipped,
                OnEnter = () => RecordXagmanOwnerProcessingEnd(charName),
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
            // A pass deferral is a scheduling decision, not an owner outcome. Guard every
            // generated travel/trade/sendoff step so another region or already served owner
            // cannot inherit the previous owner's active character or completion state.
            for (var ownerStepIndex = ownerStepsStart; ownerStepIndex < steps.Count; ownerStepIndex++)
                steps[ownerStepIndex] = MonthlyReloggerTask.WithSkip(steps[ownerStepIndex], () => passDeferred);
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S651B47099F0DB7ED"),
            ShouldSkip = () => xagmanOwnerStandbyPending,
            OnEnter = () =>
            {
                if (supplyCycle)
                {
                    FinishXagmanOwnerSupplyPass();
                    return;
                }
                if ((collectionOnly || restockOnly) && xagmanTravelRouteFatalError)
                {
                    xagmanPhaseComplete = false;
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText =
                        global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SAB84F31DD4EBF94E", xagmanRunPhase));
                    runner.SuppressLogoutCancel = true;
                    runner.AddLog(
                        $"Xagman: coordinated run {xagmanRunId} remains connected in Error because a route fault occurred during {xagmanRunPhase}; Tony must not advance the phase.");
                    PublishXagmanPresence();
                    return;
                }

                if (collectionOnly)
                {
                    foreach (var failedCharacter in runner.FailedCharacters)
                    {
                        if (!string.IsNullOrWhiteSpace(failedCharacter))
                            xagmanCollectionFirstFailedCharacters.Add(failedCharacter);
                    }
                    xagmanPhaseResolvedCharacters = xagmanPhaseTotalCharacters;
                    xagmanPhaseComplete = true;
                    xagmanOwnerCurrentCharacterIndex = characters.Count;
                    xagmanOwnerSweepPendingDataCenter = string.Empty;
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (runner.FailedCharacters.Count == 0 ? global::XASlave.Localization.UiMessage.Key("SBC8DB308BD3DC0FC") : global::XASlave.Localization.UiMessage.Format("SC3DEA4AB7EC444F1", runner.FailedCharacters.Count)));
                    runner.SuppressLogoutCancel = true;
                    runner.AddLog(
                        $"Xagman: collection phase complete for run {xagmanRunId}; this FO client is paused at the global barrier.");
                    PublishXagmanPresence();
                    return;
                }

                if (restockOnly)
                {
                    foreach (var failedCharacter in runner.FailedCharacters)
                    {
                        if (!string.IsNullOrWhiteSpace(failedCharacter))
                            xagmanCollectionFirstFailedCharacters.Add(failedCharacter);
                    }
                    FinalizeXagmanCollectionFirstOwnerSelections();
                    xagmanPhaseResolvedCharacters = xagmanPhaseTotalCharacters;
                    xagmanPhaseComplete = true;
                    xagmanOwnerCurrentCharacterIndex = characters.Count;
                    xagmanOwnerSweepPendingDataCenter = string.Empty;
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (runner.FailedCharacters.Count == 0 ? global::XASlave.Localization.UiMessage.Key("SB254A9B0353CC1C7") : global::XASlave.Localization.UiMessage.Format("SD87CCBA5B6474A61", runner.FailedCharacters.Count)));
                    runner.SuppressLogoutCancel = true;
                    runner.AddLog(
                        $"Xagman: restock phase complete for run {xagmanRunId}; this FO client is holding its connection until Tony completes the global barrier.");
                    PublishXagmanPresence();
                    return;
                }
                SetXagmanRunning(false);
                xagmanOwnerCurrentCharacterIndex = characters.Count;
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (runner.FailedCharacters.Count == 0 ? global::XASlave.Localization.UiMessage.Key("S2EFA861C1B277E74") : global::XASlave.Localization.UiMessage.Format("SC0EF46BDE5BDED89", runner.FailedCharacters.Count)));
                runner.SuppressLogoutCancel = MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete);
                PublishXagmanPresence();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (supplyCycle)
        {
            AddXagmanSupplyPassMenuWaitSteps(steps);
            return steps;
        }
        if (collectionOnly || restockOnly)
        {
            // Coordinated phases must remain connected with trade-safety state intact while Tony
            // observes the phase acknowledgement. Tony sends the scoped completion directive
            // after the global restock barrier, which owns the one final cleanup sequence.
            return steps;
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S22D90A193F6844DE"),
            ShouldSkip = () => xagmanOwnerStandbyPending || xagmanStatus != XagmanStatus.Completed,
            OnEnter = () =>
            {
                if (RequestXagmanTonyCompletion())
                {
                    runner.AddLog(string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
                        ? "Xagman: sent Tony completion signal."
                        : $"Xagman: sent Tony completion signal to {xagmanPreferredTonyCharacter}.");
                }
                else
                {
                    runner.AddLog("Xagman: could not send Tony completion signal before disconnecting the local TCP peer service.");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S08F239E59C053C25"),
            ShouldSkip = () => xagmanOwnerStandbyPending || xagmanStatus != XagmanStatus.Completed,
            OnEnter = () =>
            {
                if (!DisconnectXagmanPeerService())
                    runner.AddLog("Xagman: local TCP peer service was already disconnected before completion cleanup.");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        AddXagmanTradeSafetyCompletionStep(
            steps,
            runner,
            "Franchise Owner completion",
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete));
        MonthlyReloggerTask.AddSharedCompletionSteps(steps, runner, cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete, cfg.XagmanEnableArMultiOnComplete);
        return steps;
    }

    private void AddXagmanRelogRetryPreFlightSteps(
        List<TaskStep> steps,
        string charName,
        int attempt,
        int maxAttempts,
        TaskRunner runner,
        Func<bool> shouldSkip)
    {
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S34E7615BF7CC4229", charName, attempt, maxAttempts),
            ShouldSkip = shouldSkip,
            OnEnter = () =>
            {
                xagmanExpectedLogout = false;
                runner.AddLog(
                    $"Xagman: relog attempt {attempt - 1}/{maxAttempts} did not reach a safe {charName}; " +
                    $"running pre-flight before attempt {attempt}/{maxAttempts}.");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        var helper = new MonthlyReloggerTask(plugin);
        foreach (var preFlightStep in helper.BuildPreFlightOnlySteps(new List<string> { charName }, runner, RunXagmanPreflightArMultiGuard))
            steps.Add(MonthlyReloggerTask.WithSkip(preFlightStep, shouldSkip));

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S2E8F6EA1577180D1", charName, attempt, maxAttempts),
            ShouldSkip = shouldSkip,
            OnEnter = () => runner.AddLog(
                $"Xagman: retry pre-flight complete; issuing relog attempt {attempt}/{maxAttempts} for {charName}."),
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    private long ArmXagmanTravelFailure(string command, string? expectedCharacter = null)
    {
        var character = expectedCharacter ?? MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        EndXagmanTravelFailure(xagmanTravelFailureToken);
        if (string.IsNullOrWhiteSpace(character) || character.EndsWith("@Unknown", StringComparison.OrdinalIgnoreCase))
            return 0;
        xagmanTravelFailureMonitor ??= new XagmanTravelFailureMonitor(
            plugin.MessageLog, MonthlyReloggerTask.GetCurrentCharacterNameWorld);
        xagmanTravelFailureToken = xagmanTravelFailureMonitor.Arm(character, command);
        return xagmanTravelFailureToken;
    }

    private void EndXagmanTravelFailure(long token)
    {
        xagmanTravelFailureMonitor?.End(token);
        if (token == xagmanTravelFailureToken)
            xagmanTravelFailureToken = 0;
    }

    private void ResetXagmanTravelFailureMonitor()
    {
        EndXagmanLobbyOperation();
        xagmanTonyCharacterFailurePending = false;
        xagmanTravelFailureMonitor?.Dispose();
        xagmanTravelFailureMonitor = null;
        xagmanTravelFailureToken = 0;
    }

    private bool TryTakeXagmanTravelFailure(long token, TaskRunner runner, string label)
    {
        if (xagmanTravelFailureMonitor == null
            || !xagmanTravelFailureMonitor.TryTakeFailure(token, out var failure))
            return false;

        xagmanTravelFailureToken = 0;
        ClearXagmanExpectedTravelLogoutWindow();
        xagmanSkipFcReturnAfterFailureCharacter = failure.ExpectedCharacter;
        xagmanCharacterFailureReasons[failure.ExpectedCharacter] = failure.ReasonMessage;
        runner.RecordFailedCharacter(failure.ExpectedCharacter);
        runner.RecordItemEnd(failure.ExpectedCharacter);
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S389905C4661EF261", failure.ExpectedCharacter, failure.ReasonMessage));
        runner.AddLog($"Xagman: {xagmanStatusText} Travel '{label}', command '{failure.Command}', message: {failure.Message}");
        if (!plugin.IpcClient.LifestreamAbort())
            runner.AddLog($"Xagman: Lifestream abort could not be confirmed after the travel failure for {failure.ExpectedCharacter}.");
        return true;
    }

    private void AddXagmanRelogSteps(List<TaskStep> steps, string charName, TaskRunner runner, SysAction onEnter, SysAction onReady, SysAction onTimeout, Func<bool>? externalSkip = null, bool recoverOwnerLogout = false, Action<string>? onDestinationSkip = null)
    {
        var maxAttempts = recoverOwnerLogout ? 2 : XagmanRelogMaxAttempts;
        var currentAttempt = 0;
        var attemptInterrupted = false;
        var relogFailed = false;
        var relogConfirmed = false;
        var skipRelog = false;
        var recentlyReturnedToFc = false;
        var returnToFcBeforeRelog = false;
        var loggedInCharacter = string.Empty;
        var recoveryRequired = false;
        var pendingLogoutFailure = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        var loginWorld = string.Empty;
        var readyPasses = 0;
        var nextReadyPassUtc = DateTime.MinValue;
        var nextDestinationPollUtc = DateTime.MinValue;
        var lastDestinationWaitReason = string.Empty;
        var loginCommandSent = false;
        var relogDatabasePullRequested = false;
        long loginTravelFailureToken = 0;
        void EndLoginTravelObservation()
        {
            EndXagmanTravelFailure(loginTravelFailureToken);
            loginTravelFailureToken = 0;
        }
        bool ShouldExternalSkip()
        {
            if (externalSkip?.Invoke() != true) return false;
            EndLoginTravelObservation();
            return true;
        }
        void FailLogin(global::XASlave.Localization.UiMessage reason)
        {
            if (relogFailed)
                return;
            relogFailed = true;
            EndLoginTravelObservation();
            EndXagmanLobbyOperation();
            var cleanupFailed = loginCommandSent && !plugin.IpcClient.LifestreamAbort();
            loginCommandSent = false;
            recoveryRequired = recoverOwnerLogout;
            if (recoverOwnerLogout)
                xagmanOwnerRelogLogoutFailure = _ => { };
            xagmanExpectedLogout = false;
            xagmanCharacterFailureReasons[charName] = reason;
            runner.RecordFailedCharacter(charName);
            runner.RecordItemEnd(charName);
            runner.AddLog($"Xagman: failed to log into {charName}: {reason}");
            onTimeout();
            if (cleanupFailed)
                runner.RequestHalt("Lifestream login cleanup could not be confirmed; recovery commands were blocked.");
        }
        void ArmOwnerRelogFailure()
        {
            if (recoverOwnerLogout)
                xagmanOwnerRelogLogoutFailure = reason =>
                {
                    // Apply task/peer mutations on the task poll, not inside ClientState's event.
                    if (string.IsNullOrEmpty(pendingLogoutFailure.Original)) pendingLogoutFailure = reason;
                };
        }
        bool ObserveLoginReady()
        {
            // Lifestream can teleport after login while still on the wrong world. Consume
            // terminal travel errors before readiness checks or the remaining relog retries.
            if (!relogFailed && xagmanTravelFailureMonitor != null
                && xagmanTravelFailureMonitor.TryTakeFailure(loginTravelFailureToken, out var failure))
            {
                xagmanSkipFcReturnAfterFailureCharacter = failure.ExpectedCharacter;
                ClearXagmanExpectedTravelLogoutWindow();
                runner.AddLog($"Xagman: login travel failed for {failure.ExpectedCharacter}; command '{failure.Command}', message: {failure.Message}");
                FailLogin(failure.ReasonMessage);
            }
            if (!relogFailed && !string.IsNullOrEmpty(pendingLogoutFailure.Original))
            {
                var reason = pendingLogoutFailure;
                pendingLogoutFailure = global::XASlave.Localization.UiMessage.Raw(string.Empty);
                EndLoginTravelObservation();
                EndXagmanLobbyOperation();
                xagmanExpectedLogout = false;
                xagmanOwnerRelogLogoutFailure = _ => { };
                if (loginCommandSent && !plugin.IpcClient.LifestreamAbort())
                {
                    FailLogin(global::XASlave.Localization.UiMessage.Format("SE3AA3716243365B8", reason));
                    runner.RequestHalt("Lifestream disconnect cleanup could not be confirmed; recovery commands were blocked.");
                    return true;
                }
                loginCommandSent = false;
                readyPasses = 0;
                if (currentAttempt >= maxAttempts)
                    FailLogin(reason);
                else
                {
                    attemptInterrupted = true;
                    runner.AddLog($"Xagman: {charName} disconnected during login attempt {currentAttempt}/{maxAttempts}: {reason}. Not marked failed; running pre-flight before retry.");
                }
                return true;
            }
            if (attemptInterrupted) return true;
            if (relogFailed) return true;
            var ready = MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(loginWorld)
                && GetCurrentWorldName().Equals(loginWorld, StringComparison.OrdinalIgnoreCase)
                && plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy
                && CharacterSafetyHelper.IsCharacterSafeWaitReady();
            if (!ready)
            {
                readyPasses = 0;
                nextReadyPassUtc = DateTime.UtcNow.AddSeconds(1);
                return false;
            }
            if (DateTime.UtcNow < nextReadyPassUtc) return false;
            nextReadyPassUtc = DateTime.UtcNow.AddSeconds(1);
            readyPasses++;
            runner.AddLog($"Xagman: Lifestream relog CharacterSafeWait {readyPasses}/3 for {charName} on {loginWorld}; Lifestream idle.");
            if (readyPasses < 3) return false;
            relogConfirmed = true;
            EndLoginTravelObservation();
            EndXagmanLobbyOperation();
            xagmanExpectedLogout = false;
            xagmanOwnerRelogLogoutFailure = null;
            runner.AddLog($"Xagman: relog confirmed for {charName} on {loginWorld}; three safe passes completed before meetup teleport.");
            return true;
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S1DD16A2BA7A1EAB9", charName),
            ShouldSkip = ShouldExternalSkip,
            OnEnter = () =>
            {
                xagmanExpectedLogout = false;
                xagmanOwnerRelogLogoutFailure = null;
                onEnter();
                skipRelog = false;
                returnToFcBeforeRelog = false;
                loginWorld = GetXagmanActiveMeetWorld();
                readyPasses = 0;
                loggedInCharacter = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                if (loggedInCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase)
                    && GetCurrentWorldName().Equals(loginWorld, StringComparison.OrdinalIgnoreCase)
                    && plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy)
                {
                    skipRelog = true;
                    ArmOwnerRelogFailure();
                    runner.AddLog($"Xagman: already logged into {charName} on {loginWorld}; verifying three safe passes before meetup travel.");
                    return;
                }

                if (xagmanSkipFcReturnAfterFailureCharacter.Equals(loggedInCharacter, StringComparison.OrdinalIgnoreCase))
                {
                    xagmanSkipFcReturnAfterFailureCharacter = string.Empty;
                    runner.AddLog($"Xagman: skipping /li fc for {loggedInCharacter} after its detected travel failure; continuing login to {charName}.");
                    return;
                }

                recentlyReturnedToFc = plugin.Configuration.XagmanAutoReturnToFc
                    && ConsumeXagmanRecentFcReturn(loggedInCharacter);
                returnToFcBeforeRelog = plugin.Configuration.XagmanAutoReturnToFc
                    && !recentlyReturnedToFc
                    && !string.IsNullOrWhiteSpace(loggedInCharacter)
                    && !loggedInCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase);
                if (recentlyReturnedToFc)
                    runner.AddLog($"Xagman: skipping duplicate /li fc for {loggedInCharacter} before relogging to {charName}.");
                if (returnToFcBeforeRelog)
                    runner.AddLog($"Xagman: returning {loggedInCharacter} to FC before relogging to {charName}.");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        AddXagmanTeleportSteps(
            steps,
            global::XASlave.Localization.UiMessage.Format("S3B742CE97FA5F854", charName),
            () => "fc",
            runner,
            null,
            false,
            () =>
            {
                if (returnToFcBeforeRelog)
                    runner.AddLog($"Xagman: sending /li fc for {loggedInCharacter} before /li {charName}.");
            },
            () =>
            {
                if (returnToFcBeforeRelog)
                {
                    RememberXagmanRecentFcReturn(loggedInCharacter);
                    runner.AddLog($"Xagman: /li fc completed for {loggedInCharacter}; continuing relog to {charName}.");
                }
            },
            () =>
            {
                if (xagmanSkipFcReturnAfterFailureCharacter.Equals(loggedInCharacter, StringComparison.OrdinalIgnoreCase))
                {
                    xagmanSkipFcReturnAfterFailureCharacter = string.Empty;
                    returnToFcBeforeRelog = false;
                    runner.AddLog($"Xagman: /li fc failed for outgoing {loggedInCharacter}; continuing login to {charName}.");
                    return;
                }
                FailLogin(global::XASlave.Localization.UiMessage.Format("SD0A6C78C5EAE6487", loggedInCharacter));
            },
            () => relogFailed || skipRelog || !returnToFcBeforeRelog || ShouldExternalSkip(),
            expectCrossDataCenterLogout: true);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var capturedAttempt = attempt;
            Func<bool> skipAttempt = () => relogFailed || relogConfirmed || ShouldExternalSkip();

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S8375E695EB29E73A", charName, capturedAttempt, maxAttempts),
                ShouldSkip = skipAttempt,
                IsComplete = () => plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy,
                TimeoutSec = 60f,
                OnTimeout = () => FailLogin(global::XASlave.Localization.UiMessage.Key("S211B8215585BBF46")),
            });

            if (capturedAttempt > 1)
            {
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S5B2678661E6A9981", charName),
                    ShouldSkip = skipAttempt,
                    IsComplete = () => !AddonHelper.IsAddonVisible("Dialogue")
                        && (AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu")
                            || AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu")
                            || CharacterSafetyHelper.IsCharacterSafeWaitReady()),
                    TimeoutSec = 30f,
                    OnTimeout = () => FailLogin(global::XASlave.Localization.UiMessage.Key("S4F11C5D318128D06")),
                });
                AddXagmanRelogRetryPreFlightSteps(
                    steps,
                    charName,
                    capturedAttempt,
                    maxAttempts,
                    runner,
                    skipAttempt);
            }

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S543134156FCE713B", charName, capturedAttempt, maxAttempts),
                ShouldSkip = skipAttempt,
                IsComplete = () =>
                {
                    if (DateTime.UtcNow < nextDestinationPollUtc) return false;
                    nextDestinationPollUtc = DateTime.UtcNow.AddSeconds(1);
                    if (recoverOwnerLogout)
                    {
                        if (!TryCaptureFreshXagmanOwnerMeetDestination(charName, out var world, out var area, out var reason, out var shouldSkip))
                        {
                            if (shouldSkip && onDestinationSkip != null)
                            {
                                relogFailed = true;
                                onDestinationSkip(reason);
                                return true;
                            }
                            if (reason != lastDestinationWaitReason)
                            {
                                runner.AddLog($"Xagman: login for {charName} is waiting without consuming an attempt: {reason}.");
                                lastDestinationWaitReason = reason;
                            }
                            return false;
                        }
                        SetXagmanActiveMeetDestination(world, area);
                        loginWorld = world;
                    }
                    else loginWorld = GetXagmanActiveMeetWorld();
                    if (string.IsNullOrWhiteSpace(loginWorld))
                    { FailLogin(global::XASlave.Localization.UiMessage.Key("S9FDE43B07A007DAE")); return true; }
                    if (!TryValidateXagmanMeetTravelMessage(charName, loginWorld, out var routeFailure))
                    { FailLogin(global::XASlave.Localization.UiMessage.Format("S2EC88D6DCBD2E138", routeFailure)); return true; }
                    return true;
                },
                TimeoutSec = 86400f,
                OnTimeout = () => FailLogin(global::XASlave.Localization.UiMessage.Key("S1A00CF2035820D95")),
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S202EE1DD5B34C736", charName, capturedAttempt, maxAttempts),
                ShouldSkip = skipAttempt,
                OnEnter = () =>
                {
                    if (!plugin.IpcClient.TryGetLifestreamBusy(out var busy) || busy)
                    { FailLogin(global::XASlave.Localization.UiMessage.Key("S02215CC2C694109C")); return; }
                    currentAttempt = capturedAttempt;
                    attemptInterrupted = false;
                    readyPasses = 0;
                    nextReadyPassUtc = DateTime.UtcNow.AddSeconds(1);
                    pendingLogoutFailure = global::XASlave.Localization.UiMessage.Raw(string.Empty);
                    ArmOwnerRelogFailure();
                    if (MonthlyReloggerTask.GetCurrentCharacterNameWorld().Equals(charName, StringComparison.OrdinalIgnoreCase)
                        && GetCurrentWorldName().Equals(loginWorld, StringComparison.OrdinalIgnoreCase)
                        && CharacterSafetyHelper.IsCharacterSafeWaitReady())
                    {
                        runner.AddLog($"Xagman: {charName} already reached {loginWorld} while waiting; verifying three safe passes without another relog.");
                        return;
                    }
                    // Explicitly pin even a home-world destination: a logged-out character may be visiting elsewhere.
                    var command = $"{charName} {loginWorld}";
                    runner.AddLog($"Xagman: sending /li {command} (attempt {capturedAttempt}/{maxAttempts}); meetup area will be sent separately after three safe passes.");
                    runner.AddLog(returnToFcBeforeRelog && capturedAttempt == 1
                        ? $"Xagman: relogging to {charName} after returning {loggedInCharacter} to FC (attempt {capturedAttempt}/{maxAttempts})."
                        : $"Xagman: relogging to {charName} (attempt {capturedAttempt}/{maxAttempts}).");
                    // At the title screen there is no outgoing character logout to consume.
                    xagmanExpectedLogout = !string.IsNullOrWhiteSpace(MonthlyReloggerTask.GetCurrentCharacterNameWorld());
                    loginCommandSent = true;
                    BeginXagmanLobbyOperation(command, charName, global::XASlave.Localization.UiMessage.Format("SBD61363F73505F57", charName), true, false,
                        beforeReplay: () => { readyPasses = 0; nextReadyPassUtc = DateTime.UtcNow.AddSeconds(1); pendingLogoutFailure = global::XASlave.Localization.UiMessage.Raw(string.Empty); ArmOwnerRelogFailure(); loginTravelFailureToken = ArmXagmanTravelFailure(command, charName); },
                        completed: () => GetCurrentWorldName().Equals(loginWorld, StringComparison.OrdinalIgnoreCase));
                    loginTravelFailureToken = ArmXagmanTravelFailure(command, charName);
                    if (!plugin.IpcClient.LifestreamExecuteCommand(command))
                        FailLogin(global::XASlave.Localization.UiMessage.Key("S78E44CF31CD7D6D9"));
                    else if (!relogDatabasePullRequested)
                    {
                        relogDatabasePullRequested = true;
                        QueueXagmanRelogDatabasePull();
                    }
                },
                IsComplete = ObserveLoginReady,
                TimeoutSec = XagmanRelogTimeoutSeconds,
                OnTimeout = () =>
                {
                    EndLoginTravelObservation();
                    EndXagmanLobbyOperation();
                    xagmanExpectedLogout = false;
                    if (recoverOwnerLogout) xagmanOwnerRelogLogoutFailure = _ => { };
                    var current = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    var reason = global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S6EFBBF8F1CA4110A", capturedAttempt, maxAttempts, XagmanRelogTimeoutSeconds), global::XASlave.Localization.UiMessage.Format("S4EBC823BE91EEBB1", current, GetCurrentWorldName(), loginWorld, readyPasses)), global::XASlave.Localization.UiMessage.Format("SE2997E282A83F870", plugin.IpcClient.TryGetLifestreamBusy(out var busy), busy));
                    if (loginCommandSent && !plugin.IpcClient.LifestreamAbort())
                    { FailLogin(global::XASlave.Localization.UiMessage.Format("SE13E55B2DB6D4796", reason)); return; }
                    loginCommandSent = false;
                    runner.AddLog($"Xagman: unable to confirm login to {charName}: {reason}");
                    if (capturedAttempt < maxAttempts)
                    {
                        runner.AddLog(
                            $"Xagman: relog attempt {capturedAttempt}/{maxAttempts} timed out before a safe {charName}; " +
                            "the next attempt will run pre-flight first.");
                        return;
                    }

                    FailLogin(reason);
                },
            });
        }
        if (recoverOwnerLogout)
        {
            void StopRecovery(global::XASlave.Localization.UiMessage reasonPresentation)
            {
                var reason = reasonPresentation.Original;
                global::XASlave.Localization.UiMessage? messageUiPresentation = null;
                var message = global::XASlave.Localization.UiMessage.Store(ref messageUiPresentation, global::XASlave.Localization.UiMessage.Format("SD3D06AA5E309B21D", charName, reason));
                runner.AddLog(message);
                StopXagmanTask();
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Capture(ref messageUiPresentation, message));
            }
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S7CF3EB3D4F7B3CE0", charName),
                ShouldSkip = () => !recoveryRequired,
                IsComplete = () => plugin.IpcClient.TryGetLifestreamBusy(out var busy) && !busy,
                TimeoutSec = 60f,
                OnTimeout = () => StopRecovery(global::XASlave.Localization.UiMessage.Key("SE44E08505EC7F57A")),
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S6CCF36D7A1054A43", charName),
                ShouldSkip = () => !recoveryRequired,
                OnEnter = () =>
                {
                    xagmanExpectedLogout = false;
                    xagmanOwnerRelogLogoutFailure = _ => { }; // Repeated lobby notifications remain inside bounded recovery.
                    ClearXagmanExpectedTravelLogoutWindow();
                    if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S7380580E35D610B0")))
                        return;
                    if (!TryClearXagmanDropbox(out var queueFailure))
                    {
                        StopRecovery(global::XASlave.Localization.UiMessage.Format("SE50F08D76E751C78", queueFailure));
                        return;
                    }
                    xagmanQueueRequestedAtUtc = DateTime.MinValue;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanObservedDropboxBusy = false;
                    xagmanTradeQuantitySnapshot.Clear();
                    SetXagmanOwnerRequestedItems(Array.Empty<XagmanTradeRequestEntry>(), false);
                    ClearXagmanFocusTarget();
                    PublishXagmanPresence();
                    runner.AddLog($"Xagman: {charName} marked failed; running pre-flight before continuing the remaining owners.");
                },
                TimeoutSec = 1f,
            });
            // Wait for the disconnect screen transition before detecting the pre-flight state.
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SFB46963B260E27F7", charName),
                ShouldSkip = () => !recoveryRequired,
                IsComplete = () => !AddonHelper.IsAddonVisible("Dialogue")
                    && (AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu")
                        || AddonHelper.IsAddonVisible("CharaSelect") || AddonHelper.IsAddonVisible("_CharaSelectListMenu")
                        || AddonHelper.IsAddonVisible("MovieStaffList") || CharacterSafetyHelper.IsCharacterSafeWaitReady()),
                TimeoutSec = 30f,
                OnTimeout = () => StopRecovery(global::XASlave.Localization.UiMessage.Key("S44DE841FD4BC3212")),
            });
            var recoveryHelper = new MonthlyReloggerTask(plugin);
            foreach (var preFlightStep in recoveryHelper.BuildPreFlightOnlySteps(new List<string> { charName }, runner, RunXagmanPreflightArMultiGuard))
            {
                var recoveryStep = preFlightStep;
                steps.Add(new TaskStep
                {
                    NameMessage = global::XASlave.Localization.UiMessage.Format("S6CCF36D7A1054A43", recoveryStep.Name),
                    ShouldSkip = () => !recoveryRequired || (recoveryStep.ShouldSkip?.Invoke() ?? false),
                    OnEnter = recoveryStep.OnEnter,
                    IsComplete = recoveryStep.IsComplete,
                    TimeoutSec = recoveryStep.TimeoutSec,
                    OnTimeout = () => StopRecovery(global::XASlave.Localization.UiMessage.Format("S47A37737E7B7373D", recoveryStep.Name)),
                });
            }
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SE11607832A158B09", charName),
                ShouldSkip = () => !recoveryRequired,
                IsComplete = () => !AddonHelper.IsAddonVisible("Dialogue")
                    && (AddonHelper.IsAddonVisible("_TitleLogo") || AddonHelper.IsAddonVisible("_TitleMenu")
                        || CharacterSafetyHelper.IsCharacterSafeWaitReady()),
                TimeoutSec = 10f,
                OnTimeout = () => StopRecovery(global::XASlave.Localization.UiMessage.Key("S7DAF00F42E0FF30E")),
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S367F375D33F7AC6E", charName),
                ShouldSkip = () => !recoveryRequired,
                OnEnter = () =>
                {
                    xagmanOwnerRelogLogoutFailure = null;
                    // A recovered character failure must not poison the later supply-pass gate.
                    xagmanStatus = XagmanStatus.Preflight;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S007EC5C86CBEB1EE", charName));
                    runner.AddLog($"Xagman: recovery after {charName} passed; continuing to the next eligible owner.");
                },
                TimeoutSec = 1f,
            });
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S02DD52ACB0BC2AB0", charName),
            OnEnter = () =>
            {
                EndLoginTravelObservation();
                xagmanExpectedLogout = false;
                xagmanOwnerRelogLogoutFailure = null;
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S0AF4019AEC2D4E66", charName),
            ShouldSkip = () => relogFailed || ShouldExternalSkip(),
            OnEnter = onReady,
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    /// <summary>
    /// Travels one Franchise Owner to the active meetup with an explicit, bounded retry group.
    /// Each attempt requires the intended character, observes the Lifestream busy/idle lifecycle
    /// when it occurs, runs CharacterSafeWait, and verifies the final world/zone before succeeding.
    /// </summary>
    private bool TryCaptureFreshXagmanOwnerMeetDestination(
        string charName,
        out string meetWorld,
        out string meetAetheryte,
        out string reason,
        out bool ownerShouldSkip)
    {
        var result = TryCaptureFreshXagmanOwnerMeetDestinationMessage(charName, out meetWorld, out meetAetheryte, out var presentation, out ownerShouldSkip);
        reason = presentation.Original;
        return result;
    }

    private bool TryCaptureFreshXagmanOwnerMeetDestinationMessage(
        string charName,
        out string meetWorld,
        out string meetAetheryte,
        out global::XASlave.Localization.UiMessage reason,
        out bool ownerShouldSkip)
    {
        ownerShouldSkip = false;
        var serverMatchingTony = GetXagmanServerMatchingTonyPeer();
        if (serverMatchingTony != null)
            xagmanServerMatchingActive = true;

        if (xagmanServerMatchingActive)
        {
            var decision = EvaluateXagmanOwnerServerGateMessage(charName, out meetWorld, out meetAetheryte, out reason);
            if (decision == XagmanOwnerServerGate.Proceed
                && HasCompleteXagmanMeetDestination(meetWorld, meetAetheryte))
            {
                return true;
            }

            ownerShouldSkip = decision == XagmanOwnerServerGate.Skip;
            meetWorld = string.Empty;
            meetAetheryte = string.Empty;
            if (string.IsNullOrWhiteSpace(reason.Original))
                reason = global::XASlave.Localization.UiMessage.Key("S52FD090B288083D5");
            return false;
        }

        if (TryGetXagmanMeetDestinationForOwner(out meetWorld, out meetAetheryte, charName)
            && HasCompleteXagmanMeetDestination(meetWorld, meetAetheryte))
        {
            reason = global::XASlave.Localization.UiMessage.Raw(string.Empty);
            return true;
        }

        meetWorld = string.Empty;
        meetAetheryte = string.Empty;
        reason = global::XASlave.Localization.UiMessage.Key("SD931A3D738F3BCDF");
        return false;
    }

    private void AddXagmanOwnerMeetTravelSteps(
        List<TaskStep> steps,
        string charName,
        TaskRunner runner,
        SysAction onReady,
        SysAction onFailure,
        Action<string> onSkip,
        Func<bool>? externalSkip = null)
    {
        const int maxAttempts = 3;
        const float waitStartTimeoutSec = 15f;
        const float waitCompleteTimeoutSec = 600f;
        const float safeWaitTimeoutSec = 30f;
        const int stableObservationPasses = 3;
        const double stableObservationIntervalSec = 1d;
        var travelComplete = false;
        var travelFailed = false;
        long travelFailureToken = 0;

        void EndTravelFailureObservation()
        {
            EndXagmanLobbyOperation();
            EndXagmanTravelFailure(travelFailureToken);
            travelFailureToken = 0;
        }

        bool ShouldExternalSkip()
        {
            var shouldSkip = externalSkip?.Invoke() ?? false;
            if (shouldSkip)
            {
                EndTravelFailureObservation();
                ClearXagmanExpectedTravelLogoutWindow();
            }
            return shouldSkip;
        }
        bool ShouldStopTravel()
        {
            if (travelComplete || travelFailed || ShouldExternalSkip())
                return true;
            if (!TryTakeXagmanTravelFailure(travelFailureToken, runner, $"owner {charName} meetup travel"))
                return false;

            travelFailed = true;
            EndTravelFailureObservation();
            ClearXagmanExpectedTravelLogoutWindow();
            global::XASlave.Localization.UiMessage? failureStatusTextUiPresentation = null;
            var failureStatusText = global::XASlave.Localization.UiMessage.Store(ref failureStatusTextUiPresentation, global::XASlave.Localization.UiMessage.Capture(ref xagmanStatusTextUiMessage, xagmanStatusText));
            onFailure();
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Capture(ref failureStatusTextUiPresentation, failureStatusText));
            return true;
        }
        void FailTravelBeforeCommand(string reason)
        {
            if (ShouldStopTravel())
                return;

            EndTravelFailureObservation();
            ClearXagmanExpectedTravelLogoutWindow();
            travelFailed = true;
            runner.AddLog($"Xagman: owner {charName} meetup travel stopped before a command could be sent: {reason}");
            onFailure();
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5BFFDC81A0A924B4", charName));
        }

        static bool IsCharacterIdentityUnavailable(string characterNameWorld)
        {
            return string.IsNullOrWhiteSpace(characterNameWorld)
                || characterNameWorld.EndsWith("@Unknown", StringComparison.OrdinalIgnoreCase);
        }

        static void ResetStableObservation(
            ref string stableValue,
            ref int stableCount,
            ref DateTime lastObservationUtc)
        {
            stableValue = string.Empty;
            stableCount = 0;
            lastObservationUtc = DateTime.MinValue;
        }

        bool RecordStableObservation(
            string observedValue,
            ref string stableValue,
            ref int stableCount,
            ref DateTime lastObservationUtc)
        {
            var now = DateTime.UtcNow;
            if (!observedValue.Equals(stableValue, StringComparison.OrdinalIgnoreCase))
            {
                stableValue = observedValue;
                stableCount = 0;
                lastObservationUtc = DateTime.MinValue;
            }

            if (lastObservationUtc != DateTime.MinValue
                && (now - lastObservationUtc).TotalSeconds < stableObservationIntervalSec)
            {
                return false;
            }

            lastObservationUtc = now;
            stableCount++;
            return stableCount >= stableObservationPasses;
        }

        static bool TryGetOwnerTravelTransitionReason(out string reason)
        {
            try
            {
                if (!Plugin.PlayerState.IsLoaded)
                {
                    reason = "PlayerState is not loaded";
                    return true;
                }

                var local = Plugin.ObjectTable.LocalPlayer;
                if (local == null)
                {
                    reason = "LocalPlayer is unavailable";
                    return true;
                }

                var condition = Plugin.Condition;
                if (condition[ConditionFlag.Casting])
                {
                    reason = nameof(ConditionFlag.Casting);
                    return true;
                }
                if (condition[ConditionFlag.BetweenAreas])
                {
                    reason = nameof(ConditionFlag.BetweenAreas);
                    return true;
                }
                if (condition[ConditionFlag.BetweenAreas51])
                {
                    reason = nameof(ConditionFlag.BetweenAreas51);
                    return true;
                }

                if (local.HomeWorld.RowId == 0 || WorldData.GetById(local.HomeWorld.RowId) == null)
                {
                    reason = "home-world identity is unresolved";
                    return true;
                }
                if (local.CurrentWorld.RowId == 0 || WorldData.GetById(local.CurrentWorld.RowId) == null)
                {
                    reason = "current world is unresolved";
                    return true;
                }
                if (Plugin.ClientState.TerritoryType == 0)
                {
                    reason = "current territory is unresolved";
                    return true;
                }
                if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
                {
                    reason = "CharacterSafeWait is not ready";
                    return true;
                }
            }
            catch (Exception ex)
            {
                reason = $"game state is unreadable ({ex.GetType().Name})";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var capturedAttempt = attempt;
            var attemptFailed = false;
            var retryPending = false;
            var alreadyAtDestination = false;
            var sawBusy = false;
            var commandAccepted = false;
            var attemptWorld = string.Empty;
            var attemptAetheryte = string.Empty;
            var attemptDestinationLabel = string.Empty;
            var startObservationFailure = string.Empty;
            var preCommandWaitReason = string.Empty;
            var preCommandBusyLogged = false;
            var preCommandTransitionReason = string.Empty;
            var preCommandMismatchIdentity = string.Empty;
            var preCommandMismatchConfirmCount = 0;
            var lastPreCommandMismatchUtc = DateTime.MinValue;
            var destinationWaitLogged = false;
            var busyObservedLogged = false;
            var idleConfirmedLogged = false;
            var safeWaitDeferralLogged = false;
            var idleConfirmCount = 0;
            var lastIdleConfirmUtc = DateTime.MinValue;
            var finalBusyObserved = false;
            var finalBusyObservedLogged = false;
            var finalIdleConfirmCount = 0;
            var lastFinalIdleConfirmUtc = DateTime.MinValue;
            var finalWaitReason = string.Empty;
            var finalTransitionReason = string.Empty;
            var finalMismatchIdentity = string.Empty;
            var finalIdentityMismatchConfirmCount = 0;
            var lastFinalIdentityMismatchUtc = DateTime.MinValue;
            var finalDestinationMismatchKey = string.Empty;
            var finalDestinationMismatchConfirmCount = 0;
            var lastFinalDestinationMismatchUtc = DateTime.MinValue;

            bool ShouldSkipAttempt() => ShouldStopTravel() || attemptFailed;

            void ResetPreCommandIdentityConfirmation()
            {
                ResetStableObservation(
                    ref preCommandMismatchIdentity,
                    ref preCommandMismatchConfirmCount,
                    ref lastPreCommandMismatchUtc);
            }

            void ResetFinalStableVerification()
            {
                ResetStableObservation(
                    ref finalMismatchIdentity,
                    ref finalIdentityMismatchConfirmCount,
                    ref lastFinalIdentityMismatchUtc);
                ResetStableObservation(
                    ref finalDestinationMismatchKey,
                    ref finalDestinationMismatchConfirmCount,
                    ref lastFinalDestinationMismatchUtc);
            }

            void FailAttempt(
                string reason,
                string? observedCharacter = null,
                bool retryThroughPreCommandGate = false)
            {
                if (ShouldStopTravel())
                    return;

                EndTravelFailureObservation();
                ClearXagmanExpectedTravelLogoutWindow();
                attemptFailed = true;
                var currentCharacter = observedCharacter
                    ?? MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                var stillOnExpectedCharacter = currentCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase);
                var identityUnavailable = IsCharacterIdentityUnavailable(currentCharacter);
                var retryAllowed = stillOnExpectedCharacter
                    || retryThroughPreCommandGate;
                if (retryAllowed && capturedAttempt < maxAttempts)
                {
                    retryPending = true;
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S0E68504B72963C28", charName, attemptDestinationLabel, capturedAttempt + 1, maxAttempts));
                    runner.AddLog(stillOnExpectedCharacter
                        ? $"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts} failed: {reason} Still logged into the same character; retrying."
                        : identityUnavailable
                            ? $"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts} failed: {reason} Current identity is unavailable; the next pre-dispatch gate must re-confirm {charName} before another command."
                            : $"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts} failed at a bounded timeout: {reason} Current identity '{currentCharacter}' was not stability-confirmed; the next pre-dispatch gate must re-confirm {charName} before another command.");
                    return;
                }

                travelFailed = true;
                var currentLabel = string.IsNullOrWhiteSpace(currentCharacter) ? "<not logged in>" : currentCharacter;
                runner.AddLog(stillOnExpectedCharacter
                    ? $"Xagman: owner {charName} failed meetup travel after {capturedAttempt}/{maxAttempts} attempts: {reason}"
                    : retryThroughPreCommandGate
                        ? $"Xagman: owner {charName} failed meetup travel after {capturedAttempt}/{maxAttempts} attempts: {reason} The current identity at the bounded timeout was not used as character-change proof."
                        : $"Xagman: owner {charName} meetup travel stopped after attempt {capturedAttempt}/{maxAttempts}: expected the same character, but current is '{currentLabel}'.");
                onFailure();
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (stillOnExpectedCharacter || retryThroughPreCommandGate ? global::XASlave.Localization.UiMessage.Format("SA00700D139356FA3", charName, capturedAttempt, maxAttempts) : global::XASlave.Localization.UiMessage.Format("SB58E59196C2F18E5", charName)));
            }

            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SE0BE42C7F132DCBC", charName, capturedAttempt, maxAttempts),
                ShouldSkip = ShouldStopTravel,
                OnEnter = () =>
                {
                    if (ShouldStopTravel())
                        return;

                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE31C21BAF04885C4", charName));
                },
                IsComplete = () =>
                {
                    if (ShouldStopTravel())
                        return true;
                    if (plugin.IpcClient.LifestreamIsBusy())
                    {
                        ResetPreCommandIdentityConfirmation();
                        preCommandTransitionReason = string.Empty;
                        if (!preCommandBusyLogged)
                        {
                            preCommandBusyLogged = true;
                            runner.AddLog($"Xagman: Lifestream is already busy before owner {charName} meetup dispatch; waiting for idle before acquiring a fresh destination.");
                        }
                        preCommandWaitReason = $"Lifestream remained busy longer than {waitCompleteTimeoutSec:0} seconds before dispatch.";
                        return false;
                    }

                    if (TryGetOwnerTravelTransitionReason(out var transitionReason))
                    {
                        ResetPreCommandIdentityConfirmation();
                        preCommandBusyLogged = false;
                        preCommandWaitReason = $"Owner game state remained transitional before dispatch ({transitionReason}).";
                        xagmanStatus = XagmanStatus.Paused;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S65726AE5E541833F", charName, transitionReason));
                        if (!transitionReason.Equals(preCommandTransitionReason, StringComparison.Ordinal))
                        {
                            preCommandTransitionReason = transitionReason;
                            runner.AddLog($"Xagman: owner {charName} meetup dispatch is waiting without consuming an attempt while game state is transitional ({transitionReason}).");
                        }
                        return false;
                    }

                    var currentCharacter = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (IsCharacterIdentityUnavailable(currentCharacter))
                    {
                        ResetPreCommandIdentityConfirmation();
                        preCommandBusyLogged = false;
                        const string identityUnavailableReason = "owner identity is temporarily unavailable";
                        preCommandWaitReason = $"The {identityUnavailableReason} before dispatch.";
                        xagmanStatus = XagmanStatus.Paused;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S3A4D6388287287A2", charName));
                        if (!identityUnavailableReason.Equals(preCommandTransitionReason, StringComparison.Ordinal))
                        {
                            preCommandTransitionReason = identityUnavailableReason;
                            runner.AddLog($"Xagman: owner {charName} meetup dispatch is waiting without consuming an attempt because the current character identity is unavailable.");
                        }
                        return false;
                    }

                    if (!currentCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        preCommandBusyLogged = false;
                        preCommandTransitionReason = string.Empty;
                        preCommandWaitReason = $"A stable different character remained logged in before dispatch: expected '{charName}', observed '{currentCharacter}'.";
                        var mismatchConfirmed = RecordStableObservation(
                            currentCharacter,
                            ref preCommandMismatchIdentity,
                            ref preCommandMismatchConfirmCount,
                            ref lastPreCommandMismatchUtc);
                        xagmanStatus = XagmanStatus.Paused;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S29273B2C4C49D72A", preCommandMismatchConfirmCount, stableObservationPasses));
                        if (preCommandMismatchConfirmCount == 1)
                            runner.AddLog($"Xagman: expected owner {charName}, but observed '{currentCharacter}' before meetup dispatch; requiring {stableObservationPasses} one-second stable checks before failing.");
                        if (!mismatchConfirmed)
                            return false;

                        FailTravelBeforeCommand($"expected '{charName}', but stable non-empty identity '{currentCharacter}' was observed {stableObservationPasses}/{stableObservationPasses} after Lifestream became idle.");
                        return true;
                    }

                    ResetPreCommandIdentityConfirmation();
                    preCommandBusyLogged = false;
                    preCommandTransitionReason = string.Empty;

                    if (string.IsNullOrWhiteSpace(attemptWorld))
                    {
                        if (!TryCaptureFreshXagmanOwnerMeetDestinationMessage(
                                charName,
                                out attemptWorld,
                                out attemptAetheryte,
                                out var destinationWaitMessage,
                                out var ownerShouldSkip))
                        {
                            preCommandWaitReason = destinationWaitMessage.Original;
                            if (ownerShouldSkip)
                            {
                                EndTravelFailureObservation();
                                ClearXagmanExpectedTravelLogoutWindow();
                                travelFailed = true;
                                runner.AddLog($"Xagman: owner {charName} meetup dispatch skipped without sending a travel command: {preCommandWaitReason}.");
                                onSkip(preCommandWaitReason);
                                return true;
                            }

                            xagmanStatus = XagmanStatus.Paused;
                            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5FDF45FDEE18BE83", charName, destinationWaitMessage));
                            if (!destinationWaitLogged)
                            {
                                destinationWaitLogged = true;
                                runner.AddLog($"Xagman: owner {charName} meetup dispatch is waiting without consuming a travel attempt: {preCommandWaitReason}.");
                            }
                            return false;
                        }
                    }

                    attemptDestinationLabel = GetPrepLogisticsDestinationLabel(attemptWorld, attemptAetheryte);
                    if (!TryValidateXagmanMeetTravelMessage(charName, attemptWorld, out var routeFailure))
                    {
                        if (IsXagmanCollectionFirstRunActive())
                        {
                            ReportXagmanTravelRouteError(
                                global::XASlave.Localization.UiMessage.Format("S477BD60631D8AC31", charName, attemptDestinationLabel, routeFailure));
                        }
                        FailTravelBeforeCommand(
                            $"the pinned destination {attemptDestinationLabel} is not reachable: {routeFailure}");
                        return true;
                    }

                    SetXagmanActiveMeetDestination(attemptWorld, attemptAetheryte);
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SF7AF795DAC6B1B64", charName, attemptDestinationLabel, capturedAttempt, maxAttempts));
                    if (destinationWaitLogged)
                        runner.AddLog($"Xagman: owner {charName} acquired a fresh meetup destination at {attemptDestinationLabel}; beginning travel attempt {capturedAttempt}/{maxAttempts}.");

                    alreadyAtDestination = IsXagmanAtMeetDestination(attemptWorld, attemptAetheryte);
                    if (alreadyAtDestination)
                    {
                        ClearXagmanExpectedTravelLogoutWindow();
                        runner.AddLog($"Xagman: owner {charName} is already at {attemptDestinationLabel}; running CharacterSafeWait and pinned destination verification.");
                        return true;
                    }

                    var command = GetCurrentWorldName().Equals(attemptWorld, StringComparison.OrdinalIgnoreCase)
                        ? attemptAetheryte
                        : $"{attemptWorld}, {attemptAetheryte}";
                    travelFailureToken = ArmXagmanTravelFailure(command);
                    var lobbyResumeStep = runner.CurrentStep + 2; // Init delay, then Wait Start.
                    BeginXagmanLobbyOperation(command, charName, global::XASlave.Localization.UiMessage.Format("S4D8333E003AA236C", charName), false, true,
                        beforeReplay: () =>
                        {
                            sawBusy = false; alreadyAtDestination = false; commandAccepted = true;
                            busyObservedLogged = false; idleConfirmedLogged = false;
                            finalBusyObserved = false; finalIdleConfirmCount = 0;
                            finalIdentityMismatchConfirmCount = 0; finalDestinationMismatchConfirmCount = 0;
                            startObservationFailure = string.Empty;
                            runner.ResumeAtStep(lobbyResumeStep);
                        },
                        completed: () => IsXagmanAtMeetDestination(attemptWorld, attemptAetheryte));
                    commandAccepted = ExecuteXagmanTravelCommandWithExpectedLogout(
                        command,
                        $"owner meetup travel ({charName}, attempt {capturedAttempt}/{maxAttempts})");
                    if (!commandAccepted)
                        startObservationFailure = "The Lifestream command IPC call failed.";
                    runner.AddLog(commandAccepted
                        ? $"Xagman: sent owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts} to pinned destination {attemptDestinationLabel}; waiting for Lifestream busy."
                        : $"Xagman: Lifestream command IPC invocation returned false for owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}; waiting briefly before retry evaluation.");
                    return true;
                },
                TimeoutSec = waitCompleteTimeoutSec,
                OnTimeout = () => FailTravelBeforeCommand(string.IsNullOrWhiteSpace(preCommandWaitReason)
                    ? "a fresh complete meetup destination did not become available before the pre-dispatch timeout."
                    : preCommandWaitReason),
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(
                global::XASlave.Localization.UiMessage.Format("SA22D016700F75801", charName, capturedAttempt, maxAttempts),
                1f,
                ShouldSkipAttempt));
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDC9CBAAFA52FB27D", charName, capturedAttempt, maxAttempts),
                ShouldSkip = () => ShouldSkipAttempt() || alreadyAtDestination || sawBusy,
                IsComplete = () =>
                {
                    if (ShouldSkipAttempt())
                        return true;
                    if (plugin.IpcClient.LifestreamIsBusy())
                    {
                        sawBusy = true;
                        MarkXagmanExpectedTravelLogoutBusyObserved();
                        if (!busyObservedLogged)
                        {
                            busyObservedLogged = true;
                            runner.AddLog($"Xagman: Lifestream busy observed for owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}.");
                        }
                        return true;
                    }
                    if (!IsXagmanAtMeetDestination(attemptWorld, attemptAetheryte))
                        return false;

                    ClearXagmanExpectedTravelLogoutWindow();
                    alreadyAtDestination = true;
                    runner.AddLog($"Xagman: owner {charName} reached pinned destination {attemptDestinationLabel} without a visible Lifestream busy edge on attempt {capturedAttempt}/{maxAttempts}.");
                    return true;
                },
                TimeoutSec = waitStartTimeoutSec,
                OnTimeout = () =>
                {
                    if (ShouldStopTravel())
                        return;
                    ClearXagmanExpectedTravelLogoutWindow();
                    startObservationFailure = commandAccepted
                        ? $"Lifestream never reported busy within {waitStartTimeoutSec:0} seconds."
                        : $"{startObservationFailure} No busy state appeared within {waitStartTimeoutSec:0} seconds.".Trim();
                    runner.AddLog($"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}: {startObservationFailure} Continuing through CharacterSafeWait and destination verification.");
                },
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S34B43DE4CC8147AE", charName, capturedAttempt, maxAttempts),
                ShouldSkip = () => ShouldSkipAttempt() || alreadyAtDestination || !sawBusy,
                IsComplete = () =>
                {
                    if (ShouldSkipAttempt())
                        return true;
                    if (plugin.IpcClient.LifestreamIsBusy())
                    {
                        MarkXagmanExpectedTravelLogoutBusyObserved();
                        return false;
                    }
                    ClearXagmanExpectedTravelLogoutWindow();
                    if (!idleConfirmedLogged)
                    {
                        idleConfirmedLogged = true;
                        runner.AddLog($"Xagman: Lifestream returned idle for owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}; confirming the idle state.");
                    }
                    return true;
                },
                TimeoutSec = waitCompleteTimeoutSec,
                OnTimeout = () => FailAttempt(
                    $"Lifestream remained busy longer than {waitCompleteTimeoutSec:0} seconds.",
                    retryThroughPreCommandGate: true),
            });
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SAF8F6DA4C8051ED8", charName, capturedAttempt, maxAttempts),
                ShouldSkip = () => ShouldSkipAttempt() || alreadyAtDestination || !sawBusy,
                OnEnter = () =>
                {
                    idleConfirmCount = 0;
                    lastIdleConfirmUtc = DateTime.UtcNow;
                },
                IsComplete = () =>
                {
                    if (ShouldSkipAttempt())
                        return true;
                    if ((DateTime.UtcNow - lastIdleConfirmUtc).TotalSeconds < 1)
                        return false;

                    lastIdleConfirmUtc = DateTime.UtcNow;
                    if (plugin.IpcClient.LifestreamIsBusy())
                    {
                        idleConfirmCount = 0;
                        return false;
                    }

                    idleConfirmCount++;
                    if (idleConfirmCount < 3)
                        return false;

                    runner.AddLog($"Xagman: Lifestream idle confirmed 3/3 for owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}; running CharacterSafeWait.");
                    return true;
                },
                TimeoutSec = 20f,
                OnTimeout = () =>
                {
                    if (ShouldSkipAttempt())
                        return;
                    var lifestreamBusy = plugin.IpcClient.LifestreamIsBusy();
                    var ownerTransitioning = TryGetOwnerTravelTransitionReason(out var transitionReason);
                    if (lifestreamBusy || ownerTransitioning)
                    {
                        startObservationFailure = lifestreamBusy
                            ? "Lifestream resumed busy during idle confirmation."
                            : $"Owner state became transitional during idle confirmation ({transitionReason}).";
                        runner.AddLog($"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}: {startObservationFailure} Continuing through CharacterSafeWait and the bounded final verifier without consuming the attempt.");
                        return;
                    }

                    FailAttempt(
                        "Lifestream did not remain idle for three consecutive one-second checks.",
                        retryThroughPreCommandGate: true);
                },
            });
            foreach (var safeWait in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(
                         global::XASlave.Localization.UiMessage.Format("SCF7696A129C306D6", charName, capturedAttempt, maxAttempts),
                         safeWaitTimeoutSec))
            {
                var originalComplete = safeWait.IsComplete;
                steps.Add(new TaskStep
                {
                    NameMessage = safeWait.NameMessage,
                    ShouldSkip = ShouldSkipAttempt,
                    OnEnter = safeWait.OnEnter,
                    IsComplete = () => ShouldSkipAttempt() || originalComplete(),
                    TimeoutSec = safeWait.TimeoutSec,
                    MaxRetries = safeWait.MaxRetries,
                    OnTimeout = () =>
                    {
                        if (ShouldSkipAttempt())
                            return;
                        var lifestreamBusy = plugin.IpcClient.LifestreamIsBusy();
                        var ownerTransitioning = TryGetOwnerTravelTransitionReason(out var transitionReason);
                        if (lifestreamBusy || ownerTransitioning)
                        {
                            startObservationFailure = lifestreamBusy
                                ? "Lifestream became busy during CharacterSafeWait."
                                : $"CharacterSafeWait remained transitional ({transitionReason}).";
                            if (!safeWaitDeferralLogged)
                            {
                                safeWaitDeferralLogged = true;
                                runner.AddLog($"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}: {startObservationFailure} Deferring failure to the bounded final verifier.");
                            }
                            return;
                        }

                        FailAttempt(
                            "CharacterSafeWait did not stabilize after the travel attempt.",
                            retryThroughPreCommandGate: true);
                    },
                });
            }
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDBA9AFAD2275FEDB", charName, capturedAttempt, maxAttempts),
                ShouldSkip = ShouldSkipAttempt,
                OnEnter = () =>
                {
                    finalBusyObserved = false;
                    finalBusyObservedLogged = false;
                    finalIdleConfirmCount = 0;
                    lastFinalIdleConfirmUtc = DateTime.UtcNow;
                    finalWaitReason = "final destination verification did not stabilize";
                    finalTransitionReason = string.Empty;
                    ResetFinalStableVerification();
                },
                IsComplete = () =>
                {
                    if (ShouldSkipAttempt())
                        return true;
                    if (plugin.IpcClient.LifestreamIsBusy())
                    {
                        finalBusyObserved = true;
                        finalIdleConfirmCount = 0;
                        lastFinalIdleConfirmUtc = DateTime.UtcNow;
                        finalWaitReason = "Lifestream remained busy before final destination verification";
                        finalTransitionReason = string.Empty;
                        ResetFinalStableVerification();
                        MarkXagmanExpectedTravelLogoutBusyObserved();
                        if (!finalBusyObservedLogged)
                        {
                            finalBusyObservedLogged = true;
                            runner.AddLog($"Xagman: Lifestream became busy before final destination verification for owner {charName} attempt {capturedAttempt}/{maxAttempts}; waiting for three stable idle checks.");
                        }
                        return false;
                    }

                    if (TryGetOwnerTravelTransitionReason(out var transitionReason))
                    {
                        finalIdleConfirmCount = 0;
                        lastFinalIdleConfirmUtc = DateTime.UtcNow;
                        finalWaitReason = $"owner game state remained transitional ({transitionReason})";
                        ResetFinalStableVerification();
                        xagmanStatus = XagmanStatus.Traveling;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S385E38094C10BE07", charName, transitionReason));
                        if (!transitionReason.Equals(finalTransitionReason, StringComparison.Ordinal))
                        {
                            finalTransitionReason = transitionReason;
                            runner.AddLog($"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts} is still transitional ({transitionReason}); deferring identity and destination verification without consuming an attempt.");
                        }
                        return false;
                    }

                    if (finalBusyObserved)
                    {
                        if ((DateTime.UtcNow - lastFinalIdleConfirmUtc).TotalSeconds < 1)
                            return false;
                        lastFinalIdleConfirmUtc = DateTime.UtcNow;
                        finalIdleConfirmCount++;
                        if (finalIdleConfirmCount < stableObservationPasses)
                            return false;
                        finalBusyObserved = false;
                        finalBusyObservedLogged = false;
                        finalIdleConfirmCount = 0;
                        ResetFinalStableVerification();
                        runner.AddLog($"Xagman: final Lifestream idle confirmed {stableObservationPasses}/{stableObservationPasses} for owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts}; resuming stable identity and destination checks.");
                    }

                    var currentCharacter = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
                    if (IsCharacterIdentityUnavailable(currentCharacter))
                    {
                        finalWaitReason = "owner identity remained temporarily unavailable";
                        ResetFinalStableVerification();
                        xagmanStatus = XagmanStatus.Traveling;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA367883422B49FAA", charName));
                        if (!finalTransitionReason.Equals("identity unavailable", StringComparison.Ordinal))
                        {
                            finalTransitionReason = "identity unavailable";
                            runner.AddLog($"Xagman: owner {charName} meetup travel attempt {capturedAttempt}/{maxAttempts} has no readable current character identity; waiting without consuming an attempt.");
                        }
                        return false;
                    }

                    if (!currentCharacter.Equals(charName, StringComparison.OrdinalIgnoreCase))
                    {
                        ResetStableObservation(
                            ref finalDestinationMismatchKey,
                            ref finalDestinationMismatchConfirmCount,
                            ref lastFinalDestinationMismatchUtc);
                        finalWaitReason = $"a different character remained logged in: expected '{charName}', observed '{currentCharacter}'";
                        var identityMismatchConfirmed = RecordStableObservation(
                            currentCharacter,
                            ref finalMismatchIdentity,
                            ref finalIdentityMismatchConfirmCount,
                            ref lastFinalIdentityMismatchUtc);
                        xagmanStatus = XagmanStatus.Traveling;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S401D706A119A3655", finalIdentityMismatchConfirmCount, stableObservationPasses));
                        if (finalIdentityMismatchConfirmCount == 1)
                            runner.AddLog($"Xagman: owner meetup verification expected {charName}, but observed '{currentCharacter}'; requiring {stableObservationPasses} one-second stable checks before failing.");
                        if (!identityMismatchConfirmed)
                            return false;

                        FailAttempt(
                            $"destination verification observed stable non-empty character '{currentCharacter}' instead of intended owner '{charName}' ({stableObservationPasses}/{stableObservationPasses}).",
                            currentCharacter);
                        return true;
                    }

                    ResetStableObservation(
                        ref finalMismatchIdentity,
                        ref finalIdentityMismatchConfirmCount,
                        ref lastFinalIdentityMismatchUtc);
                    finalTransitionReason = string.Empty;

                    if (!IsXagmanAtMeetDestination(attemptWorld, attemptAetheryte))
                    {
                        var destinationMismatchKey = $"{currentCharacter}|{attemptWorld}|{attemptAetheryte}";
                        finalWaitReason = $"owner remained outside pinned destination {attemptDestinationLabel}";
                        var destinationMismatchConfirmed = RecordStableObservation(
                            destinationMismatchKey,
                            ref finalDestinationMismatchKey,
                            ref finalDestinationMismatchConfirmCount,
                            ref lastFinalDestinationMismatchUtc);
                        xagmanStatus = XagmanStatus.Traveling;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S258E3483C1689477", charName, attemptDestinationLabel, finalDestinationMismatchConfirmCount, stableObservationPasses));
                        if (finalDestinationMismatchConfirmCount == 1)
                            runner.AddLog($"Xagman: owner {charName} is not yet at pinned destination {attemptDestinationLabel}; requiring {stableObservationPasses} one-second stable checks before consuming attempt {capturedAttempt}/{maxAttempts}.");
                        if (!destinationMismatchConfirmed)
                            return false;

                        var prefix = string.IsNullOrWhiteSpace(startObservationFailure)
                            ? string.Empty
                            : $"{startObservationFailure} ";
                        FailAttempt(
                            $"{prefix}CharacterSafeWait completed and the same owner remained outside pinned destination {attemptDestinationLabel} for {stableObservationPasses}/{stableObservationPasses} stable checks.",
                            currentCharacter);
                        return true;
                    }

                    ResetStableObservation(
                        ref finalDestinationMismatchKey,
                        ref finalDestinationMismatchConfirmCount,
                        ref lastFinalDestinationMismatchUtc);
                    travelComplete = true;
                    EndTravelFailureObservation();
                    ClearXagmanExpectedTravelLogoutWindow();
                    runner.AddLog($"Xagman: owner {charName} meetup travel confirmed at pinned destination {attemptDestinationLabel} on attempt {capturedAttempt}/{maxAttempts}.");
                    onReady();
                    return true;
                },
                TimeoutSec = waitCompleteTimeoutSec,
                OnTimeout = () =>
                {
                    if (ShouldStopTravel())
                        return;
                    FailAttempt(
                        $"{finalWaitReason} within the {waitCompleteTimeoutSec:0}-second final verification timeout.",
                        retryThroughPreCommandGate: true);
                },
            });
            steps.Add(MonthlyReloggerTask.MakeDelay(
                global::XASlave.Localization.UiMessage.Format("S351D046B7AB3F115", charName, capturedAttempt, maxAttempts),
                2f,
                () => ShouldStopTravel() || !retryPending));
        }
    }

    private void AddXagmanTeleportSteps(List<TaskStep> steps, global::XASlave.Localization.UiMessage label, Func<string> commandProvider, TaskRunner runner, Func<bool>? alreadyThere, bool allowNoBusy, SysAction onEnter, SysAction onReady, SysAction onTimeout, Func<bool>? externalSkip = null, float waitStartTimeoutSec = 0f, bool reissueWhileWaiting = false, bool expectCrossDataCenterLogout = false, Func<string>? travelSourceCharacterProvider = null, Func<string>? travelDestinationWorldProvider = null)
    {
        var skipTeleport = false;
        var sawBusy = false;
        var teleportFailed = false;
        var lastReissueUtc = DateTime.MinValue;
        var lobbyWaitStartStep = -1;
        long travelFailureToken = 0;

        void EndTravelFailureObservation()
        {
            EndXagmanLobbyOperation();
            EndXagmanTravelFailure(travelFailureToken);
            travelFailureToken = 0;
        }

        void ClearExpectedTravelLogout()
        {
            if (expectCrossDataCenterLogout)
                ClearXagmanExpectedTravelLogoutWindow();
        }

        bool ShouldExternalSkip()
        {
            var shouldSkip = externalSkip?.Invoke() ?? false;
            if (shouldSkip)
            {
                EndTravelFailureObservation();
                ClearExpectedTravelLogout();
            }
            return shouldSkip;
        }

        bool ShouldStopTeleport()
        {
            if (teleportFailed || ShouldExternalSkip())
                return true;
            if (!TryTakeXagmanTravelFailure(travelFailureToken, runner, label.Original))
                return false;

            teleportFailed = true;
            EndTravelFailureObservation();
            ClearExpectedTravelLogout();
            onTimeout();
            return true;
        }

        void ExecuteTeleportCommand()
        {
            if (ShouldStopTeleport())
                return;
            if (travelSourceCharacterProvider != null && travelDestinationWorldProvider != null)
            {
                var sourceCharacter = travelSourceCharacterProvider();
                var destinationWorld = travelDestinationWorldProvider();
                if (!TryValidateXagmanMeetTravelMessage(sourceCharacter, destinationWorld, out var routeFailure))
                {
                    EndTravelFailureObservation();
                    ClearExpectedTravelLogout();
                    teleportFailed = true;
                    runner.AddLog(
                        $"Xagman: {label} travel command rejected before Lifestream IPC for {sourceCharacter}: {routeFailure}");
                    onTimeout();
                    return;
                }
            }

            var command = commandProvider();
            lastReissueUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(command))
            {
                EndTravelFailureObservation();
                ClearExpectedTravelLogout();
                return;
            }

            travelFailureToken = ArmXagmanTravelFailure(command);
            var lobbyResumeStep = lobbyWaitStartStep;
            BeginXagmanLobbyOperation(command, MonthlyReloggerTask.GetCurrentCharacterNameWorld(), label, false, expectCrossDataCenterLogout,
                beforeReplay: () => { sawBusy = false; skipTeleport = false; lastReissueUtc = DateTime.UtcNow; runner.ResumeAtStep(lobbyResumeStep); },
                completed: alreadyThere);
            if (expectCrossDataCenterLogout)
                ExecuteXagmanTravelCommandWithExpectedLogout(command, $"Xagman Lifestream operation ({label})");
            else
                plugin.IpcClient.LifestreamExecuteCommand(command);
        }

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S34012710CCD6AF86", label),
            OnEnter = () =>
            {
                lobbyWaitStartStep = runner.CurrentStep + 1;
                onEnter();
                sawBusy = false;
                skipTeleport = alreadyThere?.Invoke() ?? false;
                teleportFailed = false;
                if (ShouldExternalSkip())
                {
                    skipTeleport = true;
                    return;
                }
                if (!skipTeleport)
                    ExecuteTeleportCommand();
                else
                {
                    EndTravelFailureObservation();
                    ClearExpectedTravelLogout();
                }
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SAEC541F0F03CF2C5", label),
            ShouldSkip = () => ShouldStopTeleport() || skipTeleport,
            IsComplete = () =>
            {
                if (ShouldStopTeleport())
                {
                    skipTeleport = true;
                    return true;
                }
                if (plugin.IpcClient.LifestreamIsBusy())
                {
                    sawBusy = true;
                    if (expectCrossDataCenterLogout)
                        MarkXagmanExpectedTravelLogoutBusyObserved();
                    return true;
                }
                if (alreadyThere?.Invoke() ?? false)
                {
                    skipTeleport = true;
                    ClearExpectedTravelLogout();
                    return true;
                }
                // Long meet-travel waits (Tony still logging in / DC traveling) can outlast a single
                // Lifestream command that was dropped right after login; re-issue periodically.
                if (reissueWhileWaiting && (DateTime.UtcNow - lastReissueUtc).TotalSeconds >= 10)
                    ExecuteTeleportCommand();
                return false;
            },
            TimeoutSec = waitStartTimeoutSec > 0f ? waitStartTimeoutSec : (allowNoBusy ? 15f : 45f),
            OnTimeout = () =>
            {
                if (ShouldStopTeleport())
                {
                    skipTeleport = true;
                    return;
                }
                ClearExpectedTravelLogout();
                if (allowNoBusy || (alreadyThere?.Invoke() ?? false))
                {
                    skipTeleport = true;
                    return;
                }
                EndTravelFailureObservation();
                teleportFailed = true;
                onTimeout();
            },
        });
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S7A2538F7ED59B58F", label),
            ShouldSkip = () => ShouldStopTeleport() || (skipTeleport && !sawBusy),
            IsComplete = () =>
            {
                if (ShouldStopTeleport())
                    return true;
                if (alreadyThere?.Invoke() ?? false)
                {
                    ClearExpectedTravelLogout();
                    return true;
                }
                var busy = plugin.IpcClient.LifestreamIsBusy();
                if (busy)
                {
                    sawBusy = true;
                    if (expectCrossDataCenterLogout)
                        MarkXagmanExpectedTravelLogoutBusyObserved();
                    return false;
                }
                if (!sawBusy)
                {
                    if (allowNoBusy)
                    {
                        skipTeleport = true;
                        return true;
                    }
                    return false;
                }
                ClearExpectedTravelLogout();
                return alreadyThere == null;
            },
            TimeoutSec = 600f,
            OnTimeout = () =>
            {
                if (ShouldStopTeleport())
                    return;
                ClearExpectedTravelLogout();
                if (allowNoBusy || (alreadyThere?.Invoke() ?? false))
                {
                    skipTeleport = true;
                    return;
                }
                EndTravelFailureObservation();
                teleportFailed = true;
                onTimeout();
            },
        });
        foreach (var safeWait in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("S67F3D579BA0E1FF2", label), 30f))
        {
            var originalComplete = safeWait.IsComplete;
            steps.Add(new TaskStep
            {
                NameMessage = safeWait.NameMessage,
                ShouldSkip = ShouldStopTeleport,
                OnEnter = safeWait.OnEnter,
                IsComplete = () => ShouldStopTeleport() || originalComplete(),
                TimeoutSec = safeWait.TimeoutSec,
                MaxRetries = safeWait.MaxRetries,
                OnTimeout = () =>
                {
                    if (ShouldStopTeleport())
                        return;
                    EndTravelFailureObservation();
                    ClearExpectedTravelLogout();
                    teleportFailed = true;
                    onTimeout();
                },
            });
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S05EC04F98904BFCE", label),
            ShouldSkip = ShouldStopTeleport,
            OnEnter = () =>
            {
                if (ShouldStopTeleport())
                    return;
                EndTravelFailureObservation();
                ClearExpectedTravelLogout();
                onReady();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
    }

    private void UpdateXagmanFrameworkTick()
    {
        UpdateXagmanTeleportHelperRequirement();
        UpdateXagmanCustomMovementOwnership();
        UpdateXagmanMiniSnapshot();
        ProcessXagmanRelogDatabasePull();
        if (xagmanRunning && xagmanArMultiGuardActive
            && Environment.TickCount64 >= xagmanArMultiNextCheck)
        {
            xagmanArMultiNextCheck = Environment.TickCount64 + 250;
            if (!TryKeepXagmanArMultiDisabledMessage(false, out var reason))
            {
                plugin.TaskRunner.AddLog($"Xagman: AutoRetainer Multi guard failed: {reason}; stopping the local run.");
                StopXagmanTask();
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S53C90BB90A1CEC19", reason));
                PublishXagmanPresence();
                return;
            }
        }
        if (xagmanLobbyOperation is { } lobbyOp
            && (plugin.TaskRunner.CurrentRunId != lobbyOp.RunId || (lobbyOp.RunnerOwned && !plugin.TaskRunner.IsRunning)))
            EndXagmanLobbyOperation();
        // Completion receipts remain live even while lobby recovery owns task dispatch.
        ObserveXagmanOwnerClientCompletions();
        var lobbyRecoveryOwnsTick = xagmanLobbyOperation != null && !plugin.TaskRunner.IsRunning && PollXagmanLobbyRecovery();
        if (lobbyRecoveryOwnsTick || xagmanLobbyOperation?.Pending == true)
        {
            // Recovery gates task actions, not the heartbeat peers use to retain this client.
            if (!plugin.Configuration.XagmanOutsideNetworkHelper && (DateTime.UtcNow - xagmanLastPresencePublishUtc).TotalSeconds >= 1)
                PublishXagmanPresence();
            UpdatePriorityTaskExternalStatus();
            return;
        }
        var totalStopwatch = Stopwatch.StartNew();
        if (!string.IsNullOrWhiteSpace(xagmanExpectedTravelLogoutContext)
            && IsXagmanExpectedTravelLogoutWindowActive()
            && plugin.IpcClient.LifestreamIsBusy())
        {
            xagmanExpectedTravelLogoutSawBusy = true;
        }
        MeasureFrameworkUpdateStep("Xagman.ProcessPendingMatchSelection", ProcessXagmanPendingMatchSelection);
        MeasureFrameworkUpdateStep("Xagman.UpdateTradeCapacityForecast", UpdateXagmanTradeCapacityForecast);
        var capacityRecoveryOwnsTick = xagmanRunning && !plugin.Configuration.XagmanOutsideNetworkHelper
            && UpdateXagmanCapacityRecovery();
        if (capacityRecoveryOwnsTick)
        {
            // Recovery keeps publishing scoped progress while normal task dispatch is held.
        }
        else if (xagmanRunning && plugin.Configuration.XagmanOutsideNetworkHelper)
        {
            // Outside Network Helper has no peer network; it drives its own self-hosted state
            // machine and never publishes presence.
            MeasureFrameworkUpdateStep("Xagman.UpdateOnhRuntime", UpdateXagmanOnhRuntime);
        }
        else if (xagmanRunning && xagmanActiveRole == XagmanRole.Tony && !plugin.TaskRunner.IsRunning)
            MeasureFrameworkUpdateStep("Xagman.UpdateTonyRuntime", UpdateXagmanTonyRuntime);
        else if (xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner && !plugin.TaskRunner.IsRunning)
        {
            var startedRestock = false;
            MeasureFrameworkUpdateStep(
                "Xagman.StartCollectionFirstOwnerRestock",
                () => startedRestock = TryStartXagmanCollectionFirstOwnerRestockPhase());
            if (!startedRestock)
            {
                MeasureFrameworkUpdateStep("Xagman.UpdateOwnerSupplyCycle", UpdateXagmanOwnerSupplyCycle);
                if (!xagmanOwnerSupplyWaiting)
                    MeasureFrameworkUpdateStep("Xagman.ResumeStandbyOwnerOnTonyCall", () => TryResumeXagmanOwnerStandbyFromCallingTony());
            }
        }
        var publishInterval = xagmanRunning ? 1.0 : 5.0;
        if ((!plugin.Configuration.XagmanOutsideNetworkHelper)
            && (DateTime.UtcNow - xagmanLastPresencePublishUtc).TotalSeconds >= publishInterval)
            MeasureFrameworkUpdateStep("Xagman.PublishPresence", PublishXagmanPresence);
        MeasureFrameworkUpdateStep("Xagman.UpdatePriorityTaskExternalStatus", UpdatePriorityTaskExternalStatus);
        totalStopwatch.Stop();
        LogFrameworkUpdateStepDuration("Xagman.UpdateXagmanFrameworkTick", totalStopwatch.Elapsed.TotalMilliseconds);
    }

    private bool TryResumeXagmanOwnerStandbyFromCallingTony()
    {
        if (!xagmanOwnerStandbyPending || string.IsNullOrWhiteSpace(xagmanActiveCharacter))
            return false;
        if (!xagmanOwnerStandbyPriorTonyCallReleased
            && IsXagmanStandbyPriorTonyCallObservedReleased())
        {
            // The old explicit call has disappeared. A later call from the same Tony identity is now
            // a fresh post-sell/post-rotation handoff and must not remain permanently blacklisted.
            xagmanOwnerStandbyPriorTonyCallReleased = true;
        }
        var excludedTonyCharacter = xagmanOwnerStandbyPriorTonyCallReleased
            ? string.Empty
            : xagmanOwnerStandbyTonyCharacter;
        var excludedTonyInstanceId = xagmanOwnerStandbyPriorTonyCallReleased
            ? string.Empty
            : xagmanOwnerStandbyTonyInstanceId;
        if (!TryAdoptXagmanCallingTonyPeer(
                xagmanActiveCharacter,
                out _,
                excludedTonyCharacter,
                excludedTonyInstanceId))
            return false;

        var callingTony = xagmanActiveTradePartner;
        xagmanOwnerStartRequested = true;
        if (StartXagmanFranchiseTask(true, true))
        {
            plugin.TaskRunner.AddLog($"Xagman: resumed standby owner {xagmanActiveCharacter} from Tony {callingTony}'s fresh persistent explicit call.");
            return true;
        }
        return false;
    }

    private bool IsXagmanStandbyPriorTonyCallObservedReleased()
    {
        if (string.IsNullOrWhiteSpace(xagmanOwnerStandbyTonyCharacter)
            || string.IsNullOrWhiteSpace(xagmanActiveCharacter))
        {
            return false;
        }

        var priorTonyPeers = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.ActiveCharacter.Equals(xagmanOwnerStandbyTonyCharacter, StringComparison.OrdinalIgnoreCase))
            .Where(peer => string.IsNullOrWhiteSpace(xagmanOwnerStandbyTonyInstanceId)
                || peer.InstanceId.Equals(xagmanOwnerStandbyTonyInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (priorTonyPeers.Count == 0)
            return false;

        // Require positive fresh peer evidence that the prior call was cleared. A transient hub gap
        // cannot unlock the old identity and recreate the cancel/resume race.
        return priorTonyPeers.All(peer => (peer.SupplyDrainOnly
                && peer.InstanceId == xagmanOwnerSupplyTonyInstance
                && peer.SupplyPassId == xagmanOwnerSupplyPassId
                && peer.SupplyInventoryComplete)
            || !peer.ActiveTradePartner.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
            || !peer.ActiveTradePartnerInstanceId.Equals(plugin.InstanceId, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryReassertXagmanTonyMeetup()
    {
        const int maxMeetRetries = 3;
        const double retryCooldownSeconds = 3.0;
        const double crossWorldCommandTimeoutSeconds = 600.0;
        const double targetWorldSettleTimeoutSeconds = 60.0;

        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return false;
        if (xagmanObservedDropboxBusy || plugin.IpcClient.DropboxIsBusy())
            return false;
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return false;

        if (TryTakeXagmanTravelFailure(xagmanTravelFailureToken, plugin.TaskRunner, "Tony meetup runtime reassert"))
        {
            xagmanTonyCharacterFailurePending = true;
            AdvanceXagmanTonyAfterCharacterFailure();
            return true;
        }

        var meetWorld = GetXagmanActiveMeetWorld();
        var meetAetheryte = GetXagmanActiveMeetAetheryte();
        if (string.IsNullOrWhiteSpace(meetWorld))
        {
            EndXagmanTravelFailure(xagmanTravelFailureToken);
            return false;
        }
        if (!TryValidateXagmanMeetTravelMessage(xagmanActiveCharacter, meetWorld, out var routeFailure))
        {
            EndXagmanTravelFailure(xagmanTravelFailureToken);
            ReportXagmanTravelRouteError(
                global::XASlave.Localization.UiMessage.Format("SE0E99FF6B3FCA4DB", xagmanActiveCharacter, GetXagmanActiveMeetDestinationLabel(), routeFailure));
            return true;
        }

        xagmanStatus = XagmanStatus.Traveling;
        if (plugin.IpcClient.LifestreamIsBusy())
        {
            MarkXagmanExpectedTravelLogoutBusyObserved();
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE18403529F1F8BFF", xagmanActiveCharacter, GetXagmanActiveMeetDestinationLabel()));
            return true;
        }

        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
        {
            xagmanStatusText =
                global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE84A9AB43765D15C", xagmanActiveCharacter, GetXagmanActiveMeetDestinationLabel()));
            return true;
        }

        if (IsXagmanAtMeetDestination(meetWorld, meetAetheryte))
        {
            EndXagmanTravelFailure(xagmanTravelFailureToken);
            ClearXagmanExpectedTravelLogoutWindow();
            ResetXagmanTonyMeetRetryState();
            if (ReassertXagmanCustomMeetingPoint())
                return true;

            // A supply barrier can return Blocked while owners are still approaching.
            // Advertise readiness here so those owners can queue and finish their pass.
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S40666FC97D6B7A07", xagmanActiveCharacter));
            return false;
        }

        var now = DateTime.UtcNow;
        if (xagmanTonyMeetCommandPhase == XagmanTonyMeetCommandPhase.AwaitingTargetWorld
            && GetCurrentWorldName().Equals(meetWorld, StringComparison.OrdinalIgnoreCase))
        {
            xagmanTonyMeetCommandPhase = XagmanTonyMeetCommandPhase.SettlingTargetWorld;
            xagmanTonyMeetCommandDeadlineUtc = now.AddSeconds(targetWorldSettleTimeoutSeconds);
            plugin.TaskRunner.AddLog(
                $"Xagman: Tony {xagmanActiveCharacter} reached target world {meetWorld} in a safe readable state; preserving the accepted compound command for a fresh {targetWorldSettleTimeoutSeconds:0}-second local-teleport settling window.");
        }

        if (xagmanTonyMeetCommandPhase != XagmanTonyMeetCommandPhase.None)
        {
            if (now < xagmanTonyMeetCommandDeadlineUtc)
            {
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (xagmanTonyMeetCommandPhase == XagmanTonyMeetCommandPhase.AwaitingTargetWorld ? global::XASlave.Localization.UiMessage.Format("S22F78694ED6B4B85", xagmanActiveCharacter, meetWorld) : global::XASlave.Localization.UiMessage.Format("SDD634685803B5C49", xagmanActiveCharacter, GetXagmanActiveMeetDestinationLabel())));
                return true;
            }

            plugin.TaskRunner.AddLog(
                $"Xagman: Tony {xagmanActiveCharacter}'s accepted meetup command did not reach {GetXagmanActiveMeetDestinationLabel()} within its bounded {xagmanTonyMeetCommandPhase} window; retry evaluation may continue.");
            xagmanTonyMeetCommandPhase = XagmanTonyMeetCommandPhase.None;
            xagmanTonyMeetCommandDeadlineUtc = DateTime.MinValue;
        }

        if ((now - xagmanTonyLastMeetRetryUtc).TotalSeconds < retryCooldownSeconds)
        {
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S6AECB6D68030C6ED", xagmanActiveCharacter, GetXagmanActiveMeetDestinationLabel()));
            return true;
        }

        if (xagmanTonyMeetRetryCount >= maxMeetRetries)
        {
            var failedRetryCount = xagmanTonyMeetRetryCount;
            ClearXagmanExpectedTravelLogoutWindow();
            ResetXagmanTonyMeetRetryState();
            plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} failed meetup recheck {failedRetryCount} times; rotating Tony.");
            RotateXagmanTony();
            return true;
        }

        var destinationCommand = GetXagmanActiveMeetDestinationCommand();
        if (string.IsNullOrWhiteSpace(destinationCommand))
        {
            EndXagmanTravelFailure(xagmanTravelFailureToken);
            ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        xagmanTonyLastMeetRetryUtc = now;
        xagmanTonyMeetRetryCount++;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9AAFD140A2D6793D", xagmanActiveCharacter, GetXagmanActiveMeetDestinationLabel(), xagmanTonyMeetRetryCount, maxMeetRetries));
        plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} is not at the meet spot; retrying Lifestream command ({xagmanTonyMeetRetryCount}/{maxMeetRetries}).");
        ArmXagmanTravelFailure(destinationCommand);
        BeginXagmanLobbyOperation(destinationCommand, xagmanActiveCharacter, global::XASlave.Localization.UiMessage.Key("S81B5F59C025D3C84"), false, true,
            beforeReplay: () => { xagmanTonyLastMeetRetryUtc = DateTime.UtcNow; xagmanTonyMeetCommandDeadlineUtc = DateTime.UtcNow.AddSeconds(crossWorldCommandTimeoutSeconds); },
            completed: () => IsXagmanAtMeetDestination(meetWorld, meetAetheryte));
        var commandAccepted = ExecuteXagmanTravelCommandWithExpectedLogout(
            destinationCommand,
            "Tony meetup runtime reassert");
        if (!commandAccepted)
        {
            EndXagmanTravelFailure(xagmanTravelFailureToken);
            plugin.TaskRunner.AddLog($"Xagman: Lifestream did not accept Tony {xagmanActiveCharacter}'s meetup reassert command.");
        }
        else if (!string.IsNullOrWhiteSpace(meetAetheryte))
        {
            var alreadyOnTargetWorld = GetCurrentWorldName().Equals(meetWorld, StringComparison.OrdinalIgnoreCase);
            xagmanTonyMeetCommandPhase = alreadyOnTargetWorld
                ? XagmanTonyMeetCommandPhase.SettlingTargetWorld
                : XagmanTonyMeetCommandPhase.AwaitingTargetWorld;
            xagmanTonyMeetCommandDeadlineUtc = now.AddSeconds(
                alreadyOnTargetWorld ? targetWorldSettleTimeoutSeconds : crossWorldCommandTimeoutSeconds);
            plugin.TaskRunner.AddLog(alreadyOnTargetWorld
                ? $"Xagman: accepted Tony meetup command is protected by a {targetWorldSettleTimeoutSeconds:0}-second local-teleport settling window."
                : $"Xagman: accepted Tony meetup command is protected by a {crossWorldCommandTimeoutSeconds:0}-second cross-world completion window before any retry.");
        }
        return true;
    }

    private void UpdateXagmanTonyRuntime()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony)
            return;
        if (IsXagmanSupplyCycleActive() && string.IsNullOrWhiteSpace(xagmanSupplyPassId) && !xagmanSweepAwaitingStart) BeginXagmanTonySupplyPass();
        if (xagmanTonyCharacterFailurePending)
        {
            AdvanceXagmanTonyAfterCharacterFailure();
            return;
        }
        if (xagmanStatus == XagmanStatus.Error)
            return;
        if (UpdateXagmanTonySupplyDrain()) return;
        ObserveXagmanCollectionFirstPhaseAcknowledgements();
        if (xagmanSweepAwaitingStart)
        {
            if (IsXagmanCollectionFirstCollectionPhase()
                && TryAdvanceXagmanCollectionFirstTonyToRestock())
            {
                return;
            }
            if (IsXagmanCollectionFirstRestockPhase()
                && AreAllExpectedXagmanFranchiseOwnersAcknowledged(XagmanRunPhase.Restock))
            {
                xagmanSweepAwaitingStart = false;
                StartXagmanTonyCompletionTask(
                    string.Empty,
                    autoDetectedNoRemainingOwners: true,
                    completedWithWarnings: false,
                    broadcastPeerCompletion: true);
                return;
            }
            if (IsXagmanCollectionFirstCollectionPhase())
                UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase.Collection);
            else if (IsXagmanCollectionFirstRestockPhase())
                UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase.Restock);
            if (xagmanStatus == XagmanStatus.Error)
                return;
            TryBeginXagmanSweepFromDiscovery();
            return;
        }
        var liveRelevantOwnerPeers = GetXagmanRelevantOwnerPeersForTony(xagmanActiveCharacter, enabledOnly: true, freshOnly: true);
        UpdateXagmanTonyOwnerDisconnectCompletionState(liveRelevantOwnerPeers);
        if (TryReassertXagmanTonyMeetup())
            return;
        if (IsXagmanSupplyCycleActive())
        {
            if (plugin.IpcClient.DropboxIsBusy() || AddonHelper.IsAddonVisible("Trade"))
                xagmanSupplyInventoryUnreadableSinceUtc = DateTime.MinValue;
            else if (!ObserveXagmanSupplyCohort() || HoldXagmanSupplyDeferral()) return;
            UpdateXagmanDepletedSupplyPass();
        }
        var queue = GetXagmanQueueForTony(xagmanActiveCharacter);
        if (!xagmanTonyObservedOwnerWork)
        {
            var relevantOwnerPeers = GetXagmanRelevantOwnerPeersForTony(xagmanActiveCharacter);
            if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
                || queue.Count > 0
                || relevantOwnerPeers.Any(peer => (peer.TonyCompletionRequestedAtUtc > DateTime.MinValue && peer.TonyCompletionRequestedAtUtc >= xagmanTonyRunStartedAtUtc)
                    || peer.QueueRequestedAtUtc > DateTime.MinValue
                    || peer.Status is XagmanStatus.ReadyForQueue or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.Called or XagmanStatus.Trading or XagmanStatus.Standby or XagmanStatus.Paused or XagmanStatus.Completed
                    || peer.TotalCharacters > 0
                    || peer.CompletedCharacters > 0
                    || (peer.RequestedItems?.Count ?? 0) > 0))
                xagmanTonyObservedOwnerWork = true;
        }
        var busy = plugin.IpcClient.DropboxIsBusy();
        if (busy)
        {
            xagmanObservedDropboxBusy = true;
            xagmanStatus = XagmanStatus.Trading;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (string.IsNullOrWhiteSpace(xagmanActiveTradePartner) ? global::XASlave.Localization.UiMessage.Format("SBE998526C2478BAE", xagmanActiveCharacter) : global::XASlave.Localization.UiMessage.Format("SAE6D33BC8AA639F7", xagmanActiveCharacter, xagmanActiveTradePartner)));
            return;
        }
        if (xagmanObservedDropboxBusy)
        {
            if (!xagmanSupplyDrainOnly && !IsXagmanCapacityDrainActive() && TryBeginXagmanTonySupplyDrain()) return;
            xagmanObservedDropboxBusy = false;
            if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            {
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S4FAC203B3E9E945A", xagmanActiveTradePartner)))
                    return;
                ClearXagmanDropbox();
                plugin.TaskRunner.AddLog($"Xagman: Dropbox trade queue ended for {xagmanActiveTradePartner}; waiting for owner inventory reconciliation before treating the handoff as complete.");
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
            }
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD2D8EED3E8AF55B9", xagmanActiveCharacter));
        }
        var ownerStandbyRotationReady = TryObserveXagmanOwnerStandbyRotationRequest();
        if (ownerStandbyRotationReady == false)
            return;
        if (TryRotateXagmanTonyForPendingOwnerStandbyRequest())
            return;
        if (!xagmanSupplyDrainOnly && !IsXagmanCapacityDrainActive() && TryBeginXagmanTonySupplyDrain()) return;
        if (TryReleaseXagmanTonyStalePartner())
        {
            if (!xagmanRunning)
                return;
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SD2D8EED3E8AF55B9", xagmanActiveCharacter));
        }
        var allOwnersReloggingIdle = string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
            && xagmanTonyObservedOwnerWork
            && queue.Count == 0
            && liveRelevantOwnerPeers.Count > 0
            && liveRelevantOwnerPeers.All(peer => peer.Status == XagmanStatus.Relogging);
        if (!allOwnersReloggingIdle)
        {
            xagmanTonyOpportunisticSellArmed = true;
        }
        else if (xagmanTonyOpportunisticSellArmed)
        {
            // All Franchise Owners are relogging with none ready to trade; use the idle window to sell Tony's inventory.
            xagmanTonyOpportunisticSellArmed = false;
            if (plugin.Configuration.XagmanSellWhenInventoryFull && !IsXagmanSupplyCycleActive()
                && !IsXagmanCollectionFirstRunActive())
            {
                if (GetXagmanLiveLocalItemQuantity(1, false) >= XagmanTonySellGilLimit)
                {
                    // Already at the gil cap during idle selling: run the same return-home/relog-next-Tony rotation the
                    // normal Sell When Inventory Is Full flow uses (the next Tony then travels back to the meet spot and resumes).
                    StartXagmanTonyFullInventoryFallback(string.Empty, $"Xagman: Tony {xagmanActiveCharacter} is at the gil cap during idle selling; using normal Tony full-inventory behavior to rotate to the next Tony.");
                    return;
                }
                // Mid-sell gil cap is handled inside the sell task, which routes through the same full-inventory rotation fallback.
                if (TryStartXagmanTonySellWhenInventoryFull(string.Empty))
                    return;
            }
        }
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
        {
            var activePartnerPeer = GetXagmanActiveTradeOwnerPeer();
            var activePartnerRequestedItems = activePartnerPeer?.RequestedItems == null
                ? new List<XagmanTradeRequestEntry>()
                : CloneXagmanTradeRequests(activePartnerPeer.RequestedItems);
            if (!xagmanObservedDropboxBusy && activePartnerRequestedItems.Count > 0)
            {
                if (IsXagmanCollectionFirstCollectionPhase())
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SFBE011F169819B63", xagmanActiveTradePartner));
                    plugin.TaskRunner.AddLog(
                        $"Xagman: protocol/state error - owner {xagmanActiveTradePartner} advertised Tony supply requests during collection; requests were not dispatched.");
                    PublishXagmanPresence();
                    return;
                }
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA0D95621B5D6C302", xagmanActiveCharacter, xagmanActiveTradePartner));
                if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
                    return;
                if (activePartnerPeer != null)
                    xagmanActiveTradePartnerInstanceId = activePartnerPeer.InstanceId;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                var activePartner = xagmanActiveTradePartner;
                if (StartXagmanTonyTrade(activePartnerRequestedItems, activePartnerPeer))
                    plugin.TaskRunner.AddLog($"Xagman: Tony resumed active owner {activePartner} for {activePartnerRequestedItems.Count} requested supply entr{(activePartnerRequestedItems.Count == 1 ? "y" : "ies")}.");
                return;
            }
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA4DAFB67A8EB6D73", xagmanActiveCharacter, xagmanActiveTradePartner));
            return;
        }
        var inFlightOwner = GetXagmanInFlightOwnerForTony(xagmanActiveCharacter);
        if (inFlightOwner != null)
        {
            var inFlightRequestedItems = inFlightOwner.RequestedItems == null
                ? new List<XagmanTradeRequestEntry>()
                : CloneXagmanTradeRequests(inFlightOwner.RequestedItems);
            if (inFlightRequestedItems.Count > 0)
            {
                if (IsXagmanCollectionFirstCollectionPhase())
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SFBE011F169819B63", inFlightOwner.ActiveCharacter));
                    plugin.TaskRunner.AddLog(
                        $"Xagman: protocol/state error - owner {inFlightOwner.ActiveCharacter} advertised Tony supply requests during collection; requests were not dispatched.");
                    PublishXagmanPresence();
                    return;
                }
                xagmanStatus = XagmanStatus.Called;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SA0D95621B5D6C302", xagmanActiveCharacter, inFlightOwner.ActiveCharacter));
                if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
                    return;
                xagmanActiveTradePartner = inFlightOwner.ActiveCharacter;
                xagmanActiveTradePartnerInstanceId = inFlightOwner.InstanceId;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                if (StartXagmanTonyTrade(inFlightRequestedItems, inFlightOwner))
                    plugin.TaskRunner.AddLog($"Xagman: Tony resumed {inFlightOwner.ActiveCharacter} for {inFlightRequestedItems.Count} requested supply entr{(inFlightRequestedItems.Count == 1 ? "y" : "ies")}.");
                return;
            }
            if (string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
                || !xagmanActiveTradePartner.Equals(inFlightOwner.ActiveCharacter, StringComparison.OrdinalIgnoreCase))
            {
                xagmanActiveTradePartner = inFlightOwner.ActiveCharacter;
                xagmanActiveTradePartnerInstanceId = inFlightOwner.InstanceId;
                if (!xagmanSupplyDrainOnly && !IsXagmanCapacityDrainActive()
                    && !TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Format("S2821BD471543EE8E", inFlightOwner.ActiveCharacter)))
                    return;
            }
            xagmanStatus = inFlightOwner.Status == XagmanStatus.Trading ? XagmanStatus.Trading : XagmanStatus.Called;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE10D8A0AF9072F17", xagmanActiveCharacter, inFlightOwner.ActiveCharacter));
            return;
        }
        if (queue.Count == 0)
        {
            if (!xagmanServerMatchingActive && TryAdvanceXagmanSupplyCycle()) return;
            if (IsXagmanCollectionFirstCollectionPhase()
                && TryAdvanceXagmanCollectionFirstTonyToRestock())
            {
                return;
            }
            if (xagmanServerMatchingActive)
            {
                switch (StepXagmanSweep())
                {
                    case XagmanSweepStep.Advanced:
                        return;
                    case XagmanSweepStep.Settling:
                        xagmanStatus = XagmanStatus.AtMeetSpot;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S7859AD31611F4721", xagmanActiveCharacter, xagmanSweepDataCenter));
                        return;
                    case XagmanSweepStep.WaitingForOwners:
                        xagmanStatus = XagmanStatus.AtMeetSpot;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S16EC8507B5298E96", xagmanActiveCharacter, xagmanSweepDataCenter, GetXagmanTonySweepMeetWorld()));
                        return;
                    case XagmanSweepStep.Blocked:
                    case XagmanSweepStep.Error:
                        return;
                    case XagmanSweepStep.Finished:
                        if (IsXagmanCollectionFirstCollectionPhase())
                        {
                            UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase.Collection);
                            return;
                        }
                        if (IsXagmanSupplyCycleActive())
                        {
                            var hasShortages = xagmanPartialOwners.Values.Any(state => state.PendingGiveItems.Count > 0 || state.RequestedItems.Any(request => !IsXagmanOpenEndedSupplyRequest(request)))
                                || plugin.XagmanPeers.Peers.Any(peer => peer.Role == XagmanRole.FranchiseOwner
                                    && peer.XagmanEnabled && IsXagmanPeerInCurrentRunPhase(peer)
                                    && IsXagmanPeerInSupplyCoordinatorScope(peer)
                                    && GetXagmanFinalUnresolvedOwnerCount(peer) > 0);
                            StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: !hasShortages,
                                completedWithWarnings: hasShortages, broadcastPeerCompletion: true);
                            return;
                        }
                        if (IsXagmanCollectionFirstRestockPhase())
                        {
                            ObserveXagmanCollectionFirstPhaseAcknowledgements();
                            if (!AreAllExpectedXagmanFranchiseOwnersAcknowledged(XagmanRunPhase.Restock))
                            {
                                UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase.Restock);
                                return;
                            }
                        }
                        plugin.TaskRunner.AddLog($"Xagman: Tony {xagmanActiveCharacter} finished the Server Matching sweep; completing.");
                        StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: true, completedWithWarnings: false, broadcastPeerCompletion: true);
                        return;
                }
            }
            if (IsXagmanCollectionFirstCollectionPhase())
            {
                UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase.Collection);
                return;
            }
            if (TryStartXagmanTonyCompletion())
                return;
            xagmanStatus = XagmanStatus.AtMeetSpot;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SC14A65FF9BEBC9C0", xagmanActiveCharacter));
            return;
        }
        var next = queue[0];
        var requestedItems = next.RequestedItems == null
            ? new List<XagmanTradeRequestEntry>()
            : CloneXagmanTradeRequests(next.RequestedItems);
        var hasRequestedItems = requestedItems.Count > 0;
        if (hasRequestedItems && IsXagmanCollectionFirstCollectionPhase())
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SFBE011F169819B63", next.ActiveCharacter));
            plugin.TaskRunner.AddLog(
                $"Xagman: protocol/state error - owner {next.ActiveCharacter} entered the collection queue with Tony supply requests; requests were not dispatched.");
            PublishXagmanPresence();
            return;
        }
        xagmanStatus = XagmanStatus.ReadyForQueue;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SB3F1C80A707E9208", xagmanActiveCharacter, queue.Count));
        if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
            return;
        xagmanActiveTradePartner = next.ActiveCharacter;
        xagmanActiveTradePartnerInstanceId = next.InstanceId;
        if (!hasRequestedItems && !xagmanSupplyDrainOnly && !IsXagmanCapacityDrainActive())
        {
            if (!TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Format("S2821BD471543EE8E", next.ActiveCharacter)))
                return;
        }
        xagmanStatus = XagmanStatus.Called;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S4473BA5D4482AB2A", xagmanActiveCharacter, next.ActiveCharacter));
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        plugin.TaskRunner.AddLog($"Xagman: Tony called {next.ActiveCharacter}.");
        if (hasRequestedItems)
            plugin.TaskRunner.AddLog($"Xagman: {next.ActiveCharacter} requested {requestedItems.Count} Tony supply item entr{(requestedItems.Count == 1 ? "y" : "ies")}.");
        if (hasRequestedItems)
            StartXagmanTonyTrade(requestedItems, next);
    }

    private bool TryReleaseXagmanTonyStalePartner()
    {
        if (string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return false;
        if ((DateTime.UtcNow - xagmanLastTonyActionAtUtc).TotalSeconds < 2)
            return false;
        var activePartner = xagmanActiveTradePartner;
        var ownerPeer = GetXagmanActiveTradeOwnerPeer();
        if (ownerPeer != null)
        {
            var ownerIsTerminal = ownerPeer.Status is XagmanStatus.Idle or XagmanStatus.Completed or XagmanStatus.Error;
            var ownerStillInTradeFlow = IsXagmanPeerFresh(ownerPeer)
                && !ownerIsTerminal
                && (ownerPeer.Status is XagmanStatus.Called or XagmanStatus.Trading
                    || ownerPeer.QueueRequestedAtUtc > DateTime.MinValue);
            if (ownerStillInTradeFlow)
                return false;
        }
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SAF26313F37A73F4D", activePartner)))
            return true;
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        ResetXagmanTonyApproachWait();
        xagmanObservedDropboxBusy = false;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        plugin.TaskRunner.AddLog($"Xagman: released stale active owner {activePartner}.");
        return true;
    }

    private bool TryStartXagmanTonyCompletion()
    {
        const double disconnectedOwnerCompletionDelaySeconds = 30.0;

        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return false;
        if (IsXagmanCollectionFirstCollectionPhase() || IsXagmanCapacityRecoveryActive())
            return false;
        if (IsXagmanCollectionFirstRestockPhase())
        {
            ObserveXagmanCollectionFirstPhaseAcknowledgements();
            if (!AreAllExpectedXagmanFranchiseOwnersAcknowledged(XagmanRunPhase.Restock))
            {
                UpdateXagmanCollectionFirstBarrierStatus(XagmanRunPhase.Restock);
                return false;
            }
            StartXagmanTonyCompletionTask(
                string.Empty,
                autoDetectedNoRemainingOwners: true,
                completedWithWarnings: false,
                broadcastPeerCompletion: true);
            return true;
        }
        var liveRelevantOwnerPeers = GetXagmanRelevantOwnerPeersForTony(xagmanActiveCharacter, enabledOnly: true, freshOnly: true);
        var completionRequester = liveRelevantOwnerPeers
            .Where(peer => peer.TonyCompletionRequestedAtUtc > DateTime.MinValue && peer.TonyCompletionRequestedAtUtc >= xagmanTonyRunStartedAtUtc)
            .OrderByDescending(peer => peer.TonyCompletionRequestedAtUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();
        if (completionRequester != null)
            xagmanTonyObservedOwnerWork = true;
        if (completionRequester != null)
        {
            StartXagmanTonyCompletionTask(
                completionRequester.ActiveCharacter,
                autoDetectedNoRemainingOwners: false,
                completedWithWarnings: false,
                broadcastPeerCompletion: IsXagmanCollectionFirstRestockPhase());
            return true;
        }
        if (!xagmanTonyObservedOwnerWork)
            return false;
        if (liveRelevantOwnerPeers.Count == 0)
        {
            if (xagmanTonyAllOwnersCompletedObservedAtUtc == DateTime.MinValue
                || xagmanTonyNoConnectedOwnerPeersSinceUtc == DateTime.MinValue)
                return false;
            if ((DateTime.UtcNow - xagmanTonyNoConnectedOwnerPeersSinceUtc).TotalSeconds < disconnectedOwnerCompletionDelaySeconds)
                return false;
            StartXagmanTonyCompletionTask(
                string.Empty,
                autoDetectedNoRemainingOwners: true,
                completedWithWarnings: false,
                broadcastPeerCompletion: IsXagmanCollectionFirstRestockPhase());
            return true;
        }
        var remainingFranchiseOwners = GetXagmanRemainingFranchiseOwnerCountForTony(xagmanActiveCharacter, freshOnly: true);
        if (remainingFranchiseOwners > 0)
            return false;
        StartXagmanTonyCompletionTask(
            string.Empty,
            autoDetectedNoRemainingOwners: true,
            completedWithWarnings: false,
            broadcastPeerCompletion: IsXagmanCollectionFirstRestockPhase());
        return true;
    }

    private void StartXagmanTonyCompletionTask(string requestedBy, bool autoDetectedNoRemainingOwners = false, bool completedWithWarnings = false, bool broadcastPeerCompletion = false)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;
        if (HoldXagmanFinalCompletionDelivery()) return;
        var cfg = plugin.Configuration;
        var runner = plugin.TaskRunner;
        var tonyCharacter = xagmanActiveCharacter;
        var capacityCompletion = IsXagmanCapacityWarningCompletion();
        var coordinatedCompletion = broadcastPeerCompletion && (IsXagmanCollectionFirstRestockPhase() || capacityCompletion);
        var supplyCompletion = IsXagmanSupplyCycleActive();
        var completionSupplyPassId = supplyCompletion ? xagmanSupplyPassId : string.Empty;
        var completionCapacityDrainId = capacityCompletion ? xagmanCapacityDrainId : string.Empty;
        var completionCapacityRecoveryEpoch = capacityCompletion ? xagmanCapacityRecoveryEpoch : 0;
        System.Threading.Tasks.Task<bool>? peerCompletionTask = null;
        var peerCompletionDeliveryFailed = false;
        var steps = new List<TaskStep>();

        void MarkPeerCompletionDeliveryFailed(global::XASlave.Localization.UiMessage messagePresentation)
        {
            var message = messagePresentation.Original;
            if (peerCompletionDeliveryFailed || !coordinatedCompletion)
                return;

            peerCompletionDeliveryFailed = true;
            SetXagmanRunning(true);
            xagmanActiveRole = XagmanRole.Tony;
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, messagePresentation);
            runner.SuppressLogoutCancel = true;
            runner.AddLog($"Xagman: {message}");
            PublishXagmanPresence();
        }

        bool PollPeerCompletionDelivery()
        {
            if (peerCompletionTask == null || !peerCompletionTask.IsCompleted)
                return false;

            var acknowledged = false;
            try
            {
                acknowledged = peerCompletionTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                runner.AddLog($"Xagman: scoped peer completion task failed: {ex.Message}");
            }

            if (!acknowledged)
            {
                MarkPeerCompletionDeliveryFailed(
                    global::XASlave.Localization.UiMessage.Key("S0EAAFCC999AC8643"));
            }
            return true;
        }

        runner.SuppressLogoutCancel = true;
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("SE59DB2FBE0633162", tonyCharacter),
            OnEnter = () =>
            {
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S725C064B3A5005BB", tonyCharacter)))
                    return;
                ClearXagmanDropbox();
                if (supplyCompletion && !capacityCompletion) FinalizeXagmanOpenEndedSupplyRequests();
                if (broadcastPeerCompletion)
                    peerCompletionTask ??= CompleteAllXagmanPeersAsync(
                        completionSupplyPassId, completionCapacityDrainId, completionCapacityRecoveryEpoch);
                MarkXagmanTonyConsumed(tonyCharacter);
                var warningLines = completedWithWarnings
                    ? BuildXagmanCompletionWarningSummaryLines(tonyCharacter)
                    : new List<string>();
                SetXagmanRunning(false);
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanObservedDropboxBusy = false;
                xagmanQueueRequestedAtUtc = DateTime.MinValue;
                xagmanStatus = XagmanStatus.Completed;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (completedWithWarnings ? global::XASlave.Localization.UiMessage.Format("S9A9AD92182C98B8A", tonyCharacter) : (autoDetectedNoRemainingOwners ? global::XASlave.Localization.UiMessage.Format("S31DB3C8DE1E3423C", tonyCharacter) : ((string.IsNullOrWhiteSpace(requestedBy) ? global::XASlave.Localization.UiMessage.Format("SBD95A427FC4ABE4F", tonyCharacter) : global::XASlave.Localization.UiMessage.Format("S2E6A88C1614F3370", tonyCharacter, requestedBy))))));
                runner.AddLog(completedWithWarnings
                    ? $"Xagman: Tony {tonyCharacter} is completing with unresolved owner work after the selected supplier passes."
                    : autoDetectedNoRemainingOwners
                        ? $"Xagman: Tony {tonyCharacter} detected 0 remaining Franchise Owners and is completing."
                        : (string.IsNullOrWhiteSpace(requestedBy)
                            ? $"Xagman: Tony {tonyCharacter} received completion signal."
                            : $"Xagman: Tony {tonyCharacter} received completion signal from {requestedBy}.")); 
                if (completedWithWarnings)
                {
                    if (capacityCompletion && !string.IsNullOrWhiteSpace(xagmanCapacityWarningReason))
                        runner.AddLog($"Xagman: completion warning: {xagmanCapacityWarningReason}");
                    if (warningLines.Count == 0)
                    {
                        runner.AddLog("Xagman: completion warning: Tony capacity cleanup finished with unresolved work, but no detailed owner summary was available.");
                    }
                    else
                    {
                        foreach (var line in warningLines)
                            runner.AddLog($"Xagman: completion warning: {line}");
                    }
                }
                runner.SuppressLogoutCancel = MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete);
                UpdateXagmanTonyTaskRunnerProgress();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (cfg.XagmanAutoReturnToFc)
        {
            AddXagmanTeleportSteps(
                steps,
                global::XASlave.Localization.UiMessage.Key("SB4777CCC172FB07E"),
                () => "fc",
                runner,
                null,
                true,
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5636A2549343F579", tonyCharacter));
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S4FA334438BFC9400", tonyCharacter));
                    RememberXagmanRecentFcReturn(tonyCharacter);
                },
                () =>
                {
                    xagmanStatus = XagmanStatus.ReturningHome;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S37F5A19137CF35AE", tonyCharacter));
                },
                expectCrossDataCenterLogout: true);
        }
        if (broadcastPeerCompletion)
        {
            steps.Add(new TaskStep
            {
                NameMessage = global::XASlave.Localization.UiMessage.Key("S698CB6C951EFBF71"),
                OnEnter = () => peerCompletionTask ??= CompleteAllXagmanPeersAsync(
                    completionSupplyPassId, completionCapacityDrainId, completionCapacityRecoveryEpoch),
                IsComplete = PollPeerCompletionDelivery,
                TimeoutSec = 40f,
                OnTimeout = () => MarkPeerCompletionDeliveryFailed(
                    global::XASlave.Localization.UiMessage.Key("S66E24FDAA07DAF35")),
            });
        }
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("S59C8947A0B195070"),
            ShouldSkip = () => coordinatedCompletion && peerCompletionDeliveryFailed,
            OnEnter = () =>
            {
                if (!DisconnectXagmanPeerService())
                    runner.AddLog("Xagman: local TCP peer service was already disconnected before Tony completion cleanup.");
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        AddXagmanTradeSafetyCompletionStep(
            steps,
            runner,
            "Tony completion",
            MonthlyReloggerTask.ShouldKeepLogoutCancelSuppressed(cfg.XagmanLogoutOnComplete, cfg.XagmanKillGameOnComplete),
            skipAutoClean: () => coordinatedCompletion && peerCompletionDeliveryFailed);
        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Key("SFB33FCBE6149FA37"),
            ShouldSkip = () => !coordinatedCompletion || !peerCompletionDeliveryFailed,
            OnEnter = () => xagmanExpectedLogout = false,
            IsComplete = () => true,
            TimeoutSec = 1f,
        });
        if (coordinatedCompletion)
        {
            var sharedCompletionSteps = new List<TaskStep>();
            MonthlyReloggerTask.AddSharedCompletionSteps(
                sharedCompletionSteps,
                runner,
                cfg.XagmanLogoutOnComplete,
                cfg.XagmanKillGameOnComplete,
                cfg.XagmanEnableArMultiOnComplete);
            steps.AddRange(sharedCompletionSteps.Select(step =>
                MonthlyReloggerTask.WithSkip(step, () => peerCompletionDeliveryFailed)));
        }
        else
        {
            MonthlyReloggerTask.AddSharedCompletionSteps(
                steps,
                runner,
                cfg.XagmanLogoutOnComplete,
                cfg.XagmanKillGameOnComplete,
                cfg.XagmanEnableArMultiOnComplete);
        }
        plugin.TaskRunner.Start(
            global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"),
            steps,
            onFinished: () =>
            {
                if (coordinatedCompletion && peerCompletionDeliveryFailed)
                {
                    SetXagmanRunning(true);
                    xagmanActiveRole = XagmanRole.Tony;
                    xagmanStatus = XagmanStatus.Error;
                    runner.SuppressLogoutCancel = true;
                    PublishXagmanPresence();
                    return;
                }

                FinalizeXagmanLocalShutdown("Tony completion", disconnectBeforeStop: true);
            },
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"),
            preserveRunHistory: true);
    }

    private void HandleXagmanTonySupplyDepletion(IReadOnlyList<XagmanTradeRequestEntry> requestedItems, string partnerName)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;

        if (TryDeferXagmanTonySupplyOwner()) return;
        var requestedEntries = requestedItems.Count(IsValidXagmanTradeRequest);
        var requestedUnits = requestedItems
            .Where(IsValidXagmanTradeRequest)
            .Sum(entry => Math.Max(0, entry.Quantity));
        var activeTony = xagmanActiveCharacter;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SDDA4FB7D45BCEF9C", partnerName)))
            return;
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanObservedDropboxBusy = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;

        var depletionContext = $"Xagman: Tony {activeTony} cannot currently supply {requestedEntries} requested entr{(requestedEntries == 1 ? "y" : "ies")} totaling {requestedUnits} units to {partnerName}.";
        if (TryRotateXagmanTonyForCapacityExhaustion(depletionContext))
            return;

        plugin.TaskRunner.AddLog($"{depletionContext} No alternate Tony remains; finalizing with warning summary and peer completion cleanup.");
        StartXagmanTonyCompletionTask(string.Empty, autoDetectedNoRemainingOwners: false, completedWithWarnings: true, broadcastPeerCompletion: true);
    }

    private bool StartXagmanTonyTrade(IReadOnlyList<XagmanTradeRequestEntry>? requestedItems = null, XagmanPeerPresence? ownerPeer = null)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning || string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return false;
        var runner = plugin.TaskRunner;
        var partnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
        string GetCurrentPartnerVisibleName()
        {
            var currentPartnerName = GetCharacterNameFromKey(xagmanActiveTradePartner);
            return string.IsNullOrWhiteSpace(currentPartnerName)
                ? partnerName
                : currentPartnerName;
        }
        bool ShouldSkipTonyTradeFlow()
        {
            return !xagmanRunning
                || xagmanActiveRole != XagmanRole.Tony
                || string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
                || xagmanStatus == XagmanStatus.Error;
        }
        void AbortTonyTrade(global::XASlave.Localization.UiMessage messagePresentation, bool cleanupTradeWindow)
        {
            var message = messagePresentation.Original;
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, messagePresentation);
            runner.AddLog($"Xagman: {message}");
            if (cleanupTradeWindow)
            {
                var stoppedQueue = TryStopXagmanDropboxTradeQueue();
                var sentEscape = TryAbortXagmanTradeWindow();
                ClearXagmanDropbox();
                if (stoppedQueue)
                    runner.AddLog($"Xagman: stopped Dropbox item trade queue after Tony trade failure with {partnerName}.");
                if (sentEscape)
                    runner.AddLog($"Xagman: sent ESC to close Trade after Tony trade failure with {partnerName}.");
            }
            if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S35359BDEF10BA7FF", partnerName)))
                return;
            ClearXagmanFocusTarget();
            xagmanObservedDropboxBusy = false;
            xagmanActiveTradePartner = string.Empty;
            xagmanActiveTradePartnerInstanceId = string.Empty;
            xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        }
        // A non-empty peer payload is a supply request even when malformed. Build validation must
        // see and reject the complete batch rather than silently falling back to receive mode.
        var usingSupplyRequests = requestedItems?.Count > 0;
        var capturedOwnerRequests = usingSupplyRequests
            ? CloneXagmanTradeRequests(requestedItems!)
            : new List<XagmanTradeRequestEntry>();
        var usingGreenValueRequests = capturedOwnerRequests.Any(entry =>
            IsXagmanGreenValueSelector(entry.SelectorKind)) == true;
        var validatedOwnerPeer = ownerPeer;
        if (usingGreenValueRequests
            && !TryValidateXagmanLiveGreenSupplyPeer(
                capturedOwnerRequests,
                true,
                true,
                out validatedOwnerPeer,
                out var peerValidationFailure))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText =
                global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S92A87979B41BCB89", partnerName, peerValidationFailure));
            runner.AddLog(
                $"Xagman: {xagmanStatusText} Reload every participating client from the same source.");
            PublishXagmanPresence();
            return false;
        }
        if (usingGreenValueRequests)
            ownerPeer = validatedOwnerPeer;
        var supplyRequests = usingSupplyRequests
            ? BuildXagmanTonySupplyRequests(capturedOwnerRequests)
            : new List<XagmanTradeRequestEntry>();
        if (!string.IsNullOrWhiteSpace(xagmanGreenSupplyValidationError))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S80AEC8140F1CF391", partnerName, xagmanGreenSupplyValidationError));
            runner.AddLog($"Xagman: {xagmanStatusText}");
            PublishXagmanPresence();
            return false;
        }
        var items = plugin.Configuration.XagmanItems.ToList();
        if (!usingSupplyRequests && items.Count == 0)
        {
            plugin.TaskRunner.AddLog("Xagman: shared item list is empty, skipping trade.");
            return false;
        }
        if (usingSupplyRequests && supplyRequests.Count == 0)
        {
            HandleXagmanTonySupplyDepletion(requestedItems!, partnerName);
            ResetXagmanTonyApproachWait();
            return false;
        }
        // Capacity/depletion must be resolved before range waiting so an empty replacement Tony can rotate.
        // Keep publishing the explicit call while a stocked Tony waits, then start the timed trade only once
        // the owner is already nearby. The separate 10-minute deadline prevents an unreachable owner pin.
        if (!IsCharacterInRangeWithoutMoving(partnerName))
        {
            xagmanStatus = XagmanStatus.Called;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9001710FC9A43772", partnerName, xagmanActiveCharacter));
            ExpireXagmanTonyApproachWaitIfNeeded(partnerName);
            return false;
        }
        ResetXagmanTonyApproachWait();
        if (usingSupplyRequests)
            plugin.TaskRunner.AddLog($"Xagman: Tony will supply {supplyRequests.Count} requested item entr{(supplyRequests.Count == 1 ? "y" : "ies")} to {partnerName}.");
        bool PollTonyTradeWait()
        {
            var ownerStandbyRotationReady = TryObserveXagmanOwnerStandbyRotationRequest();
            if (ownerStandbyRotationReady.HasValue)
                return ownerStandbyRotationReady.Value;

            var busy = plugin.IpcClient.DropboxIsBusy();
            XagmanPeerPresence? validatedGreenOwnerPeer = null;
            if (usingGreenValueRequests
                && !TryValidateXagmanLiveGreenSupplyPeer(
                    capturedOwnerRequests,
                    false,
                    busy,
                    out validatedGreenOwnerPeer,
                    out var peerFailure))
            {
                AbortTonyTrade(
                    global::XASlave.Localization.UiMessage.Format("S42FB75B21472743B", peerFailure),
                    true);
                return true;
            }

            if (busy)
            {
                xagmanObservedDropboxBusy = true;
                return false;
            }

            if (!usingSupplyRequests)
            {
                if (xagmanObservedDropboxBusy)
                {
                    runner.AddLog($"Xagman: Dropbox trading queue ended for {partnerName}; releasing the trade lock for owner reconciliation.");
                    if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SF813A2B8A1CE6F22", partnerName)))
                        return true;
                    ClearXagmanDropbox();
                    xagmanObservedDropboxBusy = false;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                    return true;
                }

                return false;
            }

            var liveOwnerPeer = validatedGreenOwnerPeer
                ?? GetXagmanActiveTradeOwnerPeer()
                ?? ownerPeer;
            var remainingRequests = liveOwnerPeer?.RequestedItems?.Count(IsValidXagmanTradeRequest)
                ?? requestedItems?.Count(IsValidXagmanTradeRequest)
                ?? 0;
            if (liveOwnerPeer != null && remainingRequests == 0)
            {
                xagmanObservedDropboxBusy = false;
                return true;
            }

            if (xagmanObservedDropboxBusy)
            {
                runner.AddLog($"Xagman: Dropbox trading queue ended for {partnerName} before owner confirmed requested supply; releasing the trade lock for owner reconciliation.");
                if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SEA02B5048FEA2FF8", partnerName)))
                    return true;
                ClearXagmanDropbox();
                xagmanObservedDropboxBusy = false;
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        var steps = new List<TaskStep>
        {
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S3955ABD8C5004709", partnerName),
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Called;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9001710FC9A43772", partnerName, xagmanActiveCharacter));
                },
                IsComplete = () => IsCharacterInRangeWithoutMoving(partnerName),
                TimeoutSec = 60f,
                OnTimeout = () => AbortTonyTrade(global::XASlave.Localization.UiMessage.Format("S1CF81517B7C047BE", partnerName, xagmanActiveCharacter), false),
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDA45939E4D5C7652", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Trading;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SE92013994095E6AD", partnerName));
                    OpenXagmanDropboxWindow();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SDD8004E5F36264BB", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = OpenXagmanDropboxTradeTab,
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S0DD8A0560213E610", partnerName), 1.0f, ShouldSkipTonyTradeFlow),
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SBCE3EF4B0C46759B", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    if (usingGreenValueRequests && !TryClearXagmanDropbox(out var failure))
                    {
                        AbortTonyTrade(
                            global::XASlave.Localization.UiMessage.Format("S8D3632642D6673A9", failure),
                            false);
                        return;
                    }
                    if (!usingGreenValueRequests)
                        ClearXagmanDropbox();
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SA2C23127D8DD8ABA", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    if (usingGreenValueRequests
                        && !TryValidateXagmanLiveGreenSupplyPeer(
                            capturedOwnerRequests,
                            true,
                            true,
                            out _,
                            out var peerFailure))
                    {
                        AbortTonyTrade(
                            global::XASlave.Localization.UiMessage.Format("S75AAA024924BE691", peerFailure),
                            false);
                        return;
                    }
                    if (usingSupplyRequests)
                        QueueXagmanRequestedSupplyItems(supplyRequests);
                    else
                        QueueXagmanItems(items);
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            },
            MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S75A653F94F2D2369", partnerName), 0.5f, ShouldSkipTonyTradeFlow),
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S998C4D424A49932A", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () => TryTargetCharacter(GetCurrentPartnerVisibleName()),
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S66CB233AB38E14C7", partnerName), 0.1f, ShouldSkipTonyTradeFlow),
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S667F29CFDEAA215C", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    TryTargetCharacter(GetCurrentPartnerVisibleName());
                    FocusXagmanCurrentTarget(GetCurrentPartnerVisibleName());
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S7C36EAD96CDFF5AE", partnerName), 0.15f, ShouldSkipTonyTradeFlow),
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SF96B0B4A340BE36A", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () => TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S529C70F39D9E4853", partnerName)),
                IsComplete = () => true,
                TimeoutSec = 0.5f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S25DDC594CF2DE0F1", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    if (usingGreenValueRequests
                        && !TryValidateXagmanLiveGreenSupplyPeer(
                            capturedOwnerRequests,
                            true,
                            true,
                            out _,
                            out var peerFailure))
                    {
                        AbortTonyTrade(
                            global::XASlave.Localization.UiMessage.Format("SF0E59C4EAD7B7164", peerFailure),
                            true);
                        return;
                    }
                    if (!StartXagmanDropboxTrade($"Tony trade with {partnerName}"))
                    {
                        AbortTonyTrade(global::XASlave.Localization.UiMessage.Format("S9E6946DF94C67B17", partnerName), true);
                        return;
                    }
                    xagmanObservedDropboxBusy = plugin.IpcClient.DropboxIsBusy();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S2C9BF7ED7CCCAEE9", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () => xagmanObservedDropboxBusy = false,
                IsComplete = PollTonyTradeWait,
                TimeoutSec = 600f,
                OnTimeout = () => AbortTonyTrade(global::XASlave.Localization.UiMessage.Format("SA02094CC76E4EFB9", partnerName), true),
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SFDAA7C8A0F585C94", partnerName),
                ShouldSkip = ShouldSkipTonyTradeFlow,
                OnEnter = () =>
                {
                    ClearXagmanDropbox();
                    ClearXagmanFocusTarget();
                    ApplyXagmanTonyTradeProgressToTaskRunner(partnerName, ownerPeer);
                    xagmanObservedDropboxBusy = false;
                    xagmanActiveTradePartner = string.Empty;
                    xagmanActiveTradePartnerInstanceId = string.Empty;
                    xagmanStatus = XagmanStatus.AtMeetSpot;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S88B3B964AA824894", xagmanActiveCharacter, partnerName));
                    xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("SF96B0B4A340BE36A", partnerName),
                OnEnter = () => TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S6FA483720C06EF05", partnerName)),
                IsComplete = () => true,
                TimeoutSec = 0.5f,
            },
        };
        plugin.TaskRunner.Start(global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"), steps, onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"), suppressCompletionReport: true, preserveRunHistory: true);
        ApplyXagmanTonyTradeProgressToTaskRunner(partnerName, ownerPeer);
        return true;
    }

    private void ResetXagmanTonyApproachWait()
    {
        xagmanTonyApproachWaitPartnerKey = string.Empty;
        xagmanTonyApproachWaitStartedAtUtc = DateTime.MinValue;
    }

    private bool ExpireXagmanTonyApproachWaitIfNeeded(string partnerName)
    {
        var partnerKey = $"{xagmanActiveTradePartner}|{xagmanActiveTradePartnerInstanceId}";
        if (!partnerKey.Equals(xagmanTonyApproachWaitPartnerKey, StringComparison.OrdinalIgnoreCase))
        {
            xagmanTonyApproachWaitPartnerKey = partnerKey;
            xagmanTonyApproachWaitStartedAtUtc = DateTime.UtcNow;
            return false;
        }
        if (xagmanTonyApproachWaitStartedAtUtc == DateTime.MinValue
            || (DateTime.UtcNow - xagmanTonyApproachWaitStartedAtUtc).TotalSeconds < XagmanTonyApproachWaitTimeoutSeconds)
        {
            return false;
        }

        global::XASlave.Localization.UiMessage? messageUiPresentation = null;
        var message = global::XASlave.Localization.UiMessage.Store(ref messageUiPresentation, global::XASlave.Localization.UiMessage.Format("S33727A28DEB4D83A", partnerName, xagmanActiveCharacter, XagmanTonyApproachWaitTimeoutSeconds));
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Capture(ref messageUiPresentation, message));
        plugin.TaskRunner.AddLog($"Xagman: {message}");
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SAF26313F37A73F4D", partnerName)))
            return true;
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;
        ResetXagmanTonyApproachWait();
        PublishXagmanPresence();
        return true;
    }

    private void ApplyXagmanTonyTradeProgressToTaskRunner(string partnerName, XagmanPeerPresence? ownerPeer = null)
    {
        UpdateXagmanTonyTaskRunnerProgress(partnerName);
    }
    private static bool DoesXagmanPeerMatchTradePartner(XagmanPeerPresence peer, string partnerKey)
    {
        if (string.IsNullOrWhiteSpace(partnerKey))
            return false;
        if (!string.IsNullOrWhiteSpace(peer.ActiveCharacter)
            && peer.ActiveCharacter.Equals(partnerKey, StringComparison.OrdinalIgnoreCase))
            return true;

        var partnerName = GetCharacterNameFromKey(partnerKey);
        if (string.IsNullOrWhiteSpace(partnerName))
            return false;

        if (!string.IsNullOrWhiteSpace(peer.ActiveCharacter)
            && GetCharacterNameFromKey(peer.ActiveCharacter).Equals(partnerName, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(peer.CharacterName)
            && peer.CharacterName.Equals(partnerName, StringComparison.OrdinalIgnoreCase);
    }
    private XagmanPeerPresence? GetXagmanActiveTradeOwnerPeer()
    {
        if (string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
            return null;

        var query = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.FranchiseOwner)
            .Where(peer => IsXagmanPeerFresh(peer, 15.0))
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => DoesXagmanPeerMatchTradePartner(peer, xagmanActiveTradePartner));
        if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId))
        {
            query = query.Where(peer =>
                peer.InstanceId.Equals(xagmanActiveTradePartnerInstanceId, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter)
                && peer.ActiveCharacter.Equals(xagmanActiveTradePartner, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
    }

    private bool TryValidateXagmanLiveGreenSupplyPeer(
        IReadOnlyList<XagmanTradeRequestEntry> capturedRequests,
        bool requireMatchingRequests,
        bool requireActiveTransferState,
        out XagmanPeerPresence? livePeer,
        out global::XASlave.Localization.UiMessage failure)
    {
        livePeer = GetXagmanActiveTradeOwnerPeer();
        if (livePeer == null)
        {
            failure = global::XASlave.Localization.UiMessage.Key("SF264AC0F2EF3BCC7");
            return false;
        }
        if (livePeer.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
        {
            failure =
                global::XASlave.Localization.UiMessage.Format("S5743EA0181FC9F27", XagmanGreenValueProtocolRevision);
            return false;
        }

        if (!Enum.IsDefined(livePeer.Status))
        {
            failure = global::XASlave.Localization.UiMessage.Format("SB386690AAECBDBE5", (int)livePeer.Status);
            return false;
        }

        var liveRequests = livePeer.RequestedItems ?? new List<XagmanTradeRequestEntry>();
        var needsActiveOwner = requireMatchingRequests
            || requireActiveTransferState
            || liveRequests.Count > 0;
        if (needsActiveOwner)
        {
            var expectedCharacterName = GetCharacterNameFromKey(xagmanActiveTradePartner);
            if (!livePeer.IsLoggedIn
                || livePeer.ContentId == 0
                || string.IsNullOrWhiteSpace(expectedCharacterName)
                || string.IsNullOrWhiteSpace(livePeer.CharacterName)
                || !livePeer.CharacterName.Equals(
                    expectedCharacterName,
                    StringComparison.OrdinalIgnoreCase))
            {
                failure =
                    global::XASlave.Localization.UiMessage.Key("SD417A6EDE6E72814");
                return false;
            }

            if (livePeer.Status is not (
                    XagmanStatus.Traveling
                    or XagmanStatus.AtMeetSpot
                    or XagmanStatus.ReadyForQueue
                    or XagmanStatus.WaitingRoom
                    or XagmanStatus.Queued
                    or XagmanStatus.Called
                    or XagmanStatus.Trading
                    or XagmanStatus.Standby))
            {
                failure = global::XASlave.Localization.UiMessage.Format("SCDD9B78D657CD312", livePeer.Status);
                return false;
            }
        }
        else if (livePeer.Status is XagmanStatus.Error or XagmanStatus.Idle)
        {
            failure = global::XASlave.Localization.UiMessage.Format("S9DEA359890F2FA37", livePeer.Status);
            return false;
        }

        if (liveRequests.Any(entry => entry == null || !IsValidXagmanTradeRequest(entry)))
        {
            failure = global::XASlave.Localization.UiMessage.Key("SFC7F67B0AA3E1D92");
            return false;
        }
        if (HasDuplicateXagmanExactTradeRequests(liveRequests))
        {
            failure = global::XASlave.Localization.UiMessage.Key("SB9203706A48415D4");
            return false;
        }
        if (!requireMatchingRequests)
        {
            foreach (var liveExactRequest in liveRequests.Where(entry =>
                         entry.SelectorKind == XagmanItemSelectorKind.ExactItem))
            {
                var capturedExactRequest = capturedRequests.FirstOrDefault(entry =>
                    entry != null
                    && entry.SelectorKind == XagmanItemSelectorKind.ExactItem
                    && entry.ItemId == liveExactRequest.ItemId
                    && entry.IsHq == liveExactRequest.IsHq);
                if (capturedExactRequest == null
                    || !capturedExactRequest.ItemName.Equals(
                        liveExactRequest.ItemName,
                        StringComparison.Ordinal)
                    || capturedExactRequest.Mode != liveExactRequest.Mode
                    || capturedExactRequest.TargetQuantity != liveExactRequest.TargetQuantity
                    || liveExactRequest.Quantity > capturedExactRequest.Quantity
                    || liveExactRequest.CurrentQuantity < capturedExactRequest.CurrentQuantity)
                {
                    failure =
                        global::XASlave.Localization.UiMessage.Key("S807C93B3D5AD5C9A");
                    return false;
                }
            }
        }

        var capturedGreenRequests = capturedRequests
            .Where(entry => entry != null
                && IsXagmanGreenValueSelector(entry.SelectorKind))
            .ToList();
        var liveGreenRequests = liveRequests
            .Where(entry => IsXagmanGreenValueSelector(entry.SelectorKind))
            .ToList();
        if (liveGreenRequests
            .GroupBy(entry => entry.SelectorKind)
            .Any(group => group.Count() > 1))
        {
            failure = global::XASlave.Localization.UiMessage.Key("SFB8C34E0E4BD9B51");
            return false;
        }
        foreach (var liveGreenRequest in liveGreenRequests)
        {
            if (!TryValidateXagmanGreenTradeRequestMessage(liveGreenRequest, out failure))
                return false;
        }
        if (capturedGreenRequests.Count > 0)
        {
            var snapshot = livePeer.GreenValueSnapshot;
            if (snapshot == null
                || !snapshot.Complete
                || snapshot.Revision != XagmanGreenValueProtocolRevision
                || !string.IsNullOrWhiteSpace(snapshot.Error))
            {
                failure =
                    global::XASlave.Localization.UiMessage.Key("S480F052282747238");
                return false;
            }

            foreach (var liveGreenRequest in liveGreenRequests)
            {
                var snapshotCurrentScaled2 = GetXagmanGreenMetricScaled2(
                    snapshot,
                    liveGreenRequest.SelectorKind);
                var snapshotTargetScaled2 = GetXagmanGreenTargetMetricScaled2(
                    snapshot,
                    liveGreenRequest.SelectorKind);
                var capturedGreenRequest = capturedRequests.FirstOrDefault(entry =>
                    entry != null
                    && entry.SelectorKind == liveGreenRequest.SelectorKind);
                if (capturedGreenRequest == null
                    || snapshotTargetScaled2 != liveGreenRequest.TargetValueScaled2
                    || (requireMatchingRequests
                        ? snapshotCurrentScaled2 != liveGreenRequest.CurrentValueScaled2
                        : snapshotCurrentScaled2 < liveGreenRequest.CurrentValueScaled2)
                    || (requireMatchingRequests
                        && snapshotCurrentScaled2 >= snapshotTargetScaled2)
                    || liveGreenRequest.TargetValueScaled2 != capturedGreenRequest.TargetValueScaled2
                    || liveGreenRequest.CurrentValueScaled2 < capturedGreenRequest.CurrentValueScaled2
                    || liveGreenRequest.ValueDeficitScaled2 > capturedGreenRequest.ValueDeficitScaled2)
                {
                    failure =
                        global::XASlave.Localization.UiMessage.Key("S830A24AB8D566000");
                    return false;
                }
            }

            foreach (var capturedGreenRequest in capturedGreenRequests)
            {
                if (liveGreenRequests.Any(entry =>
                        entry.SelectorKind == capturedGreenRequest.SelectorKind))
                {
                    continue;
                }

                var snapshotCurrentScaled2 = GetXagmanGreenMetricScaled2(
                    snapshot,
                    capturedGreenRequest.SelectorKind);
                var snapshotTargetScaled2 = GetXagmanGreenTargetMetricScaled2(
                    snapshot,
                    capturedGreenRequest.SelectorKind);
                if (requireMatchingRequests
                    || snapshotTargetScaled2 != capturedGreenRequest.TargetValueScaled2
                    || snapshotCurrentScaled2 < capturedGreenRequest.TargetValueScaled2)
                {
                    failure =
                        global::XASlave.Localization.UiMessage.Key("S5D8C5F78D9CA80FE");
                    return false;
                }
            }
        }

        if (requireMatchingRequests
            && !AreXagmanTradeRequestListsEquivalent(capturedRequests, liveRequests))
        {
            failure = global::XASlave.Localization.UiMessage.Key("S678D33CBAF250721");
            return false;
        }

        failure = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        return true;
    }

    private static bool AreXagmanTradeRequestListsEquivalent(
        IReadOnlyList<XagmanTradeRequestEntry> left,
        IReadOnlyList<XagmanTradeRequestEntry> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var current = left[index];
            var next = right[index];
            if (current == null || next == null
                || current.SelectorKind != next.SelectorKind
                || current.ItemId != next.ItemId
                || !string.Equals(current.ItemName, next.ItemName, StringComparison.Ordinal)
                || current.IsHq != next.IsHq
                || current.Mode != next.Mode
                || current.Quantity != next.Quantity
                || current.TargetQuantity != next.TargetQuantity
                || current.CurrentQuantity != next.CurrentQuantity
                || current.GreenValueProtocolRevision != next.GreenValueProtocolRevision
                || current.TargetValueScaled2 != next.TargetValueScaled2
                || current.CurrentValueScaled2 != next.CurrentValueScaled2
                || current.ValueDeficitScaled2 != next.ValueDeficitScaled2
                || current.GreenScanComplete != next.GreenScanComplete
                || !string.Equals(current.GreenScanError, next.GreenScanError, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void AdvanceXagmanTonyAfterCharacterFailure()
    {
        if (plugin.TaskRunner.IsRunning)
            return;
        xagmanTonyCharacterFailurePending = false;
        if (CanRotateXagmanTonyInCurrentScope())
        {
            if (!TryBeginXagmanTradeSafetySession(global::XASlave.Localization.UiMessage.Key("SC1D8B9B638463379")))
            {
                xagmanStatus = XagmanStatus.Error;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S5C283F838E0FDF3F"));
                plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
                PublishXagmanPresence();
                return;
            }
            plugin.TaskRunner.AddLog($"Xagman: advancing from failed Tony {xagmanActiveCharacter} to the next eligible Tony.");
            RotateXagmanTony();
            return;
        }

        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S458B6F0B22316106", xagmanActiveCharacter));
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        PublishXagmanPresence();
    }

    private bool CanRotateXagmanTonyInCurrentScope()
    {
        if (!xagmanRunning
            || xagmanActiveRole != XagmanRole.Tony
            || plugin.TaskRunner.IsRunning)
        {
            return false;
        }

        if (xagmanServerMatchingActive)
        {
            return !string.IsNullOrWhiteSpace(xagmanSweepRegion)
                && xagmanTonyRunList.Any(key =>
                    !key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
                    && GetXagmanRegionOfChar(key).Equals(xagmanSweepRegion, StringComparison.OrdinalIgnoreCase));
        }

        return xagmanTonyRunList.Any(key =>
            !key.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase));
    }

    private void RotateXagmanTony()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return;
        ResetXagmanTonyMeetRetryState();
        MarkXagmanTonyConsumed(xagmanActiveCharacter);
        if (xagmanTonyRunList.Count == 0)
        {
            if (xagmanServerMatchingActive)
            {
                ReportXagmanTravelRouteError(
                    global::XASlave.Localization.UiMessage.Format("S544DAD81D1E88483", xagmanSweepRegion));
            }
            return;
        }
        xagmanCurrentTonyIndex = 0;
        // In Server Matching, keep rotation within the current sweep region so a region only advances
        // once its own Tonys are exhausted.
        var nextKey = xagmanServerMatchingActive && !string.IsNullOrWhiteSpace(xagmanSweepRegion)
            ? GetXagmanNextRunListTonyInRegion(xagmanSweepRegion)
            : xagmanTonyRunList[xagmanCurrentTonyIndex];
        if (string.IsNullOrWhiteSpace(nextKey))
        {
            plugin.TaskRunner.AddLog(
                $"Xagman: no replacement Tony remains in active Server Matching region {xagmanSweepRegion}; global cross-region fallback is blocked.");
            if (IsXagmanCollectionFirstRunActive())
            {
                ReportXagmanTravelRouteError(
                    global::XASlave.Localization.UiMessage.Format("S0D00588B22667788", xagmanSweepRegion, xagmanRunPhase));
                return;
            }

            if (TryAdvanceXagmanSweepToNextNeededRegion(
                    "has no same-region replacement Tony; advancing explicitly without reusing its destination"))
            {
                return;
            }

            ReportXagmanTravelRouteError(
                global::XASlave.Localization.UiMessage.Format("SD7BFF03234435E56", xagmanSweepRegion));
            return;
        }
        if (xagmanServerMatchingActive
            && !TryValidateXagmanServerMatchingTonyRouteMessage(
                nextKey,
                xagmanSweepRegion,
                xagmanSweepDataCenter,
                out var routeFailure))
        {
            ReportXagmanTravelRouteError(global::XASlave.Localization.UiMessage.Format("SC020BA0AC573CE28", nextKey, routeFailure));
            return;
        }
        var nextEntry = plugin.Configuration.XagmanTonyCharacters.FirstOrDefault(entry => entry.CharacterNameWorld.Equals(nextKey, StringComparison.OrdinalIgnoreCase))
            ?? new XagmanTonyCharacterEntry { CharacterNameWorld = nextKey, Mode = xagmanTonyMode };
        plugin.TaskRunner.AddLog($"Xagman: rotating Tony to {nextEntry.CharacterNameWorld}.");
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        ResetXagmanTonyApproachWait();
        xagmanObservedDropboxBusy = false;
        ResetXagmanTonySellLocation();
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("S22C78063F5CEC4B4", nextEntry.CharacterNameWorld)))
            return;
        StartXagmanTonyStartup(nextEntry, true);
    }

    private static bool TryValidateXagmanMeetTravel(
        string characterNameWorld,
        string destinationWorld,
        out string reason)
    {
        var result = TryValidateXagmanMeetTravelMessage(characterNameWorld, destinationWorld, out var presentation);
        reason = presentation.Original;
        return result;
    }

    private static bool TryValidateXagmanMeetTravelMessage(
        string characterNameWorld,
        string destinationWorld,
        out global::XASlave.Localization.UiMessage reason)
    {
        var sourceHomeWorld = GetWorldFromKey(characterNameWorld);
        var reachability = WorldData.GetTravelReachability(sourceHomeWorld, destinationWorld);
        if (reachability == WorldData.WorldTravelReachability.Reachable)
        {
            reason = global::XASlave.Localization.UiMessage.Raw(string.Empty);
            return true;
        }

        reason = reachability switch
        {
            WorldData.WorldTravelReachability.UnknownSourceWorld =>
                global::XASlave.Localization.UiMessage.Format("S5AD8A3EE8903A20B", sourceHomeWorld),
            WorldData.WorldTravelReachability.UnknownDestinationWorld =>
                global::XASlave.Localization.UiMessage.Format("SA8C2E82A5FC19BA6", destinationWorld),
            _ =>
                global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S8A77EFBE5C2D75A9", WorldData.GetByName(sourceHomeWorld)?.Region ?? "Unknown"), global::XASlave.Localization.UiMessage.Format("S1050F719A3D91D4E", WorldData.GetByName(destinationWorld)?.Region ?? "Unknown")),
        };
        return false;
    }

    private string GetXagmanOwnerRouteCharacter()
    {
        if (!string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            && xagmanOwnerRunList.Any(owner =>
                owner.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)))
        {
            return xagmanActiveCharacter;
        }

        if (xagmanOwnerCurrentCharacterIndex >= 0
            && xagmanOwnerCurrentCharacterIndex < xagmanOwnerRunList.Count)
        {
            return xagmanOwnerRunList[xagmanOwnerCurrentCharacterIndex];
        }

        return xagmanOwnerRunList.FirstOrDefault()
            ?? xagmanOwnerRunPlan.FirstOrDefault()
            ?? string.Empty;
    }

    private bool IsXagmanTonyPeerReachableFromOwner(
        XagmanPeerPresence peer,
        string ownerCharacter = "")
    {
        if (peer.Status == XagmanStatus.Error
            || !HasCompleteXagmanMeetDestination(peer.MeetWorld, peer.MeetAetheryte))
        {
            return false;
        }

        var routeCharacter = string.IsNullOrWhiteSpace(ownerCharacter)
            ? GetXagmanOwnerRouteCharacter()
            : ownerCharacter;
        if (string.IsNullOrWhiteSpace(routeCharacter))
            return !xagmanRunning;
        return TryValidateXagmanMeetTravelMessage(routeCharacter, peer.MeetWorld, out _);
    }

    private bool IsXagmanOwnerPeerReachableForActiveTony(XagmanPeerPresence peer)
    {
        return peer.Status != XagmanStatus.Error
            && HasCompleteXagmanActiveMeetDestination()
            && TryValidateXagmanMeetTravelMessage(peer.ActiveCharacter, xagmanActiveMeetWorld, out _);
    }

    private void ReportXagmanTravelRouteError(string reason)
        => ReportXagmanTravelRouteError(global::XASlave.Localization.UiMessage.Raw(reason));

    private void ReportXagmanTravelRouteError(global::XASlave.Localization.UiMessage reason)
    {
        ClearXagmanExpectedTravelLogoutWindow();
        ResetXagmanTonyMeetRetryState();
        SetXagmanActiveMeetDestination(string.Empty, string.Empty);
        xagmanPhaseComplete = false;
        if (IsXagmanCollectionFirstRunActive())
            xagmanTravelRouteFatalError = true;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5E712E33A741DBAA", reason));
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        if (xagmanRunning)
            PublishXagmanPresence();
    }

    private void SetXagmanActiveMeetDestination(string world, string aetheryte)
    {
        xagmanActiveMeetWorld = world?.Trim() ?? string.Empty;
        xagmanActiveMeetAetheryte = aetheryte?.Trim() ?? string.Empty;
    }

    private string GetXagmanActiveMeetWorld()
    {
        return xagmanActiveMeetWorld;
    }

    private string GetXagmanActiveMeetAetheryte()
    {
        return xagmanActiveMeetAetheryte;
    }

    private string GetXagmanActiveMeetDestinationLabel()
    {
        return GetPrepLogisticsDestinationLabel(xagmanActiveMeetWorld, xagmanActiveMeetAetheryte);
    }

    private static bool HasCompleteXagmanMeetDestination(string world, string aetheryte)
    {
        return !string.IsNullOrWhiteSpace(world)
            && !string.IsNullOrWhiteSpace(aetheryte);
    }

    private bool HasCompleteXagmanActiveMeetDestination()
    {
        return HasCompleteXagmanMeetDestination(xagmanActiveMeetWorld, xagmanActiveMeetAetheryte);
    }

    private string GetXagmanActiveMeetDestinationCommand()
    {
        if (string.IsNullOrWhiteSpace(xagmanActiveMeetWorld))
            return string.Empty;

        if (GetCurrentWorldName().Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(xagmanActiveMeetAetheryte))
            return xagmanActiveMeetAetheryte;

        return string.IsNullOrWhiteSpace(xagmanActiveMeetAetheryte)
            ? xagmanActiveMeetWorld
            : $"{xagmanActiveMeetWorld}, {xagmanActiveMeetAetheryte}";
    }

    private bool IsXagmanOwnerTonyLocked()
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner)
            return !string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
                && (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner) || !string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId));
        return !string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
            && (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
                || !string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId)
                || xagmanStatus is XagmanStatus.Called or XagmanStatus.Trading);
    }

    private string GetXagmanLockedTonyCharacter()
    {
        return IsXagmanOwnerTonyLocked() ? xagmanPreferredTonyCharacter : string.Empty;
    }

    private XagmanPeerPresence? GetXagmanMeetTonyPeerForOwner()
    {
        var lockedTony = GetXagmanLockedTonyCharacter();
        var livePreferredTony = xagmanPreferredTonyCharacter;
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled)
            .Where(peer => peer.Role == XagmanRole.Tony)
             .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
             .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
             .Where(peer => IsXagmanTonyPeerReachableFromOwner(peer))
             .Where(peer => peer.MeetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.MeetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(lockedTony)
                && peer.ActiveCharacter.Equals(lockedTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(livePreferredTony)
                && peer.ActiveCharacter.Equals(livePreferredTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();
    }

    private XagmanPeerPresence? GetXagmanReadyMeetTonyPeerForOwner()
    {
        var lockedTony = GetXagmanLockedTonyCharacter();
        var livePreferredTony = xagmanPreferredTonyCharacter;
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled)
            .Where(peer => peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
            .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => peer.Status == XagmanStatus.AtMeetSpot)
             .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
             .Where(peer => IsXagmanTonyPeerReachableFromOwner(peer))
             .Where(peer => peer.MeetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.MeetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(lockedTony)
                && peer.ActiveCharacter.Equals(lockedTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(livePreferredTony)
                && peer.ActiveCharacter.Equals(livePreferredTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();
    }

    private XagmanPeerPresence? GetXagmanUsableMeetTonyPeerForOwner()
    {
        var lockedTony = GetXagmanLockedTonyCharacter();
        var livePreferredTony = xagmanPreferredTonyCharacter;
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled)
            .Where(peer => peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
            .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => peer.Status is XagmanStatus.AtMeetSpot or XagmanStatus.Called or XagmanStatus.Trading)
             .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
             .Where(peer => IsXagmanTonyPeerReachableFromOwner(peer))
             .Where(peer => peer.MeetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.MeetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(lockedTony)
                && peer.ActiveCharacter.Equals(lockedTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(livePreferredTony)
                && peer.ActiveCharacter.Equals(livePreferredTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();
    }

    private XagmanPeerPresence? GetXagmanTonySellLocationPeerForOwner()
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (territoryId == 0)
            return null;

        var lockedTony = GetXagmanLockedTonyCharacter();
        var livePreferredTony = xagmanPreferredTonyCharacter;
        return plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled)
            .Where(peer => peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
            .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => peer.Status is XagmanStatus.AtMeetSpot or XagmanStatus.Called or XagmanStatus.Trading)
             .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
             .Where(peer => IsXagmanTonyPeerReachableFromOwner(peer))
             .Where(peer => peer.TonySellLocationActive)
            .Where(peer => peer.TonySellLocationTerritoryId == territoryId)
            .Where(peer => !float.IsNaN(peer.TonySellLocationX) && !float.IsNaN(peer.TonySellLocationY) && !float.IsNaN(peer.TonySellLocationZ))
            .Where(peer => !float.IsInfinity(peer.TonySellLocationX) && !float.IsInfinity(peer.TonySellLocationY) && !float.IsInfinity(peer.TonySellLocationZ))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(lockedTony)
                && peer.ActiveCharacter.Equals(lockedTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(livePreferredTony)
                && peer.ActiveCharacter.Equals(livePreferredTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();
    }

    private string GetXagmanOwnerQueueTonyCharacter()
    {
        return (GetXagmanUsableMeetTonyPeerForOwner() ?? GetXagmanTonySellLocationPeerForOwner())?.ActiveCharacter ?? string.Empty;
    }

    private bool TryGetXagmanOwnerTonyApproachPosition(string ownerCharacter, out Vector3 position, out string locationLabel)
    {
        position = Vector3.Zero;
        locationLabel = string.Empty;
        var tonyPeer = GetXagmanTonySellLocationPeerForOwner() ?? GetXagmanUsableMeetTonyPeerForOwner() ?? GetXagmanMeetTonyPeerForOwner();
        if (tonyPeer == null)
        {
            return false;
        }

        var seed = $"{ownerCharacter}|{plugin.InstanceId}|{tonyPeer.InstanceId}|{tonyPeer.ActiveCharacter}|{xagmanQueueRequestedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
        if (tonyPeer.LocalPositionAvailable
            && tonyPeer.TerritoryId != 0
            && tonyPeer.TerritoryId == Plugin.ClientState.TerritoryType)
        {
            var livePosition = new Vector3(tonyPeer.LocalPositionX, tonyPeer.LocalPositionY, tonyPeer.LocalPositionZ);
            if (IsValidXagmanPosition(livePosition))
            {
                position = RandomizeXagmanPositionDeterministic(livePosition, XagmanTonyLivePositionRandomRadius, seed);
                locationLabel = string.IsNullOrWhiteSpace(tonyPeer.ActiveCharacter)
                    ? "Tony's live position"
                    : $"Tony {tonyPeer.ActiveCharacter} live position";
                return true;
            }
        }

        if (!tonyPeer.TonySellLocationActive
            || tonyPeer.TonySellLocationTerritoryId == 0
            || tonyPeer.TonySellLocationTerritoryId != Plugin.ClientState.TerritoryType)
        {
            return false;
        }

        var sellPosition = new Vector3(
            tonyPeer.TonySellLocationX,
            tonyPeer.TonySellLocationY,
            tonyPeer.TonySellLocationZ);
        if (!IsValidXagmanPosition(sellPosition))
            return false;

        position = RandomizeXagmanPositionDeterministic(sellPosition, XagmanTonyLivePositionRandomRadius, seed + "|sell");
        locationLabel = string.IsNullOrWhiteSpace(tonyPeer.TonySellLocationName)
            ? "Tony's last item-sell position"
            : $"{tonyPeer.TonySellLocationName} near Tony's last item-sell position";
        return true;
    }

    private bool EnsureXagmanOwnerTonyCoordinateApproach(string ownerCharacter, float stopDistance, bool logPathStart)
    {
        if (!TryGetXagmanOwnerTonyApproachPosition(ownerCharacter, out var position, out var locationLabel))
            return true;

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null)
            return false;

        if (Vector3.Distance(local.Position, position) <= stopDistance)
        {
            if (IsXagmanMovementActive())
            {
                plugin.IpcClient.VnavStop();
                return false;
            }

            return true;
        }

        if (xagmanActiveRole == XagmanRole.FranchiseOwner && xagmanQueueRequestedAtUtc > DateTime.MinValue)
        {
            if (xagmanStatus is not (XagmanStatus.Standby or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.ReadyForQueue or XagmanStatus.Called or XagmanStatus.Trading))
                xagmanStatus = XagmanStatus.Standby;
        }
        else
        {
            xagmanStatus = XagmanStatus.Traveling;
        }
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S32561A144EE4D061", ownerCharacter, locationLabel));
        if (!plugin.IpcClient.VnavIsReady())
            return false;

        if (!IsXagmanMovementActive())
        {
            if (plugin.IpcClient.VnavPathfindAndMoveCloseTo(position, false, stopDistance))
            {
                if (logPathStart)
                {
                    plugin.TaskRunner.AddLog(
                        $"Xagman: owner {ownerCharacter} is pathing near {locationLabel} at randomized coords {FormatXagmanTonySellPosition(position)} with vnav stop distance {stopDistance:0.###} before targeting Tony.");
                    PublishXagmanPresence();
                }
            }
            else if (logPathStart)
            {
                plugin.TaskRunner.AddLog($"Xagman: vnavmesh did not accept the owner pre-approach path near {locationLabel}.");
            }
        }

        return false;
    }

    private bool TryBindXagmanFranchiseTonyForMeetup()
    {
        if (string.IsNullOrWhiteSpace(xagmanActiveMeetWorld))
        {
            if (!TryResolveXagmanMeetDestinationForOwner())
                return false;
        }
        var tonyPeer = GetXagmanReadyMeetTonyPeerForOwner() ?? GetXagmanMeetTonyPeerForOwner();
        if (tonyPeer == null)
            return false;
        var preferredChanged = !tonyPeer.ActiveCharacter.Equals(xagmanPreferredTonyCharacter, StringComparison.OrdinalIgnoreCase);
        var modeChanged = xagmanTonyMode != tonyPeer.TonyMode;
        xagmanPreferredTonyCharacter = tonyPeer.ActiveCharacter;
        xagmanTonyMode = tonyPeer.TonyMode;
        if (preferredChanged || modeChanged)
            PublishXagmanPresence();
        return true;
    }

    private bool TryGetXagmanMeetDestinationForOwner(
        out string meetWorld,
        out string meetAetheryte,
        string ownerCharacter = "")
    {
        // Server Matching: only follow a Tony that is currently sweeping this owner character's own
        // server, so the owner only ever world-travels inside its server (never DC-travels).
        var serverMatchingTony = GetXagmanServerMatchingTonyPeer();
        if (serverMatchingTony != null
            && xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            xagmanServerMatchingActive = true;
        }

        if (xagmanServerMatchingActive || serverMatchingTony != null)
        {
            if (serverMatchingTony == null
                || !IsXagmanServerMatchingTonyMeetReady(serverMatchingTony, out _))
            {
                meetWorld = string.Empty;
                meetAetheryte = string.Empty;
                return false;
            }

            var routeOwner = string.IsNullOrWhiteSpace(ownerCharacter)
                ? GetXagmanOwnerRouteCharacter()
                : ownerCharacter;
            var ownerDataCenter = GetXagmanDataCenterOfChar(routeOwner);
            if (!string.IsNullOrWhiteSpace(ownerDataCenter)
                && string.Equals(serverMatchingTony.ServerMatchingActiveDataCenter, ownerDataCenter, StringComparison.OrdinalIgnoreCase)
                && HasCompleteXagmanMeetDestination(serverMatchingTony.MeetWorld, serverMatchingTony.MeetAetheryte))
            {
                meetWorld = serverMatchingTony.MeetWorld;
                meetAetheryte = serverMatchingTony.MeetAetheryte;
                return true;
            }

            meetWorld = string.Empty;
            meetAetheryte = string.Empty;
            return false;
        }

        var lockedTony = GetXagmanLockedTonyCharacter();
        var livePreferredTony = xagmanPreferredTonyCharacter;
        var tonyPeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
            .Where(peer => !peer.ServerMatchingEnabled)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => IsXagmanTonyPeerReachableFromOwner(peer, ownerCharacter))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(lockedTony)
                && peer.ActiveCharacter.Equals(lockedTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(livePreferredTony)
                && peer.ActiveCharacter.Equals(livePreferredTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .FirstOrDefault();

        if (tonyPeer == null)
        {
            meetWorld = string.Empty;
            meetAetheryte = string.Empty;
            return false;
        }

        meetWorld = tonyPeer.MeetWorld;
        meetAetheryte = tonyPeer.MeetAetheryte;
        return true;
    }

    private bool TryGetXagmanFixedMeetRouteFailureForOwner(
        string ownerCharacter,
        out string reason)
    {
        var result = TryGetXagmanFixedMeetRouteFailureForOwnerMessage(ownerCharacter, out var presentation);
        reason = presentation.Original;
        return result;
    }

    private bool TryGetXagmanFixedMeetRouteFailureForOwnerMessage(
        string ownerCharacter,
        out global::XASlave.Localization.UiMessage reason)
    {
        var unreachablePeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => !peer.ServerMatchingEnabled)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.Status != XagmanStatus.Error)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.ActiveCharacter))
            .Where(peer => HasCompleteXagmanMeetDestination(peer.MeetWorld, peer.MeetAetheryte))
            .FirstOrDefault(peer =>
                !TryValidateXagmanMeetTravelMessage(ownerCharacter, peer.MeetWorld, out _));
        if (unreachablePeer == null)
        {
            reason = global::XASlave.Localization.UiMessage.Raw(string.Empty);
            return false;
        }

        TryValidateXagmanMeetTravelMessage(ownerCharacter, unreachablePeer.MeetWorld, out var routeFailure);
        reason =
            global::XASlave.Localization.UiMessage.Format("S35E57C2A2EA30534", unreachablePeer.ActiveCharacter, unreachablePeer.MeetWorld, routeFailure);
        return true;
    }

    private bool TryResolveXagmanMeetDestinationForOwner(string ownerCharacter = "")
    {
        if (!TryGetXagmanMeetDestinationForOwner(
                out var meetWorld,
                out var meetAetheryte,
                ownerCharacter))
            return false;

        var destinationChanged = !meetWorld.Equals(xagmanActiveMeetWorld, StringComparison.OrdinalIgnoreCase)
            || !meetAetheryte.Equals(xagmanActiveMeetAetheryte, StringComparison.OrdinalIgnoreCase);
        SetXagmanActiveMeetDestination(meetWorld, meetAetheryte);
        if (destinationChanged)
            PublishXagmanPresence();
        return HasCompleteXagmanActiveMeetDestination();
    }

    private bool IsXagmanFranchiseStartupReady()
    {
        if (!xagmanOwnerStartRequested)
            return false;
        var serverMatchingTony = GetXagmanServerMatchingTonyPeer();
        if (serverMatchingTony != null)
        {
            xagmanServerMatchingActive = true;
            return IsXagmanServerMatchingTonyMeetReady(serverMatchingTony, out _);
        }
        if (xagmanServerMatchingActive)
            return false;
        var routeOwner = GetXagmanOwnerRouteCharacter();
        if (!TryResolveXagmanMeetDestinationForOwner(routeOwner))
            return TryGetXagmanFixedMeetRouteFailureForOwnerMessage(routeOwner, out _);
        // Franchise Owners begin relog/travel as soon as a Tony peer is advertising the meet destination;
        // they no longer wait for Tony to physically reach the meet spot, so by the time Tony calls ready
        // the owners are already standing nearby. Trading is still Tony-gated (Tony only calls owners from
        // its queue once it is AtMeetSpot), and the owner travel flow already waits/retries if Tony is absent.
        if (GetXagmanMeetTonyPeerForOwner() == null)
            return false;
        return TryBindXagmanFranchiseTonyForMeetup();
    }

    private bool IsXagmanOwnerReadyToRotateTony()
    {
        var cancellationReady = xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && xagmanOwnerStandbyPending
            && xagmanOwnerPauseForTonyRotationRequested
            && !plugin.TaskRunner.IsRunning;
        if (!cancellationReady)
            return false;

        var targetTonyPeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => peer.ActiveCharacter.Equals(xagmanOwnerStandbyTonyCharacter, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.InstanceId.Equals(xagmanOwnerStandbyTonyInstanceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
        return XagmanStandbyRotationPolicy.CanPublishRotationReady(
            cancellationReady,
            xagmanOwnerStandbyTonyCharacter,
            xagmanOwnerStandbyTonyInstanceId,
            targetTonyPeer?.CoordinationProtocolRevision ?? 0,
            XagmanCollectionFirstCoordinationProtocolRevision);
    }

    private global::XASlave.Localization.UiMessage GetXagmanPriorityStatusLabel()
    {
        var baseLabel = global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C");
        if (!xagmanRunning)
            return baseLabel;

        if (xagmanActiveRole == XagmanRole.Tony)
        {
            var total = GetXagmanLocalTonyTotalCharacters();
            if (total <= 0)
                return baseLabel;
            return global::XASlave.Localization.UiMessage.Format("SDA59406204F3E73C", baseLabel, Math.Max(1, GetXagmanLocalTonyCurrentCharacterNumber()), total);
        }

        if (xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            var total = GetXagmanLocalOwnerTotalCharacters();
            if (total <= 0)
                return baseLabel;
            var current = xagmanStatus == XagmanStatus.Completed
                ? total
                : Math.Min(total, GetXagmanLocalOwnerCompletedCharacters() + 1);
            return global::XASlave.Localization.UiMessage.Format("SDA59406204F3E73C", baseLabel, Math.Max(1, current), total);
        }

        return baseLabel;
    }

    private void PublishXagmanPresence()
    {
        if (plugin.XagmanPeers == null || plugin.XagmanPeers.IsDisposed)
        {
            Plugin.Log.Warning("[Xagman] Cannot publish presence - peer service is null or disposed");
            return;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        var activeKey = !string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? xagmanActiveCharacter
            : MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        plugin.Configuration.ReloggerCharacterInfo.TryGetValue(activeKey, out var info);
        var currentWorld = local == null ? string.Empty : WorldData.GetById(local.CurrentWorld.RowId)?.Name ?? string.Empty;
        var homeWorld = local == null ? string.Empty : WorldData.GetById(local.HomeWorld.RowId)?.Name ?? string.Empty;
        var localPositionAvailable = Plugin.PlayerState.IsLoaded && local != null && IsValidXagmanPosition(local.Position);
        var role = xagmanRunning ? xagmanActiveRole : plugin.Configuration.XagmanRole;
        var preferredTony = role == XagmanRole.FranchiseOwner && !IsXagmanOwnerTonyLocked()
            ? string.Empty
            : (!string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
                ? xagmanPreferredTonyCharacter
                : GetXagmanPreferredTonyCharacter());
        var items = plugin.Configuration.XagmanItems;
        var hasConditionalItemPolicies = role == XagmanRole.FranchiseOwner
            && HasXagmanConditionalItemPolicies(items);
        var requestedItems = xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && !IsXagmanCollectionFirstCollectionPhase()
            ? CloneXagmanTradeRequests(xagmanOwnerRequestedItems)
            : new List<XagmanTradeRequestEntry>();
        XagmanGreenValueSnapshot? greenValueSnapshot = null;
        if (HasXagmanGreenValueSelectors(items) && Plugin.ClientState.IsLoggedIn)
        {
            TryGetXagmanGreenValueScan(false, out var greenValueScan);
            greenValueSnapshot = BuildXagmanGreenValuePresenceSnapshot(
                greenValueScan.Snapshot,
                activeKey);
        }
        var supplyInventoryComplete = TryCaptureXagmanSupplyInventory(out var supplyInventory);
        var queueNumber = GetXagmanLocalQueueNumber();
        var tonyRotationReady = IsXagmanOwnerReadyToRotateTony();
        var totalCharacters = xagmanRunning
            ? xagmanActiveRole switch
            {
                XagmanRole.Tony => GetXagmanLocalTonyTotalCharacters(),
                XagmanRole.FranchiseOwner => GetXagmanLocalOwnerTotalCharacters(),
                _ => 0,
            }
            : 0;
        var completedCharacters = xagmanRunning
            ? xagmanActiveRole switch
            {
                XagmanRole.Tony => GetXagmanLocalTonyCompletedCharacters(),
                XagmanRole.FranchiseOwner => GetXagmanLocalOwnerCompletedCharacters(),
                _ => 0,
            }
            : 0;
        var publishTonySellLocation = xagmanRunning
            && role == XagmanRole.Tony
            && xagmanTonySellLocationActive
            && xagmanTonySellLocationTerritoryId != 0;
        var publishTonyMeetDestination = xagmanRunning
            && role == XagmanRole.Tony
            && xagmanStatus != XagmanStatus.Error
            && HasCompleteXagmanActiveMeetDestination();

        xagmanPublishedEta = BuildXagmanEtaReport();

        try
        {
            plugin.XagmanPeers.PublishPresence(new XagmanPeerPresence
            {
                InstanceId = plugin.InstanceId,
                PluginVersion = BuildInfo.Version,
                ProcessId = plugin.ProcessId,
                LastSeenUtc = DateTime.UtcNow,
                IsLoggedIn = Plugin.PlayerState.IsLoaded && local != null,
                ContentId = Plugin.PlayerState.ContentId,
                CharacterName = local?.Name.ToString() ?? string.Empty,
                HomeWorld = homeWorld,
                CurrentWorld = currentWorld,
                TerritoryId = Plugin.ClientState.TerritoryType,
                TerritoryName = GetCurrentLocationName(),
                LocalPositionAvailable = localPositionAvailable,
                LocalPositionX = localPositionAvailable ? local!.Position.X : 0f,
                LocalPositionY = localPositionAvailable ? local!.Position.Y : 0f,
                LocalPositionZ = localPositionAvailable ? local!.Position.Z : 0f,
                XagmanEnabled = xagmanRunning || role == XagmanRole.FranchiseOwner,
                Role = role,
                TonyMode = xagmanTonyMode,
                Status = xagmanRunning ? xagmanStatus : XagmanStatus.Idle,
                StatusText = xagmanRunning ? xagmanStatusText : "Idle",
                CoordinationProtocolRevision = XagmanCollectionFirstCoordinationProtocolRevision,
                GreenValueProtocolRevision = XagmanGreenValueProtocolRevision,
                RunId = IsXagmanCollectionFirstRunActive() ? xagmanRunId : string.Empty,
                CollectionFirstEnabled = IsXagmanCollectionFirstRunActive(),
                CollectionFirstRequested = IsXagmanCollectionFirstRequestedForRole(
                    role,
                    hasConditionalItemPolicies),
                HasConditionalItemPolicies = hasConditionalItemPolicies,
                RunPhase = IsXagmanCollectionFirstRunActive() ? xagmanRunPhase : XagmanRunPhase.Legacy,
                PhaseTotalCharacters = IsXagmanCollectionFirstRunActive() ? Math.Max(0, xagmanPhaseTotalCharacters) : 0,
                PhaseResolvedCharacters = IsXagmanCollectionFirstRunActive()
                    ? Math.Max(0, Math.Min(xagmanPhaseResolvedCharacters, xagmanPhaseTotalCharacters))
                    : 0,
                PhaseComplete = IsXagmanCollectionFirstRunActive() && xagmanPhaseComplete,
                CompletionDirectiveAcknowledged = IsXagmanCollectionFirstRunActive()
                    && xagmanCompletionDirectiveAcknowledged,
                ActiveCharacter = activeKey,
                PreferredTonyCharacter = preferredTony,
                MeetWorld = role == XagmanRole.Tony
                    ? (publishTonyMeetDestination ? xagmanActiveMeetWorld : string.Empty)
                    : (xagmanRunning ? xagmanActiveMeetWorld : string.Empty),
                MeetAetheryte = role == XagmanRole.Tony
                    ? (publishTonyMeetDestination ? xagmanActiveMeetAetheryte : string.Empty)
                    : (xagmanRunning ? xagmanActiveMeetAetheryte : string.Empty),
                ServerMatchingEnabled = role == XagmanRole.Tony && xagmanServerMatchingActive,
                ServerMatchingSweepOrdinal = role == XagmanRole.Tony && xagmanServerMatchingActive ? GetXagmanCurrentSweepOrdinal() : -1,
                ServerMatchingActiveDataCenter = role == XagmanRole.Tony && xagmanServerMatchingActive ? xagmanSweepDataCenter : string.Empty,
                ServerMatchingPendingDataCenter = role == XagmanRole.FranchiseOwner
                    && xagmanRunning
                    && !xagmanPhaseComplete
                    ? xagmanOwnerSweepPendingDataCenter
                    : string.Empty,
                QueueRequestedAtUtc = xagmanRunning && xagmanActiveRole == XagmanRole.FranchiseOwner ? xagmanQueueRequestedAtUtc : DateTime.MinValue,
                TonyCompletionRequestedAtUtc = xagmanTonyCompletionRequestedAtUtc,
                TotalCharacters = totalCharacters,
                CompletedCharacters = completedCharacters,
                QueueNumber = queueNumber,
                ActiveTradePartner = xagmanActiveTradePartner,
                ActiveTradePartnerInstanceId = xagmanActiveTradePartnerInstanceId,
                TonyRotationReady = tonyRotationReady,
                StandbyRotationTonyCharacter = xagmanOwnerStandbyPending
                    && role == XagmanRole.FranchiseOwner
                    ? xagmanOwnerStandbyTonyCharacter
                    : string.Empty,
                StandbyRotationTonyInstanceId = xagmanOwnerStandbyPending
                    && role == XagmanRole.FranchiseOwner
                    ? xagmanOwnerStandbyTonyInstanceId
                    : string.Empty,
                MainInventoryFreeSlots = GetXagmanCharacterMainInventoryFreeSlots(activeKey),
                Gil = GetXagmanCharacterGil(activeKey),
                TonyGilMinimum = GetXagmanTonyGilMinimum(),
                TonySellLocationActive = publishTonySellLocation,
                TonySellLocationTerritoryId = publishTonySellLocation ? xagmanTonySellLocationTerritoryId : 0,
                TonySellLocationName = publishTonySellLocation ? xagmanTonySellLocationName : string.Empty,
                TonySellLocationX = publishTonySellLocation ? xagmanTonySellLocationPosition.X : 0f,
                TonySellLocationY = publishTonySellLocation ? xagmanTonySellLocationPosition.Y : 0f,
                TonySellLocationZ = publishTonySellLocation ? xagmanTonySellLocationPosition.Z : 0f,
                ItemIds = items
                    .Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem && item.ItemId > 0)
                    .Select(item => item.ItemId)
                    .Distinct()
                    .ToList(),
                RequestedItems = requestedItems,
                SupplyCycleRevision = XagmanSupplyCycleRevision,
                SupplyReceivingPolicyRegions = GetXagmanSupplyReceivingPolicyRegions(),
                SupplyPassId = role == XagmanRole.Tony ? xagmanSupplyPassId : xagmanOwnerSupplyPassId,
                SupplyCoordinatorInstanceId = role == XagmanRole.Tony ? plugin.InstanceId : xagmanOwnerSupplyTonyInstance,
                SupplyPassComplete = role == XagmanRole.FranchiseOwner && xagmanOwnerSupplyPassComplete,
                SupplyPassClosing = role == XagmanRole.Tony && xagmanSupplyPassClosing,
                OwnerCompletionRevision = XagmanOwnerCompletionRevision,
                OwnerCompletionToken = role == XagmanRole.FranchiseOwner ? xagmanOwnerCompletionToken : string.Empty,
                OwnerCompletionAckReceived = role == XagmanRole.FranchiseOwner && xagmanOwnerCompletionAckReceived,
                OwnerCompletionAcknowledgements = role == XagmanRole.Tony
                    ? new Dictionary<string, string>(xagmanCompletedOwnerClients) : new Dictionary<string, string>(),
                SupplyDrainOnly = role == XagmanRole.Tony && (xagmanSupplyDrainOnly || IsXagmanCapacityDrainActive()),
                CapacityDrainId = xagmanCapacityDrainId,
                CapacityRecoveryEpoch = xagmanCapacityRecoveryEpoch,
                CapacityRecoveryCoordinatorInstanceId = xagmanCapacityRecoveryCoordinatorInstanceId,
                CapacityRecoveryRegion = xagmanCapacityRecoveryRegion,
                CapacityDrainReady = xagmanCapacityDrainReady,
                CapacityCollectionRestored = xagmanCapacityCollectionRestored,
                SupplyInventoryComplete = supplyInventoryComplete,
                SupplyInventory = supplyInventory,
                PartialOwners = xagmanPartialOwners.Values.Select(state => new XagmanPartialOwnerState
                {
                    CharacterNameWorld = state.CharacterNameWorld,
                    RequestedItems = CloneXagmanTradeRequests(state.RequestedItems),
                    PendingGiveItems = CloneXagmanTradeRequests(state.PendingGiveItems),
                    ReasonMessage = state.ReasonMessage,
                }).ToList(),
                SupplyDeferredOwner = xagmanDeferredOwnerCharacter,
                SupplyDeferredOwnerInstanceId = xagmanDeferredOwnerInstanceId,
                SupplyPendingDataCenters = GetXagmanSupplyPendingDataCenters(),
                Eta = xagmanPublishedEta,
                GreenValueSnapshot = greenValueSnapshot,
                TradeCapacityForecast = GetXagmanLocalTradeCapacityForecastForPresence(),
            });
            xagmanLastPresencePublishUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Failed to publish presence");
        }
    }

    private string GetXagmanPreferredTonyCharacter()
    {
        var selected = GetSelectedXagmanTonyCharacters();
        if (selected.Count > 0)
            return selected[0].CharacterNameWorld;
        return plugin.Configuration.XagmanTonyCharacters
            .FirstOrDefault(entry => IsAutoRetainerCharacterAllowed(
                entry.CharacterNameWorld, plugin.Configuration.XagmanHonorArExclusions))?.CharacterNameWorld
            ?? string.Empty;
    }

    private string GetXagmanQueueFocusTony()
     {
         if (xagmanRunning && xagmanActiveRole == XagmanRole.Tony)
             return xagmanActiveCharacter;
         if (!string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter))
             return xagmanPreferredTonyCharacter;
         return GetXagmanPreferredTonyCharacter();
     }

     private static int GetXagmanQueuePriority(XagmanStatus status)
    {
        return status switch
        {
            XagmanStatus.Standby => 0,
            XagmanStatus.WaitingRoom => 1,
            XagmanStatus.Queued => 1,
            XagmanStatus.Paused => 1,
            XagmanStatus.ReadyForQueue => 2,
            _ => 10,
        };
    }

     private static int GetXagmanTradeTurnPriority(XagmanStatus status)
    {
        return status switch
        {
            XagmanStatus.Called => -2,
            XagmanStatus.Trading => -1,
            XagmanStatus.Standby => 0,
            XagmanStatus.WaitingRoom => 1,
            XagmanStatus.Queued => 1,
            XagmanStatus.Paused => 1,
            XagmanStatus.ReadyForQueue => 2,
            _ => 10,
        };
    }

    private string GetXagmanActiveTradeCoordinationDataCenter()
    {
        if (!xagmanServerMatchingActive)
            return string.Empty;

        if (xagmanActiveRole == XagmanRole.Tony)
            return xagmanSweepDataCenter;

        if (xagmanActiveRole == XagmanRole.FranchiseOwner)
        {
            if (!string.IsNullOrWhiteSpace(xagmanOwnerSweepPendingDataCenter))
                return xagmanOwnerSweepPendingDataCenter;

            var activeOwnerDataCenter = GetXagmanDataCenterOfChar(xagmanActiveCharacter);
            if (!string.IsNullOrWhiteSpace(activeOwnerDataCenter))
                return activeOwnerDataCenter;

            return GetXagmanServerMatchingTonyPeer()?.ServerMatchingActiveDataCenter ?? string.Empty;
        }

        return string.Empty;
    }

    private string GetXagmanPeerTradeCoordinationDataCenter(XagmanPeerPresence peer)
    {
        return !string.IsNullOrWhiteSpace(peer.ServerMatchingPendingDataCenter)
            ? peer.ServerMatchingPendingDataCenter
            : GetXagmanPeerDataCenter(peer);
    }

    private bool IsXagmanPeerInActiveTradeCoordinationScope(XagmanPeerPresence peer)
    {
        var activeDataCenter = GetXagmanActiveTradeCoordinationDataCenter();
        return string.IsNullOrWhiteSpace(activeDataCenter)
            || string.Equals(
                GetXagmanPeerTradeCoordinationDataCenter(peer),
                activeDataCenter,
                StringComparison.OrdinalIgnoreCase);
    }

     private List<XagmanPeerPresence> GetXagmanQueueForTony(string tonyCharacter)
     {
         return plugin.XagmanPeers.Peers
              .Where(peer => peer.XagmanEnabled)
              .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
              .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
              .Where(peer => IsXagmanPeerFresh(peer))
             .Where(peer => peer.QueueRequestedAtUtc > DateTime.MinValue)
              .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                  || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                  || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
              .Where(IsXagmanPeerInActiveTradeCoordinationScope)
              .Where(IsXagmanOwnerPeerReachableForActiveTony)
              .Where(peer => peer.Status is XagmanStatus.ReadyForQueue or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.Standby or XagmanStatus.Paused)
             .OrderBy(peer => GetXagmanQueuePriority(peer.Status))
             .ThenBy(peer => peer.QueueRequestedAtUtc)
             .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(peer => peer.ProcessId)
             .ToList();
     }

     private List<XagmanPeerPresence> GetXagmanTradeTurnPeersForTony(string tonyCharacter)
     {
         return plugin.XagmanPeers.Peers
              .Where(peer => peer.XagmanEnabled)
              .Where(peer => peer.Role == XagmanRole.FranchiseOwner)
              .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
              .Where(peer => peer.QueueRequestedAtUtc > DateTime.MinValue)
             .Where(peer => string.IsNullOrWhiteSpace(tonyCharacter)
                 || string.IsNullOrWhiteSpace(peer.PreferredTonyCharacter)
                 || peer.PreferredTonyCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
              .Where(peer => IsXagmanPeerFresh(peer))
              .Where(IsXagmanPeerInActiveTradeCoordinationScope)
              .Where(IsXagmanOwnerPeerReachableForActiveTony)
              .Where(peer => peer.Status is XagmanStatus.Standby or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.ReadyForQueue or XagmanStatus.Paused or XagmanStatus.Called or XagmanStatus.Trading)
             .OrderBy(peer => GetXagmanTradeTurnPriority(peer.Status))
             .ThenBy(peer => peer.QueueRequestedAtUtc)
             .ThenBy(peer => peer.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(peer => peer.ProcessId)
             .ToList();
     }

     private XagmanPeerPresence? GetXagmanInFlightOwnerForTony(string tonyCharacter)
    {
        return GetXagmanTradeTurnPeersForTony(tonyCharacter)
            .FirstOrDefault(peer => peer.Status is XagmanStatus.Called or XagmanStatus.Trading);
    }

    private XagmanPeerPresence? GetXagmanCallingTonyPeer(
        string characterNameWorld,
        string excludedTonyCharacter = "",
        string excludedTonyInstanceId = "")
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return null;

        var preferredTony = GetXagmanLockedTonyCharacter();
        var previousTonyInstanceId = xagmanActiveTradePartnerInstanceId;
        var activeDataCenter = GetXagmanActiveTradeCoordinationDataCenter();
        // The exact Tony -> owner plugin-instance call is authoritative. A stale preferred Tony name or
        // Tony client instance may influence a tie only; neither may reject a fresh replacement call.
         return plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
             .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
             .Where(peer => IsXagmanPeerFresh(peer))
            .Where(peer => IsXagmanTonyPeerReachableFromOwner(peer, characterNameWorld))
            .Where(peer => peer.ActiveTradePartner.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase))
            .Where(peer => peer.ActiveTradePartnerInstanceId.Equals(plugin.InstanceId, StringComparison.OrdinalIgnoreCase))
            .Where(peer => string.IsNullOrWhiteSpace(excludedTonyCharacter)
                || !peer.ActiveCharacter.Equals(excludedTonyCharacter, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(excludedTonyInstanceId)
                    && !peer.InstanceId.Equals(excludedTonyInstanceId, StringComparison.OrdinalIgnoreCase)))
            .Where(peer => string.IsNullOrWhiteSpace(activeDataCenter)
                || (peer.ServerMatchingEnabled
                    && peer.ServerMatchingActiveDataCenter.Equals(activeDataCenter, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(previousTonyInstanceId)
                && peer.InstanceId.Equals(previousTonyInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => !string.IsNullOrWhiteSpace(preferredTony)
                && peer.ActiveCharacter.Equals(preferredTony, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
    }

    private bool TryAdoptXagmanCallingTonyPeer(
        string characterNameWorld,
        out bool changed,
        string excludedTonyCharacter = "",
        string excludedTonyInstanceId = "")
    {
        changed = false;
        var tonyPeer = GetXagmanCallingTonyPeer(characterNameWorld, excludedTonyCharacter, excludedTonyInstanceId);
        if (tonyPeer == null)
            return false;

        changed = !xagmanPreferredTonyCharacter.Equals(tonyPeer.ActiveCharacter, StringComparison.OrdinalIgnoreCase)
            || !xagmanActiveTradePartner.Equals(tonyPeer.ActiveCharacter, StringComparison.OrdinalIgnoreCase)
            || !xagmanActiveTradePartnerInstanceId.Equals(tonyPeer.InstanceId, StringComparison.OrdinalIgnoreCase);
        if (IsXagmanSupplyCycleActive() && tonyPeer.SupplyPassId != xagmanOwnerSupplyPassId)
        {
            if (!TryBindXagmanOwnerSupplyPass()) return false;
        }
        xagmanPreferredTonyCharacter = tonyPeer.ActiveCharacter;
        xagmanActiveTradePartner = tonyPeer.ActiveCharacter;
        xagmanActiveTradePartnerInstanceId = tonyPeer.InstanceId;
        xagmanTonyMode = tonyPeer.TonyMode;
        if (changed
            && ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(characterNameWorld)
            && !TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage.Format("SF31A1118FA0BCF92", characterNameWorld)))
        {
            return false;
        }
        return true;
    }

     private int GetXagmanLocalQueueNumber()
     {
         if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner || xagmanQueueRequestedAtUtc == DateTime.MinValue)
             return 0;
         if (xagmanStatus is not (XagmanStatus.Standby or XagmanStatus.WaitingRoom or XagmanStatus.Queued or XagmanStatus.ReadyForQueue or XagmanStatus.Paused))
             return 0;
         var tonyCharacter = xagmanPreferredTonyCharacter;
         var peers = GetXagmanQueueForTony(tonyCharacter)
             .Select(peer => (Priority: GetXagmanQueuePriority(peer.Status), peer.QueueRequestedAtUtc, peer.ActiveCharacter, peer.InstanceId))
             .ToList();
         peers.Add((GetXagmanQueuePriority(xagmanStatus), xagmanQueueRequestedAtUtc, xagmanActiveCharacter, plugin.InstanceId));
         var ordered = peers
             .OrderBy(entry => entry.Priority)
             .ThenBy(entry => entry.QueueRequestedAtUtc)
             .ThenBy(entry => entry.ActiveCharacter, StringComparer.OrdinalIgnoreCase)
             .ThenBy(entry => entry.InstanceId, StringComparer.OrdinalIgnoreCase)
             .ToList();
         for (var i = 0; i < ordered.Count; i++)
         {
             if (!ordered[i].InstanceId.Equals(plugin.InstanceId, StringComparison.OrdinalIgnoreCase))
                 continue;
             return i + 1;
         }
         return 0;
     }

     private bool RequestXagmanTonyCompletion()
    {
        if (string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter) && !TryBindXagmanFranchiseTonyForMeetup())
            return false;
        var preferredTony = xagmanPreferredTonyCharacter;
        if (string.IsNullOrWhiteSpace(preferredTony))
            return false;
        xagmanPreferredTonyCharacter = preferredTony;
        xagmanTonyCompletionRequestedAtUtc = DateTime.UtcNow;
        PublishXagmanPresence();
        return true;
    }

     private bool ShouldPreArmXagmanOwnerAutoAcceptForPendingTonySupply(string characterNameWorld)
    {
        return xagmanRunning
            && xagmanActiveRole == XagmanRole.FranchiseOwner
            && !string.IsNullOrWhiteSpace(characterNameWorld)
            && xagmanOwnerRequestedItems.Count > 0
            && (IsXagmanCollectionFirstRestockPhase() || IsXagmanCapacityDrainActive() || IsXagmanOwnerSupplyDraining()
                || !HasXagmanOwnerCollectionItemsRemaining(plugin.Configuration.XagmanItems, characterNameWorld));
    }

     private bool IsXagmanOwnerCalled(string characterNameWorld)
     {
         var wasRunning = xagmanRunning;
         var adopted = TryAdoptXagmanCallingTonyPeer(characterNameWorld, out _);
         return adopted || (wasRunning && !xagmanRunning);
     }
     private XagmanTonyMode GetXagmanActiveTonyMode()
     {
         var preferredTony = string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter) ? GetXagmanPreferredTonyCharacter() : xagmanPreferredTonyCharacter;
         var liveTony = plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
             .Where(IsXagmanPeerInCurrentRunPhase)
             .FirstOrDefault(peer => string.IsNullOrWhiteSpace(preferredTony) || peer.ActiveCharacter.Equals(preferredTony, StringComparison.OrdinalIgnoreCase));
         if (liveTony != null)
             return liveTony.TonyMode;
         var configuredTony = plugin.Configuration.XagmanTonyCharacters.FirstOrDefault(entry => entry.CharacterNameWorld.Equals(preferredTony, StringComparison.OrdinalIgnoreCase));
         return configuredTony?.Mode ?? XagmanTonyMode.Collection;
     }
     private bool IsXagmanAtMeetDestination(string targetWorld, string targetAetheryte)
     {
         if (string.IsNullOrWhiteSpace(targetWorld))
             return false;
         var currentWorld = GetCurrentWorldName();
         if (!currentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
             return false;
         if (string.IsNullOrWhiteSpace(targetAetheryte))
             return true;
         var targetZoneId = AetheryteData.GetZoneIdForAetheryteWithFallback(targetAetheryte);
         if (targetZoneId != 0)
             return Plugin.ClientState.TerritoryType == targetZoneId;
         return AetheryteData.IsInCorrectZoneForAetheryte(targetAetheryte);
     }

     private void PrimeXagmanDropbox()
     {
         OpenXagmanDropboxWindow();
         OpenXagmanDropboxTradeTab();
         ClearXagmanDropbox();
     }

     private void AppendXagmanDropboxAutoAcceptStep(
        List<TaskStep> steps,
        global::XASlave.Localization.UiMessage label,
        bool enabled,
        Func<bool>? shouldSkip = null,
        bool requireSuccess = false,
        System.Action? onFailure = null)
     {
         steps.Add(new TaskStep
         {
             NameMessage = global::XASlave.Localization.UiMessage.Format("S468809FC44812762", label, ((enabled ? global::XASlave.Localization.UiMessage.Key("S5342E09F2729FBC6") : global::XASlave.Localization.UiMessage.Key("SB7E3E4AA4257B9A1")))),
             ShouldSkip = shouldSkip,
              OnEnter = () =>
              {
                  var success = TrySetXagmanDropboxAutoAcceptOrStop(
                      enabled,
                      label,
                      requireSuccess ? onFailure : null);
                  if (requireSuccess && !success)
                      plugin.TaskRunner.AddLog($"Xagman: failed to {(enabled ? "enable" : "disable")} Dropbox auto-accept trades for {label}.");
             },
             IsComplete = () => true,
             TimeoutSec = 0.5f,
         });
     }

     private void OpenXagmanDropboxWindow()
     {
         ChatHelper.SendMessage("/dropbox");
     }

     private void OpenXagmanDropboxTradeTab()
     {
         ChatHelper.SendMessage("/dropbox OpenTradeTab");
     }

     private void ClearXagmanDropbox()
     {
         TryClearXagmanDropbox(out _);
     }

     private bool TryClearXagmanDropbox(out string failure)
     {
         if (plugin.DropboxQueue.TryClearQueue(out var message))
         {
             failure = string.Empty;
             return true;
         }

         plugin.TaskRunner.AddLog($"Xagman: {message}");
         failure = message;
         return false;
     }

     private void ResetXagmanTradeCamera(string contextLabel)
     {
         plugin.TaskRunner.AddLog($"Xagman: requesting camera reset before {contextLabel}.");
         AddonHelper.ResetCamera();
     }

     private bool StartXagmanDropboxTrade(string contextLabel)
     {
         ResetXagmanTradeCamera(contextLabel);
         if (plugin.IpcClient.DropboxBeginTrading())
             return true;

         plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(contextLabel)
             ? "Xagman: failed to start the Dropbox trading queue."
             : $"Xagman: failed to start the Dropbox trading queue for {contextLabel}.");
         return false;
     }

     private void CleanupXagmanDropboxTradeAttempt(global::XASlave.Localization.UiMessage contextLabel)
     {
         var stoppedQueue = TryStopXagmanDropboxTradeQueue();
         var sentEscape = TryAbortXagmanTradeWindow();
         ClearXagmanDropbox();
         if (!TrySetXagmanDropboxAutoAcceptOrStop(false, contextLabel))
             return;
         ClearXagmanFocusTarget();
         xagmanObservedDropboxBusy = false;

         if (stoppedQueue)
             plugin.TaskRunner.AddLog($"Xagman: stopped Dropbox item trade queue for {contextLabel}.");
         else
             plugin.TaskRunner.AddLog($"Xagman: could not confirm Dropbox item trade queue stop for {contextLabel}.");

         if (sentEscape)
             plugin.TaskRunner.AddLog($"Xagman: sent ESC to close Trade for {contextLabel}.");
     }

     private static XagmanTradeFailureKind ClassifyXagmanTradeFailureText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return XagmanTradeFailureKind.None;

        foreach (var failureText in xagmanTradeFailureTexts)
        {
            if (text.Contains(failureText.Text, StringComparison.OrdinalIgnoreCase))
                return failureText.Kind;
        }

        return XagmanTradeFailureKind.None;
    }

     private static List<(XagmanTradeFailureKind Kind, string Text)> GetXagmanTradeFailureMatches(IEnumerable<string> textEntries)
    {
        return textEntries
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(text => (Kind: ClassifyXagmanTradeFailureText(text), Text: text))
            .Where(entry => entry.Kind != XagmanTradeFailureKind.None)
            .ToList();
    }

     private XagmanTradeFailureKind GetXagmanTradeFailureKind(out string matchedText)
    {
        const string addonName = "_TextError";
        matchedText = string.Empty;

        if (!AddonHelper.IsAddonVisible(addonName))
            return XagmanTradeFailureKind.None;

        var firstMatch = GetXagmanTradeFailureMatches(AddonHelper.GetAddonTextEntries(addonName)).FirstOrDefault();
        matchedText = firstMatch.Text ?? string.Empty;
        return firstMatch.Kind;
    }

     private static bool TryGetXagmanTonySellGilCapTextError(out string matchedText)
    {
        matchedText = string.Empty;
        if (!AddonHelper.IsAddonVisible(AddonHelper.TextErrorAddonName))
            return false;

        matchedText = AddonHelper.GetAddonTextEntries(AddonHelper.TextErrorAddonName)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)
                && text.Contains(XagmanTonySellGilCapText, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(matchedText);
    }

     private static bool TryCloseXagmanShopAddonAfterTonySellGilCap()
    {
        const string shopAddonName = "Shop";
        if (!AddonHelper.IsAddonVisible(shopAddonName))
            return false;

        if (AddonHelper.FireCallbackAndClose(shopAddonName, -1))
            return true;

        AddonHelper.CloseAddon(shopAddonName);
        return !AddonHelper.IsAddonVisible(shopAddonName);
    }

     private static string GetXagmanOwnerStandbyRotationRequestKey(XagmanPeerPresence ownerPeer)
    {
        var ownerKey = !string.IsNullOrWhiteSpace(ownerPeer.InstanceId)
            ? ownerPeer.InstanceId
            : !string.IsNullOrWhiteSpace(ownerPeer.ActiveCharacter)
                ? ownerPeer.ActiveCharacter
                : ownerPeer.CharacterName;
        return XagmanStandbyRotationPolicy.CreateRequestKey(
            ownerKey,
            ownerPeer.QueueRequestedAtUtc,
            ownerPeer.StandbyRotationTonyCharacter,
            ownerPeer.StandbyRotationTonyInstanceId,
            ownerPeer.CoordinationProtocolRevision);
    }

     private bool? TryObserveXagmanOwnerStandbyRotationRequest()
    {
        var ownerRotationPeer = GetXagmanOwnerRequestedTonyRotationPeer();
        if (ownerRotationPeer == null)
            return null;

        var ownerRotationRequestKey = GetXagmanOwnerStandbyRotationRequestKey(ownerRotationPeer);
        if (!XagmanStandbyRotationPolicy.TryAcceptRotationRequest(
                ownerRotationRequestKey,
                ownerRotationPeer.TonyRotationReady,
                ownerRotationPeer.StandbyRotationTonyCharacter,
                ownerRotationPeer.StandbyRotationTonyInstanceId,
                ownerRotationPeer.CoordinationProtocolRevision,
                xagmanActiveCharacter,
                plugin.InstanceId,
                XagmanCollectionFirstCoordinationProtocolRevision,
                xagmanLastConsumedOwnerStandbyRotationRequestKey,
                out var waitingForCancellation))
        {
            if (waitingForCancellation)
            {
                xagmanStatus = XagmanStatus.Paused;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S73390DF5C870B8DB", xagmanActiveCharacter, ownerRotationPeer.ActiveCharacter));
                return false;
            }

            return null;
        }

        if (!xagmanTonyRotationRequestedByOwnerStandby)
            plugin.TaskRunner.AddLog($"Xagman: owner {ownerRotationPeer.ActiveCharacter} confirmed Tony {xagmanActiveCharacter}'s scoped rotation is safe after standby cancel.");
        xagmanLastConsumedOwnerStandbyRotationRequestKey = ownerRotationRequestKey;
        xagmanTonyRotationRequestedByOwnerStandby = true;
        return true;
    }

     private XagmanPeerPresence? GetXagmanOwnerRequestedTonyRotationPeer()
    {
        var ownerPeer = GetXagmanActiveTradeOwnerPeer();
        if (ownerPeer == null)
            return null;

        return ownerPeer.Status == XagmanStatus.Standby
            && ownerPeer.QueueRequestedAtUtc > DateTime.MinValue
            ? ownerPeer
            : null;
    }

    private bool IsXagmanTaskRunnerActive()
    {
        return plugin.TaskRunner.IsRunning
            && plugin.TaskRunner.CurrentTaskName.Equals("Xagman", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryArmXagmanExpectedTravelLogoutWindow(string command, string contextLabel, out bool newlyArmed)
    {
        newlyArmed = false;
        var characterSnapshot = xagmanActiveCharacter.Trim();
        var localCharacterSnapshot = MonthlyReloggerTask.GetCurrentCharacterNameWorld().Trim();
        var contentIdSnapshot = Plugin.PlayerState.ContentId;
        var commandSnapshot = command.Trim();
        var taskRunnerActiveSnapshot = plugin.TaskRunner.IsRunning;
        if ((!xagmanRunning && !xagmanTradeSafetySessionActive)
            || string.IsNullOrWhiteSpace(localCharacterSnapshot)
            || contentIdSnapshot == 0
            || string.IsNullOrWhiteSpace(commandSnapshot)
            || string.IsNullOrWhiteSpace(contextLabel)
            || (taskRunnerActiveSnapshot && !IsXagmanTaskRunnerActive()))
        {
            ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        if (IsXagmanExpectedTravelLogoutWindowActive()
            && xagmanExpectedTravelLogoutContext.Equals(contextLabel, StringComparison.Ordinal))
        {
            var sameMarker = xagmanExpectedTravelLogoutCommand.Equals(commandSnapshot, StringComparison.OrdinalIgnoreCase)
                && xagmanExpectedTravelLogoutContentId == contentIdSnapshot
                && xagmanExpectedTravelLogoutTaskRunnerActive == taskRunnerActiveSnapshot;
            if (sameMarker)
                return true;
        }

        ClearXagmanExpectedTravelLogoutWindow();
        xagmanExpectedTravelLogoutContext = contextLabel;
        xagmanExpectedTravelLogoutCharacter = characterSnapshot;
        xagmanExpectedTravelLogoutLocalCharacter = localCharacterSnapshot;
        xagmanExpectedTravelLogoutContentId = contentIdSnapshot;
        xagmanExpectedTravelLogoutCommand = commandSnapshot;
        xagmanExpectedTravelLogoutRole = xagmanActiveRole;
        xagmanExpectedTravelLogoutStatus = xagmanStatus;
        xagmanExpectedTravelLogoutTaskRunnerActive = taskRunnerActiveSnapshot;
        xagmanExpectedTravelLogoutSawBusy = false;
        xagmanExpectedTravelLogoutUntilUtc = DateTime.UtcNow.AddSeconds(XagmanExpectedTravelLogoutWindowSeconds);
        newlyArmed = true;
        return true;
    }

    private bool ExecuteXagmanTravelCommandWithExpectedLogout(string command, string contextLabel)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        var markerArmed = TryArmXagmanExpectedTravelLogoutWindow(command, contextLabel, out var newlyArmed);
        var commandAccepted = plugin.IpcClient.LifestreamExecuteCommand(command);
        if (!commandAccepted)
        {
            if (newlyArmed)
                ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        if (!markerArmed)
        {
            plugin.TaskRunner.AddLog($"Xagman: Lifestream accepted '{command}', but a safe expected-logout marker could not be armed for {contextLabel}; any logout remains unexpected.");
            return true;
        }

        if (plugin.IpcClient.LifestreamIsBusy())
            MarkXagmanExpectedTravelLogoutBusyObserved();
        xagmanExpectedTravelLogoutUntilUtc = DateTime.UtcNow.AddSeconds(XagmanExpectedTravelLogoutWindowSeconds);
        if (newlyArmed)
            plugin.TaskRunner.AddLog($"Xagman: armed one expected logout for {contextLabel}; outgoing character {xagmanExpectedTravelLogoutLocalCharacter}, planned character {xagmanExpectedTravelLogoutCharacter}, command '{xagmanExpectedTravelLogoutCommand}' ({XagmanExpectedTravelLogoutWindowSeconds:0} seconds).");
        return true;
    }

    private void MarkXagmanExpectedTravelLogoutBusyObserved()
    {
        if (IsXagmanExpectedTravelLogoutWindowActive())
            xagmanExpectedTravelLogoutSawBusy = true;
    }

    private void ClearXagmanExpectedTravelLogoutWindow()
    {
        xagmanExpectedTravelLogoutUntilUtc = DateTime.MinValue;
        xagmanExpectedTravelLogoutContext = string.Empty;
        xagmanExpectedTravelLogoutCharacter = string.Empty;
        xagmanExpectedTravelLogoutLocalCharacter = string.Empty;
        xagmanExpectedTravelLogoutContentId = 0;
        xagmanExpectedTravelLogoutCommand = string.Empty;
        xagmanExpectedTravelLogoutRole = default;
        xagmanExpectedTravelLogoutStatus = XagmanStatus.Idle;
        xagmanExpectedTravelLogoutTaskRunnerActive = false;
        xagmanExpectedTravelLogoutSawBusy = false;
    }

    private bool IsXagmanExpectedTravelLogoutWindowActive()
    {
        if ((!xagmanRunning && !xagmanTradeSafetySessionActive)
            || string.IsNullOrWhiteSpace(xagmanExpectedTravelLogoutContext)
            || string.IsNullOrWhiteSpace(xagmanExpectedTravelLogoutLocalCharacter)
            || xagmanExpectedTravelLogoutContentId == 0
            || string.IsNullOrWhiteSpace(xagmanExpectedTravelLogoutCommand)
            || DateTime.UtcNow > xagmanExpectedTravelLogoutUntilUtc
            || xagmanActiveRole != xagmanExpectedTravelLogoutRole
            || xagmanStatus != xagmanExpectedTravelLogoutStatus
            || !xagmanActiveCharacter.Equals(xagmanExpectedTravelLogoutCharacter, StringComparison.OrdinalIgnoreCase)
            || plugin.TaskRunner.IsRunning != xagmanExpectedTravelLogoutTaskRunnerActive
            || (xagmanExpectedTravelLogoutTaskRunnerActive && !IsXagmanTaskRunnerActive()))
        {
            ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        return true;
    }

    private bool TryConsumeXagmanExpectedTravelLogout(ulong logoutContentId, out string contextLabel, out string localCharacter)
    {
        contextLabel = string.Empty;
        localCharacter = string.Empty;
        if (!IsXagmanExpectedTravelLogoutWindowActive())
            return false;

        if (logoutContentId == 0
            || logoutContentId != xagmanExpectedTravelLogoutContentId
            || (!xagmanExpectedTravelLogoutSawBusy && !plugin.IpcClient.LifestreamIsBusy()))
        {
            ClearXagmanExpectedTravelLogoutWindow();
            return false;
        }

        contextLabel = xagmanExpectedTravelLogoutContext;
        localCharacter = xagmanExpectedTravelLogoutLocalCharacter;
        ClearXagmanExpectedTravelLogoutWindow();
        return true;
    }

    internal bool HandleUnexpectedXagmanLogout(ulong logoutContentId)
    {
        if (xagmanLobbyOperation is { } lobbyOperation && xagmanRunning
            && lobbyOperation.RunId == plugin.TaskRunner.CurrentRunId
            && (!lobbyOperation.RunnerOwned || plugin.TaskRunner.IsRunning)
            && (lobbyOperation.Pending || plugin.LobbyErrorAutoClose.HasSupportedError
                || lobbyOperation.ErrorSequence != plugin.LobbyErrorAutoClose.ErrorSequence))
        {
            xagmanExpectedLogout = false;
            plugin.TaskRunner.AddLog("Xagman: logout observed inside bounded lobby recovery; preserving the interrupted operation.");
            return true;
        }
        if (IsXagmanTaskRunnerActive())
            plugin.TaskRunner.AddLog($"Xagman: logout notification: type={plugin.LastClientStateLogoutType}, code={plugin.LastClientStateLogoutCode}, owner={xagmanActiveCharacter}, step={plugin.TaskRunner.CurrentStep}, expected={xagmanExpectedLogout}, ownerRelogRecoveryArmed={xagmanOwnerRelogLogoutFailure != null}.");
        if (xagmanExpectedLogout)
        {
            var xagmanTaskRunnerWasActive = IsXagmanTaskRunnerActive();
            xagmanExpectedLogout = false;
            plugin.TaskRunner.AddLog("Xagman: expected relog/completion logout observed.");
            return xagmanTaskRunnerWasActive || !plugin.TaskRunner.IsRunning;
        }

        if (IsXagmanTaskRunnerActive() && xagmanOwnerRelogLogoutFailure is { } failOwnerRelog)
        {
            failOwnerRelog(global::XASlave.Localization.UiMessage.Format("SC7685E3F07F17F43", plugin.LastClientStateLogoutType, plugin.LastClientStateLogoutCode));
            plugin.TaskRunner.AddLog("Xagman: logout contained within owner relog/recovery; preserving the remaining roster.");
            return true;
        }

        if (TryConsumeXagmanExpectedTravelLogout(logoutContentId, out var travelContext, out var localCharacter))
        {
            plugin.TaskRunner.AddLog($"Xagman: expected logout for outgoing character {localCharacter} observed during {travelContext}; the one-shot window was consumed and the Xagman operation remains active.");
            return true;
        }

        if (!xagmanRunning && !xagmanTradeSafetySessionActive)
            return false;

        var xagmanTaskRunnerWasRunning = IsXagmanTaskRunnerActive();
        plugin.TaskRunner.AddLog("Xagman: unexpected logout detected; stopping Xagman and closing Dropbox auto-accept.");
        StopXagmanTask();
        if (xagmanStatus != XagmanStatus.Error)
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("SBD68DCAF108ECE72"));
        }
        return xagmanTaskRunnerWasRunning || !plugin.TaskRunner.IsRunning;
    }

    private bool xagmanArMultiGuardActive;
    private long xagmanArMultiNextCheck;

    private bool RunXagmanPreflightArMultiGuard()
    {
        if (!TryKeepXagmanArMultiDisabledMessage(true, out var reason))
        {
            plugin.TaskRunner.AddLog($"Pre-flight: AutoRetainer Multi safety check failed: {reason}");
            StopXagmanTask();
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S5B67935C082F9F54", reason));
            PublishXagmanPresence();
            return false;
        }
        xagmanArMultiGuardActive = true;
        xagmanArMultiNextCheck = 0;
        plugin.TaskRunner.AddLog("Pre-flight: AutoRetainer Multi is off (or AutoRetainer is not loaded); no pending AR work detected.");
        return true;
    }

    private bool TryKeepXagmanArMultiDisabled(bool starting, out string reason)
    {
        var result = TryKeepXagmanArMultiDisabledMessage(starting, out var presentation);
        reason = presentation.Original;
        return result;
    }

    private bool TryKeepXagmanArMultiDisabledMessage(bool starting, out global::XASlave.Localization.UiMessage reason)
    {
        reason = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        if (!plugin.IpcClient.TryGetAutoRetainerMultiModeEnabled(out var enabled))
        {
            // An absent plugin cannot schedule Multi work. A loaded plugin with broken IPC is unknown.
            if (!Plugin.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "AutoRetainer"))
                return true;
            reason = global::XASlave.Localization.UiMessage.Key("SE9E14159C4DDA3F2");
            return false;
        }

        if (enabled)
        {
            if (!plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false)
                || !plugin.IpcClient.TryGetAutoRetainerMultiModeEnabled(out var stillEnabled)
                || stillEnabled)
            {
                reason = global::XASlave.Localization.UiMessage.Key("S425115DB374D22A3");
                return false;
            }
            plugin.TaskRunner.AddLog(starting
                ? "Xagman: disabled AutoRetainer Multi before starting."
                : "Xagman: AutoRetainer Multi was re-enabled during the run; disabled it again.");
        }

        // Disabling Multi is not proof that previously scheduled logout/retainer work was canceled.
        // Do not abort AR tasks here: Lifestream also uses AR for legitimate relog operations.
        if (starting || enabled)
        {
            if (!plugin.IpcClient.TryGetAutoRetainerBusy(out var busy))
            {
                reason = global::XASlave.Localization.UiMessage.Key("SD821C5684C210B83");
                return false;
            }
            if (busy)
            {
                reason = global::XASlave.Localization.UiMessage.Key("SA963C89C730A4632");
                return false;
            }
        }
        return true;
    }

     private bool TryBeginXagmanTradeSafetySession(global::XASlave.Localization.UiMessage contextLabel)
    {
        if (!TryKeepXagmanArMultiDisabledMessage(true, out var arReason))
        {
            plugin.TaskRunner.AddLog($"Xagman: {contextLabel} refused: {arReason}");
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S927DE1A29F83096B", arReason));
            return false;
        }
        xagmanArMultiGuardActive = true;
        xagmanArMultiNextCheck = 0;
        if (xagmanTradeSafetySessionActive)
        {
            if (!xagmanRunning)
            {
                var recovered = TrySetXagmanDropboxAutoAccept(false);
                if (!recovered && xagmanDropboxAutoAcceptState == false)
                {
                    xagmanTradeSafetySessionActive = false;
                    plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.Inactive);
                }

                return recovered;
            }

            return TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SA23FF14004DB5D0D", contextLabel));
        }

        xagmanDropboxAutoAcceptState = null;
        xagmanLastTradeSafetyFailure = string.Empty;
        xagmanTradeSafetySessionActive = true;
        if (TrySetXagmanDropboxAutoAccept(false))
        {
            if (plugin.Configuration.XagmanRefuseTradesWhenIdle)
                plugin.TaskRunner.AddLog($"Xagman: Refuse Trades When Idle armed for {contextLabel}.");
            return true;
        }

        if (xagmanDropboxAutoAcceptState == false)
        {
            xagmanTradeSafetySessionActive = false;
            plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.Inactive);
        }

        ReportXagmanTradeSafetyFailureOnce(
            $"Xagman: could not safely establish trade coordination for {contextLabel}; the run was not started.");
        return false;
    }

     private bool TryEndXagmanTradeSafetySession(string contextLabel)
    {
        xagmanOwnerRelogLogoutFailure = null;
        xagmanExpectedLogout = false;
        ClearXagmanExpectedTravelLogoutWindow();
        if (!xagmanTradeSafetySessionActive)
        {
            var inactivePreferenceRestored = plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.Inactive);
            xagmanDropboxAutoAcceptState = null;
            if (inactivePreferenceRestored)
            {
                xagmanLastTradeSafetyFailure = string.Empty;
                return true;
            }

            ReportXagmanTradeSafetyFailureOnce(
                $"Xagman: {contextLabel} found no active safety session, but the saved Refuse Trade Request preference could not be restored.");
            return false;
        }

        TrySetXagmanDropboxAutoAccept(false);
        if (xagmanDropboxAutoAcceptState != false)
        {
            ReportXagmanTradeSafetyFailureOnce(
                $"Xagman: {contextLabel} could not confirm Dropbox auto-accept off; trade refusal remains suppressed.");
            return false;
        }

        xagmanTradeSafetySessionActive = false;
        var manualPreferenceRestored = plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.Inactive);
        xagmanDropboxAutoAcceptState = null;
        if (manualPreferenceRestored)
        {
            xagmanLastTradeSafetyFailure = string.Empty;
            return true;
        }

        ReportXagmanTradeSafetyFailureOnce(
            $"Xagman: {contextLabel} closed Dropbox auto-accept, but the saved Refuse Trade Request preference could not be restored.");
        return false;
    }

     private bool ReconcileXagmanTradeSafetyOption()
    {
        if (!plugin.Configuration.XagmanRefuseTradesWhenIdle)
        {
            if (xagmanTradeSafetySessionActive && xagmanDropboxAutoAcceptState != false)
            {
                if (!xagmanRunning)
                    return TryEndXagmanTradeSafetySession("disabled option recovery");

                return plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression);
            }

            xagmanLastTradeSafetyFailure = string.Empty;
            return plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.Inactive);
        }

        if (!xagmanRunning && !xagmanTradeSafetySessionActive)
            return plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.Inactive);

        xagmanTradeSafetySessionActive = true;
        if (!ApplyXagmanTradeRefusalConfiguration())
        {
            ReportXagmanTradeSafetyFailureOnce(
                "Xagman: the saved Refuse Trade Request preference could not be synchronized.");
            return false;
        }

        var targetOverride = xagmanDropboxAutoAcceptState == false
            ? XagmanTradeRefusalOverride.IdleDemand
            : XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression;
        var applied = plugin.AutoRefuseTrade.SetXagmanOverride(targetOverride);
        if (!applied)
        {
            ReportXagmanTradeSafetyFailureOnce(
                "Xagman: Refuse Trades When Idle could not activate the existing Refuse Trade Request hooks.");
        }
        else
        {
            xagmanLastTradeSafetyFailure = string.Empty;
        }

        return applied;
    }

     private bool ApplyXagmanTradeRefusalConfiguration()
    {
        plugin.AutoRefuseTrade.ApplyConfiguration(
            plugin.Configuration.AutoRefuseTradeShowNotification,
            plugin.Configuration.AutoRefuseTradeSendEcho,
            plugin.Configuration.AutoRefuseTradeExtraCommands);
        var manualPreference = plugin.Configuration.AutoRefuseTradeRequestEnabled;
        var manualPreferenceApplied = plugin.AutoRefuseTrade.SetEnabled(manualPreference);
        return !manualPreference || manualPreferenceApplied;
    }

     private void ReportXagmanTradeSafetyFailureOnce(string message)
    {
        if (message.Equals(xagmanLastTradeSafetyFailure, StringComparison.Ordinal))
            return;

        xagmanLastTradeSafetyFailure = message;
        plugin.TaskRunner.AddLog(message);
    }

     private bool TrySetXagmanDropboxAutoAccept(bool enabled)
    {
        if (xagmanTradeSafetySessionActive)
        {
            xagmanDropboxAutoAcceptState = null;
            if (!plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression))
            {
                ReportXagmanTradeSafetyFailureOnce(
                    "Xagman: could not suspend Refuse Trade Request before changing Dropbox auto-accept.");
                return false;
            }

            if (!ApplyXagmanTradeRefusalConfiguration())
            {
                ReportXagmanTradeSafetyFailureOnce(
                    "Xagman: the saved Refuse Trade Request preference could not be synchronized while refusal was suppressed.");
                return false;
            }
        }

        if (!TryWriteXagmanDropboxAutoAccept(enabled))
        {
            xagmanDropboxAutoAcceptState = null;
            if (xagmanTradeSafetySessionActive)
            {
                plugin.AutoRefuseTrade.SetXagmanOverride(XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression);
                ReportXagmanTradeSafetyFailureOnce(
                    $"Xagman: Dropbox auto-accept {(enabled ? "enable" : "disable")} failed; trade refusal remains suppressed because Dropbox state is unknown.");
            }

            return false;
        }

        xagmanDropboxAutoAcceptState = enabled;
        if (!xagmanTradeSafetySessionActive)
            return true;

        if (enabled)
        {
            xagmanLastTradeSafetyFailure = string.Empty;
            return true;
        }

        var targetOverride = plugin.Configuration.XagmanRefuseTradesWhenIdle
            ? XagmanTradeRefusalOverride.IdleDemand
            : XagmanTradeRefusalOverride.Inactive;
        var applied = plugin.AutoRefuseTrade.SetXagmanOverride(targetOverride);
        if (!applied)
        {
            ReportXagmanTradeSafetyFailureOnce(
                "Xagman: Dropbox auto-accept is off, but the requested manual/idle trade refusal state could not be restored.");
            return false;
        }

        xagmanLastTradeSafetyFailure = string.Empty;
        return true;
    }

     private bool TryWriteXagmanDropboxAutoAccept(bool enabled)
    {
        try
        {
            var dropboxPlugin = GetXagmanDropboxPlugin();
            if (dropboxPlugin == null)
                return false;

            var config = GetXagmanDropboxPluginConfig(dropboxPlugin);
            return config != null && TrySetXagmanDropboxConfigProperty(config, "Active", enabled);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[Xagman] Failed to set Dropbox auto-accept to {enabled}.");
            return false;
        }
      }

     private bool TrySetXagmanDropboxAutoAcceptOrStop(
        bool enabled,
        global::XASlave.Localization.UiMessage contextLabel,
        System.Action? beforeStop = null)
    {
        if (TrySetXagmanDropboxAutoAccept(enabled))
            return true;

        var transitionLabel = (enabled ? global::XASlave.Localization.UiMessage.Key("SB8D31E852725AFB1") : global::XASlave.Localization.UiMessage.Key("SB4DC66DDE806261B"));
        plugin.TaskRunner.AddLog(
            $"Xagman: stopping because Dropbox auto-accept could not be confirmed {transitionLabel} for {contextLabel}.");
        try
        {
            beforeStop?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[Xagman] Failure bookkeeping threw before the {contextLabel} fail-stop.");
        }
        StopXagmanTask();
        var safelyClosed = !xagmanTradeSafetySessionActive;
        var stopCleanupRestoredRefusal = xagmanStatus == XagmanStatus.Idle;
        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, (!safelyClosed ? global::XASlave.Localization.UiMessage.Format("SC7FBE3B215064376", transitionLabel, contextLabel) : (stopCleanupRestoredRefusal ? global::XASlave.Localization.UiMessage.Format("SC4F646E7EDC58334", transitionLabel, contextLabel) : global::XASlave.Localization.UiMessage.Format("S01945F8D7E7CD454", transitionLabel, contextLabel))));
        return false;
    }

     private bool TryRequireXagmanReceiverAutoAccept(global::XASlave.Localization.UiMessage contextLabel)
    {
        // This guard also runs while waiting; reset only when entering receive mode.
        if (xagmanDropboxAutoAcceptState != true)
            ResetXagmanTradeCamera(contextLabel.Original);
        if (TrySetXagmanDropboxAutoAcceptOrStop(true, contextLabel))
            return true;
        return false;
    }

     private bool TryStopXagmanDropboxTradeQueue()
    {
        try
        {
            var dropboxPlugin = GetXagmanDropboxPlugin();
            if (dropboxPlugin == null)
                return false;

            var taskManager = GetXagmanDropboxTaskManager(dropboxPlugin);
            return taskManager != null && TryAbortXagmanDropboxTaskManager(taskManager);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Failed to stop the Dropbox item trade queue.");
            return false;
        }
    }

     private static object? GetXagmanDropboxPlugin()
    {
        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");

            if (pluginManagerServiceType == null || pluginManagerType == null)
                return null;

            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);

            if (pluginManager == null)
                return null;

            var installedPlugins = pluginManager.GetType()
                .GetProperty("InstalledPlugins")
                ?.GetValue(pluginManager) as System.Collections.IList;

            if (installedPlugins == null)
                return null;

            foreach (var pluginEntry in installedPlugins)
            {
                if (pluginEntry == null)
                    continue;

                var internalName = pluginEntry.GetType()
                    .GetProperty("InternalName")
                    ?.GetValue(pluginEntry)
                    ?.ToString();

                if (!string.Equals(internalName, "Dropbox", StringComparison.Ordinal))
                    continue;

                var pluginType = pluginEntry.GetType().Name == "LocalDevPlugin"
                    ? pluginEntry.GetType().BaseType
                    : pluginEntry.GetType();

                if (pluginType == null)
                    continue;

                var instanceField = pluginType.GetField(
                    "instance",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                return instanceField?.GetValue(pluginEntry);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Failed to get the Dropbox plugin instance via reflection.");
        }

        return null;
    }

     private static object? GetXagmanDropboxPluginConfig(object pluginInstance)
    {
        try
        {
            var configFieldNames = new[] { "C", "Config", "configuration", "Configuration" };
            var pluginType = pluginInstance.GetType();
            var bindingFlags = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static;

            foreach (var fieldName in configFieldNames)
            {
                var field = pluginType.GetField(fieldName, bindingFlags);
                if (field == null)
                    continue;

                var config = field.GetValue(field.IsStatic ? null : pluginInstance);
                if (config != null)
                    return config;
            }

            foreach (var propertyName in configFieldNames)
            {
                var property = pluginType.GetProperty(propertyName, bindingFlags);
                if (property == null)
                    continue;

                var getter = property.GetGetMethod(true);
                var config = property.GetValue(getter != null && getter.IsStatic ? null : pluginInstance);
                if (config != null)
                    return config;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Failed to read Dropbox config via reflection.");
        }

        return null;
    }

     private static bool TrySetXagmanDropboxConfigProperty(object config, string propertyName, object value)
    {
        try
        {
            var bindingFlags = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance;

            var field = config.GetType().GetField(propertyName, bindingFlags);
            if (field != null)
            {
                field.SetValue(config, value);
                return Equals(field.GetValue(config), value);
            }

            var property = config.GetType().GetProperty(propertyName, bindingFlags);
            if (property != null && property.CanWrite && property.CanRead)
            {
                property.SetValue(config, value);
                return Equals(property.GetValue(config), value);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[Xagman] Failed to set Dropbox config property '{propertyName}'.");
        }

        return false;
    }

     private static object? GetXagmanDropboxTaskManager(object pluginInstance)
    {
        try
        {
            var bindingFlags = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static;

            var field = pluginInstance.GetType().GetField("TaskManager", bindingFlags);
            if (field != null)
            {
                var taskManager = field.GetValue(field.IsStatic ? null : pluginInstance);
                if (taskManager != null)
                    return taskManager;
            }

            var property = pluginInstance.GetType().GetProperty("TaskManager", bindingFlags);
            if (property != null)
            {
                var getter = property.GetGetMethod(true);
                var taskManager = property.GetValue(getter != null && getter.IsStatic ? null : pluginInstance);
                if (taskManager != null)
                    return taskManager;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Failed to get the Dropbox task manager via reflection.");
        }

        return null;
    }

     private static bool TryAbortXagmanDropboxTaskManager(object taskManager)
    {
        try
        {
            var abortMethod = taskManager.GetType().GetMethod(
                "Abort",
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);

            if (abortMethod == null)
                return false;

            abortMethod.Invoke(abortMethod.IsStatic ? null : taskManager, null);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Xagman] Failed to abort the Dropbox task manager via reflection.");
            return false;
        }
    }

     private static bool TryAbortXagmanTradeWindow()
    {
        try
        {
            KeyInputHelper.PressKey(KeyInputHelper.VK_ESCAPE);
            return true;
        }
        catch
        {
            return false;
        }
    }


     private int GetXagmanTonyGilMinimum()
    {
        return Math.Max(0, plugin.Configuration.XagmanTonyGilMinimum);
    }

     private int GetXagmanEffectiveTonyGilMinimum(string tonyCharacter)
    {
        var fallbackMinimum = GetXagmanTonyGilMinimum();
        if (string.IsNullOrWhiteSpace(tonyCharacter))
            return fallbackMinimum;
        var liveTonyPeer = plugin.XagmanPeers.Peers
            .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
            .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(peer => peer.ActiveCharacter.Equals(tonyCharacter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId)
                && peer.InstanceId.Equals(xagmanActiveTradePartnerInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
        return liveTonyPeer == null
            ? fallbackMinimum
            : (liveTonyPeer.TonyGilMinimum < 0
                ? fallbackMinimum
                : Math.Max(0, liveTonyPeer.TonyGilMinimum));
    }

     private int GetXagmanCharacterTradableQuantity(string characterNameWorld, uint itemId, bool isHq, string itemName, int? gilMinimumOverride = null)
    {
        // A running supplier can spend only the logged-in character's current gil.
        // Never fall back to a previous Tony's peer/database balance during relog.
        if (IsXagmanGilItem(itemId) && xagmanRunning && xagmanActiveRole == XagmanRole.Tony)
        {
            if (!characterNameWorld.Equals(xagmanActiveCharacter, StringComparison.OrdinalIgnoreCase)
                || !IsXagmanCurrentLocalCharacter(characterNameWorld) || !TryGetXagmanLiveGil(out var liveGil))
                return 0;
            return Math.Max(0, liveGil - Math.Max(0, gilMinimumOverride ?? GetXagmanTonyGilMinimum()));
        }
        var availableQuantity = GetXagmanCharacterItemQuantity(characterNameWorld, itemId, isHq, itemName);
        if (!IsXagmanGilItem(itemId))
            return availableQuantity;
        var gilMinimum = gilMinimumOverride ?? GetXagmanTonyGilMinimum();
        return Math.Max(0, availableQuantity - Math.Max(0, gilMinimum));
    }

    private int GetXagmanRequestedExactSupplyQuantity(
        XagmanTradeRequestEntry request,
        string localCharacter,
        out int localAvailable,
        out int localTradableQuantity)
    {
        localAvailable = GetXagmanCharacterHeldItemQuantity(
            localCharacter,
            request.ItemId,
            request.IsHq,
            request.ItemName);
        localTradableQuantity = GetXagmanCharacterTradableQuantity(
            localCharacter,
            request.ItemId,
            request.IsHq,
            request.ItemName);
        var requestedQuantity = request.Quantity <= 0
            ? localTradableQuantity
            : request.Quantity;
        return Math.Min(localTradableQuantity, requestedQuantity);
    }

     private string GetXagmanActiveTonyCharacterForGilRequests()
    {
        if (xagmanRunning)
        {
            if (xagmanActiveRole == XagmanRole.Tony && !string.IsNullOrWhiteSpace(xagmanActiveCharacter))
                return xagmanActiveCharacter;
            if (xagmanActiveRole == XagmanRole.FranchiseOwner)
            {
                if (!string.IsNullOrWhiteSpace(xagmanActiveTradePartner))
                    return xagmanActiveTradePartner;
                if (!string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter))
                    return xagmanPreferredTonyCharacter;
            }
        }
        var liveTony = GetXagmanLiveTonyPeer();
        if (liveTony != null && !string.IsNullOrWhiteSpace(liveTony.ActiveCharacter))
            return liveTony.ActiveCharacter;
        if (!string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter))
            return xagmanPreferredTonyCharacter;
        return GetXagmanPreferredTonyCharacter();
    }


     private List<XagmanTradeRequestEntry> BuildXagmanOwnerTradeRequests(IReadOnlyList<XagmanItemEntry> items, string ownerCharacter, bool logGilRequestSuppression = true)
    {
        var requests = new List<XagmanTradeRequestEntry>();
        var effectiveItems = ResolveXagmanItemsForOwner(items, ownerCharacter);
        var tonyCharacter = GetXagmanActiveTonyCharacterForGilRequests();
        var tonyGilMinimum = GetXagmanEffectiveTonyGilMinimum(tonyCharacter);
        XagmanGreenValueScanResult? greenScan = null;
        var greenScanAttempted = false;
        var greenScanComplete = false;
        var loggedGilRequestSuppression = false;
        void LogGilRequestSuppressed()
        {
            if (!logGilRequestSuppression)
                return;
            if (loggedGilRequestSuppression)
                return;
            var tonyLabel = string.IsNullOrWhiteSpace(tonyCharacter)
                ? "Tony"
                : $"Tony {tonyCharacter}";
            plugin.TaskRunner.AddLog($"Xagman: skipping gil request for {ownerCharacter} because {tonyLabel} can no longer trade gil above the configured minimum ({tonyGilMinimum.ToString("N0", CultureInfo.InvariantCulture)}).");
            loggedGilRequestSuppression = true;
        }
        foreach (var item in effectiveItems)
        {
            if (IsXagmanGreenValueSelector(item.SelectorKind))
            {
                if (!greenScanAttempted)
                {
                    greenScanAttempted = true;
                    greenScanComplete = TryGetXagmanGreenValueScan(true, out greenScan);
                }

                var targetScaled2 = Math.Max(0L, item.Quantity) * 2L;
                if (!greenScanComplete || greenScan == null)
                {
                    var scanError = greenScan?.ErrorMessage;
                    if (scanError == null || string.IsNullOrWhiteSpace(scanError.Original))
                        scanError = global::XASlave.Localization.UiMessage.Key("S112E1434C18190AF");
                    requests.Add(new XagmanTradeRequestEntry
                    {
                        SelectorKind = item.SelectorKind,
                        ItemId = 0,
                        ItemName = GetXagmanGreenValueSelectorName(item.SelectorKind),
                        IsHq = false,
                        Mode = XagmanItemMode.TopUp,
                        Quantity = (int)Math.Min(int.MaxValue, (targetScaled2 + 1L) / 2L),
                        TargetQuantity = Math.Max(0, item.Quantity),
                        CurrentQuantity = 0,
                        GreenValueProtocolRevision = XagmanGreenValueProtocolRevision,
                        TargetValueScaled2 = targetScaled2,
                        CurrentValueScaled2 = 0,
                        ValueDeficitScaled2 = targetScaled2,
                        GreenScanComplete = false,
                        GreenScanErrorMessage = scanError,
                    });
                    global::XASlave.Localization.UiMessage? failureTextUiPresentation = null;
                    var failureText = global::XASlave.Localization.UiMessage.Store(ref failureTextUiPresentation, global::XASlave.Localization.UiMessage.Format("S6447FAD333CB2F38", ownerCharacter, scanError));
                    if (!xagmanStatusText.Equals(failureText, StringComparison.Ordinal))
                        plugin.TaskRunner.AddLog($"Xagman: {failureText}");
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Capture(ref failureTextUiPresentation, failureText));
                    continue;
                }

                var currentScaled2 = GetXagmanGreenMetricScaled2(greenScan.Snapshot, item.SelectorKind);
                var deficitScaled2 = Math.Max(0L, targetScaled2 - currentScaled2);
                if (deficitScaled2 <= 0)
                    continue;
                requests.Add(new XagmanTradeRequestEntry
                {
                    SelectorKind = item.SelectorKind,
                    ItemId = 0,
                    ItemName = GetXagmanGreenValueSelectorName(item.SelectorKind),
                    IsHq = false,
                    Mode = XagmanItemMode.TopUp,
                    Quantity = (int)Math.Min(int.MaxValue, (deficitScaled2 + 1L) / 2L),
                    TargetQuantity = Math.Max(0, item.Quantity),
                    CurrentQuantity = (int)Math.Min(int.MaxValue, currentScaled2 / 2L),
                    GreenValueProtocolRevision = XagmanGreenValueProtocolRevision,
                    TargetValueScaled2 = targetScaled2,
                    CurrentValueScaled2 = currentScaled2,
                    ValueDeficitScaled2 = deficitScaled2,
                    GreenScanComplete = true,
                });
                continue;
            }
            if (item.Mode == XagmanItemMode.Give)
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            var tonyTradableQuantity = IsXagmanGilItem(item.ItemId)
                ? GetXagmanCharacterTradableQuantity(tonyCharacter, item.ItemId, item.IsHq, item.ItemName, tonyGilMinimum)
                : 0;
            if (item.Mode == XagmanItemMode.Take)
            {
                var takeCurrentQuantity = GetXagmanCharacterHeldItemQuantity(
                    ownerCharacter,
                    item.ItemId,
                    item.IsHq,
                    item.ItemName);
                var configuredTakeQuantity = Math.Max(0, item.Quantity);
                var takeStartingQuantity = takeCurrentQuantity;
                var takeTargetQuantity = 0;
                var requestedQuantity = configuredTakeQuantity;
                if (configuredTakeQuantity > 0)
                {
                    var finiteTakeGoalKey = GetXagmanFiniteTakeGoalKey(
                        ownerCharacter,
                        item.ItemId,
                        item.IsHq);
                    if (!xagmanFiniteTakeGoals.TryGetValue(finiteTakeGoalKey, out var finiteTakeGoal))
                    {
                        finiteTakeGoal = (
                            takeStartingQuantity,
                            (int)Math.Min(
                                int.MaxValue,
                                (long)takeStartingQuantity + configuredTakeQuantity));
                        xagmanFiniteTakeGoals[finiteTakeGoalKey] = finiteTakeGoal;
                    }

                    takeStartingQuantity = finiteTakeGoal.StartingQuantity;
                    takeTargetQuantity = finiteTakeGoal.TargetQuantity;
                    requestedQuantity = Math.Max(0, takeTargetQuantity - takeCurrentQuantity);
                    if (requestedQuantity <= 0)
                        continue;
                }
                if (IsXagmanGilItem(item.ItemId) && !IsXagmanSupplyCycleActive())
                {
                    requestedQuantity = item.Quantity <= 0
                        ? tonyTradableQuantity
                        : Math.Min(requestedQuantity, tonyTradableQuantity);
                    if (requestedQuantity <= 0)
                    {
                        LogGilRequestSuppressed();
                        continue;
                    }
                }
                requests.Add(new XagmanTradeRequestEntry
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    IsHq = item.IsHq,
                    Mode = item.Mode,
                    Quantity = requestedQuantity,
                    TargetQuantity = takeTargetQuantity,
                    CurrentQuantity = takeStartingQuantity,
                });
                continue;
            }
            var neededQuantity = Math.Max(0, item.Quantity - currentQuantity);
            if (IsXagmanGilItem(item.ItemId) && !IsXagmanSupplyCycleActive())
            {
                neededQuantity = Math.Min(neededQuantity, tonyTradableQuantity);
                if (neededQuantity <= 0 && item.Quantity > currentQuantity)
                {
                    LogGilRequestSuppressed();
                    continue;
                }
            }
            if (neededQuantity <= 0)
                continue;
            requests.Add(new XagmanTradeRequestEntry
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                IsHq = item.IsHq,
                Mode = item.Mode,
                Quantity = neededQuantity,
                TargetQuantity = Math.Max(0, item.Quantity),
                CurrentQuantity = currentQuantity,
            });
        }
        return requests;
     }

     private bool HasXagmanOwnerCollectionItemsRemaining(IReadOnlyList<XagmanItemEntry> items, string ownerCharacter)
    {
        if (IsXagmanSupplyCycleActive() && xagmanOwnerCollectionCompletedKeys.Contains(ownerCharacter))
            return false;
        var effectiveItems = ResolveXagmanItemsForOwner(items, ownerCharacter);
        foreach (var item in effectiveItems)
        {
            if (item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            var pendingQuantity = GetXagmanPendingGiveQuantity(ownerCharacter, item, currentQuantity);
            if (xagmanStatus == XagmanStatus.Error) return true;
            if (pendingQuantity > 0)
                return true;
        }
        return false;
     }

     private List<XagmanTradeRequestEntry> BuildXagmanTonySupplyRequests(IEnumerable<XagmanTradeRequestEntry> requests)
    {
        xagmanGreenSupplyValidationError = global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var requestList = requests?.ToList() ?? new List<XagmanTradeRequestEntry>();
        if (requestList.Any(entry => entry == null || !IsValidXagmanTradeRequest(entry)))
        {
            xagmanGreenSupplyValidationError =
                global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, global::XASlave.Localization.UiMessage.Key("S4D0131B5B64A037E"));
            return new List<XagmanTradeRequestEntry>();
        }
        if (HasDuplicateXagmanExactTradeRequests(requestList))
        {
            xagmanGreenSupplyValidationError =
                global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, global::XASlave.Localization.UiMessage.Key("S0E8DF634290D3039"));
            return new List<XagmanTradeRequestEntry>();
        }
        var validRequests = requestList;
        var supplyRequests = validRequests
            .Where(entry => entry.SelectorKind == XagmanItemSelectorKind.ExactItem)
            .Select(entry => new XagmanTradeRequestEntry
            {
                SelectorKind = entry.SelectorKind,
                ItemId = entry.ItemId,
                ItemName = entry.ItemName,
                IsHq = entry.IsHq,
                Mode = entry.Mode,
                Quantity = entry.Quantity,
                TargetQuantity = entry.TargetQuantity,
                CurrentQuantity = entry.CurrentQuantity,
            })
            .Where(entry =>
            {
                var localTradableQuantity = GetXagmanCharacterTradableQuantity(localCharacter, entry.ItemId, entry.IsHq, entry.ItemName);
                var requestedQuantity = entry.Quantity <= 0 ? localTradableQuantity : entry.Quantity;
                return Math.Min(localTradableQuantity, requestedQuantity) > 0;
            })
            .ToList();

        var greenRequests = validRequests
            .Where(entry => IsXagmanGreenValueSelector(entry.SelectorKind))
            .ToList();
        if (greenRequests.Count == 0)
            return supplyRequests;
        if (greenRequests
            .GroupBy(entry => entry.SelectorKind)
            .Any(group => group.Count() > 1))
        {
            xagmanGreenSupplyValidationError =
                global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, global::XASlave.Localization.UiMessage.Key("S3206CBF66A2013BB"));
            return supplyRequests;
        }
        foreach (var greenRequest in greenRequests)
        {
            if (!TryValidateXagmanGreenTradeRequestMessage(greenRequest, out var validationError))
            {
                xagmanGreenSupplyValidationError = global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, validationError);
                return supplyRequests;
            }
        }
        if (!TryGetXagmanGreenValueScan(true, out var localGreenScan))
        {
            xagmanGreenSupplyValidationError = global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, (string.IsNullOrWhiteSpace(localGreenScan.Snapshot.Error) ? global::XASlave.Localization.UiMessage.Key("S9D7CA82CD350B3AC") : global::XASlave.Localization.UiMessage.Format("S3EF7BC74F548B304", localGreenScan.ErrorMessage)));
            return supplyRequests;
        }
        if (localGreenScan.Snapshot.DropboxSafeItemCount <= 0
            && supplyRequests.Count == 0)
            return supplyRequests;
        if (!TryPlanXagmanGreenSupplyMessage(
                greenRequests,
                supplyRequests,
                out _,
                out var plannerError))
        {
            xagmanGreenSupplyValidationError = global::XASlave.Localization.UiMessage.Store(ref xagmanGreenSupplyValidationErrorUiMessage, plannerError);
            return supplyRequests;
        }
        // Preserve the green requests even when exact-item supply alone covers this pass. The queue
        // step replans from current inventory and must not lose the aggregate deficit if exact stock
        // changes while Tony is waiting for the owner.
        supplyRequests.AddRange(CloneXagmanTradeRequests(greenRequests));
        return supplyRequests;
     }

    private static bool TryValidateXagmanGreenTradeRequest(
        XagmanTradeRequestEntry request,
        out string error)
    {
        var result = TryValidateXagmanGreenTradeRequestMessage(request, out var presentation);
        error = presentation.Original;
        return result;
    }

    private static bool TryValidateXagmanGreenTradeRequestMessage(
        XagmanTradeRequestEntry request,
        out global::XASlave.Localization.UiMessage error)
    {
        error = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        if (!IsValidXagmanTradeRequest(request)
            || !request.ItemName.Equals(
                GetXagmanGreenValueSelectorName(request.SelectorKind),
                StringComparison.Ordinal))
        {
            error = global::XASlave.Localization.UiMessage.Key("SF498A0D5F90F4B19");
            return false;
        }
        if (request.GreenValueProtocolRevision != XagmanGreenValueProtocolRevision)
        {
            error =
                global::XASlave.Localization.UiMessage.Format("SDFF79207204DE5C1", request.GreenValueProtocolRevision);
            return false;
        }
        if (!request.GreenScanComplete)
        {
            error = (string.IsNullOrWhiteSpace(request.GreenScanError) ? global::XASlave.Localization.UiMessage.Key("S7C59FC14F4185C9C") : global::XASlave.Localization.UiMessage.Format("S89B72219D971E8E9", request.GreenScanErrorMessage));
            return false;
        }
        if (request.TargetQuantity <= 0
            || request.TargetValueScaled2 != (long)request.TargetQuantity * 2L
            || request.CurrentQuantity < 0
            || request.CurrentValueScaled2 < 0)
        {
            error = global::XASlave.Localization.UiMessage.Key("SA4A595C00B3E38B8");
            return false;
        }

        var expectedDeficitScaled2 = Math.Max(
            0L,
            request.TargetValueScaled2 - request.CurrentValueScaled2);
        var expectedQuantity = expectedDeficitScaled2 / 2L
            + (expectedDeficitScaled2 % 2L == 0 ? 0L : 1L);
        if (expectedDeficitScaled2 <= 0
            || request.ValueDeficitScaled2 != expectedDeficitScaled2
            || request.Quantity != (int)Math.Min(int.MaxValue, expectedQuantity)
            || request.CurrentQuantity != (int)Math.Min(
                int.MaxValue,
                request.CurrentValueScaled2 / 2L))
        {
            error = global::XASlave.Localization.UiMessage.Key("S54A29B09A605A948");
            return false;
        }

        return true;
    }

     private static List<XagmanTradeRequestEntry> CloneXagmanTradeRequests(IEnumerable<XagmanTradeRequestEntry> requests)
    {
        return requests
            .Select(entry => entry == null
                ? new XagmanTradeRequestEntry
                {
                    SelectorKind = (XagmanItemSelectorKind)(-1),
                    Mode = (XagmanItemMode)(-1),
                    GreenScanComplete = false,
                    GreenScanErrorMessage = global::XASlave.Localization.UiMessage.Key("S44A8ABCE3C1A9557"),
                }
                : new XagmanTradeRequestEntry
            {
                SelectorKind = entry.SelectorKind,
                ItemId = entry.ItemId,
                ItemName = entry.ItemName,
                IsHq = entry.IsHq,
                Mode = entry.Mode,
                Quantity = entry.Quantity,
                TargetQuantity = entry.TargetQuantity,
                CurrentQuantity = entry.CurrentQuantity,
                GreenValueProtocolRevision = entry.GreenValueProtocolRevision,
                TargetValueScaled2 = entry.TargetValueScaled2,
                CurrentValueScaled2 = entry.CurrentValueScaled2,
                ValueDeficitScaled2 = entry.ValueDeficitScaled2,
                GreenScanComplete = entry.GreenScanComplete,
                GreenScanErrorMessage = entry.GreenScanErrorMessage,
            })
            .ToList();
    }

     private static string GetXagmanTradeSnapshotKey(uint itemId, bool isHq)
    {
        return $"{itemId}:{(isHq ? 1 : 0)}";
    }

    private static string GetXagmanFiniteTakeGoalKey(
        string ownerCharacter,
        uint itemId,
        bool isHq)
    {
        return $"{ownerCharacter.Trim()}|{GetXagmanTradeSnapshotKey(itemId, isHq)}";
    }

    private void ResetXagmanFiniteTakeGoals()
    {
        xagmanFiniteTakeGoals.Clear();
    }

     private bool HasXagmanOwnerCollectionTradeCompleted(IReadOnlyList<XagmanItemEntry> items, string ownerCharacter)
    {
        if (IsXagmanSupplyCycleActive() && xagmanOwnerCollectionCompletedKeys.Contains(ownerCharacter))
            return true;
        var effectiveItems = ResolveXagmanItemsForOwner(items, ownerCharacter);
        foreach (var item in effectiveItems)
        {
            if (item.Mode is not (XagmanItemMode.Give or XagmanItemMode.Balance))
                continue;
            var currentQuantity = GetXagmanCharacterItemQuantity(ownerCharacter, item.ItemId, item.IsHq, item.ItemName);
            if (GetXagmanPendingGiveQuantity(ownerCharacter, item, currentQuantity) > 0
                || xagmanStatus == XagmanStatus.Error)
                return false;
        }
        return true;
    }

     private bool HasXagmanRequestedTradeProgress(IReadOnlyList<XagmanTradeRequestEntry> requests, string ownerCharacter)
    {
        XagmanGreenValueScanResult? greenScan = null;
        var greenScanAttempted = false;
        foreach (var request in requests)
        {
            if (IsXagmanGreenValueSelector(request.SelectorKind))
            {
                if (!greenScanAttempted)
                {
                    greenScanAttempted = true;
                    if (!TryGetXagmanGreenValueScan(true, out greenScan))
                    {
                        var error = string.IsNullOrWhiteSpace(greenScan.Snapshot.Error)
                            ? global::XASlave.Localization.UiMessage.Key("S63CCAF3BF44B4AF6")
                            : greenScan.ErrorMessage;
                        xagmanStatus = XagmanStatus.Error;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SCDF9B9F14E9D108C", ownerCharacter, error));
                        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
                        return false;
                    }
                }
                var currentScaled2 = GetXagmanGreenMetricScaled2(greenScan!.Snapshot, request.SelectorKind);
                if (currentScaled2 > request.CurrentValueScaled2
                    || (request.TargetValueScaled2 > 0 && currentScaled2 >= request.TargetValueScaled2))
                {
                    return true;
                }
                continue;
            }
            if (request.ItemId == 0)
                continue;
            var currentQuantity = request.Mode == XagmanItemMode.Take
                ? GetXagmanCharacterHeldItemQuantity(
                    ownerCharacter,
                    request.ItemId,
                    request.IsHq,
                    request.ItemName)
                : GetXagmanCharacterItemQuantity(
                    ownerCharacter,
                    request.ItemId,
                    request.IsHq,
                    request.ItemName);
            if (currentQuantity > request.CurrentQuantity)
                return true;
            if (request.Mode is XagmanItemMode.Balance or XagmanItemMode.TopUp
                && request.TargetQuantity > 0
                && currentQuantity >= request.TargetQuantity)
                return true;
        }
        return false;
    }

    private void LogXagmanFiniteTakeReconciliation(
        IReadOnlyList<XagmanTradeRequestEntry> nextRequests,
        string ownerCharacter)
    {
        foreach (var previous in xagmanOwnerRequestedItems.Where(request =>
                     request.SelectorKind == XagmanItemSelectorKind.ExactItem
                     && request.Mode == XagmanItemMode.Take
                     && request.TargetQuantity > request.CurrentQuantity))
        {
            var next = nextRequests.FirstOrDefault(request =>
                request.SelectorKind == XagmanItemSelectorKind.ExactItem
                && request.ItemId == previous.ItemId
                && request.IsHq == previous.IsHq
                && request.Mode == XagmanItemMode.Take);
            var currentQuantity = GetXagmanCharacterHeldItemQuantity(
                ownerCharacter,
                previous.ItemId,
                previous.IsHq,
                previous.ItemName);
            var configuredQuantity = previous.TargetQuantity - previous.CurrentQuantity;
            var receivedQuantity = Math.Clamp(
                currentQuantity - previous.CurrentQuantity,
                0,
                configuredQuantity);
            var requestLabel = GetXagmanTradeRequestLabel(previous);

            if (next == null && currentQuantity >= previous.TargetQuantity)
            {
                plugin.TaskRunner.AddLog(
                    $"Xagman: Take {requestLabel} complete for {ownerCharacter}; started with {previous.CurrentQuantity}, now has {currentQuantity}, and received the requested {configuredQuantity}/{configuredQuantity} from Tony.");
                continue;
            }

            if (next != null
                && next.Quantity < previous.Quantity
                && currentQuantity > previous.CurrentQuantity)
            {
                plugin.TaskRunner.AddLog(
                    $"Xagman: Take {requestLabel} progress for {ownerCharacter}; started with {previous.CurrentQuantity}, now has {currentQuantity}, received {receivedQuantity}/{configuredQuantity}, and still needs {next.Quantity} from Tony to reach {previous.TargetQuantity}.");
            }
        }
    }

     private void SetXagmanOwnerRequestedItems(
        IReadOnlyList<XagmanTradeRequestEntry> requests,
        bool logRequests = true,
        string reconciliationOwner = "")
    {
        if (!string.IsNullOrWhiteSpace(reconciliationOwner))
            LogXagmanFiniteTakeReconciliation(requests, reconciliationOwner);

        xagmanOwnerRequestedItems.Clear();
        xagmanOwnerRequestedItems.AddRange(CloneXagmanTradeRequests(requests));
        if (logRequests)
        {
            var requestUnits = xagmanOwnerRequestedItems.Sum(item => Math.Max(0, item.Quantity));
            var allAvailableRequests = xagmanOwnerRequestedItems.Count(item => item.Mode == XagmanItemMode.Take && item.Quantity <= 0);
            foreach (var request in xagmanOwnerRequestedItems)
            {
                if (IsXagmanGreenValueSelector(request.SelectorKind))
                {
                    plugin.TaskRunner.AddLog(
                        $"Xagman: request {GetXagmanTradeRequestLabel(request)} <= {GetXagmanTradeRequestAmountLabel(request)} from Tony (owner={FormatXagmanScaled2(request.CurrentValueScaled2)}, target={FormatXagmanScaled2(request.TargetValueScaled2)}, scan={(request.GreenScanComplete ? "complete" : "incomplete")}).");
                }
                else
                {
                    var requestLabel = GetXagmanTradeRequestLabel(request);
                    if (request.Mode == XagmanItemMode.Take
                        && request.TargetQuantity > request.CurrentQuantity)
                    {
                        var configuredQuantity = request.TargetQuantity - request.CurrentQuantity;
                        plugin.TaskRunner.AddLog(
                            $"Xagman: owner {xagmanActiveCharacter} has {request.CurrentQuantity} {requestLabel}; Take {configuredQuantity} needs {request.Quantity} from Tony and completes at {request.TargetQuantity}.");
                    }
                    else
                    {
                        plugin.TaskRunner.AddLog($"Xagman: request {requestLabel} <= {GetXagmanTradeRequestAmountLabel(request)} from Tony (mode={request.Mode}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
                    }
                }
            }
            plugin.TaskRunner.AddLog($"Xagman: requested {xagmanOwnerRequestedItems.Count} Tony supply entr{(xagmanOwnerRequestedItems.Count == 1 ? "y" : "ies")} totaling {requestUnits} units{(allAvailableRequests > 0 ? $" + {allAvailableRequests} all-available request(s)" : string.Empty)}.");
        }
        PublishXagmanPresence();
    }

    private void SetXagmanOwnerRequestedItemsIfChanged(
        IReadOnlyList<XagmanTradeRequestEntry> requests,
        string reconciliationOwner = "")
    {
        if (xagmanOwnerRequestedItems.Count == requests.Count)
        {
            var unchanged = true;
            for (var i = 0; i < requests.Count; i++)
            {
                var current = xagmanOwnerRequestedItems[i];
                var next = requests[i];
                if (current.SelectorKind == next.SelectorKind
                    && current.ItemId == next.ItemId
                    && current.ItemName.Equals(next.ItemName, StringComparison.Ordinal)
                    && current.IsHq == next.IsHq
                    && current.Mode == next.Mode
                    && current.Quantity == next.Quantity
                    && current.TargetQuantity == next.TargetQuantity
                    && current.CurrentQuantity == next.CurrentQuantity
                    && current.GreenValueProtocolRevision == next.GreenValueProtocolRevision
                    && current.TargetValueScaled2 == next.TargetValueScaled2
                    && current.CurrentValueScaled2 == next.CurrentValueScaled2
                    && current.ValueDeficitScaled2 == next.ValueDeficitScaled2
                    && current.GreenScanComplete == next.GreenScanComplete
                    && current.GreenScanError.Equals(next.GreenScanError, StringComparison.Ordinal))
                {
                    continue;
                }

                unchanged = false;
                break;
            }

            if (unchanged)
                return;
        }

        SetXagmanOwnerRequestedItems(requests, false, reconciliationOwner);
    }

    private bool TryEnqueueXagmanDropboxItem(
        uint itemId,
        bool isHq,
        int quantity,
        string context)
    {
        if (plugin.DropboxQueue.TryEnqueueXagmanItemMessage(itemId, isHq, quantity, out var message))
            return true;

        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S0C1D08335B8B5F81", context, message));
        plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
        ClearXagmanDropbox();
        return false;
    }

     private int QueueXagmanOwnerCollectionItems(IReadOnlyList<XagmanItemEntry> items)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        if (IsXagmanSupplyCycleActive() && xagmanOwnerCollectionCompletedKeys.Contains(localCharacter))
            return 0;
        var effectiveItems = ResolveXagmanItemsForOwner(items, localCharacter, out var skippedUnknownConditionalGroup);
        if (skippedUnknownConditionalGroup)
            plugin.TaskRunner.AddLog($"Xagman: skipped conditional Shared Item policies for {localCharacter} because AutoRetainer registration is unknown.");
        var ownerGiveItems = effectiveItems
            .Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem)
            .Where(item => item.Mode is XagmanItemMode.Give or XagmanItemMode.Balance)
            .ToList();
        xagmanTradeQuantitySnapshot.Clear();
        var queuedEntries = 0;
        var queuedUnits = 0;
        foreach (var item in ownerGiveItems)
        {
            var localAvailable = GetXagmanCharacterItemQuantity(localCharacter, item.ItemId, item.IsHq, item.ItemName);
            xagmanTradeQuantitySnapshot[GetXagmanTradeSnapshotKey(item.ItemId, item.IsHq)] = localAvailable;
            var itemLabel = GetXagmanTradeItemLabel(item);
            var policyLabel = GetXagmanItemPolicyLabel(item);
            var limitLabel = GetXagmanTradeLimitLabel(item);
            var quantity = GetXagmanPendingGiveQuantity(localCharacter, item, localAvailable);
            if (xagmanStatus == XagmanStatus.Error) return 0;
            if (quantity <= 0)
            {
                plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => 0 (policy={policyLabel}, limit={limitLabel}, local={localAvailable}, partner=0).");
                continue;
            }
            if (!TryEnqueueXagmanDropboxItem(item.ItemId, item.IsHq, quantity, $"owner collection {itemLabel}"))
                return 0;
            queuedEntries++;
            queuedUnits += quantity;
            plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => {quantity} (policy={policyLabel}, limit={limitLabel}, local={localAvailable}, partner=0).");
        }
        plugin.TaskRunner.AddLog($"Xagman: queued {queuedEntries}/{ownerGiveItems.Count} owner give entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedUnits} units.");
        return queuedEntries;
     }

     private void QueueXagmanItems(IReadOnlyList<XagmanItemEntry> items)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var partnerCharacter = xagmanActiveTradePartner;
        var effectiveItems = ResolveXagmanItemsForOwner(items, partnerCharacter, out var skippedUnknownConditionalGroup);
        if (skippedUnknownConditionalGroup)
            plugin.TaskRunner.AddLog($"Xagman: skipped conditional Shared Item policies for {partnerCharacter} because AutoRetainer registration is unknown.");
        var queuedEntries = 0;
        var queuedUnits = 0;
        foreach (var item in effectiveItems.Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem))
        {
            var quantity = GetXagmanTradeQuantity(item, localCharacter, partnerCharacter, out var localAvailable, out var partnerAvailable);
            var itemLabel = GetXagmanTradeItemLabel(item);
            var policyLabel = GetXagmanItemPolicyLabel(item);
            var limitLabel = GetXagmanTradeLimitLabel(item);
            if (quantity <= 0)
            {
                plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => 0 (policy={policyLabel}, limit={limitLabel}, local={localAvailable}, partner={partnerAvailable}).");
                continue;
            }
            if (!TryEnqueueXagmanDropboxItem(item.ItemId, item.IsHq, quantity, $"shared item {itemLabel}"))
                return;
            queuedEntries++;
            queuedUnits += quantity;
            plugin.TaskRunner.AddLog($"Xagman: queue {itemLabel} => {quantity} (policy={policyLabel}, limit={limitLabel}, local={localAvailable}, partner={partnerAvailable}).");
        }
        plugin.TaskRunner.AddLog($"Xagman: queued {queuedEntries}/{effectiveItems.Count} effective item entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedUnits} units.");
     }

     private void QueueXagmanRequestedSupplyItems(IReadOnlyList<XagmanTradeRequestEntry> requests)
    {
        var localCharacter = string.IsNullOrWhiteSpace(xagmanActiveCharacter)
            ? MonthlyReloggerTask.GetCurrentCharacterNameWorld()
            : xagmanActiveCharacter;
        var exactRequests = requests
            .Where(entry => entry.SelectorKind == XagmanItemSelectorKind.ExactItem)
            .ToList();
        var greenRequests = requests
            .Where(entry => IsXagmanGreenValueSelector(entry.SelectorKind))
            .ToList();
        var greenQueue = new List<XagmanGreenQueueEntry>();
        if (greenRequests.Count > 0
            && !TryPlanXagmanGreenSupplyMessage(
                greenRequests,
                exactRequests,
                out greenQueue,
                out var plannerError))
        {
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SB95F2BE948567BBE", plannerError));
            plugin.TaskRunner.AddLog($"Xagman: {xagmanStatusText}");
            ClearXagmanDropbox();
            return;
        }
        var queuedEntries = 0;
        var queuedUnits = 0;
        foreach (var request in exactRequests)
        {
            var quantity = GetXagmanRequestedExactSupplyQuantity(
                request,
                localCharacter,
                out var localAvailable,
                out var localTradableQuantity);
            var requestLabel = GetXagmanTradeRequestLabel(request);
            var requestAmountLabel = GetXagmanTradeRequestAmountLabel(request);
            if (quantity <= 0)
            {
                plugin.TaskRunner.AddLog($"Xagman: supply {requestLabel} => 0 (requested={requestAmountLabel}, mode={request.Mode}, local={localAvailable}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
                continue;
            }
            if (!TryEnqueueXagmanDropboxItem(request.ItemId, request.IsHq, quantity, $"Tony supply {requestLabel}"))
                return;
            queuedEntries++;
            queuedUnits += quantity;
            if (request.Mode == XagmanItemMode.Take
                && request.TargetQuantity > request.CurrentQuantity)
            {
                plugin.TaskRunner.AddLog(
                    $"Xagman: supply {requestLabel} => {quantity} (Take remaining={requestAmountLabel}, Tony before={localAvailable}, Tony tradable={localTradableQuantity}, owner start={request.CurrentQuantity}, owner complete={request.TargetQuantity}).");
            }
            else
            {
                plugin.TaskRunner.AddLog($"Xagman: supply {requestLabel} => {quantity} (requested={requestAmountLabel}, mode={request.Mode}, local={localAvailable}, owner={request.CurrentQuantity}, target={request.TargetQuantity}).");
            }
        }
        foreach (var entry in greenQueue)
        {
            if (!TryEnqueueXagmanDropboxItem(entry.ItemId, entry.IsHq, entry.Quantity, $"green-value supply {entry.ItemName}"))
                return;
            queuedEntries++;
            queuedUnits += entry.Quantity;
            plugin.TaskRunner.AddLog(
                $"Xagman: green supply {(entry.IsHq ? $"{entry.ItemName} HQ" : entry.ItemName)} => {entry.Quantity} (GC Seals/ea={FormatXagmanScaled2(entry.GcSealsScaled2Each)}, FC Credits/ea={FormatXagmanScaled2(entry.FcCreditsScaled2Each)}).");
        }
        plugin.TaskRunner.AddLog($"Xagman: Tony queued {queuedEntries} requested supply entr{(queuedEntries == 1 ? "y" : "ies")} totaling {queuedUnits} physical item(s).");
     }

    private int GetXagmanTradeQuantity(XagmanItemEntry item, string localCharacter, string partnerCharacter, out int localAvailable, out int partnerAvailable)
    {
        localAvailable = GetXagmanCharacterItemQuantity(localCharacter, item.ItemId, item.IsHq, item.ItemName);
        partnerAvailable = GetXagmanCharacterItemQuantity(partnerCharacter, item.ItemId, item.IsHq, item.ItemName);
        var localTradableQuantity = GetXagmanCharacterTradableQuantity(localCharacter, item.ItemId, item.IsHq, item.ItemName);
        return item.Mode switch
        {
            XagmanItemMode.Give => item.Quantity <= 0 ? localTradableQuantity : Math.Min(localTradableQuantity, item.Quantity),
            XagmanItemMode.Balance => Math.Min(localTradableQuantity, Math.Max(0, item.Quantity - partnerAvailable)),
            XagmanItemMode.TopUp => Math.Min(localTradableQuantity, Math.Max(0, item.Quantity - partnerAvailable)),
            _ => 0,
        };
    }

    private static string GetXagmanTradeItemLabel(XagmanItemEntry item)
    {
        return item.IsHq ? $"{item.ItemName} HQ" : item.ItemName;
    }

    private static global::XASlave.Localization.UiMessage GetXagmanGreenValueSelectorNameMessage(XagmanItemSelectorKind selector)
        => selector switch
        {
            XagmanItemSelectorKind.GreenItemGcSeals => global::XASlave.Localization.UiMessage.Key("S99A8E806161A604D"),
            XagmanItemSelectorKind.GreenItemFcCreditsRankProgress => global::XASlave.Localization.UiMessage.Key("S69FD630CA71F1AD8"),
            _ => global::XASlave.Localization.UiMessage.Raw(GetXagmanGreenValueSelectorName(selector)),
        };

    private static global::XASlave.Localization.UiMessage GetXagmanTradeRequestLabelMessage(XagmanTradeRequestEntry request)
        => IsXagmanGreenValueSelector(request.SelectorKind)
            ? GetXagmanGreenValueSelectorNameMessage(request.SelectorKind)
            : request.IsHq ? global::XASlave.Localization.UiMessage.Format("S5864848AE53E7CAF", request.ItemName) : global::XASlave.Localization.UiMessage.Raw(request.ItemName);

    private static global::XASlave.Localization.UiMessage GetXagmanTradeRequestAmountLabelMessage(XagmanTradeRequestEntry request)
        => IsXagmanGreenValueSelector(request.SelectorKind)
            ? global::XASlave.Localization.UiMessage.Raw(FormatXagmanScaled2(request.ValueDeficitScaled2))
            : request.Mode == XagmanItemMode.Take && request.Quantity <= 0
                ? global::XASlave.Localization.UiMessage.Key("S5EF5EF0364B6939C") : global::XASlave.Localization.UiMessage.Raw(Math.Max(0, request.Quantity).ToString(CultureInfo.InvariantCulture));

    private static string GetXagmanTradeRequestLabel(XagmanTradeRequestEntry request)
    {
        if (IsXagmanGreenValueSelector(request.SelectorKind))
            return GetXagmanGreenValueSelectorName(request.SelectorKind);
        return request.IsHq ? $"{request.ItemName} HQ" : request.ItemName;
    }

    private static string GetXagmanTradeLimitLabel(XagmanItemEntry item)
    {
        return item.Quantity <= 0 ? "all" : item.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetXagmanTradeRequestAmountLabel(XagmanTradeRequestEntry request)
    {
        if (IsXagmanGreenValueSelector(request.SelectorKind))
            return FormatXagmanScaled2(request.ValueDeficitScaled2);
        return request.Mode == XagmanItemMode.Take && request.Quantity <= 0
            ? "all"
            : Math.Max(0, request.Quantity).ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsXagmanGilItem(uint itemId)
    {
        return itemId == 1;
    }

    private bool IsXagmanCurrentLocalCharacter(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return false;
        var currentCharacter = MonthlyReloggerTask.GetCurrentCharacterNameWorld();
        return !string.IsNullOrWhiteSpace(currentCharacter)
            && currentCharacter.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase);
    }

    private unsafe int GetXagmanLiveLocalItemQuantity(uint itemId, bool isHq, bool tradableOnly = true)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return 0;
            if (IsXagmanGilItem(itemId))
                return (int)Math.Min((ulong)int.MaxValue, inventoryManager->GetGil());

            var quantity = 0;
            var loadedContainerCount = 0;
            var inventoryTypes = IsXagmanElementalCrystalItem(itemId)
                ? XagmanCrystalInventoryTypes
                : XagmanMainInventoryTypes;
            foreach (var inventoryType in inventoryTypes)
            {
                if (!NativeArrayAccess.TryGetInventoryContainer(inventoryManager, inventoryType, out var container))
                    continue;
                loadedContainerCount++;
                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    if (!NativeArrayAccess.TryGetInventorySlot(container, slotIndex, out var slot)
                        || slot->ItemId == 0
                        || slot->GetBaseItemId() != itemId
                        || slot->IsHighQuality() != isHq
                        || (tradableOnly
                            && (slot->IsSymbolic
                                || slot->SpiritbondOrCollectability > 0)))
                    {
                        continue;
                    }
                    quantity += (int)slot->Quantity;
                }
            }

            return loadedContainerCount == inventoryTypes.Length ? quantity : 0;
        }
        catch
        {
            return 0;
        }
    }

    private unsafe int GetXagmanLiveLocalMainInventoryFreeSlots()
        => TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots) ? freeSlots : 0;

    private unsafe bool TryGetXagmanLiveLocalMainInventoryFreeSlots(out int freeSlots)
    {
        freeSlots = 0;
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return false;
            foreach (var bag in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
            {
                var container = inventoryManager->GetInventoryContainer(bag);
                if (container == null || !container->IsLoaded) return false;
            }
            freeSlots = (int)inventoryManager->GetEmptySlotsInBag();
            return freeSlots >= 0;
        }
        catch
        {
            return false;
        }
    }

    private int GetXagmanCharacterMainInventoryFreeSlots(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return 0;
        if (IsXagmanCurrentLocalCharacter(characterNameWorld))
            return GetXagmanLiveLocalMainInventoryFreeSlots();
        var livePeer = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.ActiveCharacter.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase));
        if (livePeer != null)
            return Math.Max(0, livePeer.MainInventoryFreeSlots);
        return plugin.Configuration.ReloggerCharacterInfo.TryGetValue(characterNameWorld, out var info)
            ? Math.Max(0, info?.MainInventoryFreeSlots ?? 0)
            : 0;
    }

    private int GetXagmanCharacterGil(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return 0;
        if (IsXagmanCurrentLocalCharacter(characterNameWorld))
            return GetXagmanLiveLocalItemQuantity(1, false);
        var livePeer = plugin.XagmanPeers.Peers.FirstOrDefault(peer => peer.ActiveCharacter.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase));
        if (livePeer != null)
            return Math.Max(0, livePeer.Gil);
        return plugin.Configuration.ReloggerCharacterInfo.TryGetValue(characterNameWorld, out var info)
            ? Math.Max(0, info?.Gil ?? 0)
            : 0;
    }

     private int GetXagmanCharacterItemQuantity(string characterNameWorld, uint itemId, bool isHq, string itemName)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld) || itemId == 0)
            return 0;
        if (IsXagmanGilItem(itemId))
            return GetXagmanCharacterGil(characterNameWorld);
        if (IsXagmanCurrentLocalCharacter(characterNameWorld))
            return GetXagmanLiveLocalItemQuantity(itemId, isHq);
        var query = GetXagmanItemSearchQuery(itemId, itemName);
        if (string.IsNullOrWhiteSpace(query))
            return 0;
        return SearchXagmanCharacterMatches(query)
            .Where(match => match.CharacterNameWorld.Equals(characterNameWorld, StringComparison.OrdinalIgnoreCase))
            .Where(match => match.ItemId == itemId)
            .Where(match => match.IsHq == isHq)
            .Sum(match => Math.Max(0, match.Quantity));
    }

    private int GetXagmanCharacterHeldItemQuantity(
        string characterNameWorld,
        uint itemId,
        bool isHq,
        string itemName)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld) || itemId == 0)
            return 0;
        if (IsXagmanGilItem(itemId))
            return GetXagmanCharacterGil(characterNameWorld);
        if (IsXagmanCurrentLocalCharacter(characterNameWorld))
            return GetXagmanLiveLocalItemQuantity(itemId, isHq, tradableOnly: false);

        // XA Database item snapshots describe held Inventory 1-4 quantities plus the dedicated
        // Crystals container for elemental shards/crystals/clusters. The local path above is the
        // only place the Dropbox eligibility filter differs.
        return GetXagmanCharacterItemQuantity(characterNameWorld, itemId, isHq, itemName);
    }

     private XagmanPeerPresence? GetXagmanLiveTonyPeer()
    {
        var preferredTony = string.IsNullOrWhiteSpace(xagmanPreferredTonyCharacter)
            ? GetXagmanPreferredTonyCharacter()
            : xagmanPreferredTonyCharacter;
         return plugin.XagmanPeers.Peers
             .Where(peer => peer.XagmanEnabled && peer.Role == XagmanRole.Tony)
             .Where(IsXagmanPeerInCurrentRunPhase)
            .Where(IsXagmanPeerInSupplyCoordinatorScope)
             .Where(peer => string.IsNullOrWhiteSpace(preferredTony) || peer.ActiveCharacter.Equals(preferredTony, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(peer => !string.IsNullOrWhiteSpace(xagmanActiveTradePartnerInstanceId)
                && peer.InstanceId.Equals(xagmanActiveTradePartnerInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(peer => peer.LastSeenUtc)
            .FirstOrDefault();
    }

     private bool HasXagmanActiveTonyTradeLock(string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return false;
        var wasRunning = xagmanRunning;
        var adopted = TryAdoptXagmanCallingTonyPeer(characterNameWorld, out _);
        return adopted || (wasRunning && !xagmanRunning);
    }

     private bool ShouldXagmanOwnerStandbyForTonyRotation(string characterNameWorld)
    {
        if (!xagmanRunning || xagmanActiveRole != XagmanRole.FranchiseOwner || string.IsNullOrWhiteSpace(characterNameWorld))
            return false;
        var tonyPeer = GetXagmanLiveTonyPeer();
        if (tonyPeer == null)
            return false;
        var tonyRotating = tonyPeer.Status is XagmanStatus.ReturningHome or XagmanStatus.Relogging or XagmanStatus.Traveling or XagmanStatus.Preflight or XagmanStatus.Paused or XagmanStatus.Error;
        return tonyRotating;
    }

     private void EnterXagmanOwnerStandby(string characterNameWorld, string? logMessage = null)
    {
        ResetXagmanTravelFailureMonitor();
        xagmanOwnerStandbyTonyCharacter = string.IsNullOrWhiteSpace(xagmanActiveTradePartner)
            ? xagmanPreferredTonyCharacter
            : xagmanActiveTradePartner;
        xagmanOwnerStandbyTonyInstanceId = xagmanActiveTradePartnerInstanceId;
        xagmanOwnerStandbyPriorTonyCallReleased = string.IsNullOrWhiteSpace(xagmanOwnerStandbyTonyCharacter);
        xagmanQueueRequestedAtUtc = DateTime.UtcNow;
        xagmanOwnerStartRequested = false;
        xagmanOwnerStandbyPending = true;
        xagmanOwnerPauseForTonyRotationRequested = true;
        xagmanStatus = XagmanStatus.Standby;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S2DBC7D5F876C83B6", characterNameWorld));
        xagmanPreferredTonyCharacter = string.Empty;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanObservedDropboxBusy = false;
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SE1C9745CE0E74014", characterNameWorld)))
            return;
        ClearXagmanFocusTarget();
        if (xagmanOwnerRequestedItems.Count > 0
            && HasXagmanOwnerCollectionItemsRemaining(plugin.Configuration.XagmanItems, characterNameWorld))
        {
            xagmanOwnerRequestedItems.Clear();
            plugin.TaskRunner.AddLog($"Xagman: cleared pending Tony supply requests for {characterNameWorld} because owner give-items still remain before the next Tony handoff.");
        }
        plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(logMessage)
            ? $"Xagman: owner {characterNameWorld} is on standby for the next Tony because the current Tony rotated."
            : logMessage);
        if (plugin.TaskRunner.IsRunning)
            plugin.TaskRunner.Cancel();
        PublishXagmanPresence();
    }

     private bool TryGetXagmanTonySellDestination(out XagmanTonySellDestination destination)
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (xagmanTonySellDestinationsByTerritoryId.TryGetValue(territoryId, out var foundDestination))
        {
            destination = foundDestination;
            return true;
        }

        destination = null!;
        return false;
    }

     private static string FormatXagmanTonySellPosition(Vector3 position)
    {
        return $"{position.X:0.000}, {position.Y:0.000}, {position.Z:0.000}";
    }

     private bool IsXagmanLocalPlayerNearPosition(Vector3 position, float maxDistance)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null)
            return false;
        return Vector3.Distance(local.Position, position) <= maxDistance;
    }

     private bool TryStartXagmanTonySellWhenInventoryFull(string activePartner, System.Action<bool, bool>? onCompleted = null)
    {
        if (!plugin.Configuration.XagmanSellWhenInventoryFull)
            return false;

        var activeTony = xagmanActiveCharacter;
        var currentGil = GetXagmanLiveLocalItemQuantity(1, false);
        if (currentGil >= XagmanTonySellGilLimit)
        {
            plugin.TaskRunner.AddLog($"Xagman: Tony {activeTony} has {currentGil.ToString("N0", CultureInfo.InvariantCulture)} gil, so Sell When Inventory Is Full is skipping item selling and using normal Tony rotation to avoid the 999,999,999 gil cap.");
            return false;
        }

        if (!TryGetXagmanTonySellDestination(out var destination))
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            var territoryName = GetCurrentLocationName();
            plugin.TaskRunner.AddLog($"Xagman: Sell When Inventory Is Full is enabled, but territory {territoryId.ToString(CultureInfo.InvariantCulture)} ({territoryName}) is not supported; using normal Tony full-inventory behavior.");
            return false;
        }

        if (!plugin.Configuration.XagmanUseDirectNpcSell && !plugin.IpcClient.IsAutoRetainerAvailable())
        {
            plugin.TaskRunner.AddLog("Xagman: Sell When Inventory Is Full is enabled, but AutoRetainer IPC is not available; using normal Tony full-inventory behavior.");
            return false;
        }

        if (!plugin.IpcClient.VnavIsReady())
        {
            plugin.TaskRunner.AddLog("Xagman: Sell When Inventory Is Full is enabled, but vnavmesh is not ready; using normal Tony full-inventory behavior.");
            return false;
        }

        return StartXagmanTonySellWhenInventoryFullTask(destination, activePartner, currentGil, onCompleted);
    }

     private bool StartXagmanTonySellWhenInventoryFullTask(XagmanTonySellDestination destination, string activePartner, int currentGil, System.Action<bool, bool>? onCompleted = null)
    {
        var runner = plugin.TaskRunner;
        var activeTony = xagmanActiveCharacter;
        var sellFailed = false;
        var sellSucceeded = false;
        var pathStarted = false;
        var arBusyObserved = false;
        var directSell = plugin.Configuration.XagmanUseDirectNpcSell;
        NpcSellSession? npcSell = null;
        var freeSlotsBefore = GetXagmanLiveLocalMainInventoryFreeSlots();
        var arStartupDeadlineUtc = DateTime.MinValue;
        var arCommandSent = false;
        var sellCleanupUncertain = false;
        var nextArBusyPollUtc = DateTime.MinValue;
        var destinationLabel = global::XASlave.Localization.UiMessage.Format("SF0A220C816CC527B", destination.NpcName, destination.LocationName);
        var randomizedDestinationPosition = RandomizeXagmanPosition(destination.Position, XagmanTonySellVendorRandomRadius);
        var sellFallbackReason = $"Xagman: Tony {activeTony} item-sell cleanup did not complete; using normal Tony full-inventory behavior.";

        void MarkSellFailed(string message, string? fallbackReasonOverride = null)
        {
            if (sellFailed)
                return;
            sellFailed = true;
            if (arCommandSent)
            {
                // This sequence dispatched the command only after proving AR idle.
                if (!plugin.IpcClient.TryGetAutoRetainerBusy(out var stillBusy) || stillBusy)
                    sellCleanupUncertain = !plugin.IpcClient.AutoRetainerPluginStateAbortAllTasks();
                if (AddonHelper.IsAddonVisible("Shop"))
                    AddonHelper.FireCallbackAndClose("Shop", -1);
            }
            if (!string.IsNullOrWhiteSpace(fallbackReasonOverride))
                sellFallbackReason = fallbackReasonOverride;
            runner.AddLog(message);
            xagmanStatus = XagmanStatus.Error;
            xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S8AB241B5CAAFD05B", activeTony));
        }

        bool ShouldSkipSellStep() => sellFailed || !xagmanRunning || xagmanActiveRole != XagmanRole.Tony;

        bool TryAbortSellForGilCap()
        {
            if (!TryGetXagmanTonySellGilCapTextError(out var matchedText))
                return false;

            var shopClosed = TryCloseXagmanShopAddonAfterTonySellGilCap();
            MarkSellFailed(
                shopClosed
                    ? $"Xagman: Tony {activeTony} hit the gil cap while item selling ('{matchedText}'); fired callback Shop true -1 and is using normal Tony full-inventory behavior."
                    : $"Xagman: Tony {activeTony} hit the gil cap while item selling ('{matchedText}'); attempted callback Shop true -1, but Shop was not visible or did not confirm closed. Using normal Tony full-inventory behavior.",
                $"Xagman: Tony {activeTony} hit the gil cap while item selling; using normal Tony full-inventory behavior.");
            return true;
        }

        var steps = new List<TaskStep>
        {
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S56302D2BFA1B4A2B", activeTony),
                OnEnter = () =>
                {
                    if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Format("SA0002A8E528A298A", activeTony)))
                        return;
                    ClearXagmanDropbox();
                    ClearXagmanFocusTarget();
                    if (onCompleted == null)
                    {
                        // Standby owners must observe the old explicit call disappear before
                        // accepting another call from this same Tony. Publish that release
                        // throughout selling, not just in the brief post-sale queue cooldown.
                        // The captured activePartner still identifies any failure fallback.
                        xagmanActiveTradePartner = string.Empty;
                        xagmanActiveTradePartnerInstanceId = string.Empty;
                        xagmanObservedDropboxBusy = false;
                    }
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S200DB3C2E750DA0F", activeTony));
                    runner.AddLog($"Xagman: Tony {activeTony} inventory is full; Sell When Inventory Is Full is routing to randomized coords {FormatXagmanTonySellPosition(randomizedDestinationPosition)} within {XagmanTonySellVendorRandomRadius:0.###}y of {destinationLabel} ({destination.ZoneName}) with vnav stop distance {XagmanTonySellVendorStopDistance:0.###}.");
                    runner.AddLog($"Xagman: Tony {activeTony} gil before selling is {currentGil.ToString("N0", CultureInfo.InvariantCulture)}; selling is disabled at {XagmanTonySellGilLimit.ToString("N0", CultureInfo.InvariantCulture)} or above.");
                    if (onCompleted == null) PublishXagmanPresence();
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S3EC3ADAA59072258", destinationLabel),
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Traveling;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S7AB609B5CA6678A2", activeTony, destinationLabel));
                    pathStarted = false;
                    if (!plugin.IpcClient.VnavPathfindAndMoveCloseTo(randomizedDestinationPosition, false, XagmanTonySellVendorStopDistance))
                        MarkSellFailed($"Xagman: vnavmesh did not accept the item-sell path to {destinationLabel}.");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S6126ACD5B1E2F599", destinationLabel),
                ShouldSkip = ShouldSkipSellStep,
                IsComplete = () =>
                {
                    if (IsXagmanMovementActive())
                    {
                        pathStarted = true;
                        return true;
                    }
                    if (IsXagmanLocalPlayerNearPosition(randomizedDestinationPosition, XagmanTonySellDestinationArrivalTolerance))
                    {
                        pathStarted = true;
                        return true;
                    }
                    return false;
                },
                TimeoutSec = 15f,
                OnTimeout = () => MarkSellFailed($"Xagman: vnavmesh did not start moving Tony {activeTony} toward {destinationLabel}."),
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S3B4C3B4AD5E880C4", destinationLabel),
                ShouldSkip = ShouldSkipSellStep,
                IsComplete = () =>
                {
                    if (!pathStarted)
                        return false;
                    if (IsXagmanMovementActive())
                        return false;
                    if (!IsXagmanLocalPlayerNearPosition(randomizedDestinationPosition, XagmanTonySellDestinationArrivalTolerance))
                    {
                        MarkSellFailed($"Xagman: Tony {activeTony} stopped before reaching {destinationLabel}; /ays itemsell was not sent.");
                        return true;
                    }
                    return true;
                },
                TimeoutSec = 300f,
                OnTimeout = () => MarkSellFailed($"Xagman: timed out waiting for Tony {activeTony} to finish pathing to {destinationLabel}."),
            },
            MonthlyReloggerTask.MakeDelay(global::XASlave.Localization.UiMessage.Format("S33E62C7CFFF0F42F", activeTony), 0.25f, ShouldSkipSellStep),
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S768A87F82E9DACF2", activeTony),
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Paused;
                    freeSlotsBefore = GetXagmanLiveLocalMainInventoryFreeSlots();
                    if (directSell)
                    {
                        if (!NpcSellSession.TryParseIds(NpcSellSession.TreasureItemIds, out var ids, out var error))
                        { MarkSellFailed($"Xagman: {error}"); return; }
                        npcSell = new NpcSellSession(ids, runner.AddLog);
                        xagmanNpcSell = npcSell;
                        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("SAD9F9ABDD42058BC", activeTony));
                        runner.AddLog($"Xagman: reached {destinationLabel}; XA NPC seller IDs=[{string.Join(",", ids.OrderBy(id => id))}], free slots before={freeSlotsBefore}.");
                        return;
                    }
                    runner.AddLog($"Xagman: {AutoRetainerUiReflectionService.InspectNpcSellSettings()}");
                    if (!plugin.IpcClient.TryGetAutoRetainerBusy(out var alreadyBusy) || alreadyBusy)
                    { MarkSellFailed("Xagman: AutoRetainer is busy or its busy IPC is unavailable; item selling was not started."); return; }
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S9FCCE51E471FAB08", activeTony));
                    runner.AddLog($"Xagman: Tony {activeTony} reached {destinationLabel}; sending /ays itemsell (free slots before={freeSlotsBefore}).");
                    ChatHelper.SendMessage("/ays itemsell");
                    arCommandSent = true;
                    arBusyObserved = false;
                    arStartupDeadlineUtc = DateTime.UtcNow.AddSeconds(8);
                    nextArBusyPollUtc = DateTime.UtcNow.AddSeconds(1);
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            },
            new()
            {
                NameMessage = global::XASlave.Localization.UiMessage.Format("S2153866A508831B5", activeTony),
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = () =>
                {
                    xagmanStatus = XagmanStatus.Paused;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S27158D3EEA74570F", activeTony, ((directSell ? global::XASlave.Localization.UiMessage.Key("S12AA4AFE1BBC1E74") : global::XASlave.Localization.UiMessage.Key("SC1088FAE2C530913")))));
                },
                IsComplete = () =>
                {
                    if (directSell)
                    {
                        if (npcSell == null) { MarkSellFailed("Xagman: XA NPC selling did not initialize."); return true; }
                        if (!npcSell.Tick()) return false;
                        xagmanNpcSell = null;
                        if (!npcSell.Succeeded) MarkSellFailed($"Xagman: {npcSell.Result}");
                        return true;
                    }
                    if (DateTime.UtcNow < nextArBusyPollUtc)
                        return false;

                    if (!plugin.IpcClient.TryGetAutoRetainerBusy(out var busy))
                    { MarkSellFailed("Xagman: AutoRetainer busy IPC failed during selling; completion is unknown."); return true; }
                    nextArBusyPollUtc = DateTime.UtcNow.AddSeconds(1);
                    if (busy)
                    {
                        arBusyObserved = true;
                        if (TryAbortSellForGilCap())
                            return true;
                        return false;
                    }

                    if (TryAbortSellForGilCap())
                        return true;

                    if (!arBusyObserved && DateTime.UtcNow < arStartupDeadlineUtc)
                        return false;
                    if (arBusyObserved)
                        runner.AddLog($"Xagman: AutoRetainer item selling finished for Tony {activeTony}.");
                    else
                        runner.AddLog($"Xagman: AutoRetainer did not report busy within 8 seconds for Tony {activeTony}; sale is unconfirmed and inventory capacity must be verified.");
                    return true;
                },
                TimeoutSec = 900f,
                OnTimeout = () =>
                {
                    npcSell?.Abort("Xagman sell timeout");
                    xagmanNpcSell = null;
                    MarkSellFailed($"Xagman: timed out waiting for item selling to finish for Tony {activeTony}.");
                },
            },
        };

        foreach (var safeWait in MonthlyReloggerTask.BuildCharacterSafeWait3Pass(global::XASlave.Localization.UiMessage.Format("S57BF2BDFAA9F0A51", activeTony), 30f))
        {
            var originalComplete = safeWait.IsComplete;
            steps.Add(new TaskStep
            {
                NameMessage = safeWait.NameMessage,
                ShouldSkip = ShouldSkipSellStep,
                OnEnter = safeWait.OnEnter,
                IsComplete = () => ShouldSkipSellStep() || originalComplete(),
                TimeoutSec = safeWait.TimeoutSec,
                MaxRetries = safeWait.MaxRetries,
                OnTimeout = () => MarkSellFailed($"Xagman: CharacterSafeWait timed out after item selling for Tony {activeTony}."),
            });
        }

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S8D0ABDF73D623F30", activeTony),
            ShouldSkip = ShouldSkipSellStep,
            OnEnter = () =>
            {
                if (arCommandSent && (!plugin.IpcClient.TryGetAutoRetainerBusy(out var busy) || busy))
                { MarkSellFailed("Xagman: AutoRetainer is no longer confirmed idle at sell verification; owner restart was blocked."); return; }
                var freeSlotsAfter = GetXagmanLiveLocalMainInventoryFreeSlots();
                var gilAfter = GetXagmanLiveLocalItemQuantity(1, false);
                runner.AddLog($"Xagman: sell verification for Tony {activeTony}: free slots {freeSlotsBefore} -> {freeSlotsAfter}, gil {currentGil:N0} -> {gilAfter:N0}.");
                if (freeSlotsAfter <= 2 || (freeSlotsBefore <= 2 && freeSlotsAfter <= freeSlotsBefore))
                {
                    MarkSellFailed($"Xagman: Tony {activeTony} is still full after attempted selling. {(directSell ? "XA NPC selling did not recover usable capacity." : "AutoRetainer sell rules or execution may be blocking recovery; inspect the effective plan logged above.")} Rotating Tony or failing if none remain.");
                    return;
                }
                sellSucceeded = true;
                if (onCompleted != null)
                {
                    xagmanStatus = XagmanStatus.AtMeetSpot;
                    runner.AddLog($"Xagman ONH: Tony {activeTony} recovered inventory space; returning to the meetup before resuming.");
                    return;
                }
                SetXagmanTonySellLocation(destination, randomizedDestinationPosition);
                // A capacity handoff kept its rotation acknowledgement while selling.
                // Clear it only after verified recovery, preserving the
                // owner's finite Give ledger and the consumed standby request key.
                ResetXagmanTonySupplyDrain();
                xagmanTonyRotationRequestedByOwnerStandby = false;
                xagmanObservedDropboxBusy = false;
                xagmanActiveTradePartner = string.Empty;
                xagmanActiveTradePartnerInstanceId = string.Empty;
                xagmanSupplyInventoryCachedAtUtc = DateTime.MinValue;
                xagmanStatus = XagmanStatus.AtMeetSpot;
                xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Format("S67809AE7719174A9", activeTony));
                xagmanLastTonyActionAtUtc = DateTime.UtcNow;
                runner.AddLog($"Xagman: Tony {activeTony} finished item selling at {destinationLabel} and is resuming Xagman operations from that location.");
                PublishXagmanPresence();
                StartAllXagmanPeers();
            },
            IsComplete = () => true,
            TimeoutSec = 1f,
        });

        steps.Add(new TaskStep
        {
            NameMessage = global::XASlave.Localization.UiMessage.Format("S48E69FF1C264589D", activeTony),
            ShouldSkip = () => !sellFailed || !xagmanRunning || xagmanActiveRole != XagmanRole.Tony,
            IsComplete = () =>
            {
                if ((arCommandSent || (directSell && plugin.IpcClient.IsAutoRetainerAvailable()))
                    && (!plugin.IpcClient.TryGetAutoRetainerBusy(out var busy) || busy))
                    return false;
                if (AddonHelper.IsAddonVisible("Shop"))
                { AddonHelper.FireCallbackAndClose("Shop", -1); return false; }
                if (!CharacterSafetyHelper.IsCharacterSafeWaitReady()) return false;
                sellCleanupUncertain = false;
                return true;
            },
            TimeoutSec = 15f,
            OnTimeout = () => sellCleanupUncertain = true,
        });

        return runner.Start(
            global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"),
            steps,
            onFinished: () =>
            {
                if (onCompleted != null)
                {
                    onCompleted(sellSucceeded, sellCleanupUncertain);
                    return;
                }
                if (sellSucceeded)
                {
                    UpdateXagmanTonyTaskRunnerProgress();
                    return;
                }

                if (sellCleanupUncertain)
                {
                    xagmanStatus = XagmanStatus.Error;
                    xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Key("S2E861C01D28D9E8B"));
                    runner.RecordFailedCharacter(activeTony);
                    runner.AddLog($"Xagman: {xagmanStatusText}");
                    PublishXagmanPresence();
                    return;
                }

                ScheduleXagmanTonyFullInventoryFallback(activePartner, sellFallbackReason);
            },
            onLog: message => Plugin.Log.Information($"[TaskLogs] {message}"),
            suppressCompletionReport: true,
            preserveRunHistory: true,
            onTerminal: () =>
            {
                npcSell?.Dispose();
                if (ReferenceEquals(xagmanNpcSell, npcSell)) xagmanNpcSell = null;
            });
    }

     private void ScheduleXagmanTonyFullInventoryFallback(string activePartner, string fallbackReason)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
                await Plugin.RunOnGameThread(() =>
                {
                    if (!xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
                        return;
                    StartXagmanTonyFullInventoryFallback(activePartner, fallbackReason);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[Xagman] Failed to schedule Tony full-inventory fallback after item selling.");
            }
        });
    }

     private void StartXagmanTonyFullInventoryFallback(string activePartner, string? fallbackReason = null)
    {
        if (!xagmanSupplyCapacityRotationPending && TryBeginXagmanTonySupplyDrain()) return;
        if (IsXagmanCollectionFirstCollectionPhase() && !CanRotateXagmanTonyInCurrentScope()
            && TryBeginXagmanCapacityRecovery()) return;
        ResetXagmanTonySellLocation();
        if (!string.IsNullOrWhiteSpace(fallbackReason))
            plugin.TaskRunner.AddLog(fallbackReason);

        // This handoff completes Tony's receiving turn, not the owners' outstanding work.
        // A final capacity read is diagnostic only: an accepted standby handoff can happen
        // with spare slots, stack/gil limits, or after selling/draining changed the inventory.
        // Never create a character failure here or erase a previously recorded travel/login failure.
        var completedAtCapacity = IsXagmanCurrentLocalCharacter(xagmanActiveCharacter)
            && ((TryGetXagmanLiveLocalMainInventoryFreeSlots(out var freeSlots) && freeSlots == 0)
                || (!plugin.Configuration.XagmanOutsideNetworkHelper
                    && (IsXagmanCollectionFirstCollectionPhase() || xagmanSupplyCapacityRotationPending)
                    && TryGetXagmanLiveGil(out var currentGil) && currentGil >= XagmanTonySellGilLimit));
        var fallbackContext = completedAtCapacity
            ? $"Xagman: Tony {xagmanActiveCharacter} completed receiving at verified capacity."
            : string.IsNullOrWhiteSpace(activePartner)
            ? $"Xagman: Tony {xagmanActiveCharacter} completed its receiving turn at the capacity/standby handoff."
            : $"Xagman: Tony {xagmanActiveCharacter} completed its receiving turn after owner {activePartner} requested a replacement; remaining owner work is preserved.";
        if (CanRotateXagmanTonyInCurrentScope())
        {
            plugin.TaskRunner.AddLog($"{fallbackContext} Rotating to the next eligible Tony.");
            RotateXagmanTony();
            return;
        }

        xagmanStatus = XagmanStatus.Error;
        xagmanStatusText = global::XASlave.Localization.UiMessage.Store(ref xagmanStatusTextUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("SFF8FA889648BFA8F", xagmanActiveCharacter), ((plugin.Configuration.XagmanAutoReturnToFc ? global::XASlave.Localization.UiMessage.Key("S4D31AFBE602B996C") : global::XASlave.Localization.UiMessage.Key("S8728B6DFFDCFB854")))));
        MarkXagmanTonyConsumed(xagmanActiveCharacter);
        plugin.TaskRunner.AddLog($"{fallbackContext} {xagmanStatusText}");
        if (!TrySetXagmanDropboxAutoAcceptOrStop(false, global::XASlave.Localization.UiMessage.Key("S50E9D7E3CB571903")))
            return;
        ClearXagmanDropbox();
        ClearXagmanFocusTarget();
        PublishXagmanPresence();
        StartXagmanFailureRecallTask(global::XASlave.Localization.UiMessage.Capture(ref xagmanStatusTextUiMessage, xagmanStatusText), tonyCapacityExhausted: true);
    }

     private bool TryRotateXagmanTonyForPendingOwnerStandbyRequest()
    {
        if (!xagmanTonyRotationRequestedByOwnerStandby || !xagmanRunning || xagmanActiveRole != XagmanRole.Tony || plugin.TaskRunner.IsRunning)
            return false;

        if (TryBeginXagmanTonySupplyDrain()) return true;
        if (IsXagmanCollectionFirstCollectionPhase() && !CanRotateXagmanTonyInCurrentScope()
            && TryBeginXagmanCapacityRecovery()) return true;
        xagmanTonyRotationRequestedByOwnerStandby = false;
        var activePartner = xagmanActiveTradePartner;
        xagmanObservedDropboxBusy = false;
        xagmanActiveTradePartner = string.Empty;
        xagmanActiveTradePartnerInstanceId = string.Empty;
        xagmanLastTonyActionAtUtc = DateTime.UtcNow;

        // Preserve collected stock for the later supply phase; another selected receiver
        // is tried before capacity recovery recalls these stocked Tonys.
        if (!IsXagmanCollectionFirstRunActive() && !CanRotateXagmanTonyInCurrentScope()
            && TryStartXagmanTonySellWhenInventoryFull(activePartner))
            return true;

        StartXagmanTonyFullInventoryFallback(activePartner);
        return true;
    }

     private bool FocusXagmanCurrentTarget(string characterName)
     {
         var visibleCharacterName = GetCharacterNameFromKey(characterName);
         if (string.IsNullOrWhiteSpace(visibleCharacterName))
             return false;

         TryTargetCharacter(visibleCharacterName);

         var target = Plugin.TargetManager.Target;
         var targetName = target?.Name.ToString() ?? string.Empty;
         if (target == null || !targetName.Equals(visibleCharacterName, StringComparison.OrdinalIgnoreCase))
         {
             var focusTargetName = Plugin.TargetManager.FocusTarget?.Name.ToString() ?? string.Empty;
             if (!focusTargetName.Equals(visibleCharacterName, StringComparison.OrdinalIgnoreCase))
                 ClearXagmanFocusTarget();

             plugin.TaskRunner.AddLog(string.IsNullOrWhiteSpace(targetName)
                 ? $"Xagman: could not focus {visibleCharacterName}; no current target was selected."
                 : $"Xagman: could not focus {visibleCharacterName}; current target was {targetName}.");
             return false;
         }

         Plugin.TargetManager.FocusTarget = target;
         return true;
     }

     private static void ClearXagmanFocusTarget()
     {
         Plugin.TargetManager.FocusTarget = null;
     }

     private void TryTargetCharacter(string characterName)
     {
         var visibleCharacterName = GetCharacterNameFromKey(characterName);
         if (string.IsNullOrWhiteSpace(visibleCharacterName))
             return;
         AddonHelper.TargetByName(visibleCharacterName);
     }
     private bool TryPathToCurrentTarget(float stopDistance = 0.5f, string expectedCharacterName = "")
     {
         var local = Plugin.ObjectTable.LocalPlayer;
         var target = local?.TargetObject;
         if (local == null || !plugin.IpcClient.VnavIsReady())
             return false;
         var visibleExpectedName = GetCharacterNameFromKey(expectedCharacterName);
         if (target == null)
             return TryPathToVisibleCharacter(visibleExpectedName, stopDistance);
         if (!string.IsNullOrWhiteSpace(visibleExpectedName)
             && !target.Name.ToString().Equals(visibleExpectedName, StringComparison.OrdinalIgnoreCase))
         {
             return TryPathToVisibleCharacter(visibleExpectedName, stopDistance);
         }
         return plugin.IpcClient.VnavPathfindAndMoveCloseTo(target.Position, false, stopDistance);
     }

     private bool TryPathToVisibleCharacter(string characterName, float stopDistance)
     {
         var visibleCharacterName = GetCharacterNameFromKey(characterName);
         if (string.IsNullOrWhiteSpace(visibleCharacterName) || !plugin.IpcClient.VnavIsReady())
             return false;

         foreach (var gameObject in Plugin.ObjectTable)
         {
             if (gameObject == null)
                 continue;
             if (!gameObject.Name.ToString().Equals(visibleCharacterName, StringComparison.OrdinalIgnoreCase))
                 continue;
             return plugin.IpcClient.VnavPathfindAndMoveCloseTo(gameObject.Position, false, stopDistance);
         }

         return false;
     }

     private bool IsXagmanMovementActive()
    {
        return plugin.IpcClient.VnavPathIsRunning()
            || plugin.IpcClient.VnavNavPathfindInProgress()
            || plugin.IpcClient.VnavSimpleMovePathfindInProgress();
    }

     private bool IsCurrentTargetWithinStopDistanceAndStopped(string characterName, float stopDistance)
     {
         var visibleCharacterName = GetCharacterNameFromKey(characterName);
         if (string.IsNullOrWhiteSpace(visibleCharacterName))
             return false;
         var local = Plugin.ObjectTable.LocalPlayer;
         var target = local?.TargetObject;
         if (local == null)
             return false;
         var movementActive = IsXagmanMovementActive();
         if (target == null)
         {
             TryTargetCharacter(visibleCharacterName);
             if (!movementActive)
                 TryPathToVisibleCharacter(visibleCharacterName, stopDistance);
             return false;
         }
         var targetName = target.Name.ToString();
         if (!targetName.Equals(visibleCharacterName, StringComparison.OrdinalIgnoreCase))
         {
             TryTargetCharacter(visibleCharacterName);
             if (!movementActive)
                 TryPathToVisibleCharacter(visibleCharacterName, stopDistance);
             return false;
         }
         var dx = target.Position.X - local.Position.X;
         var dy = target.Position.Y - local.Position.Y;
         var dz = target.Position.Z - local.Position.Z;
         var centerDistance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
         var ringDistance = centerDistance - local.HitboxRadius - target.HitboxRadius;
         if (ringDistance <= stopDistance)
         {
             if (movementActive)
             {
                 plugin.IpcClient.VnavStop();
                 return false;
             }
             return true;
         }
         if (!movementActive)
             TryPathToCurrentTarget(stopDistance, visibleCharacterName);
         return false;
     }

     private bool IsCharacterInRangeWithoutMoving(string characterName)
    {
        var visibleCharacterName = GetCharacterNameFromKey(characterName);
        if (string.IsNullOrWhiteSpace(visibleCharacterName))
            return false;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null)
            return false;
        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject == null)
                continue;
            var objectName = gameObject.Name.ToString();
            if (!objectName.Equals(visibleCharacterName, StringComparison.OrdinalIgnoreCase))
                continue;
            var dx = gameObject.Position.X - local.Position.X;
            var dy = gameObject.Position.Y - local.Position.Y;
            var dz = gameObject.Position.Z - local.Position.Z;
            var centerDistance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var ringDistance = centerDistance - local.HitboxRadius - gameObject.HitboxRadius;
            return ringDistance <= 3.0f;
        }
        return false;
    }

     private bool IsCurrentTargetInRange(string characterName)
     {
         var visibleCharacterName = GetCharacterNameFromKey(characterName);
         if (string.IsNullOrWhiteSpace(visibleCharacterName))
             return false;
         var local = Plugin.ObjectTable.LocalPlayer;
         var target = local?.TargetObject;
         if (local == null || target == null)
             return false;
         var targetName = target.Name.ToString();
         if (!targetName.Equals(visibleCharacterName, StringComparison.OrdinalIgnoreCase))
         {
             TryTargetCharacter(visibleCharacterName);
             return false;
         }
         var dx = target.Position.X - local.Position.X;
         var dy = target.Position.Y - local.Position.Y;
         var dz = target.Position.Z - local.Position.Z;
         var centerDistance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
         var ringDistance = centerDistance - local.HitboxRadius - target.HitboxRadius;
         if (ringDistance <= 3.0f)
             return true;
         if (!plugin.IpcClient.VnavPathIsRunning() && !plugin.IpcClient.VnavSimpleMovePathfindInProgress())
             TryPathToCurrentTarget();
         return false;
     }
     private static string GetCharacterNameFromKey(string value)
     {
         if (string.IsNullOrWhiteSpace(value))
             return string.Empty;
         var atIndex = value.IndexOf('@');
         var characterName = atIndex >= 0 ? value.Substring(0, atIndex) : value;
         return characterName
             .Trim()
             .Replace("\\\"", "\"")
             .Trim('"', '\u201c', '\u201d')
             .Trim();
     }
     private static void ReindexSelectionSet(HashSet<int> selectedIndices, int removedIndex)
     {
         selectedIndices.Remove(removedIndex);
         var updated = new HashSet<int>();
         foreach (var index in selectedIndices)
             updated.Add(index > removedIndex ? index - 1 : index);
         selectedIndices.Clear();
         foreach (var index in updated)
             selectedIndices.Add(index);
     }
 }
